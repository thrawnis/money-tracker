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
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserPasskeyCredential> PasskeyCredentials => Set<UserPasskeyCredential>();

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
