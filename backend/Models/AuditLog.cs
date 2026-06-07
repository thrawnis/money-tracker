namespace MoneyTracker.Models;

public class AuditLog
{
    public long Id { get; set; }
    public string? UserId { get; set; }          // null for system actions
    public string? UserEmail { get; set; }
    public string Action { get; set; } = null!;  // e.g. CREATE, UPDATE, DELETE, LOGIN, LOGOUT
    public string? EntityType { get; set; }      // Transaction, Account, Category, Payee, etc.
    public int? EntityId { get; set; }
    public string? Details { get; set; }         // JSON string
    public string? IpAddress { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsSystem { get; set; }
}
