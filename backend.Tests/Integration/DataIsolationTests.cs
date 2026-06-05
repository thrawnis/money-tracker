using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MoneyTracker.Auth.Dtos;

namespace MoneyTracker.Tests.Integration;

/// <summary>
/// Verifies that no data leaks between users.
/// </summary>
public class DataIsolationTests(ApiFactory factory)
    : IClassFixture<ApiFactory>
{
    private async Task<HttpClient> AuthedClient(string emailSuffix)
    {
        var reg = new RegisterRequest($"isolate{emailSuffix}@example.com", "P@ssw0rd!Secure1");
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", reg);
        var tok = await res.Content.ReadFromJsonAsync<TokenResponse>();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return client;
    }

    [Fact]
    public async Task AccountsAreNotVisibleToOtherUsers()
    {
        var a = await AuthedClient("_iso_accts_a");
        var b = await AuthedClient("_iso_accts_b");

        // User A creates an account
        var createRes = await a.PostAsJsonAsync("/api/accounts", new
        {
            name           = "User A Checking",
            type           = "Checking",
            openingBalance = 1000m,
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // User B should see an empty list
        var listRes = await b.GetAsync("/api/accounts");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var accounts = await listRes.Content.ReadFromJsonAsync<List<object>>();
        accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task CategoriesAreNotVisibleToOtherUsers()
    {
        var a = await AuthedClient("_iso_cats_a");
        var b = await AuthedClient("_iso_cats_b");

        await a.PostAsJsonAsync("/api/categories", new { name = "Food" });

        var res = await b.GetAsync("/api/categories");
        var cats = await res.Content.ReadFromJsonAsync<List<object>>();
        cats.Should().BeEmpty();
    }

    [Fact]
    public async Task PayeesAreNotVisibleToOtherUsers()
    {
        var a = await AuthedClient("_iso_payee_a");
        var b = await AuthedClient("_iso_payee_b");

        await a.PostAsJsonAsync("/api/payees", new { name = "Grocery Store" });

        var res = await b.GetAsync("/api/payees");
        var payees = await res.Content.ReadFromJsonAsync<List<object>>();
        payees.Should().BeEmpty();
    }
}
