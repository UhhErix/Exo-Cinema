using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoCinema.Account;

/// <summary>The sign-in or creation answer: this PC's token and the account, and the recovery code on creation or reset.</summary>
internal sealed record AccountSignIn(string Token, string AccountId, string? RecoveryCode);

/// <summary>
/// The HTTP calls of the account service (cloud/account/CLIENT.md section 3), the same client as Exo Launcher's
/// (Services/Account/ExoAccountClient.cs). Returns answers, never a token to the page: the caller keeps the token in
/// <see cref="AccountSessionStore"/>. Every request goes through <see cref="SendAsync"/> and the egress checkpoint.
/// </summary>
internal sealed class ExoAccountClient : IDisposable
{
    /// <summary>The live service, or the test override EXO_ACCOUNT_URL names (Net/AccountEndpoint.cs).</summary>
    internal static Uri Service => Net.AccountEndpoint.Current;
    private readonly HttpClient _http;

    internal ExoAccountClient(HttpMessageHandler? handler = null, Uri? service = null)
    {
        _http = new HttpClient(Net.Egress.Checkpoint(Net.Egress.Purpose.Account, handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }))
        {
            BaseAddress = service ?? Service,
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Sends one request. Network failure and timeouts come back as status 0; a non-JSON error body as "bad-response".</summary>
    internal async Task<AccountReply> SendAsync(HttpMethod method, string path, string? token, JsonObject? body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try { if (text.Length > 0) json = JsonNode.Parse(text); }
            catch (JsonException) { /* no JSON: the error below says so */ }
            var error = json is JsonObject obj && obj["error"] is JsonValue value && value.TryGetValue(out string? code) ? code : null;
            if (error is null && !response.IsSuccessStatusCode) error = "bad-response";
            var retry = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : (int?)null;
            return new AccountReply((int)response.StatusCode, json, error, retry);
        }
        catch (HttpRequestException)
        {
            return new AccountReply(0, null, "network", null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AccountReply(0, null, "network", null);
        }
    }

    internal Task<AccountReply> CreateAccountAsync(string email, string password, string device) =>
        SendAsync(HttpMethod.Post, "v1/account", null, Body(("email", email), ("password", password), ("device", device)));

    internal Task<AccountReply> SignInAsync(string email, string password, string device) =>
        SendAsync(HttpMethod.Post, "v1/session", null, Body(("email", email), ("password", password), ("device", device)));

    internal Task<AccountReply> GetAccountAsync(string token) => SendAsync(HttpMethod.Get, "v1/account", token, null);

    internal Task<AccountReply> SignOutAsync(string token) => SendAsync(HttpMethod.Post, "v1/signout", token, null);

    internal Task<AccountReply> ChangePasswordAsync(string token, string current, string next) =>
        SendAsync(HttpMethod.Put, "v1/password", token, Body(("current", current), ("password", next)));

    internal Task<AccountReply> NewRecoveryCodeAsync(string token, string password) =>
        SendAsync(HttpMethod.Post, "v1/recovery", token, Body(("password", password)));

    internal Task<AccountReply> ResetAsync(string email, string code, string password, string device) =>
        SendAsync(HttpMethod.Post, "v1/recovery/reset", null, Body(("email", email), ("code", code), ("password", password), ("device", device)));

    internal Task<AccountReply> ListSessionsAsync(string token) => SendAsync(HttpMethod.Get, "v1/sessions", token, null);

    /// <summary>A PUT of changed items (CLIENT.md 7.4): one answer with what was written and what conflicts.</summary>
    internal Task<AccountReply> PutSyncAsync(string token, JsonObject body) => SendAsync(HttpMethod.Put, "v1/sync", token, body);

    /// <summary>One page of items after this cursor (CLIENT.md 7.6).</summary>
    internal Task<AccountReply> PullSyncAsync(string token, long after) =>
        SendAsync(HttpMethod.Get, "v1/sync?after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture), token, null);

    /// <summary>One page of the export (CLIENT.md 7.7). Tombstones come back with a null value.</summary>
    internal Task<AccountReply> ExportPageAsync(string token, long after) =>
        SendAsync(HttpMethod.Get, "v1/export?after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture), token, null);

    internal Task<AccountReply> RemoveSessionAsync(string token, string device) =>
        SendAsync(HttpMethod.Delete, "v1/sessions/" + device, token, null);

    internal Task<AccountReply> DeleteAccountAsync(string token, string password) =>
        SendAsync(HttpMethod.Delete, "v1/account", token, Body(("password", password)));

    /// <summary>The account id and this PC's token from an answer that carries them (201 create, 200 sign-in or reset).</summary>
    internal static AccountSignIn? ReadSignIn(AccountReply reply)
    {
        if (!reply.Ok || reply.Body is not JsonObject obj) return null;
        var token = obj["token"]?.GetValue<string>();
        var id = obj["account"]?["id"]?.GetValue<string>();
        if (token is null || id is null) return null;
        return new AccountSignIn(token, id, obj["recoveryCode"]?.GetValue<string>());
    }

    private static JsonObject Body(params (string Key, string Value)[] fields)
    {
        var body = new JsonObject();
        foreach (var (key, value) in fields) body[key] = value;
        return body;
    }
}
