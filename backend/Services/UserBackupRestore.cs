using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>What to restore from a backup.</summary>
public record RestoreRequest(
    List<string> Sections,
    /// <summary>Accounts to restore (backup ids) and how: "replace" puts the current account back as it was; "copy" adds it as a new account.</summary>
    List<RestoreAccount> Accounts,
    /// <summary>Whole-account restore: delete current accounts that aren't in the backup.</summary>
    bool RemoveOtherAccounts);

public record RestoreAccount(int BackupAccountId, string Mode);

public record RestoreResult(List<string> Lines, List<string> Warnings);

/// <summary>
/// Applies a BackupSnapshot to the user's data — selectively — inside ONE
/// database transaction, so a restore either happens completely or not at all.
///
/// <list type="bullet">
/// <item>Categories and payees are matched by name (case-insensitive): missing
/// ones are recreated, nothing current is deleted (other data may use them).
/// With their sections selected, payee defaults and the mapping rules are restored too.</item>
/// <item>Accounts: <b>replace</b> — the account's details, transactions (with
/// splits and as-imported originals), loan details and value history become
/// exactly the backup's; transfers are re-paired with the other account's side
/// (relinked if it still exists, recreated from the backup if it was deleted),
/// and the other half of any transfer made since the backup is removed, so
/// transfers always stay in pairs. An account deleted since is recreated.
/// <b>copy</b> — a new account "Name (restored Mon d)" with the backup's
/// transactions; its transfers become ordinary transactions (the other side
/// already exists in the other account).</item>
/// <item>Bills, IOUs and saved reports are replaced by the backup's; links to
/// accounts / transactions are mapped to the restored (or still existing) ones.</item>
/// <item>IOU and bank-sync links to transactions that were replaced are moved
/// to the restored copies, so bank sync never re-imports them.</item>
/// </list>
/// Loan interest/escrow charges are recomputed afterwards (LoanService).
/// </summary>
public class UserBackupRestore(AppDbContext db, IEncryptionService encryption, LoanService loans)
{
    private ApplicationUser user = null!;
    private string dek = "";
    private BackupSnapshot snap = null!;
    private readonly List<string> lines = [], warnings = [];

    private string? E(string? plain) => string.IsNullOrWhiteSpace(plain) ? null : encryption.Encrypt(plain, dek);
    private string? D(string? cipher) => encryption.Decrypt(cipher, dek);

    // backup id -> current id
    private readonly Dictionary<int, int> catMap = [], payeeMap = [], acctMap = [], txMap = [], billMap = [], institutionMap = [];
    private List<Category> currentCats = [];
    private List<(Payee P, string Name)> currentPayees = [];

    public async Task<RestoreResult> RestoreAsync(ApplicationUser u, BackupSnapshot s, RestoreRequest req)
    {
        user = u; dek = u.EncryptedDataKey; snap = s;
        if (s.UserId != u.Id) throw new InvalidOperationException("This backup belongs to a different user.");
        if (s.Version != 1) throw new InvalidOperationException($"This backup's format (version {s.Version}) isn't supported.");
        var sections = req.Sections.Where(s.Sections.Contains).ToHashSet();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await LoadCurrentAsync();
            if (sections.Contains(BackupSections.Categories)) await RestoreCategoriesAsync();
            if (sections.Contains(BackupSections.Payees)) await RestorePayeesAsync();
            if (sections.Contains(BackupSections.Accounts)) await RestoreAccountsAsync(req);
            if (sections.Contains(BackupSections.Bills)) await RestoreBillsAsync();
            if (sections.Contains(BackupSections.Accounts)) await FixBillLinksAsync(sections.Contains(BackupSections.Bills));
            if (sections.Contains(BackupSections.Ious)) await RestoreIousAsync();
            if (sections.Contains(BackupSections.Reports)) await RestoreReportsAsync();
            if (sections.Contains(BackupSections.Preferences)) RestorePreferences();
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        db.ChangeTracker.Clear();
        await loans.SyncUserAsync(u.Id);
        return new RestoreResult(lines, warnings);
    }

    // ── Categories & payees (matched by name) ──────────────────────────────

    private async Task LoadCurrentAsync()
    {
        currentCats = await db.Categories.Where(c => c.UserId == user.Id).ToListAsync();
        currentPayees = (await db.Payees.Where(p => p.UserId == user.Id).ToListAsync()).Select(p => (p, D(p.NameEncrypted) ?? "")).ToList();
    }

    /// <summary>The current category for a backup category id: same name under the same parent, created if missing.</summary>
    private async Task<int?> CatAsync(int? backupId)
    {
        if (backupId is not int bid) return null;
        if (catMap.TryGetValue(bid, out var known)) return known;
        var bc = snap.Categories.FirstOrDefault(c => c.Id == bid);
        if (bc is null) return null;
        var parent = await CatAsync(bc.ParentId);
        var match = currentCats.FirstOrDefault(c => c.ParentId == parent
            && string.Equals((D(c.NameEncrypted) ?? "").Trim(), bc.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            match = new Category { UserId = user.Id, ParentId = parent, NameEncrypted = E(bc.Name.Trim()) ?? E("(unnamed)")! };
            db.Categories.Add(match);
            await db.SaveChangesAsync();
            currentCats.Add(match);
        }
        catMap[bid] = match.Id;
        return match.Id;
    }

    /// <summary>The current payee for a backup payee id: same name, created if missing.</summary>
    private async Task<int?> PayeeAsync(int? backupId)
    {
        if (backupId is not int bid) return null;
        if (payeeMap.TryGetValue(bid, out var known)) return known;
        var bp = snap.Payees.FirstOrDefault(p => p.Id == bid);
        if (bp is null || string.IsNullOrWhiteSpace(bp.Name)) return null;
        var match = currentPayees.FirstOrDefault(p => string.Equals(p.Name.Trim(), bp.Name.Trim(), StringComparison.OrdinalIgnoreCase)).P;
        if (match is null)
        {
            match = new Payee { UserId = user.Id, NameEncrypted = E(bp.Name.Trim())!, DefaultCategoryId = await CatAsync(bp.DefaultCategoryId),
                BlockAutoDefaultCategory = bp.BlockAutoDefaultCategory };
            db.Payees.Add(match);
            await db.SaveChangesAsync();
            currentPayees.Add((match, bp.Name.Trim()));
        }
        payeeMap[bid] = match.Id;
        return match.Id;
    }

    private async Task RestoreCategoriesAsync()
    {
        var before = currentCats.Count;
        foreach (var c in snap.Categories) await CatAsync(c.Id);
        lines.Add($"Categories: {snap.Categories.Count} checked, {currentCats.Count - before} recreated.");
    }

    private async Task RestorePayeesAsync()
    {
        var before = currentPayees.Count;
        foreach (var bp in snap.Payees)
        {
            var id = await PayeeAsync(bp.Id);
            var p = currentPayees.First(x => x.P.Id == id).P;
            p.DefaultCategoryId = bp.BlockAutoDefaultCategory ? null : await CatAsync(bp.DefaultCategoryId);
            p.BlockAutoDefaultCategory = bp.BlockAutoDefaultCategory;
        }
        await db.PayeeMappingRules.Where(r => r.UserId == user.Id).ExecuteDeleteAsync();
        foreach (var r in snap.Rules)
            if (await PayeeAsync(r.TargetPayeeId) is int target)
                db.PayeeMappingRules.Add(new PayeeMappingRule { UserId = user.Id, PatternEncrypted = E(r.Pattern)!, IsRegex = r.IsRegex, TargetPayeeId = target });
        await db.SaveChangesAsync();
        lines.Add($"Payees: {snap.Payees.Count} checked, {currentPayees.Count - before} recreated; {snap.Rules.Count} mapping rule{(snap.Rules.Count == 1 ? "" : "s")} restored.");
    }

    // ── Accounts & transactions ────────────────────────────────────────────

    private async Task<int?> InstitutionAsync(int? backupId)
    {
        if (backupId is not int bid) return null;
        if (institutionMap.TryGetValue(bid, out var known)) return known;
        var bi = snap.Institutions.FirstOrDefault(i => i.Id == bid);
        if (bi is null) return null;
        var match = await db.Institutions.FirstOrDefaultAsync(i => i.UserId == user.Id && i.Name.ToLower() == bi.Name.ToLower());
        if (match is null)
        {
            match = new Institution { UserId = user.Id, Name = bi.Name };
            db.Institutions.Add(match);
            await db.SaveChangesAsync();
        }
        institutionMap[bid] = match.Id;
        return match.Id;
    }

    private async Task<string> FreeNameAsync(string wanted, int? exceptId)
    {
        var taken = (await db.Accounts.Where(a => a.UserId == user.Id && a.Id != (exceptId ?? 0)).Select(a => a.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;
        for (var i = 2; ; i++) if (!taken.Contains($"{wanted} ({i})")) return $"{wanted} ({i})";
    }

    private async Task RestoreAccountsAsync(RestoreRequest req)
    {
        var requested = req.Accounts
            .Select(a => (Backup: snap.Accounts.FirstOrDefault(b => b.Id == a.BackupAccountId), Mode: a.Mode?.ToLowerInvariant() == "copy" ? "copy" : "replace"))
            .Where(a => a.Backup is not null).Select(a => (Backup: a.Backup!, a.Mode)).DistinctBy(a => a.Backup.Id).ToList();
        var current = await db.Accounts.Where(a => a.UserId == user.Id).ToListAsync();

        // Which current accounts are replaced (same id — or, if that's gone, same name) and which are new.
        var targets = new List<(BAccount B, Account A, string Mode)>();
        foreach (var (b, mode) in requested)
        {
            Account? a = null;
            if (mode == "replace")
                a = current.FirstOrDefault(x => x.Id == b.Id) ?? current.FirstOrDefault(x => x.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)
                    && !targets.Any(t => t.A == x) && !snap.Accounts.Any(o => o.Id == x.Id && o.Id != b.Id));
            if (a is null)
            {
                var name = mode == "copy" ? $"{b.Name} (restored {snap.CreatedAt:MMM d})" : b.Name;
                a = new Account { UserId = user.Id, Name = await FreeNameAsync(name, null), Type = b.Type, CreatedAt = b.CreatedAt };
                db.Accounts.Add(a);
                await db.SaveChangesAsync();
                lines.Add(mode == "copy" ? $"\"{b.Name}\" restored as a new account \"{a.Name}\"." : $"\"{b.Name}\" recreated (it had been deleted).");
            }
            else
            {
                a.Name = await FreeNameAsync(b.Name, a.Id);
                a.Type = b.Type;
            }
            a.OpeningBalance = b.OpeningBalance;
            a.InstitutionId = await InstitutionAsync(b.InstitutionId);
            a.AccountNumberEncrypted = E(b.AccountNumber);
            a.NotesEncrypted = E(b.Notes);
            a.LowBalanceThreshold = b.LowBalanceThreshold;
            a.IsActive = b.IsActive;
            // Only replaced accounts stand in for the backup's account elsewhere (transfers, bills, IOUs);
            // a copy is an extra account, so links keep pointing at the original.
            if (mode == "replace") acctMap[b.Id] = a.Id;
            targets.Add((b, a, mode));
        }
        var replacedIds = targets.Where(t => t.Mode == "replace").Select(t => t.A.Id).ToHashSet();
        // Whole-account restore: accounts that aren't in the backup at all (by id or name) are removed.
        var removedIds = req.RemoveOtherAccounts
            ? current.Where(a => !targets.Any(t => t.A.Id == a.Id)
                    && !snap.Accounts.Any(b => b.Id == a.Id || b.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Id).ToHashSet()
            : [];
        await db.SaveChangesAsync();

        // Transactions being replaced or removed — and what pointed at them.
        var goneAccounts = replacedIds.Concat(removedIds).ToList();
        var goneTxIds = await db.Transactions.Where(t => goneAccounts.Contains(t.AccountId)).Select(t => t.Id).ToListAsync();
        var goneSet = goneTxIds.ToHashSet();
        // The other side of transfers into these accounts: unlinked now, re-paired below, or removed if their pair is gone.
        var partners = await db.Transactions.Where(t => t.Account.UserId == user.Id && !goneAccounts.Contains(t.AccountId)
            && t.TransferTransactionId != null && goneTxIds.Contains(t.TransferTransactionId.Value)).ToListAsync();
        foreach (var p in partners) { p.TransferTransactionId = null; p.TransferAccountId = null; }
        var iouEntryLinks = await db.IouEntries.Where(e => e.UserId == user.Id && e.TransactionId != null && goneTxIds.Contains(e.TransactionId.Value))
            .Select(e => new { e.Id, Old = e.TransactionId!.Value }).ToListAsync();
        var iouPaymentLinks = await db.IouPayments.Where(p => p.UserId == user.Id && p.TransactionId != null && goneTxIds.Contains(p.TransactionId.Value))
            .Select(p => new { p.Id, Old = p.TransactionId!.Value }).ToListAsync();
        var syncLinks = await db.SimpleFinImportedTransactions.Where(i => i.UserId == user.Id && i.TransactionId != null && goneTxIds.Contains(i.TransactionId.Value))
            .Select(i => new { i.Id, Old = i.TransactionId!.Value }).ToListAsync();
        await db.SaveChangesAsync();

        // Out with the old (splits and loan charges go with them; IOU/bank-sync links are cleared and moved back below).
        await db.Transactions.Where(t => goneAccounts.Contains(t.AccountId)).ExecuteDeleteAsync();
        if (removedIds.Count > 0)
        {
            var names = current.Where(a => removedIds.Contains(a.Id)).Select(a => a.Name).ToList();
            await db.Accounts.Where(a => removedIds.Contains(a.Id)).ExecuteDeleteAsync();
            lines.Add($"Removed {names.Count} account{(names.Count == 1 ? "" : "s")} not in the backup: {string.Join(", ", names)}.");
        }
        var restoredAcctIds = targets.Select(t => t.A.Id).ToList();
        await db.AccountValueSnapshots.Where(v => restoredAcctIds.Contains(v.AccountId)).ExecuteDeleteAsync();
        await db.LoanDetails.Where(l => restoredAcctIds.Contains(l.AccountId)).ExecuteDeleteAsync();

        // In with the backup's.
        var created = new Dictionary<int, Transaction>();   // backup tx id -> new row
        foreach (var (b, a, mode) in targets)
        {
            var count = 0;
            foreach (var t in snap.Transactions.Where(t => t.AccountId == b.Id))
            {
                var row = await NewTxAsync(t, a.Id);
                if (mode == "copy") { row.TransferAccountId = null; row.ScheduledTransactionId = null; }   // the other side / bill already exist
                db.Transactions.Add(row);
                created[t.Id] = row;
                if (mode == "replace") txMap[t.Id] = 0;   // filled after save
                count++;
            }
            foreach (var v in b.ValueHistory) db.AccountValueSnapshots.Add(new AccountValueSnapshot { UserId = user.Id, AccountId = a.Id, Date = v.Date, Balance = v.Balance });
            if (b.Loan is BLoan l)
                db.LoanDetails.Add(new LoanDetails
                {
                    UserId = user.Id, AccountId = a.Id, AnnualRate = l.AnnualRate, OriginalAmount = l.OriginalAmount, TermMonths = l.TermMonths,
                    FirstPaymentDate = l.FirstPaymentDate, MonthlyPayment = l.MonthlyPayment, EscrowAmount = l.EscrowAmount,
                    AutoCharges = l.AutoCharges, AutoChargesFrom = l.AutoChargesFrom,
                    InterestCategoryId = await CatAsync(l.InterestCategoryId), EscrowCategoryId = await CatAsync(l.EscrowCategoryId),
                });
            if (mode == "replace") lines.Add($"\"{a.Name}\": {count} transaction{(count == 1 ? "" : "s")} restored.");
            else lines.Add($"\"{a.Name}\": {count} transaction{(count == 1 ? "" : "s")}.");
        }
        await db.SaveChangesAsync();
        foreach (var key in txMap.Keys.ToList()) txMap[key] = created[key].Id;

        // Re-pair transfers (replace mode only).
        var replaceBackupIds = targets.Where(t => t.Mode == "replace").Select(t => t.B.Id).ToHashSet();
        var relinkedPartners = new HashSet<int>();
        var unpaired = 0;
        foreach (var t in snap.Transactions.Where(t => replaceBackupIds.Contains(t.AccountId) && t.TransferTransactionId != null))
        {
            var mine = created[t.Id];
            if (mine.TransferTransactionId != null) continue;   // already paired from the other side
            var otherId = t.TransferTransactionId!.Value;
            if (created.TryGetValue(otherId, out var both) && txMap.ContainsKey(otherId))
            {
                Link(mine, both);
                continue;
            }
            var otherB = snap.Transactions.FirstOrDefault(x => x.Id == otherId);
            int? otherAcct = otherB is null ? null : await CurrentAccountAsync(otherB.AccountId);
            Transaction? other = null;
            if (otherB is not null && otherAcct is int oa)
            {
                // The other account wasn't restored: its side is still there (same id) unless it was deleted since.
                other = partners.FirstOrDefault(p => p.Id == otherId)
                    ?? await db.Transactions.FirstOrDefaultAsync(x => x.Id == otherId && x.AccountId == oa && x.TransferTransactionId == null);
                // Still there but paired with something else since: leave it alone.
                if (other is null && await db.Transactions.AnyAsync(x => x.Id == otherId && x.Account.UserId == user.Id))
                {
                    mine.TransferAccountId = null; unpaired++; continue;
                }
                if (other is null)
                {
                    other = await NewTxAsync(otherB, oa);
                    db.Transactions.Add(other);
                    await db.SaveChangesAsync();
                    created[otherId] = other;
                }
            }
            if (other is null) { mine.TransferAccountId = null; unpaired++; continue; }
            Link(mine, other);
            relinkedPartners.Add(other.Id);
        }
        // The other side of transfers made after the backup: their pair is gone, so they go too.
        var orphans = partners.Where(p => !relinkedPartners.Contains(p.Id) && p.TransferTransactionId == null).ToList();
        if (orphans.Count > 0)
        {
            db.Transactions.RemoveRange(orphans);
            warnings.Add($"{orphans.Count} transfer{(orphans.Count == 1 ? "" : "s")} made after the backup also removed from the other account{(orphans.Count == 1 ? "" : "s")}, so transfers stay in pairs.");
        }
        if (unpaired > 0) warnings.Add($"{unpaired} transfer{(unpaired == 1 ? "" : "s")} kept as ordinary transactions: the other side no longer exists or is now paired with a different transaction.");
        await db.SaveChangesAsync();

        // Move IOU and bank-sync links back onto the restored transactions (ids from the backup's time are the same).
        foreach (var l in iouEntryLinks) if (txMap.TryGetValue(l.Old, out var n)) await db.IouEntries.Where(e => e.Id == l.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.TransactionId, n));
        foreach (var l in iouPaymentLinks) if (txMap.TryGetValue(l.Old, out var n)) await db.IouPayments.Where(e => e.Id == l.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.TransactionId, n));
        foreach (var l in syncLinks) if (txMap.TryGetValue(l.Old, out var n)) await db.SimpleFinImportedTransactions.Where(e => e.Id == l.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.TransactionId, n));
    }

    private static void Link(Transaction a, Transaction b)
    {
        a.TransferTransactionId = b.Id; a.TransferAccountId = b.AccountId;
        b.TransferTransactionId = a.Id; b.TransferAccountId = a.AccountId;
    }

    /// <summary>The current account for a backup account id: the restored one, else the same id if it still exists.</summary>
    private async Task<int?> CurrentAccountAsync(int backupAccountId)
    {
        if (acctMap.TryGetValue(backupAccountId, out var id)) return id;
        return await db.Accounts.AnyAsync(a => a.Id == backupAccountId && a.UserId == user.Id) ? backupAccountId : null;
    }

    private async Task<Transaction> NewTxAsync(BTransaction t, int accountId)
    {
        var row = new Transaction
        {
            AccountId = accountId, Date = t.Date, PostDate = t.PostDate, CheckNumberEncrypted = E(t.CheckNumber),
            PayeeId = await PayeeAsync(t.PayeeId), CategoryId = t.Splits.Count > 0 ? null : await CatAsync(t.CategoryId),
            MemoEncrypted = E(t.Memo), Amount = t.Amount, Status = t.Status, IsVoided = t.IsVoided,
            TransferAccountId = t.TransferAccountId is int ta ? await CurrentAccountAsync(ta) : null,
            ScheduledTransactionId = t.ScheduledTransactionId,   // mapped in FixBillLinksAsync
            CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt,
        };
        if (t.Imported is BImported im)
        {
            row.ImportedPayeeEncrypted = E(im.Payee); row.ImportedMemoEncrypted = E(im.Memo); row.ImportedDate = im.Date;
            row.ImportedAmount = im.Amount; row.ImportExternalId = im.ExternalId; row.ImportSourceEncrypted = E(im.Source); row.ImportedAt = im.At;
        }
        foreach (var s in t.Splits)
            row.Splits.Add(new TransactionSplit { CategoryId = await CatAsync(s.CategoryId), Amount = s.Amount, MemoEncrypted = E(s.Memo) });
        return row;
    }

    // ── Bills ──────────────────────────────────────────────────────────────

    private async Task RestoreBillsAsync()
    {
        await db.ScheduledTransactions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
        var skipped = 0;
        var made = new List<(int BackupId, ScheduledTransaction Row)>();
        foreach (var b in snap.Bills)
        {
            if (await CurrentAccountAsync(b.AccountId) is not int acct) { skipped++; continue; }
            var row = new ScheduledTransaction
            {
                UserId = user.Id, Name = b.Name, AccountId = acct, PayeeId = await PayeeAsync(b.PayeeId), CategoryId = await CatAsync(b.CategoryId),
                TransferAccountId = b.TransferAccountId is int t ? await CurrentAccountAsync(t) : null, MemoEncrypted = E(b.Memo), Amount = b.Amount,
                FrequencyInterval = b.FrequencyInterval, FrequencyUnit = b.FrequencyUnit, NextDueDate = b.NextDueDate, DaysOfWeekMask = b.DaysOfWeekMask,
                ReminderDays = b.ReminderDays, AutoCreate = b.AutoCreate, AutoCreateDaysAhead = b.AutoCreateDaysAhead, IsActive = b.IsActive, CreatedAt = b.CreatedAt,
            };
            if (b.TransferAccountId is not null && row.TransferAccountId is null) { skipped++; continue; }
            db.ScheduledTransactions.Add(row);
            made.Add((b.Id, row));
        }
        await db.SaveChangesAsync();
        foreach (var (bid, row) in made) billMap[bid] = row.Id;
        lines.Add($"Bills & reminders: {made.Count} restored.");
        if (skipped > 0) warnings.Add($"{skipped} bill{(skipped == 1 ? "" : "s")} not restored: {(skipped == 1 ? "its" : "their")} account no longer exists.");
    }

    /// <summary>Restored transactions' "created from this bill" links: to the restored bill, else the same bill if it still exists.</summary>
    private async Task FixBillLinksAsync(bool billsRestored)
    {
        var ids = txMap.Values.ToList();
        var rows = await db.Transactions.Where(t => ids.Contains(t.Id) && t.ScheduledTransactionId != null).ToListAsync();
        var existing = (await db.ScheduledTransactions.Where(s => s.UserId == user.Id).Select(s => s.Id).ToListAsync()).ToHashSet();
        foreach (var t in rows)
            t.ScheduledTransactionId = billsRestored && billMap.TryGetValue(t.ScheduledTransactionId!.Value, out var b) ? b
                : !billsRestored && existing.Contains(t.ScheduledTransactionId!.Value) ? t.ScheduledTransactionId : null;
        await db.SaveChangesAsync();
    }

    // ── IOUs, reports, preferences ─────────────────────────────────────────

    /// <summary>A backup transaction id as it is now: the restored copy, else the same transaction if it still exists.</summary>
    private async Task<int?> TxAsync(int? backupTxId)
    {
        if (backupTxId is not int id) return null;
        if (txMap.TryGetValue(id, out var n)) return n;
        return await db.Transactions.AnyAsync(t => t.Id == id && t.Account.UserId == user.Id) ? id : null;
    }

    private async Task RestoreIousAsync()
    {
        await db.IouPeople.Where(p => p.UserId == user.Id).ExecuteDeleteAsync();   // entries, payments, allocations go with them
        var people = new Dictionary<int, IouPerson>();
        foreach (var p in snap.Ious.People)
        {
            var row = new IouPerson { UserId = user.Id, NameEncrypted = E(p.Name)!, NotesEncrypted = E(p.Notes), EmailEncrypted = E(p.Email), CreatedAt = p.CreatedAt };
            db.IouPeople.Add(row); people[p.Id] = row;
        }
        await db.SaveChangesAsync();
        var entries = new Dictionary<int, IouEntry>();
        foreach (var e in snap.Ious.Entries.Where(e => people.ContainsKey(e.PersonId)))
        {
            var row = new IouEntry
            {
                UserId = user.Id, PersonId = people[e.PersonId].Id, Date = e.Date, Amount = e.Amount, DescriptionEncrypted = E(e.Description),
                NotesEncrypted = E(e.Notes), TransactionId = await TxAsync(e.TransactionId), PaidOffDate = e.PaidOffDate,
                PaidOffManually = e.PaidOffManually, CreatedAt = e.CreatedAt, UpdatedAt = DateTime.UtcNow,
            };
            db.IouEntries.Add(row); entries[e.Id] = row;
        }
        await db.SaveChangesAsync();
        var payments = new Dictionary<int, IouPayment>();
        foreach (var p in snap.Ious.Payments.Where(p => people.ContainsKey(p.PersonId)))
        {
            var row = new IouPayment
            {
                UserId = user.Id, PersonId = people[p.PersonId].Id, Date = p.Date, Amount = p.Amount, Direction = p.Direction, Method = p.Method,
                NotesEncrypted = E(p.Notes), TransactionId = await TxAsync(p.TransactionId),
                AppliesToEntryId = p.AppliesToEntryId is int ae && entries.TryGetValue(ae, out var en) ? en.Id : null, CreatedAt = p.CreatedAt,
            };
            db.IouPayments.Add(row); payments[p.Id] = row;
        }
        await db.SaveChangesAsync();
        foreach (var a in snap.Ious.Allocations)
            if (payments.TryGetValue(a.PaymentId, out var p) && entries.TryGetValue(a.EntryId, out var e))
                db.IouAllocations.Add(new IouAllocation { PaymentId = p.Id, EntryId = e.Id, Amount = a.Amount });
        await db.SaveChangesAsync();
        lines.Add($"IOUs: {people.Count} {(people.Count == 1 ? "person" : "people")}, {entries.Count} IOU{(entries.Count == 1 ? "" : "s")}, {payments.Count} payment{(payments.Count == 1 ? "" : "s")} restored.");
    }

    private async Task RestoreReportsAsync()
    {
        await db.SavedReports.Where(r => r.UserId == user.Id).ExecuteDeleteAsync();
        foreach (var r in snap.Reports)
            db.SavedReports.Add(new SavedReport { UserId = user.Id, Name = r.Name, Type = r.Type, ParametersJson = r.ParametersJson, IsDefault = r.IsDefault, CreatedAt = r.CreatedAt, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        lines.Add($"Saved reports: {snap.Reports.Count} restored.");
    }

    private void RestorePreferences()
    {
        if (snap.Preferences is not BPreferences p) return;
        var u = db.Users.Local.FirstOrDefault(x => x.Id == user.Id) ?? user;
        if (db.Entry(u).State == EntityState.Detached) db.Users.Attach(u);
        if (!string.IsNullOrWhiteSpace(p.TimeZoneId)) u.TimeZoneId = p.TimeZoneId;
        u.DefaultRegisterSortBy = p.DefaultRegisterSortBy; u.DefaultRegisterSortDir = p.DefaultRegisterSortDir; u.DefaultFutureDays = p.DefaultFutureDays;
        u.DigestEnabled = p.DigestEnabled; u.DigestHour = p.DigestHour; u.DigestIncludeAmounts = p.DigestIncludeAmounts; u.DigestOnlyWhenNews = p.DigestOnlyWhenNews;
        u.LoginAlertsEnabled = p.LoginAlertsEnabled; u.SyncAlertsEnabled = p.SyncAlertsEnabled; u.LargeTxAlertsEnabled = p.LargeTxAlertsEnabled;
        u.LargeTxThreshold = p.LargeTxThreshold; u.WeeklyDigestEnabled = p.WeeklyDigestEnabled; u.WeeklyDigestDay = p.WeeklyDigestDay; u.WeeklyDigestHour = p.WeeklyDigestHour;
        u.AiEnabled = p.AiEnabled; u.AiShareBalances = p.AiShareBalances; u.AiShareTransactions = p.AiShareTransactions; u.AiShareMemos = p.AiShareMemos;
        u.AiShareBills = p.AiShareBills; u.AiShareIous = p.AiShareIous; u.AiAllowProposals = p.AiAllowProposals;
        u.PaymentMethodsEncrypted = E(p.PaymentDetailsJson);
        lines.Add("Settings & preferences restored.");
    }
}
