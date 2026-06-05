using Microsoft.EntityFrameworkCore;
using MoneyTracker.Models;

namespace MoneyTracker.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ScheduledTransaction> ScheduledTransactions => Set<ScheduledTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.Property(a => a.OpeningBalance).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(18, 2);

            // A transaction may have a typed payee name OR a linked Payee — both optional
            e.HasOne(t => t.Payee)
             .WithMany(p => p.Transactions)
             .HasForeignKey(t => t.PayeeId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(t => t.Category)
             .WithMany(c => c.Transactions)
             .HasForeignKey(t => t.CategoryId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Category>(e =>
        {
            e.HasOne(c => c.Parent)
             .WithMany(c => c.SubCategories)
             .HasForeignKey(c => c.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ScheduledTransaction>(e =>
        {
            e.Property(s => s.Amount).HasPrecision(18, 2);
        });
    }
}
