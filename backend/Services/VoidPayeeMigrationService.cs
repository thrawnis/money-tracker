using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Sibling to VoidCategoryMigrationService, for the same root cause showing
/// up in a different column. That one assumed a bank/credit-card export put
/// "VOID" or "VOID 4821" in the transaction's category column; in practice
/// most plain bank CSVs don't have a category column at all, but always have
/// a description/payee column — and that's where "VOID ..." for a
/// reversed/voided transaction actually lands. The importer had no way to
/// know that wasn't a real payee, so it created one (with a numeric suffix
/// where needed, since payee names must be unique per user — hence multiple
/// distinct "VOID 4821", "VOID 9012", ... entries). Converts any transaction
/// using one of those into a properly voided transaction (see
/// Transaction.IsVoided) and removes the payee if nothing else needs it.
///
/// Runs once per user, gated by ApplicationUser.VoidPayeesMigrated, from the
/// same AuthController chokepoints (login, passkey login, demo login, and
/// silent token refresh) as the category version — see that class for why
/// the DEK is available there without the user's password.
/// </summary>
public class VoidPayeeMigrationService(
    AppDbContext db,
    IEncryptionService encryption,
    ILogger<VoidPayeeMigrationService> logger)
{
    private static readonly Regex VoidNamePattern =
        new(@"^VOID\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task RunIfNeededAsync(ApplicationUser user)
    {
        if (user.VoidPayeesMigrated) return;

        try
        {
            var payees = await db.Payees
                .Where(p => p.UserId == user.Id)
                .ToListAsync();

            var voidPayeeIds = payees
                .Where(p => VoidNamePattern.IsMatch(encryption.Decrypt(p.NameEncrypted, user.EncryptedDataKey) ?? ""))
                .Select(p => p.Id)
                .ToHashSet();

            if (voidPayeeIds.Count > 0)
            {
                // Splits don't carry their own payee — only the parent
                // Transaction does — so unlike the category version there's
                // no separate split pass here.
                var txs = await db.Transactions
                    .Where(t => t.Account.UserId == user.Id
                        && t.PayeeId != null && voidPayeeIds.Contains(t.PayeeId.Value))
                    .ToListAsync();
                foreach (var tx in txs)
                {
                    tx.IsVoided  = true;
                    tx.PayeeId   = null;
                    tx.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync();

                // Remove payees left with nothing referencing them. Left in
                // place if a scheduled bill still uses one, or a
                // PayeeMappingRule specifically targets it — same
                // conservative rule TransactionsController.CleanUpOrphanedPayeeAsync
                // applies: never destroy configuration a user set up on
                // purpose, even one that points at a nonsense payee.
                foreach (var payeeId in voidPayeeIds)
                {
                    bool stillReferenced =
                        await db.Transactions.AnyAsync(t => t.PayeeId == payeeId) ||
                        await db.ScheduledTransactions.AnyAsync(s => s.PayeeId == payeeId) ||
                        await db.PayeeMappingRules.AnyAsync(r => r.TargetPayeeId == payeeId);
                    if (!stillReferenced)
                    {
                        var payee = await db.Payees.FindAsync(payeeId);
                        if (payee is not null) db.Payees.Remove(payee);
                    }
                }

                await db.SaveChangesAsync();
            }

            user.VoidPayeesMigrated = true;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never block login/refresh over this cleanup — VoidPayeesMigrated
            // is left false on failure, so it simply retries next time.
            logger.LogError(ex, "VOID-payee migration failed for user {UserId}; will retry on next login", user.Id);
        }
    }
}
