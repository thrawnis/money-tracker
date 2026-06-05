using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MoneyTracker.Data;
using Testcontainers.PostgreSql;

namespace MoneyTracker.Tests.Integration;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithDatabase("moneytracker_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace the real DbContext with a test database
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(opts =>
                opts.UseNpgsql(_pg.GetConnectionString()));

            // Ensure schema is created
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.Migrate();
        });

        builder.UseSetting("Jwt:Key", "integrationtestkey-must-be-32-chars!!");
        builder.UseSetting("Jwt:Issuer", "TestIssuer");
        builder.UseSetting("Jwt:Audience", "TestAudience");
        builder.UseSetting("EncryptionKey", "dGVzdGtleWZvcnVuaXR0ZXN0aW5nMTIz");
        builder.UseSetting("ConnectionStrings:Default", _pg.GetConnectionString());
    }
}
