using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>
/// IOUs: money people owe the user and money the user owes them. Names,
/// descriptions and notes are encrypted with the user's DEK; amounts and
/// dates are plaintext, like transactions. Every change to a person's IOUs
/// or payments re-runs IouLedger for that person.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ious")]
public class IousController(
    AppDbContext db,
    IEncryptionService encryption,
    UserManager<ApplicationUser> userManager,
    IouLedger ledger,
    IAuditService audit) : ControllerBase
{
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // ── DTOs ────────────────────────────────────────────────────────────────

    public record PersonDto(string Name, string? Notes);

    /// <summary>One person's share. Amount: positive = they owe the user.</summary>
    public record EntryLineDto(int? PersonId, string? NewPersonName, decimal Amount);

    public record EntryBatchDto(
        DateOnly Date, string? Description, string? Notes, int? TransactionId, List<EntryLineDto> Lines);

    public record EntryDto(
        int PersonId, DateOnly Date, decimal Amount, string? Description, string? Notes,
        int? TransactionId, DateOnly? PaidOffDate, bool PaidOffManually);

    public record PaymentDto(
        int PersonId, DateOnly Date, decimal Amount, IouPaymentDirection Direction, IouPaymentMethod Method,
        string? Notes, int? TransactionId, int? AppliesToEntryId);

    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The IOUs page: everyone's balances, plus the register of IOUs and
    /// payments (optionally one person's; paid-off records hidden unless asked).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? personId, [FromQuery] bool showPaid = false)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();

        var people = await db.IouPeople.Where(p => p.UserId == userId).ToListAsync();
        var entries = await db.IouEntries.Where(e => e.UserId == userId).ToListAsync();
        var payments = await db.IouPayments.Where(p => p.UserId == userId).ToListAsync();
        var paymentIds = payments.Select(p => p.Id).ToList();
        var allocations = await db.IouAllocations.Where(a => paymentIds.Contains(a.PaymentId)).ToListAsync();

        var names = people.ToDictionary(p => p.Id, p => encryption.Decrypt(p.NameEncrypted, dek!) ?? "");
        var coveredBy = allocations.GroupBy(a => a.EntryId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var allocatedOf = allocations.GroupBy(a => a.PaymentId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var openEntryIds = entries.Where(e => e.PaidOffDate == null).Select(e => e.Id).ToHashSet();

        var peopleOut = people
            .Select(p =>
            {
                var pe = entries.Where(e => e.PersonId == p.Id).ToList();
                var pp = payments.Where(x => x.PersonId == p.Id).ToList();
                return new
                {
                    id = p.Id,
                    name = names[p.Id],
                    notes = encryption.Decrypt(p.NotesEncrypted, dek!),
                    balance = IouLedger.Balance(pe, pp),
                    openCount = pe.Count(e => e.PaidOffDate == null),
                    oldestOpen = pe.Where(e => e.PaidOffDate == null).Select(e => (DateOnly?)e.Date).Min(),
                };
            })
            .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Linked transactions, summarized for display (date, account, payee, amount).
        var txIds = entries.Select(e => e.TransactionId).Concat(payments.Select(p => p.TransactionId))
            .Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var txs = await TransactionSummariesAsync(txIds, userId!, dek!);

        var shownEntries = entries
            .Where(e => personId == null || e.PersonId == personId)
            .Where(e => showPaid || e.PaidOffDate == null)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Select(e => new
            {
                id = e.Id,
                personId = e.PersonId,
                personName = names.GetValueOrDefault(e.PersonId, ""),
                date = e.Date,
                amount = e.Amount,
                description = encryption.Decrypt(e.DescriptionEncrypted, dek!),
                notes = encryption.Decrypt(e.NotesEncrypted, dek!),
                transaction = e.TransactionId is int t ? txs.GetValueOrDefault(t) : null,
                covered = coveredBy.GetValueOrDefault(e.Id),
                remaining = e.PaidOffManually ? 0 : Math.Abs(e.Amount) - coveredBy.GetValueOrDefault(e.Id),
                paidOffDate = e.PaidOffDate,
                paidOffManually = e.PaidOffManually,
            })
            .ToList();

        // A payment stays in view while any of it went to an open IOU or it's
        // left as credit; once everything it paid is settled it's history.
        var shownPayments = payments
            .Where(p => personId == null || p.PersonId == personId)
            .Where(p => showPaid
                || allocatedOf.GetValueOrDefault(p.Id) < p.Amount
                || allocations.Any(a => a.PaymentId == p.Id && openEntryIds.Contains(a.EntryId)))
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .Select(p => new
            {
                id = p.Id,
                personId = p.PersonId,
                personName = names.GetValueOrDefault(p.PersonId, ""),
                date = p.Date,
                amount = p.Amount,
                direction = p.Direction.ToString(),
                method = p.Method.ToString(),
                notes = encryption.Decrypt(p.NotesEncrypted, dek!),
                transaction = p.TransactionId is int t ? txs.GetValueOrDefault(t) : null,
                appliesToEntryId = p.AppliesToEntryId,
                allocated = allocatedOf.GetValueOrDefault(p.Id),
            })
            .ToList();

        var balances = peopleOut.Where(p => p.balance != 0).ToList();
        return Ok(new
        {
            people = peopleOut,
            summary = balances,
            totals = new
            {
                owedToMe = balances.Where(p => p.balance > 0).Sum(p => p.balance),
                iOwe = balances.Where(p => p.balance < 0).Sum(p => p.balance),
                peopleOwingMe = balances.Count(p => p.balance > 0),
                peopleIOwe = balances.Count(p => p.balance < 0),
            },
            entries = shownEntries,
            payments = shownPayments,
        });
    }

    /// <summary>
    /// IOUs tagged on one purchase, with how much of them has been paid back —
    /// for the register's tag dialog and "received so far" on a purchase.
    /// </summary>
    [HttpGet("transactions/{transactionId:int}")]
    public async Task<IActionResult> ForTransaction(int transactionId)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        if (!await OwnsTransactionAsync(transactionId, userId)) return NotFound();

        var entries = await db.IouEntries
            .Where(e => e.UserId == userId && e.TransactionId == transactionId)
            .Include(e => e.Person)
            .OrderBy(e => e.Id)
            .ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var covered = (await db.IouAllocations.Where(a => ids.Contains(a.EntryId)).ToListAsync())
            .GroupBy(a => a.EntryId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        return Ok(new
        {
            entries = entries.Select(e => new
            {
                id = e.Id,
                personId = e.PersonId,
                personName = encryption.Decrypt(e.Person.NameEncrypted, dek!),
                date = e.Date,
                amount = e.Amount,
                description = encryption.Decrypt(e.DescriptionEncrypted, dek!),
                notes = encryption.Decrypt(e.NotesEncrypted, dek!),
                covered = covered.GetValueOrDefault(e.Id),
                paidOffDate = e.PaidOffDate,
                paidOffManually = e.PaidOffManually,
            }),
            owedToMe = entries.Where(e => e.Amount > 0).Sum(e => e.Amount),
            receivedSoFar = entries.Where(e => e.Amount > 0).Sum(e => covered.GetValueOrDefault(e.Id)),
            iOwe = -entries.Where(e => e.Amount < 0).Sum(e => e.Amount),
            paidSoFar = entries.Where(e => e.Amount < 0).Sum(e => covered.GetValueOrDefault(e.Id)),
        });
    }

    // ── People ──────────────────────────────────────────────────────────────

    [HttpPost("people")]
    public async Task<IActionResult> CreatePerson(PersonDto dto)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { message = "Enter a name." });

        if (await FindPersonAsync(userId, dek!, dto.Name) is not null)
            return Conflict(new { message = $"A person named \"{dto.Name.Trim()}\" already exists." });

        var person = NewPerson(userId, dek!, dto.Name, dto.Notes);
        db.IouPeople.Add(person);
        await db.SaveChangesAsync();
        await audit.LogAsync("CREATE", "IouPerson", person.Id);
        return Ok(new { id = person.Id, name = dto.Name.Trim() });
    }

    [HttpPut("people/{id:int}")]
    public async Task<IActionResult> UpdatePerson(int id, PersonDto dto)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest(new { message = "Enter a name." });

        var person = await db.IouPeople.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (person is null) return NotFound();
        var other = await FindPersonAsync(userId, dek!, dto.Name);
        if (other is not null && other.Id != id)
            return Conflict(new { message = $"A person named \"{dto.Name.Trim()}\" already exists." });

        person.NameEncrypted = encryption.Encrypt(dto.Name.Trim(), dek!)!;
        person.NotesEncrypted = string.IsNullOrWhiteSpace(dto.Notes) ? null : encryption.Encrypt(dto.Notes.Trim(), dek!);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Deletes a person with all their IOUs and payments.</summary>
    [HttpDelete("people/{id:int}")]
    public async Task<IActionResult> DeletePerson(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var person = await db.IouPeople.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (person is null) return NotFound();
        db.IouPeople.Remove(person);
        await db.SaveChangesAsync();
        await audit.LogAsync("DELETE", "IouPerson", id);
        return NoContent();
    }

    // ── IOUs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates one IOU per line — one for a single person, several when a
    /// purchase is split. New people named on a line are created on the fly.
    /// </summary>
    [HttpPost("entries")]
    public async Task<IActionResult> CreateEntries(EntryBatchDto dto)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        if (dto.Lines is not { Count: > 0 }) return BadRequest(new { message = "Add at least one person." });
        if (dto.Lines.Any(l => l.Amount == 0)) return BadRequest(new { message = "Each amount must be non-zero." });
        if (dto.TransactionId is int t && !await OwnsTransactionAsync(t, userId))
            return BadRequest(new { message = "Transaction not found." });

        var created = new List<IouEntry>();
        foreach (var line in dto.Lines)
        {
            var person = await ResolvePersonAsync(userId, dek!, line.PersonId, line.NewPersonName);
            if (person is null) return BadRequest(new { message = "Choose a person for every line." });
            var entry = new IouEntry
            {
                UserId = userId,
                Person = person,
                Date = dto.Date,
                Amount = line.Amount,
                DescriptionEncrypted = EncryptOrNull(dto.Description, dek!),
                NotesEncrypted = EncryptOrNull(dto.Notes, dek!),
                TransactionId = dto.TransactionId,
            };
            db.IouEntries.Add(entry);
            created.Add(entry);
        }
        await db.SaveChangesAsync();

        foreach (var personId in created.Select(e => e.PersonId).Distinct())
            await ledger.RebuildAsync(userId, personId);
        await audit.LogAsync("CREATE", "IouEntry", null, new { count = created.Count, transactionId = dto.TransactionId });
        return Ok(new { ids = created.Select(e => e.Id) });
    }

    [HttpPut("entries/{id:int}")]
    public async Task<IActionResult> UpdateEntry(int id, EntryDto dto)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        var entry = await db.IouEntries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);
        if (entry is null) return NotFound();
        if (dto.Amount == 0) return BadRequest(new { message = "The amount must be non-zero." });
        if (!await db.IouPeople.AnyAsync(p => p.Id == dto.PersonId && p.UserId == userId))
            return BadRequest(new { message = "Person not found." });
        if (dto.TransactionId is int t && !await OwnsTransactionAsync(t, userId))
            return BadRequest(new { message = "Transaction not found." });

        var oldPerson = entry.PersonId;
        entry.PersonId = dto.PersonId;
        entry.Date = dto.Date;
        entry.Amount = dto.Amount;
        entry.DescriptionEncrypted = EncryptOrNull(dto.Description, dek!);
        entry.NotesEncrypted = EncryptOrNull(dto.Notes, dek!);
        entry.TransactionId = dto.TransactionId;
        // A paid off date given here is the user's own call ("settled", or
        // forgiven); clearing it hands the IOU back to payments.
        entry.PaidOffManually = dto.PaidOffManually && dto.PaidOffDate != null;
        entry.PaidOffDate = entry.PaidOffManually ? dto.PaidOffDate : entry.PaidOffDate;
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await ledger.RebuildAsync(userId, entry.PersonId);
        if (oldPerson != entry.PersonId) await ledger.RebuildAsync(userId, oldPerson);
        return NoContent();
    }

    [HttpDelete("entries/{id:int}")]
    public async Task<IActionResult> DeleteEntry(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var entry = await db.IouEntries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);
        if (entry is null) return NotFound();
        db.IouEntries.Remove(entry);
        await db.SaveChangesAsync();
        await ledger.RebuildAsync(userId, entry.PersonId);
        await audit.LogAsync("DELETE", "IouEntry", id);
        return NoContent();
    }

    // ── Payments ────────────────────────────────────────────────────────────

    [HttpPost("payments")]
    public async Task<IActionResult> CreatePayment(PaymentDto dto)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        if (await ValidatePaymentAsync(dto, userId) is string error) return BadRequest(new { message = error });

        var payment = new IouPayment { UserId = userId };
        ApplyPayment(payment, dto, dek!);
        db.IouPayments.Add(payment);
        await db.SaveChangesAsync();
        await ledger.RebuildAsync(userId, payment.PersonId);
        await audit.LogAsync("CREATE", "IouPayment", payment.Id);
        return Ok(new { id = payment.Id });
    }

    [HttpPut("payments/{id:int}")]
    public async Task<IActionResult> UpdatePayment(int id, PaymentDto dto)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();
        var payment = await db.IouPayments.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (payment is null) return NotFound();
        if (await ValidatePaymentAsync(dto, userId) is string error) return BadRequest(new { message = error });

        var oldPerson = payment.PersonId;
        ApplyPayment(payment, dto, dek!);
        await db.SaveChangesAsync();
        await ledger.RebuildAsync(userId, payment.PersonId);
        if (oldPerson != payment.PersonId) await ledger.RebuildAsync(userId, oldPerson);
        return NoContent();
    }

    [HttpDelete("payments/{id:int}")]
    public async Task<IActionResult> DeletePayment(int id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var payment = await db.IouPayments.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (payment is null) return NotFound();
        db.IouPayments.Remove(payment);
        await db.SaveChangesAsync();
        await ledger.RebuildAsync(userId, payment.PersonId);
        await audit.LogAsync("DELETE", "IouPayment", id);
        return NoContent();
    }

    // ── Export ──────────────────────────────────────────────────────────────

    /// <summary>CSV of IOUs and payments (one person's, or everyone's).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int? personId, [FromQuery] bool showPaid = true)
    {
        var (userId, dek) = await UserAsync();
        if (userId is null) return Unauthorized();

        var names = (await db.IouPeople.Where(p => p.UserId == userId).ToListAsync())
            .ToDictionary(p => p.Id, p => encryption.Decrypt(p.NameEncrypted, dek!) ?? "");
        var entries = await db.IouEntries
            .Where(e => e.UserId == userId && (personId == null || e.PersonId == personId) && (showPaid || e.PaidOffDate == null))
            .ToListAsync();
        var payments = await db.IouPayments
            .Where(p => p.UserId == userId && (personId == null || p.PersonId == personId))
            .ToListAsync();
        var txIds = entries.Select(e => e.TransactionId).Concat(payments.Select(p => p.TransactionId))
            .Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var txs = await TransactionSummariesAsync(txIds, userId, dek!);
        string TxText(int? id) => id is int t && txs.TryGetValue(t, out var x)
            ? $"{x.date:yyyy-MM-dd} {x.payee} {x.amount.ToString("0.00", CultureInfo.InvariantCulture)} ({x.accountName})" : "";

        var rows = entries.Select(e => (e.Date, Cols: new[]
            {
                "IOU", e.Date.ToString("yyyy-MM-dd"), names.GetValueOrDefault(e.PersonId, ""),
                e.Amount > 0 ? "Owes me" : "I owe",
                e.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                encryption.Decrypt(e.DescriptionEncrypted, dek!) ?? "", encryption.Decrypt(e.NotesEncrypted, dek!) ?? "",
                e.PaidOffDate?.ToString("yyyy-MM-dd") ?? "", "", TxText(e.TransactionId),
            }))
            .Concat(payments.Select(p => (p.Date, Cols: new[]
            {
                "Payment", p.Date.ToString("yyyy-MM-dd"), names.GetValueOrDefault(p.PersonId, ""),
                p.Direction == IouPaymentDirection.Received ? "Received" : "Paid",
                (p.Direction == IouPaymentDirection.Received ? -p.Amount : p.Amount).ToString("0.00", CultureInfo.InvariantCulture),
                "", encryption.Decrypt(p.NotesEncrypted, dek!) ?? "", "", p.Method.ToString(), TxText(p.TransactionId),
            })))
            .OrderBy(r => r.Date)
            .Select(r => r.Cols);

        static string Csv(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        var sb = new StringBuilder("Type,Date,Person,Direction,Amount,Description,Notes,Paid off,Method,Linked transaction\n");
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Csv)));
        var who = personId is int pid ? names.GetValueOrDefault(pid, "person") : "all";
        await audit.LogAsync("EXPORT", "Iou", personId);
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"ious-{who}.csv");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(string? UserId, string? Dek)> UserAsync()
    {
        var userId = GetUserId();
        if (userId is null) return (null, null);
        var user = await userManager.FindByIdAsync(userId);
        return user is null ? (null, null) : (userId, user.EncryptedDataKey);
    }

    private Task<bool> OwnsTransactionAsync(int transactionId, string userId) =>
        db.Transactions.AnyAsync(t => t.Id == transactionId && t.Account.UserId == userId);

    private string? EncryptOrNull(string? text, string dek) =>
        string.IsNullOrWhiteSpace(text) ? null : encryption.Encrypt(text.Trim(), dek);

    private IouPerson NewPerson(string userId, string dek, string name, string? notes) => new()
    {
        UserId = userId,
        NameEncrypted = encryption.Encrypt(name.Trim(), dek)!,
        NotesEncrypted = EncryptOrNull(notes, dek),
    };

    // Names are encrypted, so uniqueness is checked in memory (case-insensitive).
    private async Task<IouPerson?> FindPersonAsync(string userId, string dek, string name) =>
        (await db.IouPeople.Where(p => p.UserId == userId).ToListAsync())
            .FirstOrDefault(p => string.Equals(encryption.Decrypt(p.NameEncrypted, dek), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<IouPerson?> ResolvePersonAsync(string userId, string dek, int? personId, string? newName)
    {
        if (personId is int id)
            return await db.IouPeople.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (string.IsNullOrWhiteSpace(newName)) return null;
        var existing = await FindPersonAsync(userId, dek, newName);
        if (existing is not null) return existing;
        var person = NewPerson(userId, dek, newName, null);
        db.IouPeople.Add(person);
        await db.SaveChangesAsync();
        return person;
    }

    private async Task<string?> ValidatePaymentAsync(PaymentDto dto, string userId)
    {
        if (dto.Amount <= 0) return "Enter an amount greater than zero.";
        if (!await db.IouPeople.AnyAsync(p => p.Id == dto.PersonId && p.UserId == userId)) return "Person not found.";
        if (dto.TransactionId is int t && !await OwnsTransactionAsync(t, userId)) return "Transaction not found.";
        if (dto.AppliesToEntryId is int e &&
            !await db.IouEntries.AnyAsync(x => x.Id == e && x.UserId == userId && x.PersonId == dto.PersonId))
            return "That IOU doesn't belong to this person.";
        return null;
    }

    private void ApplyPayment(IouPayment payment, PaymentDto dto, string dek)
    {
        payment.PersonId = dto.PersonId;
        payment.Date = dto.Date;
        payment.Amount = dto.Amount;
        payment.Direction = dto.Direction;
        payment.Method = dto.Method;
        payment.NotesEncrypted = EncryptOrNull(dto.Notes, dek);
        payment.TransactionId = dto.TransactionId;
        payment.AppliesToEntryId = dto.AppliesToEntryId;
    }

    private record TxSummary(int id, int accountId, string accountName, DateOnly date, string? payee, decimal amount);

    private async Task<Dictionary<int, TxSummary>> TransactionSummariesAsync(List<int> ids, string userId, string dek)
    {
        if (ids.Count == 0) return [];
        return (await db.Transactions
                .Where(t => ids.Contains(t.Id) && t.Account.UserId == userId)
                .Select(t => new { t.Id, t.AccountId, AccountName = t.Account.Name, t.Date, PayeeEnc = t.Payee != null ? t.Payee.NameEncrypted : null, t.Amount })
                .ToListAsync())
            .ToDictionary(t => t.Id, t => new TxSummary(t.Id, t.AccountId, t.AccountName, t.Date,
                encryption.Decrypt(t.PayeeEnc, dek), t.Amount));
    }
}
