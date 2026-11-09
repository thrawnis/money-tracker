using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Merges categories, subcategories and payees whose names differ only by case
/// or surrounding whitespace. Names are encrypted, so the database can't enforce
/// uniqueness and duplicates can slip in (older imports, concurrent requests).
/// The oldest record (lowest id) survives; everything pointing at a duplicate is
/// moved onto it and the duplicate is deleted. Categories are only duplicates
/// when they share a parent — "Food › Other" and "Auto › Other" are distinct.
///
/// Runs on every sign-in and silent token refresh, alongside the one-time VOID
/// migrations, since that's where the user's DEK is available. It only writes
/// when it actually finds something to merge.
/// </summary>
public class DuplicateNameMergeService(
    AppDbContext db,
    IEncryptionService encryption,
    ILogger<DuplicateNameMergeService> logger)
{
    private static string Key(string? name) => (name ?? "").Trim().ToLowerInvariant();

    public async Task RunAsync(ApplicationUser user)
    {
        try
        {
            var merged = await MergeCategoriesAsync(user) + await MergePayeesAsync(user);
            if (merged > 0)
                logger.LogInformation("Merged {Count} duplicate categories/payees for user {UserId}", merged, user.Id);
        }
        catch (Exception ex)
        {
            // Never block sign-in over cleanup; it'll try again next time.
            logger.LogError(ex, "Duplicate name merge failed for user {UserId}", user.Id);
        }
    }

    private async Task<int> MergeCategoriesAsync(ApplicationUser user)
    {
        var total = 0;
        // Merging two parents moves their children under one parent, which can
        // create new duplicates one level down — so repeat until nothing changes.
        while (true)
        {
            var cats = await db.Categories.AsNoTracking().Where(c => c.UserId == user.Id).ToListAsync();
            var groups = cats
                .GroupBy(c => (c.ParentId, Key(encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey))))
                .Where(g => g.Count() > 1)
                .ToList();
            if (groups.Count == 0) return total;

            await using var tx = await db.Database.BeginTransactionAsync();
            foreach (var g in groups)
            {
                var keep = g.MinBy(c => c.Id)!;
                var dupIds = g.Where(c => c.Id != keep.Id).Select(c => c.Id).ToList();

                await MoveCategoryReferencesAsync(keep.Id, dupIds);
                total += dupIds.Count;
            }
            await tx.CommitAsync();
        }
    }

    private async Task<int> MergePayeesAsync(ApplicationUser user)
    {
        var payees = await db.Payees.AsNoTracking().Where(p => p.UserId == user.Id).ToListAsync();
        var groups = payees
            .GroupBy(p => Key(encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey)))
            .Where(g => g.Count() > 1)
            .ToList();
        if (groups.Count == 0) return 0;

        var total = 0;
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var g in groups)
        {
            var keep = g.MinBy(p => p.Id)!;
            var dups = g.Where(p => p.Id != keep.Id).ToList();
            var dupIds = dups.Select(p => p.Id).ToList();

            await MovePayeeReferencesAsync(keep, dups);
            total += dupIds.Count;
        }
        await tx.CommitAsync();
        return total;
    }

    // Moves everything that points at the duplicate categories onto keepId,
    // then deletes them. Bulk updates throughout: nothing is loaded for
    // tracking, so a caller's tracked entities (the signed-in user) are untouched.
    private async Task MoveCategoryReferencesAsync(int keepId, List<int> dupIds)
    {
        await db.Transactions.Where(t => t.CategoryId != null && dupIds.Contains(t.CategoryId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, keepId));
        await db.TransactionSplits.Where(t => t.CategoryId != null && dupIds.Contains(t.CategoryId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, keepId));
        await db.ScheduledTransactions.Where(t => t.CategoryId != null && dupIds.Contains(t.CategoryId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, keepId));
        await db.Payees.Where(p => p.DefaultCategoryId != null && dupIds.Contains(p.DefaultCategoryId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DefaultCategoryId, keepId));
        await db.Categories.Where(c => c.ParentId != null && dupIds.Contains(c.ParentId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, keepId));
        await db.Categories.Where(c => dupIds.Contains(c.Id)).ExecuteDeleteAsync();
    }

    private async Task MovePayeeReferencesAsync(Payee keep, List<Payee> dups)
    {
        var dupIds = dups.Select(p => p.Id).ToList();
        // Keep a default category if only a duplicate had one.
        if (keep.DefaultCategoryId is null && dups.FirstOrDefault(d => d.DefaultCategoryId != null) is { } withDefault)
            await db.Payees.Where(p => p.Id == keep.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DefaultCategoryId, withDefault.DefaultCategoryId));

        await db.Transactions.Where(t => t.PayeeId != null && dupIds.Contains(t.PayeeId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PayeeId, keep.Id));
        await db.ScheduledTransactions.Where(t => t.PayeeId != null && dupIds.Contains(t.PayeeId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PayeeId, keep.Id));
        // Rules cascade-delete with their payee, so move them first.
        await db.PayeeMappingRules.Where(r => dupIds.Contains(r.TargetPayeeId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TargetPayeeId, keep.Id));
        await db.Payees.Where(p => dupIds.Contains(p.Id)).ExecuteDeleteAsync();
    }

    // ── Manual merges (Utilities → Find Duplicate Names) ──────────────────────

    /// <summary>
    /// Merges the user's payees <paramref name="mergeIds"/> into <paramref name="keepId"/>.
    /// Each merged name becomes an exact-match payee mapping rule for the kept
    /// payee, so future imports and bank syncs using the old name land on it.
    /// Returns how many payees were merged away.
    /// </summary>
    public async Task<int> MergePayeesIntoAsync(ApplicationUser user, int keepId, IEnumerable<int> mergeIds)
    {
        var ids = mergeIds.Where(id => id != keepId).Distinct().ToList();
        var all = await db.Payees.AsNoTracking()
            .Where(p => p.UserId == user.Id && (p.Id == keepId || ids.Contains(p.Id))).ToListAsync();
        var keep = all.FirstOrDefault(p => p.Id == keepId) ?? throw new KeyNotFoundException();
        var dups = all.Where(p => p.Id != keepId).ToList();
        if (dups.Count != ids.Count || dups.Count == 0) throw new KeyNotFoundException();

        var dek = user.EncryptedDataKey;
        var existingPatterns = (await db.PayeeMappingRules.AsNoTracking().Where(r => r.UserId == user.Id).ToListAsync())
            .Where(r => !r.IsRegex)
            .Select(r => (encryption.Decrypt(r.PatternEncrypted, dek) ?? "").ToLowerInvariant())
            .ToHashSet();

        await using var tx = await db.Database.BeginTransactionAsync();
        await MovePayeeReferencesAsync(keep, dups);
        foreach (var d in dups)
        {
            var name = (encryption.Decrypt(d.NameEncrypted, dek) ?? "").Trim();
            // '*' would act as a wildcard in a non-regex rule; skip such names.
            if (name.Length == 0 || name.Contains('*') || !existingPatterns.Add(name.ToLowerInvariant())) continue;
            db.PayeeMappingRules.Add(new PayeeMappingRule
            {
                UserId = user.Id,
                PatternEncrypted = encryption.Encrypt(name, dek)!,
                IsRegex = false,
                TargetPayeeId = keepId,
            });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return dups.Count;
    }

    /// <summary>
    /// Merges the user's categories <paramref name="mergeIds"/> into
    /// <paramref name="keepId"/>: transactions, splits, bills and payee defaults
    /// move over, and their subcategories move under the kept category (merging
    /// with any of the same name there). A category can't be merged into one of
    /// its own subcategories. Returns how many categories were merged away.
    /// </summary>
    public async Task<int> MergeCategoriesIntoAsync(ApplicationUser user, int keepId, IEnumerable<int> mergeIds)
    {
        var ids = mergeIds.Where(id => id != keepId).Distinct().ToList();
        var cats = await db.Categories.AsNoTracking().Where(c => c.UserId == user.Id).ToListAsync();
        var byId = cats.ToDictionary(c => c.Id);
        if (!byId.ContainsKey(keepId) || ids.Count == 0 || ids.Any(id => !byId.ContainsKey(id))) throw new KeyNotFoundException();
        // Walk up from the kept category: none of its ancestors may be merged away.
        for (var p = byId[keepId].ParentId; p is int pid; p = byId.GetValueOrDefault(pid)?.ParentId)
            if (ids.Contains(pid)) throw new InvalidOperationException("A category can't be merged into one of its own subcategories.");
        // Categories are two levels deep: a subcategory can't take on subcategories.
        if (byId[keepId].ParentId is not null && cats.Any(c => c.ParentId is int pid && ids.Contains(pid)))
            throw new InvalidOperationException("A category with subcategories can only be merged into a top-level category.");

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await MoveCategoryReferencesAsync(keepId, ids);
            await tx.CommitAsync();
        }
        // Subcategories that moved under the kept category may now duplicate
        // ones already there.
        await MergeCategoriesAsync(user);
        return ids.Count;
    }
}
