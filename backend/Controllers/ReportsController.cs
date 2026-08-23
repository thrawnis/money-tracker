using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public class ReportsController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // ── Monthly Income / Expense Comparison ───────────────────────────────────

    [HttpGet("monthly")]
    public async Task<IActionResult> MonthlyIncomeExpense(
        [FromQuery] int? fromYear,
        [FromQuery] int? fromMonth,
        [FromQuery] int? toYear,
        [FromQuery] int? toMonth,
        [FromQuery] int[]? accountIds,
        [FromQuery] int[]? categoryIds)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        // Default: last 6 months
        var today   = DateOnly.FromDateTime(DateTime.UtcNow);
        var dateTo  = new DateOnly(toYear   ?? today.Year, toMonth   ?? today.Month, 1).AddMonths(1).AddDays(-1);
        var dateFrom = new DateOnly(fromYear ?? today.AddMonths(-5).Year, fromMonth ?? today.AddMonths(-5).Month, 1);

        var query = db.Transactions
            .Where(t => t.Account.UserId == userId && !t.IsVoided)
            .Where(t => t.Date >= dateFrom && t.Date <= dateTo)
            .Include(t => t.Category)
            .Include(t => t.Splits).ThenInclude(s => s.Category)
            .AsQueryable();

        if (accountIds is { Length: > 0 })
            query = query.Where(t => accountIds.Contains(t.AccountId));

        var transactions = await query.ToListAsync();

        // All categories used across transactions (for decryption)
        var allCategories = await db.Categories
            .Where(c => c.UserId == userId)
            .ToListAsync();

        var categoryNames = allCategories.ToDictionary(
            c => c.Id,
            c => encryption.Decrypt(c.NameEncrypted, dek) ?? "(unnamed)");

        // Build month list
        var months = new List<string>();
        for (var d = dateFrom; d <= dateTo; d = d.AddMonths(1))
            months.Add($"{d.Year}-{d.Month:D2}");

        // Flatten each transaction into one line per split, or a single line
        // for non-split transactions — so a split's amount lands under its own
        // category instead of the whole transaction landing in one bucket.
        var lines = transactions.SelectMany(t =>
        {
            var month = $"{t.Date.Year}-{t.Date.Month:D2}";
            if (t.Splits.Count > 0)
                return t.Splits.Select(s => (Month: month, CategoryId: s.CategoryId, ParentId: s.Category?.ParentId, Amount: s.Amount));
            return [(Month: month, CategoryId: t.CategoryId, ParentId: t.Category?.ParentId, Amount: t.Amount)];
        });

        if (categoryIds is { Length: > 0 })
            lines = lines.Where(l => l.CategoryId != null &&
                (categoryIds.Contains(l.CategoryId.Value) || categoryIds.Contains(l.ParentId ?? -1)));

        var lineList = lines.ToList();

        var rows = lineList
            .GroupBy(l => l.CategoryId)
            .Select(g =>
            {
                var categoryName = g.Key.HasValue
                    ? categoryNames.GetValueOrDefault(g.Key.Value, "(uncategorized)")
                    : "(uncategorized)";
                var byMonth = g.GroupBy(l => l.Month).ToDictionary(mg => mg.Key, mg => mg.Sum(l => l.Amount));
                return new
                {
                    categoryId = g.Key,
                    categoryName,
                    months = byMonth,
                    total = g.Sum(l => l.Amount),
                };
            })
            .OrderBy(r => r.categoryName)
            .ToList();

        var totals = lineList
            .GroupBy(l => l.Month)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));

        return Ok(new { months, rows, totals });
    }

    // ── Transactions by Category ───────────────────────────────────────────────

    [HttpGet("category")]
    public async Task<IActionResult> TransactionsByCategory(
        [FromQuery] int?     categoryId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int[]?   accountIds,
        [FromQuery] int[]?   payeeIds)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var dek = user.EncryptedDataKey;

        var today   = DateOnly.FromDateTime(DateTime.UtcNow);
        var dateFrom = from ?? new DateOnly(today.Year, 1, 1);
        var dateTo   = to   ?? today;

        var query = db.Transactions
            .Where(t => t.Account.UserId == userId && !t.IsVoided)
            .Where(t => t.Date >= dateFrom && t.Date <= dateTo)
            .Include(t => t.Payee)
            .Include(t => t.Account)
            .Include(t => t.Category)
            .Include(t => t.Splits).ThenInclude(s => s.Category)
            .AsQueryable();

        if (accountIds is { Length: > 0 })
            query = query.Where(t => accountIds.Contains(t.AccountId));

        if (payeeIds is { Length: > 0 })
            query = query.Where(t => t.PayeeId != null && payeeIds.Contains(t.PayeeId.Value));

        var transactions = await query
            .OrderByDescending(t => t.Date)
            .ToListAsync();

        // One row per split when the transaction is split (showing just that
        // split's portion/category), or one row per non-split transaction.
        // Split rows use a negative id (split PK) so they never collide with a
        // transaction id.
        var allRows = transactions.SelectMany(t =>
        {
            var payee = t.Payee is null ? null : encryption.Decrypt(t.Payee.NameEncrypted, dek);
            var memo  = encryption.Decrypt(t.MemoEncrypted, dek);

            if (t.Splits.Count > 0)
                return t.Splits.Select(s => new
                {
                    id          = -s.Id,
                    date        = t.Date,
                    accountId   = t.AccountId,
                    accountName = t.Account.Name,
                    payee,
                    categoryId  = s.CategoryId,
                    categoryParentId = s.Category?.ParentId,
                    memo        = string.IsNullOrEmpty(s.MemoEncrypted) ? memo : encryption.Decrypt(s.MemoEncrypted, dek),
                    amount      = s.Amount,
                });

            return
            [
                new
                {
                    id          = t.Id,
                    date        = t.Date,
                    accountId   = t.AccountId,
                    accountName = t.Account.Name,
                    payee,
                    categoryId  = t.CategoryId,
                    categoryParentId = t.Category?.ParentId,
                    memo,
                    amount      = t.Amount,
                },
            ];
        }).ToList();

        var filtered = categoryId.HasValue
            ? allRows.Where(r => r.categoryId == categoryId.Value || r.categoryParentId == categoryId.Value).ToList()
            : allRows;

        var items = filtered
            .OrderByDescending(r => r.date)
            .ToList();

        var total = items.Sum(r => r.amount);

        return Ok(new { total, count = items.Count, items });
    }

    // ── Saved Reports ─────────────────────────────────────────────────────────

    [HttpGet("saved")]
    public async Task<IActionResult> GetSaved()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var reports = await db.SavedReports
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id, r.Name, r.Type, r.ParametersJson, r.IsDefault, r.CreatedAt, r.UpdatedAt,
            })
            .ToListAsync();

        return Ok(reports);
    }

    [HttpPost("saved")]
    public async Task<IActionResult> SaveReport(SaveReportRequest request)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var report = new SavedReport
        {
            UserId         = userId,
            Name           = request.Name,
            Type           = request.Type,
            ParametersJson = JsonSerializer.Serialize(request.Parameters),
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        db.SavedReports.Add(report);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetSaved), new { }, new
        {
            report.Id, report.Name, report.Type, report.ParametersJson,
            report.IsDefault, report.CreatedAt, report.UpdatedAt,
        });
    }

    [HttpPut("saved/{id}")]
    public async Task<IActionResult> UpdateSavedReport(int id, SaveReportRequest request)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var report = await db.SavedReports.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (report is null) return NotFound();

        report.Name           = request.Name;
        report.Type           = request.Type;
        report.ParametersJson = JsonSerializer.Serialize(request.Parameters);
        report.UpdatedAt      = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return Ok(new { report.Id, report.Name, report.Type, report.ParametersJson, report.UpdatedAt });
    }

    [HttpDelete("saved/{id}")]
    public async Task<IActionResult> DeleteSavedReport(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var report = await db.SavedReports.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (report is null) return NotFound();

        db.SavedReports.Remove(report);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record SaveReportRequest(
    string Name,
    ReportType Type,
    Dictionary<string, object?> Parameters);
