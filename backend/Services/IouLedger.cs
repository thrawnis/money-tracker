using Microsoft.EntityFrameworkCore;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Keeps a person's IOU payments applied to their IOUs. Called after any
/// change to that person's IOUs or payments; it rebuilds their allocations
/// from scratch (a person has tens of records, not thousands), so the result
/// never depends on the order edits happened in.
///
/// Rules, per payment in date order:
///   • money received from the person pays down IOUs where they owe the user
///     (positive amounts); money paid to them pays down IOUs the user owes
///     (negative amounts);
///   • the IOU the payment names (AppliesToEntryId) is covered first, then the
///     oldest open ones;
///   • IOUs marked paid off by hand are left alone;
///   • anything left over stays unallocated, as a credit on the balance.
/// An IOU whose amount is fully covered gets the date of the payment that
/// finished it as its paid off date; one no longer fully covered loses it
/// (unless it was set by hand).
/// </summary>
public class IouLedger(AppDbContext db)
{
    public async Task RebuildAsync(string userId, int personId)
    {
        var entries = await db.IouEntries
            .Where(e => e.UserId == userId && e.PersonId == personId)
            .OrderBy(e => e.Date).ThenBy(e => e.Id)
            .ToListAsync();
        var payments = await db.IouPayments
            .Where(p => p.UserId == userId && p.PersonId == personId)
            .OrderBy(p => p.Date).ThenBy(p => p.Id)
            .ToListAsync();

        var entryIds = entries.Select(e => e.Id).ToList();
        var paymentIds = payments.Select(p => p.Id).ToList();
        await db.IouAllocations
            .Where(a => paymentIds.Contains(a.PaymentId) || entryIds.Contains(a.EntryId))
            .ExecuteDeleteAsync();

        var covered = entries.ToDictionary(e => e.Id, _ => 0m);
        var finishedOn = new Dictionary<int, DateOnly>();

        foreach (var p in payments)
        {
            var left = p.Amount;
            var owedToUser = p.Direction == IouPaymentDirection.Received;
            var candidates = entries
                .Where(e => !e.PaidOffManually && (owedToUser ? e.Amount > 0 : e.Amount < 0))
                .OrderBy(e => e.Id == p.AppliesToEntryId ? 0 : 1)
                .ThenBy(e => e.Date).ThenBy(e => e.Id);

            foreach (var e in candidates)
            {
                if (left <= 0) break;
                var open = Math.Abs(e.Amount) - covered[e.Id];
                if (open <= 0) continue;
                var take = Math.Min(open, left);
                db.IouAllocations.Add(new IouAllocation { PaymentId = p.Id, EntryId = e.Id, Amount = take });
                covered[e.Id] += take;
                left -= take;
                if (covered[e.Id] >= Math.Abs(e.Amount)) finishedOn[e.Id] = p.Date;
            }
        }

        foreach (var e in entries.Where(e => !e.PaidOffManually))
        {
            var paidOff = finishedOn.TryGetValue(e.Id, out var d) ? d : (DateOnly?)null;
            if (e.PaidOffDate != paidOff)
            {
                e.PaidOffDate = paidOff;
                e.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A person's balance: what they owe the user (positive) or the user owes
    /// them (negative), counting IOUs not settled by hand and every payment —
    /// so an overpayment shows as credit.
    /// </summary>
    public static decimal Balance(IEnumerable<IouEntry> entries, IEnumerable<IouPayment> payments) =>
        entries.Where(e => !e.PaidOffManually).Sum(e => e.Amount)
        - payments.Sum(p => p.Direction == IouPaymentDirection.Received ? p.Amount : -p.Amount);
}
