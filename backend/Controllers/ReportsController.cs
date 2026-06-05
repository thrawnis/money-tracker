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

    [HttpGet("monthly-income-expense")]
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
            .Where(t => t.Account.UserId == userId)
            .Where(t => t.Date >= dateFrom && t.Date <= dateTo)
            .Include(t => t.Category)
            .AsQueryable();

        if (accountIds is { Length: > 0 })
            query = query.Where(t => accountIds.Contains(t.AccountId));

        if (categoryIds is { Length: > 0 })
            query = query.Where(t => t.CategoryId != null &&
                (categoryIds.Contains(t.CategoryId.Value) ||
                 categoryIds.Contains(t.Category!.ParentId ?? -1)));

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

        // Group by month + category
        var rows = transactions
            .GroupBy(t => new
            {
                Month      = $"{t.Date.Year}-{t.Date.Month:D2}",
                CategoryId = t.CategoryId,
            })
            .Select(g => new
            {
                g.Key.Month,
                g.Key.CategoryId,
                CategoryName = g.Key.CategoryId.HasValue
                    ? categoryNames.GetValueOrDefault(g.Key.CategoryId.Value, "(uncategorized)")
                    : "(uncategorized)",
                Total = g.Sum(t => t.Amount),
            })
            .OrderBy(r => r.Month)
            .ThenBy(r => r.CategoryName)
            .ToList();

        return Ok(new { months, rows });
    }

    // ── Transactions by Category ───────────────────────────────────────────────

    [HttpGet("transactions-by-category")]
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
            .Where(t => t.Account.UserId == userId)
            .Where(t => t.Date >= dateFrom && t.Date <= dateTo)
            .Include(t => t.Payee)
            .Include(t => t.Category)
            .AsQueryable();

        if (categoryId.HasValue)
            query = query.Where(t =>
                t.CategoryId == categoryId.Value ||
                t.Category!.ParentId == categoryId.Value);

        if (accountIds is { Length: > 0 })
            query = query.Where(t => accountIds.Contains(t.AccountId));

        if (payeeIds is { Length: > 0 })
            query = query.Where(t => t.PayeeId != null && payeeIds.Contains(t.PayeeId.Value));

        var transactions = await query
            .OrderBy(t => t.Category == null ? "" : t.Category.NameEncrypted)
            .ThenByDescending(t => t.Date)
            .ToListAsync();

        var rows = transactions.Select(t => new
        {
            id          = t.Id,
            date        = t.Date,
            accountId   = t.AccountId,
            payee       = t.Payee is null ? null : new
            {
                id   = t.Payee.Id,
                name = encryption.Decrypt(t.Payee.NameEncrypted, dek),
            },
            category    = t.Category is null ? null : new
            {
                id       = t.Category.Id,
                name     = encryption.Decrypt(t.Category.NameEncrypted, dek),
                parentId = t.Category.ParentId,
            },
            memo        = encryption.Decrypt(t.MemoEncrypted, dek),
            amount      = t.Amount,
            status      = t.Status,
        }).ToList();

        var total = rows.Sum(r => r.amount);

        return Ok(new { total, count = rows.Count, rows });
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
