using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MoneyTracker.Services;

public class SimpleFinException(string message, bool accessRevoked = false) : Exception(message)
{
    /// <summary>SimpleFIN rejected the credentials — the access URL was revoked or is invalid.</summary>
    public bool AccessRevoked { get; } = accessRevoked;
}

public record SfTransaction(
    string Id, long Posted, long? TransactedAt, decimal Amount,
    string? Description, string? Payee, string? Memo, bool Pending);

// One investment position, as some institutions report it (not part of the
// original protocol; SimpleFIN Bridge sends it for brokerages that provide it).
public record SfHolding(
    string Id, string? Symbol, string? Description, decimal? Shares,
    decimal? MarketValue, decimal? CostBasis, string? Currency);

public record SfAccount(
    string Id, string Name, string? OrgName, string? Currency, decimal? Balance,
    List<SfTransaction> Transactions, long? BalanceDate = null, List<SfHolding>? Holdings = null);

public record SfAccountSet(List<string> Errors, List<SfAccount> Accounts);

/// <summary>
/// Minimal client for the SimpleFIN protocol (https://www.simplefin.org/protocol.html):
///   • a setup token is a base64-encoded one-time "claim" URL; POSTing to it
///     returns the long-lived access URL (credentials embedded as user:pass@);
///   • GET {access URL}/accounts with HTTP Basic auth returns balances and,
///     unless balances-only=1, posted transactions from start-date onward.
///
/// The claim URL comes from user input, so this is a server-side request to a
/// user-chosen address — an SSRF vector. Every outbound URL must be HTTPS and
/// must not resolve to a loopback/private/link-local address, and redirects
/// are disabled on the named HttpClient (see Program.cs) so a public host
/// can't bounce the request inward. SimpleFin:AllowInsecureHosts=true lifts
/// both checks; it exists only so tests can point at a local mock server.
/// </summary>
public class SimpleFinClient(IHttpClientFactory httpFactory, IConfiguration config)
{
    public const string HttpClientName = "simplefin";

    private bool AllowInsecure => config.GetValue<bool>("SimpleFin:AllowInsecureHosts");

    public async Task<string> ClaimAsync(string setupToken, CancellationToken ct)
    {
        string claimUrl;
        try
        {
            claimUrl = Encoding.UTF8.GetString(Convert.FromBase64String(setupToken.Trim()));
        }
        catch (FormatException)
        {
            throw new SimpleFinException("That doesn't look like a SimpleFIN setup token. Copy the whole token from SimpleFIN Bridge and try again.");
        }

        var claimUri = await ValidateUrlAsync(claimUrl, ct);
        var http = httpFactory.CreateClient(HttpClientName);

        using var resp = await SendSafelyAsync(() => http.PostAsync(claimUri, new ByteArrayContent([]), ct));
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new SimpleFinException("This setup token has already been used or has expired. Create a new one in SimpleFIN Bridge.");
        if (!resp.IsSuccessStatusCode)
            throw new SimpleFinException($"SimpleFIN rejected the setup token ({(int)resp.StatusCode}).");

        var accessUrl = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        var accessUri = await ValidateUrlAsync(accessUrl, ct);
        if (string.IsNullOrEmpty(accessUri.UserInfo))
            throw new SimpleFinException("SimpleFIN returned an access URL without credentials.");
        return accessUrl;
    }

    public async Task<SfAccountSet> GetAccountsAsync(string accessUrl, DateTimeOffset? startDate, bool balancesOnly, CancellationToken ct)
    {
        var uri = await ValidateUrlAsync(accessUrl, ct);

        // Credentials travel as HTTP Basic auth, not in the request URL — HttpClient
        // doesn't apply userinfo itself, and keeping them out of the URL keeps
        // them out of any logged request line.
        var parts = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(parts[0]);
        var pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        var baseUrl = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString().TrimEnd('/');

        var query = new List<string>();
        if (startDate.HasValue) query.Add($"start-date={startDate.Value.ToUnixTimeSeconds()}");
        if (balancesOnly) query.Add("balances-only=1");
        var url = $"{baseUrl}/accounts" + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        var http = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

        using var resp = await SendSafelyAsync(() => http.SendAsync(req, ct));
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new SimpleFinException("SimpleFIN refused access. The connection may have been revoked — disconnect and connect again with a new setup token.", accessRevoked: true);
        if (resp.StatusCode == HttpStatusCode.PaymentRequired)
            throw new SimpleFinException("SimpleFIN reports the subscription for this connection is inactive.");
        if (!resp.IsSuccessStatusCode)
            throw new SimpleFinException($"SimpleFIN request failed ({(int)resp.StatusCode}). Try again later.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    private static async Task<HttpResponseMessage> SendSafelyAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            var resp = await send();
            // Redirects are disabled on the client; a 3xx means the server tried
            // to send us somewhere else, which is refused rather than followed.
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                resp.Dispose();
                throw new SimpleFinException("SimpleFIN responded with an unexpected redirect.");
            }
            return resp;
        }
        catch (HttpRequestException ex)
        {
            throw new SimpleFinException($"Couldn't reach SimpleFIN: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new SimpleFinException("SimpleFIN took too long to respond. Try again later.");
        }
    }

    private async Task<Uri> ValidateUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new SimpleFinException("SimpleFIN returned an invalid URL.");
        if (AllowInsecure) return uri;

        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new SimpleFinException("Only HTTPS SimpleFIN URLs are accepted.");

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            throw new SimpleFinException($"Couldn't resolve {uri.Host}.");
        }

        if (addresses.Length == 0 || addresses.Any(IsNonPublic))
            throw new SimpleFinException("That SimpleFIN URL points to a private or local network address, which isn't allowed.");
        return uri;
    }

    private static bool IsNonPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || b[0] == 0
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
    }

    // Tolerant parsing: amounts/balances arrive as strings, optional fields
    // may be absent, and newer servers report errors in "errlist" objects
    // alongside (or instead of) the original "errors" string array.
    private static SfAccountSet Parse(JsonElement root)
    {
        var errors = new List<string>();
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            errors.AddRange(errs.EnumerateArray().Select(e => e.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));
        if (root.TryGetProperty("errlist", out var errList) && errList.ValueKind == JsonValueKind.Array)
            errors.AddRange(errList.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object ? Str(e, "msg") : e.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))!);

        var accounts = new List<SfAccount>();
        if (root.TryGetProperty("accounts", out var accts) && accts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in accts.EnumerateArray())
            {
                var id = Str(a, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string? orgName = null;
                if (a.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object)
                    orgName = Str(org, "name") ?? Str(org, "domain");

                var txs = new List<SfTransaction>();
                if (a.TryGetProperty("transactions", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tEl.EnumerateArray())
                    {
                        var amount = Dec(t, "amount");
                        var tid = Str(t, "id");
                        if (amount is null || string.IsNullOrEmpty(tid)) continue;
                        txs.Add(new SfTransaction(
                            tid,
                            Long(t, "posted") ?? 0,
                            Long(t, "transacted_at"),
                            amount.Value,
                            Str(t, "description"),
                            Str(t, "payee"),
                            Str(t, "memo"),
                            t.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True));
                    }
                }

                var holdings = new List<SfHolding>();
                if (a.TryGetProperty("holdings", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in hEl.EnumerateArray())
                    {
                        if (h.ValueKind != JsonValueKind.Object) continue;
                        var symbol = Str(h, "symbol");
                        var description = Str(h, "description");
                        // Some feeds omit ids; the symbol (or name) identifies the
                        // position well enough to follow it from day to day.
                        var hid = Str(h, "id") ?? symbol ?? description;
                        if (string.IsNullOrEmpty(hid)) continue;
                        holdings.Add(new SfHolding(
                            hid, symbol, description, Dec(h, "shares"),
                            Dec(h, "market_value"), Dec(h, "cost_basis"), Str(h, "currency")));
                    }
                }

                accounts.Add(new SfAccount(
                    id, Str(a, "name") ?? id, orgName, Str(a, "currency"), Dec(a, "balance"), txs,
                    Long(a, "balance-date"), holdings));
            }
        }

        return new SfAccountSet(errors, accounts);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        return v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : null;
    }
}
