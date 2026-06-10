using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Export;
using MoneyTracker.Models;
using MoneyTracker.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ─────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ── Identity ──────────────────────────────────────────────────────────────────

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit           = true;
    options.Password.RequireLowercase       = true;
    options.Password.RequireUppercase       = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength         = 12;

    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(15);
    options.Lockout.AllowedForNewUsers      = true;

    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// ── JWT ───────────────────────────────────────────────────────────────────────

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key must be set in configuration.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer           = true,
        ValidIssuer              = builder.Configuration["Jwt:Issuer"] ?? "MoneyTracker",
        ValidateAudience         = true,
        ValidAudience            = builder.Configuration["Jwt:Audience"] ?? "MoneyTracker",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateLifetime         = true,
        ClockSkew                = TimeSpan.FromSeconds(30),
    };
});

builder.Services.AddAuthorization();

// ── Rate limiting (auth + demo endpoints) ─────────────────────────────────────

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window      = TimeSpan.FromMinutes(1),
                QueueLimit  = 0,
            }));
});

// ── Session (used for passkey challenge round-trip) ───────────────────────────

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout        = TimeSpan.FromMinutes(5);
    options.Cookie.HttpOnly    = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // allow plain-HTTP local dev
    options.Cookie.SameSite    = SameSiteMode.Strict;
});

// ── Passkeys (Fido2NetLib — only registered when built with FIDO2_AVAILABLE) ──

#if FIDO2_AVAILABLE
builder.Services.AddFido2(options =>
{
    options.ServerDomain = builder.Configuration["Fido2:Domain"] ?? "localhost";
    options.ServerName   = "Money Tracker";
    options.Origins      = new HashSet<string>
    {
        builder.Configuration["Fido2:Origin"] ?? "https://localhost:3000"
    };
    options.TimestampDriftTolerance = 300_000;
})
.AddCachedMetadataService(c =>
{
    c.AddStaticMetadataRepository();
});
#endif

builder.Services.AddScoped<IPasskeyService, PasskeyService>();

// ── Receipt extraction provider ───────────────────────────────────────────────

var receiptProvider = (builder.Configuration["Receipt:Provider"] ?? "ollama").ToLowerInvariant();
switch (receiptProvider)
{
    case "anthropic":
        builder.Services.AddScoped<IReceiptExtractor, AnthropicReceiptExtractor>();
        break;
    case "openai":
        builder.Services.AddScoped<IReceiptExtractor, OpenAiReceiptExtractor>();
        break;
    case "gemini":
        builder.Services.AddScoped<IReceiptExtractor, GeminiReceiptExtractor>();
        break;
    default: // "ollama"
        builder.Services.AddScoped<IReceiptExtractor, OllamaReceiptExtractor>();
        break;
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditService, AuditService>();

builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton(UrlEncoder.Default);
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddHostedService<ScheduledTransactionPostingService>();

// ── MVC & Swagger ─────────────────────────────────────────────────────────────

builder.Services.AddHttpClient();

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration["AllowedOrigins"] ?? "http://localhost:3000")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()); // required for httpOnly refresh cookie
});

var app = builder.Build();

// ── Apply migrations on startup ───────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
    await audit.LogSystemAsync("STARTUP", details: new { message = "Application started, migrations applied" });

    if (app.Configuration["DEMO_MODE"] == "true")
    {
        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
        await seeder.SeedIfNeededAsync();
    }
}

// ── Middleware pipeline ───────────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRateLimiter();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
// Expose Program for WebApplicationFactory in tests
public partial class Program { }
