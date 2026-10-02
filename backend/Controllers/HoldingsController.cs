using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

/// <summary>
/// Investment holdings recorded by balance-only bank sync, when the brokerage
/// reports them: the latest day's positions with gain/loss against cost basis,
/// and each position's value over the recorded days.
/// </summary>
[ApiController]
[Authorize]
[Route("api/accounts/{accountId:int}/holdings")]
public class HoldingsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int accountId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId && a.UserId == userId)) return NotFound();

        var dek = user.EncryptedDataKey;
        var rows = (await db.HoldingSnapshots
                .Where(h => h.AccountId == accountId && h.UserId == userId)
                .OrderBy(h => h.Date)
                .ToListAsync())
            .Select(h => new
            {
                h.Date,
                Key = encryption.Decrypt(h.HoldingKeyEncrypted, dek) ?? "",
                Symbol = encryption.Decrypt(h.SymbolEncrypted, dek),
                Description = encryption.Decrypt(h.DescriptionEncrypted, dek),
                h.Shares, h.MarketValue, h.CostBasis,
            })
            .ToList();

        if (rows.Count == 0) return Ok(new { asOf = (DateOnly?)null, holdings = Array.Empty<object>() });

        var asOf = rows.Max(r => r.Date);
        var dates = rows.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
        var dateIndex = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);

        // Every position ever held, current ones first (largest value first),
        // then ones sold since — they still have history worth seeing.
        var holdings = rows
            .GroupBy(r => r.Key)
            .Select(g =>
            {
                var latest = g.OrderBy(r => r.Date).Last();
                var current = latest.Date == asOf;
                var values = new decimal?[dates.Count];
                foreach (var r in g) values[dateIndex[r.Date]] = r.MarketValue;
                decimal? gain = current && latest.MarketValue is decimal mv && latest.CostBasis is decimal cb ? mv - cb : null;
                return new
                {
                    symbol = latest.Symbol,
                    description = latest.Description,
                    current,
                    shares = current ? latest.Shares : null,
                    marketValue = current ? latest.MarketValue : null,
                    costBasis = current ? latest.CostBasis : null,
                    gain,
                    gainPct = gain is decimal gn && latest.CostBasis is decimal c && c != 0 ? Math.Round(gn / Math.Abs(c) * 100, 2) : (decimal?)null,
                    values,
                };
            })
            .OrderByDescending(h => h.current)
            .ThenByDescending(h => h.marketValue ?? 0)
            .Select((h, i) => new { id = i + 1, h.symbol, h.description, h.current, h.shares, h.marketValue, h.costBasis, h.gain, h.gainPct, h.values })
            .ToList();

        var held = holdings.Where(h => h.current).ToList();
        var withBasis = held.Where(h => h.marketValue.HasValue && h.costBasis.HasValue).ToList();
        decimal? totalGain = withBasis.Count > 0 ? withBasis.Sum(h => h.marketValue!.Value - h.costBasis!.Value) : null;
        decimal? totalBasis = withBasis.Count > 0 ? withBasis.Sum(h => h.costBasis!.Value) : null;

        return Ok(new
        {
            asOf,
            dates,
            totals = new
            {
                marketValue = held.Sum(h => h.marketValue ?? 0),
                costBasis = totalBasis,
                gain = totalGain,
                gainPct = totalGain is decimal tg && totalBasis is decimal tb && tb != 0 ? Math.Round(tg / Math.Abs(tb) * 100, 2) : (decimal?)null,
                // Positions without a reported cost basis are left out of the gain.
                missingCostBasis = held.Count - withBasis.Count,
            },
            holdings,
        });
    }
}
