using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Models;

namespace MoneyTracker.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Institution> Institutions => Set<Institution>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ScheduledTransaction> ScheduledTransactions => Set<ScheduledTransaction>();
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<ExportToken> ExportTokens => Set<ExportToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserPasskeyCredential> PasskeyCredentials => Set<UserPasskeyCredential>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Per-user ownership ────────────────────────────────────────────────

        modelBuilder.Entity<Account>(e =>
        {
            e.Property(a => a.OpeningBalance).HasPrecision(18, 2);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Institution).WithMany(i => i.Accounts).HasForeignKey(a => a.InstitutionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Institution>(e =>
        {
            e.HasOne(i => i.User).WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Category>(e =>
        {
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Parent).WithMany(c => c.SubCategories).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payee>(e =>
        {
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.HasOne(t => t.Payee).WithMany(p => p.Transactions).HasForeignKey(t => t.PayeeId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.Category).WithMany(c => c.Transactions).HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ScheduledTransaction>(e =>
        {
            e.Property(s => s.Amount).HasPrecision(18, 2);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Account).WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Payee).WithMany().HasForeignKey(s => s.PayeeId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.Category).WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // TransferAccountId is a plain FK column with no navigation property — avoids EF ambiguity
            // from having two Account-typed navigations on the same entity
        });

        modelBuilder.Entity<SavedReport>(e =>
        {
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExportToken>(e =>
        {
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.Token).IsUnique();
        });

        // ── Auth ──────────────────────────────────────────────────────────────

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasOne(r => r.User).WithMany(u => u.RefreshTokens).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.Token).IsUnique();
        });

        modelBuilder.Entity<UserPasskeyCredential>(e =>
        {
            e.HasOne(c => c.User).WithMany(u => u.PasskeyCredentials).HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
