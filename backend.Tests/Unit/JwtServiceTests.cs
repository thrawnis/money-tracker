using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MoneyTracker.Auth.Services;
using MoneyTracker.Models;

namespace MoneyTracker.Tests.Unit;

public class JwtServiceTests
{
    private static JwtService BuildService() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"]                     = "supersecretkeythatisatleast32chars!!",
                ["Jwt:Issuer"]                  = "TestIssuer",
                ["Jwt:Audience"]                = "TestAudience",
                ["Jwt:AccessTokenExpiryMinutes"] = "15",
                ["Jwt:RefreshTokenExpiryDays"]  = "7",
            })
            .Build());

    private static ApplicationUser FakeUser() => new()
    {
        Id    = Guid.NewGuid().ToString(),
        Email = "test@example.com",
    };

    [Fact]
    public void GenerateAccessToken_ReturnsValidJwt()
    {
        var svc   = BuildService();
        var user  = FakeUser();
        var (token, expiry) = svc.GenerateAccessToken(user, "Standard");

        token.Should().NotBeNullOrEmpty();
        expiry.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void GenerateAccessToken_ContainsExpectedClaims()
    {
        var svc    = BuildService();
        var user   = FakeUser();
        var (token, _) = svc.GenerateAccessToken(user, "Standard");

        var principal = svc.ValidateAccessToken(token);
        principal.Should().NotBeNull();
        principal!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id);
        principal.FindFirstValue(ClaimTypes.Email).Should().Be(user.Email);
        principal.FindFirstValue(ClaimTypes.Role).Should().Be("Standard");
    }

    [Fact]
    public void ValidateAccessToken_InvalidToken_ReturnsNull()
    {
        var svc = BuildService();
        svc.ValidateAccessToken("not.a.jwt").Should().BeNull();
    }

    [Fact]
    public void ValidateAccessToken_TamperedToken_ReturnsNull()
    {
        var svc        = BuildService();
        var (token, _) = svc.GenerateAccessToken(FakeUser(), "Standard");
        var tampered   = token[..^4] + "XXXX";

        svc.ValidateAccessToken(tampered).Should().BeNull();
    }

    [Fact]
    public void GenerateRefreshToken_IsBase64AndFutureDated()
    {
        var svc = BuildService();
        var (token, expiry) = svc.GenerateRefreshToken();

        token.Should().NotBeNullOrEmpty();
        var bytes = Convert.FromBase64String(token); // throws if invalid base64
        bytes.Length.Should().Be(64);
        expiry.Should().BeAfter(DateTime.UtcNow.AddDays(6));
    }

    [Fact]
    public void GenerateRefreshToken_TwoCalls_ProduceUniqueTokens()
    {
        var svc = BuildService();
        var (t1, _) = svc.GenerateRefreshToken();
        var (t2, _) = svc.GenerateRefreshToken();

        t1.Should().NotBe(t2);
    }
}
