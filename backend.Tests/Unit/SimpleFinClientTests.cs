using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

/// <summary>
/// The setup token is user input that decodes to a URL the server then
/// requests — these pin down the SSRF guard that runs before any request is
/// made. No network access is involved: every case is rejected up front.
/// </summary>
public class SimpleFinClientTests
{
    private static SimpleFinClient Client(bool allowInsecure = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SimpleFin:AllowInsecureHosts"] = allowInsecure ? "true" : "false",
            })
            .Build();
        // Any request reaching the factory means validation let it through.
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        return new SimpleFinClient(factory.Object, config);
    }

    private static string Token(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url));

    [Fact]
    public async Task Rejects_setup_token_that_is_not_base64()
    {
        var act = () => Client().ClaimAsync("%%% not base64 %%%", CancellationToken.None);
        await act.Should().ThrowAsync<SimpleFinException>().WithMessage("*setup token*");
    }

    [Fact]
    public async Task Rejects_plain_http_claim_url()
    {
        var act = () => Client().ClaimAsync(Token("http://bridge.example.com/claim/x"), CancellationToken.None);
        await act.Should().ThrowAsync<SimpleFinException>().WithMessage("*HTTPS*");
    }

    [Theory]
    [InlineData("https://127.0.0.1/claim/x")]         // loopback
    [InlineData("https://10.0.0.5/claim/x")]          // RFC1918
    [InlineData("https://172.20.1.1/claim/x")]        // RFC1918 (docker bridge range)
    [InlineData("https://192.168.1.10/claim/x")]      // RFC1918
    [InlineData("https://169.254.169.254/latest/")]   // link-local — cloud metadata endpoint
    [InlineData("https://100.64.0.1/claim/x")]        // carrier-grade NAT
    [InlineData("https://[::1]/claim/x")]             // IPv6 loopback
    [InlineData("https://[fd00::1]/claim/x")]         // IPv6 unique-local
    [InlineData("https://localhost/claim/x")]         // resolves to loopback
    public async Task Rejects_claim_urls_pointing_at_non_public_addresses(string url)
    {
        var act = () => Client().ClaimAsync(Token(url), CancellationToken.None);
        await act.Should().ThrowAsync<SimpleFinException>().WithMessage("*private or local*");
    }

    [Fact]
    public async Task Access_url_is_held_to_the_same_rules()
    {
        var act = () => Client().GetAccountsAsync("https://user:pw@10.0.0.5/simplefin", null, true, CancellationToken.None);
        await act.Should().ThrowAsync<SimpleFinException>().WithMessage("*private or local*");
    }
}
