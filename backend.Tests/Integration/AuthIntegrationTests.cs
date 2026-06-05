using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MoneyTracker.Auth.Dtos;

namespace MoneyTracker.Tests.Integration;

public class AuthIntegrationTests(ApiFactory factory)
    : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static RegisterRequest NewUser(string suffix = "") => new(
        $"test{suffix}@example.com",
        "P@ssw0rd!Secure1");

    [Fact]
    public async Task Register_ValidUser_Returns200WithTokens()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/register", NewUser("_reg"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<TokenResponse>();
        body!.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns400()
    {
        var user = NewUser("_dup");
        await _client.PostAsJsonAsync("/api/auth/register", user);

        var res = await _client.PostAsJsonAsync("/api/auth/register", user);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200()
    {
        var user = NewUser("_login");
        await _client.PostAsJsonAsync("/api/auth/register", user);

        var res = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(user.Email, user.Password));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var user = NewUser("_badpw");
        await _client.PostAsJsonAsync("/api/auth/register", user);

        var res = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(user.Email, "WrongPassword!1"));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var res = await _client.GetAsync("/api/accounts");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_Returns200()
    {
        var user = NewUser("_auth");
        var regRes = await _client.PostAsJsonAsync("/api/auth/register", user);
        var tokens = await regRes.Content.ReadFromJsonAsync<TokenResponse>();

        var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var res = await authed.GetAsync("/api/accounts");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
