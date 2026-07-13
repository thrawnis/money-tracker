using System.Text.Json;
using System.Text.RegularExpressions;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

public interface IAccountBackupService
{
    /// <summary>
    /// Writes a full backup of the account (and all its transactions/splits) to
    /// disk before it's deleted. All sensitive fields are kept encrypted exactly
    /// as stored in the database — the backup file is never plaintext. Prunes
    /// the user's oldest backups beyond the configured maximum.
    /// </summary>
    Task<string> CreateBackupAsync(Account account, List<Transaction> transactions, string? note);

    List<AccountBackupSummary> ListBackups(string userId);

    /// <summary>Returns the backup file's raw bytes, or null if it doesn't exist
    /// or doesn't belong to the given user (path traversal / cross-user guard).</summary>
    byte[]? ReadBackup(string userId, string fileName);
}

public record AccountBackupSummary(string FileName, int AccountId, string AccountName, DateTime BackedUpAt, string? Note, long SizeBytes);

public class AccountBackupService(IConfiguration config, IWebHostEnvironment env) : IAccountBackupService
{
    private const int DefaultMaxBackupsPerAccount = 3;

    private string RootDir()
    {
        var configured = config["Backups:AccountBackupPath"] ?? "data/account-backups";
        return Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    private string UserDir(string userId)
    {
        var dir = Path.Combine(RootDir(), SanitizeSegment(userId));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Backups live in a subfolder per account (data/account-backups/{userId}/{accountId}/)
    // so the retention cap applies per account rather than across the whole user.
    private string AccountDir(string userId, int accountId)
    {
        var dir = Path.Combine(UserDir(userId), accountId.ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private int MaxBackupsPerAccount => config.GetValue<int?>("Backups:MaxAccountBackupsPerAccount") ?? DefaultMaxBackupsPerAccount;

    public async Task<string> CreateBackupAsync(Account account, List<Transaction> transactions, string? note)
    {
        var now = DateTime.UtcNow;
        var backup = new AccountBackupDto(
            OriginalAccountId: account.Id,
            UserId: account.UserId,
            Name: account.Name,
            Type: account.Type.ToString(),
            OpeningBalance: account.OpeningBalance,
            InstitutionId: account.InstitutionId,
            InstitutionName: account.Institution?.Name,
            AccountNumberEncrypted: account.AccountNumberEncrypted,
            NotesEncrypted: account.NotesEncrypted,
            WasActive: account.IsActive,
            AccountCreatedAt: account.CreatedAt,
            BackedUpAt: now,
            Note: string.IsNullOrWhiteSpace(note)
                ? $"Automatic backup created before deleting account \"{account.Name}\" on {now:yyyy-MM-dd HH:mm} UTC."
                : note,
            Transactions: transactions.Select(t => new TransactionBackupDto(
                Id: t.Id,
                Date: t.Date,
                PostDate: t.PostDate,
                CheckNumberEncrypted: t.CheckNumberEncrypted,
                PayeeId: t.PayeeId,
                PayeeNameEncrypted: t.Payee?.NameEncrypted,
                CategoryId: t.CategoryId,
                CategoryNameEncrypted: t.Category?.NameEncrypted,
                MemoEncrypted: t.MemoEncrypted,
                Amount: t.Amount,
                Status: t.Status.ToString(),
                TransferTransactionId: t.TransferTransactionId,
                TransferAccountId: t.TransferAccountId,
                Splits: t.Splits.Select(s => new SplitBackupDto(
                    s.Id, s.CategoryId, s.Category?.NameEncrypted, s.Amount, s.MemoEncrypted)).ToList()
            )).ToList()
        );

        var fileName = $"{now:yyyyMMdd_HHmmss}_{account.Id}_{SanitizeSegment(account.Name)}.json";
        var path = Path.Combine(AccountDir(account.UserId, account.Id), fileName);

        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);

        PruneOldBackups(account.UserId, account.Id);

        return fileName;
    }

    private void PruneOldBackups(string userId, int accountId)
    {
        var dir = AccountDir(userId, accountId);
        var files = new DirectoryInfo(dir).GetFiles("*.json")
            .OrderByDescending(f => f.Name) // filenames are timestamp-prefixed — lexicographic == chronological
            .ToList();

        foreach (var stale in files.Skip(MaxBackupsPerAccount))
        {
            try { stale.Delete(); } catch { /* best-effort prune */ }
        }
    }

    public List<AccountBackupSummary> ListBackups(string userId)
    {
        var userDir = UserDir(userId);
        var summaries = new List<AccountBackupSummary>();

        foreach (var accountDir in new DirectoryInfo(userDir).GetDirectories())
        {
            foreach (var file in accountDir.GetFiles("*.json").OrderByDescending(f => f.Name))
            {
                try
                {
                    var json = File.ReadAllText(file.FullName);
                    var dto = JsonSerializer.Deserialize<AccountBackupDto>(json);
                    if (dto is null) continue;
                    summaries.Add(new AccountBackupSummary(file.Name, dto.OriginalAccountId, dto.Name, dto.BackedUpAt, dto.Note, file.Length));
                }
                catch { /* skip unreadable/corrupt files */ }
            }
        }

        return summaries.OrderByDescending(s => s.BackedUpAt).ToList();
    }

    public byte[]? ReadBackup(string userId, string fileName)
    {
        // fileName comes from the client — reject anything that isn't a bare
        // file name (no path separators/traversal), then search only inside
        // this user's own backup directory tree.
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            return null;

        var userDir = Path.GetFullPath(UserDir(userId));
        foreach (var accountDir in new DirectoryInfo(userDir).GetDirectories())
        {
            var path = Path.GetFullPath(Path.Combine(accountDir.FullName, fileName));
            if (!path.StartsWith(userDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }

        return null;
    }

    private static string SanitizeSegment(string s)
    {
        var cleaned = Regex.Replace(s, @"[^a-zA-Z0-9_-]+", "_").Trim('_');
        return string.IsNullOrEmpty(cleaned) ? "account" : cleaned[..Math.Min(cleaned.Length, 60)];
    }
}

public record AccountBackupDto(
    int OriginalAccountId,
    string UserId,
    string Name,
    string Type,
    decimal OpeningBalance,
    int? InstitutionId,
    string? InstitutionName,
    string? AccountNumberEncrypted,
    string? NotesEncrypted,
    bool WasActive,
    DateTime AccountCreatedAt,
    DateTime BackedUpAt,
    string Note,
    List<TransactionBackupDto> Transactions);

public record TransactionBackupDto(
    int Id,
    DateOnly Date,
    DateOnly? PostDate,
    string? CheckNumberEncrypted,
    int? PayeeId,
    string? PayeeNameEncrypted,
    int? CategoryId,
    string? CategoryNameEncrypted,
    string? MemoEncrypted,
    decimal Amount,
    string Status,
    int? TransferTransactionId,
    int? TransferAccountId,
    List<SplitBackupDto> Splits);

public record SplitBackupDto(int Id, int? CategoryId, string? CategoryNameEncrypted, decimal Amount, string? MemoEncrypted);
