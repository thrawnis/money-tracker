using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// The contents of a backup: a user's data as plain values (decrypted, so a
/// backup can be restored under any key and read after download), with the
/// ids it had when taken so links between records can be rebuilt. It's only
/// ever stored inside an encrypted 7z (UserBackupService).
/// </summary>
public static class BackupSections
{
    public const string Accounts = "accounts";       // accounts, their transactions, loan details, value history
    public const string Categories = "categories";
    public const string Payees = "payees";           // payees and payee mapping rules
    public const string Bills = "bills";             // bills & reminders
    public const string Ious = "ious";
    public const string Reports = "reports";         // saved reports
    public const string Preferences = "preferences"; // settings, email digests, AI sharing, payment details

    public static readonly string[] All = [Accounts, Categories, Payees, Bills, Ious, Reports, Preferences];

    public static string Label(string s) => s switch
    {
        Accounts => "Accounts & transactions", Categories => "Categories", Payees => "Payees & mapping rules",
        Bills => "Bills & reminders", Ious => "IOUs", Reports => "Saved reports", Preferences => "Settings & preferences",
        _ => s,
    };
}

public record BackupSnapshot
{
    public int Version { get; init; } = 1;
    public DateTime CreatedAt { get; init; }
    public string UserId { get; init; } = "";
    /// <summary>"daily", "manual" or "before-restore".</summary>
    public string Kind { get; init; } = "daily";
    public List<string> Sections { get; init; } = [];

    public List<BInstitution> Institutions { get; init; } = [];
    public List<BCategory> Categories { get; init; } = [];
    public List<BPayee> Payees { get; init; } = [];
    public List<BRule> Rules { get; init; } = [];
    public List<BAccount> Accounts { get; init; } = [];
    public List<BTransaction> Transactions { get; init; } = [];
    public List<BBill> Bills { get; init; } = [];
    public BIous Ious { get; init; } = new();
    public List<BReport> Reports { get; init; } = [];
    public BPreferences? Preferences { get; init; }

    /// <summary>Counts used to check a backup reads back exactly as written.</summary>
    public Dictionary<string, int> Counts() => new()
    {
        ["institutions"] = Institutions.Count, ["categories"] = Categories.Count, ["payees"] = Payees.Count,
        ["rules"] = Rules.Count, ["accounts"] = Accounts.Count, ["transactions"] = Transactions.Count,
        ["splits"] = Transactions.Sum(t => t.Splits.Count), ["bills"] = Bills.Count,
        ["iouPeople"] = Ious.People.Count, ["iouEntries"] = Ious.Entries.Count, ["iouPayments"] = Ious.Payments.Count,
        ["iouAllocations"] = Ious.Allocations.Count, ["reports"] = Reports.Count,
        ["valueHistory"] = Accounts.Sum(a => a.ValueHistory.Count), ["loans"] = Accounts.Count(a => a.Loan is not null),
    };
}

public record BInstitution(int Id, string Name);
public record BCategory(int Id, int? ParentId, string Name);
public record BPayee(int Id, string Name, int? DefaultCategoryId, bool BlockAutoDefaultCategory);
public record BRule(string Pattern, bool IsRegex, int TargetPayeeId);
public record BValue(DateOnly Date, decimal Balance);
public record BLoan(decimal AnnualRate, decimal? OriginalAmount, int? TermMonths, DateOnly? FirstPaymentDate, decimal? MonthlyPayment,
    decimal EscrowAmount, bool AutoCharges, DateOnly AutoChargesFrom, int? InterestCategoryId, int? EscrowCategoryId);
public record BAccount(int Id, string Name, AccountType Type, decimal OpeningBalance, int? InstitutionId, string? AccountNumber,
    string? Notes, decimal? LowBalanceThreshold, bool IsActive, DateTime CreatedAt, BLoan? Loan, List<BValue> ValueHistory);
public record BSplit(int? CategoryId, decimal Amount, string? Memo);
public record BImported(string? Payee, string? Memo, DateOnly? Date, decimal? Amount, string? ExternalId, string? Source, DateTime? At);
public record BTransaction(int Id, int AccountId, DateOnly Date, DateOnly? PostDate, string? CheckNumber, int? PayeeId, int? CategoryId,
    string? Memo, decimal Amount, TransactionStatus Status, bool IsVoided, int? TransferTransactionId, int? TransferAccountId,
    int? ScheduledTransactionId, List<BSplit> Splits, BImported? Imported, DateTime CreatedAt, DateTime UpdatedAt);
public record BBill(int Id, string Name, int AccountId, int? PayeeId, int? CategoryId, int? TransferAccountId, string? Memo, decimal Amount,
    int FrequencyInterval, FrequencyUnit FrequencyUnit, DateOnly NextDueDate, int? DaysOfWeekMask, int ReminderDays,
    bool AutoCreate, int AutoCreateDaysAhead, bool IsActive, DateTime CreatedAt);
public record BIouPerson(int Id, string Name, string? Notes, string? Email, DateTime CreatedAt);
public record BIouEntry(int Id, int PersonId, DateOnly Date, decimal Amount, string? Description, string? Notes, int? TransactionId,
    DateOnly? PaidOffDate, bool PaidOffManually, DateTime CreatedAt);
public record BIouPayment(int Id, int PersonId, DateOnly Date, decimal Amount, IouPaymentDirection Direction, IouPaymentMethod Method,
    string? Notes, int? TransactionId, int? AppliesToEntryId, DateTime CreatedAt);
public record BIouAllocation(int PaymentId, int EntryId, decimal Amount);
public record BIous
{
    public List<BIouPerson> People { get; init; } = [];
    public List<BIouEntry> Entries { get; init; } = [];
    public List<BIouPayment> Payments { get; init; } = [];
    public List<BIouAllocation> Allocations { get; init; } = [];
}
public record BReport(string Name, ReportType Type, string ParametersJson, bool IsDefault, DateTime CreatedAt);
public record BPreferences(
    string? TimeZoneId, string? DefaultRegisterSortBy, string? DefaultRegisterSortDir, int? DefaultFutureDays,
    bool DigestEnabled, int DigestHour, bool DigestIncludeAmounts, bool DigestOnlyWhenNews,
    bool LoginAlertsEnabled, bool SyncAlertsEnabled, bool LargeTxAlertsEnabled, decimal LargeTxThreshold,
    bool WeeklyDigestEnabled, int WeeklyDigestDay, int WeeklyDigestHour,
    bool AiEnabled, bool AiShareBalances, bool AiShareTransactions, bool AiShareMemos, bool AiShareBills, bool AiShareIous, bool AiAllowProposals,
    string? PaymentDetailsJson);

/// <summary>Reads a user's data into a BackupSnapshot.</summary>
public class BackupSnapshotBuilder(AppDbContext db, IEncryptionService encryption)
{
    public async Task<BackupSnapshot> BuildAsync(ApplicationUser user, IEnumerable<string> sections, string kind)
    {
        var dek = user.EncryptedDataKey;
        string? D(string? c) => encryption.Decrypt(c, dek);
        var want = sections.Where(BackupSections.All.Contains).Distinct().ToList();
        bool Has(string s) => want.Contains(s);
        var uid = user.Id;

        // Categories and payees are always included: everything else refers to them by name.
        var categories = await db.Categories.AsNoTracking().Where(c => c.UserId == uid).OrderBy(c => c.Id).ToListAsync();
        var payees = await db.Payees.AsNoTracking().Where(p => p.UserId == uid).OrderBy(p => p.Id).ToListAsync();
        var snap = new BackupSnapshot
        {
            CreatedAt = DateTime.UtcNow, UserId = uid, Kind = kind, Sections = want,
            Categories = categories.Select(c => new BCategory(c.Id, c.ParentId, D(c.NameEncrypted) ?? "")).ToList(),
            Payees = payees.Select(p => new BPayee(p.Id, D(p.NameEncrypted) ?? "", p.DefaultCategoryId, p.BlockAutoDefaultCategory)).ToList(),
            Rules = Has(BackupSections.Payees)
                ? (await db.PayeeMappingRules.AsNoTracking().Where(r => r.UserId == uid).OrderBy(r => r.Id).ToListAsync())
                    .Select(r => new BRule(D(r.PatternEncrypted) ?? "", r.IsRegex, r.TargetPayeeId)).ToList()
                : [],
        };

        if (Has(BackupSections.Accounts))
        {
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.UserId == uid).OrderBy(a => a.Id).ToListAsync();
            var ids = accounts.Select(a => a.Id).ToList();
            var loans = await db.LoanDetails.AsNoTracking().Where(l => l.UserId == uid).ToDictionaryAsync(l => l.AccountId);
            var values = (await db.AccountValueSnapshots.AsNoTracking().Where(v => v.UserId == uid).OrderBy(v => v.Date).ToListAsync())
                .GroupBy(v => v.AccountId).ToDictionary(g => g.Key, g => g.Select(v => new BValue(v.Date, v.Balance)).ToList());
            snap.Institutions.AddRange((await db.Institutions.AsNoTracking().Where(i => i.UserId == uid).OrderBy(i => i.Id).ToListAsync())
                .Select(i => new BInstitution(i.Id, i.Name)));
            snap.Accounts.AddRange(accounts.Select(a => new BAccount(a.Id, a.Name, a.Type, a.OpeningBalance, a.InstitutionId,
                D(a.AccountNumberEncrypted), D(a.NotesEncrypted), a.LowBalanceThreshold, a.IsActive, a.CreatedAt,
                loans.TryGetValue(a.Id, out var l)
                    ? new BLoan(l.AnnualRate, l.OriginalAmount, l.TermMonths, l.FirstPaymentDate, l.MonthlyPayment, l.EscrowAmount,
                        l.AutoCharges, l.AutoChargesFrom, l.InterestCategoryId, l.EscrowCategoryId)
                    : null,
                values.GetValueOrDefault(a.Id) ?? [])));
            // Loan interest/escrow charges are left out: they're worked out again from the payments (LoanService).
            var txs = await db.Transactions.AsNoTracking().Include(t => t.Splits)
                .Where(t => ids.Contains(t.AccountId) && t.LoanChargeForTransactionId == null).OrderBy(t => t.Id).ToListAsync();
            snap.Transactions.AddRange(txs.Select(t => new BTransaction(t.Id, t.AccountId, t.Date, t.PostDate, D(t.CheckNumberEncrypted),
                t.PayeeId, t.CategoryId, D(t.MemoEncrypted), t.Amount, t.Status, t.IsVoided, t.TransferTransactionId, t.TransferAccountId,
                t.ScheduledTransactionId,
                t.Splits.OrderBy(s => s.Id).Select(s => new BSplit(s.CategoryId, s.Amount, D(s.MemoEncrypted))).ToList(),
                t.ImportedAt is null ? null : new BImported(D(t.ImportedPayeeEncrypted), D(t.ImportedMemoEncrypted), t.ImportedDate,
                    t.ImportedAmount, t.ImportExternalId, D(t.ImportSourceEncrypted), t.ImportedAt),
                t.CreatedAt, t.UpdatedAt)));
        }

        if (Has(BackupSections.Bills))
            snap.Bills.AddRange((await db.ScheduledTransactions.AsNoTracking().Where(s => s.UserId == uid).OrderBy(s => s.Id).ToListAsync())
                .Select(s => new BBill(s.Id, s.Name, s.AccountId, s.PayeeId, s.CategoryId, s.TransferAccountId, D(s.MemoEncrypted), s.Amount,
                    s.FrequencyInterval, s.FrequencyUnit, s.NextDueDate, s.DaysOfWeekMask, s.ReminderDays, s.AutoCreate,
                    s.AutoCreateDaysAhead, s.IsActive, s.CreatedAt)));

        if (Has(BackupSections.Ious))
        {
            var people = await db.IouPeople.AsNoTracking().Where(p => p.UserId == uid).OrderBy(p => p.Id).ToListAsync();
            var entries = await db.IouEntries.AsNoTracking().Where(e => e.UserId == uid).OrderBy(e => e.Id).ToListAsync();
            var payments = await db.IouPayments.AsNoTracking().Where(p => p.UserId == uid).OrderBy(p => p.Id).ToListAsync();
            var payIds = payments.Select(p => p.Id).ToList();
            snap.Ious.People.AddRange(people.Select(p => new BIouPerson(p.Id, D(p.NameEncrypted) ?? "", D(p.NotesEncrypted), D(p.EmailEncrypted), p.CreatedAt)));
            snap.Ious.Entries.AddRange(entries.Select(e => new BIouEntry(e.Id, e.PersonId, e.Date, e.Amount, D(e.DescriptionEncrypted),
                D(e.NotesEncrypted), e.TransactionId, e.PaidOffDate, e.PaidOffManually, e.CreatedAt)));
            snap.Ious.Payments.AddRange(payments.Select(p => new BIouPayment(p.Id, p.PersonId, p.Date, p.Amount, p.Direction, p.Method,
                D(p.NotesEncrypted), p.TransactionId, p.AppliesToEntryId, p.CreatedAt)));
            snap.Ious.Allocations.AddRange((await db.IouAllocations.AsNoTracking().Where(a => payIds.Contains(a.PaymentId)).OrderBy(a => a.Id).ToListAsync())
                .Select(a => new BIouAllocation(a.PaymentId, a.EntryId, a.Amount)));
        }

        if (Has(BackupSections.Reports))
            snap.Reports.AddRange((await db.SavedReports.AsNoTracking().Where(r => r.UserId == uid).OrderBy(r => r.Id).ToListAsync())
                .Select(r => new BReport(r.Name, r.Type, r.ParametersJson, r.IsDefault, r.CreatedAt)));

        if (Has(BackupSections.Preferences))
            snap = snap with
            {
                Preferences = new BPreferences(user.TimeZoneId, user.DefaultRegisterSortBy, user.DefaultRegisterSortDir, user.DefaultFutureDays,
                    user.DigestEnabled, user.DigestHour, user.DigestIncludeAmounts, user.DigestOnlyWhenNews,
                    user.LoginAlertsEnabled, user.SyncAlertsEnabled, user.LargeTxAlertsEnabled, user.LargeTxThreshold,
                    user.WeeklyDigestEnabled, user.WeeklyDigestDay, user.WeeklyDigestHour,
                    user.AiEnabled, user.AiShareBalances, user.AiShareTransactions, user.AiShareMemos, user.AiShareBills, user.AiShareIous,
                    user.AiAllowProposals, D(user.PaymentMethodsEncrypted)),
            };
        return snap;
    }
}
