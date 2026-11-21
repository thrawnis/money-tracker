using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Per-user backups of all Money Tracker data, as AES-256 encrypted 7z files
/// under data/user-backups/{userId}/ (7-Zip's own format; file names encrypted
/// too). Each user's files are protected by a random password the server keeps,
/// encrypted with that user's data key (BackupKeyEncrypted).
///
/// Reliability: the snapshot is streamed into 7-Zip (never written to disk
/// unencrypted), written to a temporary file, then read back, decrypted and
/// compared record-for-record against what was written before it's renamed
/// into place; only then are older backups rotated out (newest
/// <see cref="Keep"/> daily/manual backups kept, plus the latest
/// <see cref="KeepBeforeRestore"/> automatic before-restore ones). Every
/// operation for a user (backup, restore, download) runs one at a time.
/// </summary>
public partial class UserBackupService(IConfiguration config, IWebHostEnvironment env, IEncryptionService encryption,
    MoneyTracker.Data.AppDbContext db, BackupSnapshotBuilder builder, ILogger<UserBackupService> logger)
{
    public const int Keep = 7, KeepBeforeRestore = 2;
    private const string EntryName = "money-tracker-backup.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    /// <summary>Runs <paramref name="work"/> with no other backup operation for this user running.</summary>
    public static async Task<T> ExclusiveAsync<T>(string userId, Func<Task<T>> work)
    {
        var gate = Locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await work(); } finally { gate.Release(); }
    }

    // ── Files ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^mt-(\d{8})-(\d{6,9})(?:-\d{1,3})?-(daily|manual|before-restore)\.7z$")]
    private static partial Regex FileNameRx();

    private string Root()
    {
        var configured = config["Backups:UserBackupPath"] ?? "data/user-backups";
        return Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    private string UserDir(string userId)
    {
        // User ids are GUIDs; anything else is refused rather than sanitized.
        if (!Guid.TryParse(userId, out _)) throw new InvalidOperationException("Invalid user id.");
        var dir = Path.Combine(Root(), userId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The full path of one of the user's backups, or null for a name that isn't one (path tricks included).</summary>
    public string? PathFor(string userId, string fileName) =>
        FileNameRx().IsMatch(fileName) && File.Exists(Path.Combine(UserDir(userId), fileName)) ? Path.Combine(UserDir(userId), fileName) : null;

    public record Info(string FileName, DateTime CreatedAt, string Kind, List<string> Sections, Dictionary<string, int> Counts, long SizeBytes);

    /// <summary>The user's backups, newest first (from each backup's small, non-sensitive .meta.json).</summary>
    public List<Info> List(string userId)
    {
        var list = new List<Info>();
        foreach (var path in Directory.GetFiles(UserDir(userId), "mt-*.7z"))
        {
            var name = Path.GetFileName(path);
            if (!FileNameRx().IsMatch(name)) continue;
            Meta? meta = null;
            try { meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(path + ".meta.json"), Json); } catch (Exception) { /* listed without details */ }
            list.Add(new Info(name, meta?.CreatedAt ?? File.GetLastWriteTimeUtc(path), meta?.Kind ?? FileNameRx().Match(name).Groups[3].Value,
                meta?.Sections ?? [], meta?.Counts ?? [], new FileInfo(path).Length));
        }
        return list.OrderByDescending(i => i.CreatedAt).ToList();
    }

    private record Meta(DateTime CreatedAt, string Kind, List<string> Sections, Dictionary<string, int> Counts, string Sha256);

    // ── Keys ───────────────────────────────────────────────────────────────

    private async Task<string> KeyAsync(ApplicationUser user)
    {
        var key = encryption.Decrypt(user.BackupKeyEncrypted, user.EncryptedDataKey);
        if (!string.IsNullOrEmpty(key)) return key;
        // Saved only if there's still none, then read back: two first backups at once
        // must end up using the same key, or one file could never be opened.
        var fresh = encryption.Encrypt(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            user.EncryptedDataKey)!;
        await db.Users.Where(u => u.Id == user.Id && u.BackupKeyEncrypted == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.BackupKeyEncrypted, fresh));
        var stored = await db.Users.Where(u => u.Id == user.Id).Select(u => u.BackupKeyEncrypted).FirstOrDefaultAsync();
        key = encryption.Decrypt(stored, user.EncryptedDataKey);
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Couldn't set up the backup key.");
        user.BackupKeyEncrypted = stored;
        return key;
    }

    // ── Create ─────────────────────────────────────────────────────────────

    public IEnumerable<string> SectionsFor(ApplicationUser user)
    {
        var excluded = (user.BackupExcludedSections ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return BackupSections.All.Where(s => !excluded.Contains(s));
    }

    /// <summary>Makes a backup now, verifies it, then rotates old ones. Returns its file name. Call inside ExclusiveAsync.</summary>
    public async Task<string> CreateAsync(ApplicationUser user, string kind, IEnumerable<string>? sections = null)
    {
        var snap = await builder.BuildAsync(user, sections ?? SectionsFor(user), kind);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snap, Json);
        var key = await KeyAsync(user);
        var dir = UserDir(user.Id);
        // Never overwrite: milliseconds in the name, and a counter if that's still taken.
        var name = $"mt-{snap.CreatedAt:yyyyMMdd-HHmmssfff}-{kind}.7z";
        for (var n = 2; File.Exists(Path.Combine(dir, name)); n++) name = $"mt-{snap.CreatedAt:yyyyMMdd-HHmmssfff}-{n}-{kind}.7z";
        var final = Path.Combine(dir, name);
        var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp.7z");
        try
        {
            await SevenZip.CompressAsync(temp, EntryName, bytes, key);
            // Read it back exactly as a restore would, and compare.
            var back = await SevenZip.ExtractAsync(temp, EntryName, key);
            var check = JsonSerializer.Deserialize<BackupSnapshot>(back, Json)
                ?? throw new InvalidOperationException("The backup couldn't be read back.");
            if (!back.AsSpan().SequenceEqual(bytes) || !check.Counts().OrderBy(k => k.Key).SequenceEqual(snap.Counts().OrderBy(k => k.Key)))
                throw new InvalidOperationException("The backup didn't read back the same as what was written.");
            File.Move(temp, final, overwrite: false);
            var meta = new Meta(snap.CreatedAt, kind, snap.Sections, snap.Counts(), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(final))));
            await File.WriteAllTextAsync(final + ".meta.json", JsonSerializer.Serialize(meta, Json));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Rotate(user.Id);
        logger.LogInformation("Backup {File} written for user {UserId}", name, user.Id);
        return name;
    }

    private void Rotate(string userId)
    {
        var all = List(userId);
        var drop = all.Where(i => i.Kind != "before-restore").Skip(Keep)
            .Concat(all.Where(i => i.Kind == "before-restore").Skip(KeepBeforeRestore));
        foreach (var old in drop)
        {
            var path = Path.Combine(UserDir(userId), old.FileName);
            File.Delete(path);
            File.Delete(path + ".meta.json");
        }
        // Leftovers from an interrupted run.
        foreach (var t in Directory.GetFiles(UserDir(userId), ".*.tmp.7z"))
            if (File.GetLastWriteTimeUtc(t) < DateTime.UtcNow.AddHours(-1)) File.Delete(t);
    }

    // ── Read / download ────────────────────────────────────────────────────

    /// <summary>Opens a backup: checks it's unchanged (SHA-256 in its .meta.json), decrypts and reads it.</summary>
    public async Task<BackupSnapshot> ReadAsync(ApplicationUser user, string fileName)
    {
        var path = PathFor(user.Id, fileName) ?? throw new FileNotFoundException("That backup doesn't exist.");
        if (File.Exists(path + ".meta.json"))
        {
            var meta = JsonSerializer.Deserialize<Meta>(await File.ReadAllTextAsync(path + ".meta.json"), Json);
            if (meta is not null && !string.IsNullOrEmpty(meta.Sha256)
                && !string.Equals(meta.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This backup file has been changed or damaged since it was made, so it won't be restored.");
        }
        var bytes = await SevenZip.ExtractAsync(path, EntryName, await KeyAsync(user));
        var snap = JsonSerializer.Deserialize<BackupSnapshot>(bytes, Json) ?? throw new InvalidOperationException("The backup is empty.");
        if (snap.UserId != user.Id) throw new InvalidOperationException("This backup belongs to a different user.");
        return snap;
    }

    /// <summary>A copy of a backup for download: re-encrypted with the user's own password, or with none.</summary>
    public async Task<byte[]> DownloadAsync(ApplicationUser user, string fileName, string? password)
    {
        var path = PathFor(user.Id, fileName) ?? throw new FileNotFoundException("That backup doesn't exist.");
        var bytes = await SevenZip.ExtractAsync(path, EntryName, await KeyAsync(user));
        var temp = Path.Combine(UserDir(user.Id), $".{Guid.NewGuid():N}.tmp.7z");
        try
        {
            var pw = string.IsNullOrEmpty(password) ? null : password;
            await SevenZip.CompressAsync(temp, EntryName, bytes, pw);
            // A readable manifest alongside the data, so a downloaded file explains itself.
            var snap = JsonSerializer.Deserialize<BackupSnapshot>(bytes, Json)!;
            await SevenZip.CompressAsync(temp, "MANIFEST.txt", System.Text.Encoding.UTF8.GetBytes(Manifest(snap)), pw);
            var check = await SevenZip.ExtractAsync(temp, EntryName, pw);
            if (!check.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("The download copy didn't verify.");
            return await File.ReadAllBytesAsync(temp);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public partial class UserBackupService
{
    /// <summary>A plain-text description of a backup: when, what kind, full or partial, and what each area holds.</summary>
    public static string Manifest(BackupSnapshot s)
    {
        var c = s.Counts();
        var full = BackupSections.All.All(s.Sections.Contains);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Money Tracker backup");
        sb.AppendLine($"Made:     {s.CreatedAt:yyyy-MM-dd HH:mm} UTC ({s.Kind})");
        sb.AppendLine($"Contents: {(full ? "Full backup (everything)" : "Partial backup")}");
        sb.AppendLine($"Format:   {EntryName}, version {s.Version}");
        sb.AppendLine();
        foreach (var sec in BackupSections.All)
        {
            var inc = s.Sections.Contains(sec);
            sb.Append(inc ? "  [x] " : "  [ ] ").Append(BackupSections.Label(sec));
            if (inc) sb.Append(" - ").Append(Detail(sec, c));
            else sb.Append(" - not included");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Restore it in Money Tracker: Settings > Backups & Restore.");
        return sb.ToString();
    }

    /// <summary>What an included area holds, e.g. "6 accounts, 226 transactions".</summary>
    public static string Detail(string section, IReadOnlyDictionary<string, int> c)
    {
        string N(string key, string one, string many) { var n = c.GetValueOrDefault(key); return $"{n:N0} {(n == 1 ? one : many)}"; }
        return section switch
        {
            BackupSections.Accounts => string.Join(", ", new[] { N("accounts", "account", "accounts"), N("transactions", "transaction", "transactions"),
                N("splits", "split", "splits"), N("loans", "loan", "loans"), N("valueHistory", "balance record", "balance records") }),
            BackupSections.Categories => N("categories", "category", "categories"),
            BackupSections.Payees => $"{N("payees", "payee", "payees")}, {N("rules", "mapping rule", "mapping rules")}",
            BackupSections.Bills => N("bills", "bill", "bills"),
            BackupSections.Ious => $"{N("iouPeople", "person", "people")}, {N("iouEntries", "IOU", "IOUs")}, {N("iouPayments", "payment", "payments")}",
            BackupSections.Reports => N("reports", "saved report", "saved reports"),
            BackupSections.Preferences => "settings, email digests, AI sharing, payment details",
            _ => "",
        };
    }
}

/// <summary>Runs the 7-Zip command line (7zz / 7z / 7za; Backups:SevenZipPath overrides).</summary>
public static class SevenZip
{
    private static string? found;

    public static string? Binary(IConfiguration? config = null)
    {
        if (config?["Backups:SevenZipPath"] is { Length: > 0 } configured) return configured;
        if (found is not null) return found;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        found = new[] { "7zz", "7z", "7za" }.SelectMany(b => dirs.Select(d => Path.Combine(d, b))).FirstOrDefault(File.Exists);
        return found;
    }

    public static bool Available => Binary() is not null;

    private static async Task<(int Code, byte[] Out, string Err)> RunAsync(IEnumerable<string> args, byte[]? stdin)
    {
        var bin = Binary() ?? throw new InvalidOperationException("7-Zip isn't installed on the server (7zz, 7z or 7za).");
        var psi = new ProcessStartInfo(bin) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start 7-Zip.");
        var outTask = Task.Run(async () => { using var ms = new MemoryStream(); await p.StandardOutput.BaseStream.CopyToAsync(ms); return ms.ToArray(); });
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin is not null) await p.StandardInput.BaseStream.WriteAsync(stdin);
        p.StandardInput.Close();
        var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw new InvalidOperationException("7-Zip took too long."); }
        return (p.ExitCode, await outTask, await errTask);
    }

    /// <summary>Writes <paramref name="data"/> as <paramref name="entry"/> into a new 7z at <paramref name="archive"/>, AES-256 with headers encrypted when a password is given.</summary>
    public static async Task CompressAsync(string archive, string entry, byte[] data, string? password)
    {
        var args = new List<string> { "a", "-t7z", "-mx=7", "-y", "-bso0", "-bsp0", $"-si{entry}" };
        if (password is not null) { args.Add($"-p{password}"); args.Add("-mhe=on"); }
        args.Add(archive);
        var (code, _, err) = await RunAsync(args, data);
        if (code != 0) throw new InvalidOperationException($"7-Zip couldn't write the backup ({code}): {err.Trim()}");
    }

    public static async Task<byte[]> ExtractAsync(string archive, string entry, string? password)
    {
        // -p with an empty value makes 7-Zip fail instead of prompting when a password is needed.
        var args = new List<string> { "x", "-so", "-y", "-bsp0", $"-p{password ?? ""}", archive, entry };
        var (code, output, err) = await RunAsync(args, null);
        if (code != 0) throw new InvalidOperationException(err.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            ? "The backup couldn't be decrypted (wrong key)." : $"7-Zip couldn't read the backup ({code}): {err.Trim()}");
        return output;
    }
}
