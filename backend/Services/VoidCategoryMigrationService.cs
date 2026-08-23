using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// One-time cleanup for accounts that imported a bank/credit-card export
/// where a voided/reversed transaction's category column literally
/// contained text like "VOID" or "VOID 4821" — the importer had no way to
/// know that wasn't a real category, so it created one. Converts any
/// transaction using one of those into a properly voided transaction (see
/// Transaction.IsVoided) and removes the category if nothing else needs it.
///
/// Runs once per user, gated by ApplicationUser.VoidCategoriesMigrated, from
/// AuthController.IssueTokensAsync — the one chokepoint every login,
/// passkey login, demo login, and silent token refresh all go through, so it
/// naturally fires "next time the app loads" without needing its own
/// scheduled job. The user's DEK is available there without their password:
/// EncryptedDataKey is encrypted with the server's master key, not the
/// user's password, so it's decryptable any time the user record is loaded.
/// </summary>
public class VoidCategoryMigrationService(
    AppDbContext db,
    IEncryptionService encryption,
    ILogger<VoidCategoryMigrationService> logger)
{
    private static readonly Regex VoidNamePattern =
        new(@"^VOID\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task RunIfNeededAsync(ApplicationUser user)
    {
        if (user.VoidCategoriesMigrated) return;

        try
        {
            var categories = await db.Categories
                .Where(c => c.UserId == user.Id)
                .ToListAsync();

            var voidCategoryIds = categories
                .Where(c => VoidNamePattern.IsMatch(encryption.Decrypt(c.NameEncrypted, user.EncryptedDataKey) ?? ""))
                .Select(c => c.Id)
                .ToHashSet();

            if (voidCategoryIds.Count > 0)
            {
                var txs = await db.Transactions
                    .Where(t => t.Account.UserId == user.Id
                        && t.CategoryId != null && voidCategoryIds.Contains(t.CategoryId.Value))
                    .ToListAsync();
                foreach (var tx in txs)
                {
                    tx.IsVoided   = true;
                    tx.CategoryId = null;
                    tx.UpdatedAt  = DateTime.UtcNow;
                }

                // A split carrying a "VOID ..." category voids the whole parent
                // transaction — there's no concept of a partially-voided row.
                var splits = await db.TransactionSplits
                    .Include(s => s.Transaction)
                    .Where(s => s.CategoryId != null && voidCategoryIds.Contains(s.CategoryId.Value)
                        && s.Transaction.Account.UserId == user.Id)
                    .ToListAsync();
                foreach (var split in splits)
                {
                    split.CategoryId          = null;
                    split.Transaction.IsVoided  = true;
                    split.Transaction.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync();

                // Remove categories left with nothing referencing them. Left in
                // place (not silently deleted) if a scheduled bill still uses
                // one, or it's a parent with subcategories of its own.
                foreach (var catId in voidCategoryIds)
                {
                    bool stillReferenced =
                        await db.Transactions.AnyAsync(t => t.CategoryId == catId) ||
                        await db.TransactionSplits.AnyAsync(s => s.CategoryId == catId) ||
                        await db.ScheduledTransactions.AnyAsync(s => s.CategoryId == catId) ||
                        await db.Categories.AnyAsync(c => c.ParentId == catId);
                    if (!stillReferenced)
                    {
                        var cat = await db.Categories.FindAsync(catId);
                        if (cat is not null) db.Categories.Remove(cat);
                    }
                }

                await db.SaveChangesAsync();
            }

            user.VoidCategoriesMigrated = true;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never block login/refresh over this cleanup — VoidCategoriesMigrated
            // is left false on failure, so it simply retries next time. Logged
            // rather than swallowed silently: a persistent failure otherwise
            // re-runs a full category scan and decrypt on every single login,
            // forever, with nothing to explain why.
            logger.LogError(ex, "VOID-category migration failed for user {UserId}; will retry on next login", user.Id);
        }
    }
}
