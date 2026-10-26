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

                await db.Transactions.Where(t => t.CategoryId != null && dupIds.Contains(t.CategoryId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, keep.Id));
                await db.TransactionSplits.Where(t => t.CategoryId != null && dupIds.Contains(t.CategoryId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, keep.Id));
                await db.ScheduledTransactions.Where(t => t.CategoryId != null && dupIds.Contains(t.CategoryId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, keep.Id));
                await db.Payees.Where(p => p.DefaultCategoryId != null && dupIds.Contains(p.DefaultCategoryId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.DefaultCategoryId, keep.Id));
                await db.Categories.Where(c => c.ParentId != null && dupIds.Contains(c.ParentId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, keep.Id));
                await db.Categories.Where(c => dupIds.Contains(c.Id)).ExecuteDeleteAsync();
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

            // Keep a default category if only a duplicate had one.
            // (Bulk updates throughout: the caller's tracked user entity must
            // not be disturbed, and nothing here is loaded for tracking.)
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
            total += dupIds.Count;
        }
        await tx.CommitAsync();
        return total;
    }
}
