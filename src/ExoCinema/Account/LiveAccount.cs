using System.Text.Json.Nodes;

namespace ExoCinema.Account;

/// <summary>
/// The live Exo account in Exo Cinema (cloud/account/CLIENT.md): one email and password for every Exo app, the same
/// account Exo Launcher and Exo Browser sign in to. The password goes to the service once and is never kept; the token
/// goes to the DPAPI file; the recovery code goes back to the screen once. Every password route is a press.
/// </summary>
internal static class LiveAccount
{
    private static AccountSessionStore Store => new(AppInfo.Data);

    /// <summary>Who is signed in on this PC, and the account's email as typed here.</summary>
    internal static AccountSession? Current() => Store.Load();

    internal static async Task<AccountOutcome> CreateAsync(string email, string password)
    {
        using var client = new ExoAccountClient();
        var reply = await client.CreateAccountAsync(email.Trim(), password, AccountDevice.Id(AppInfo.Data));
        return Finish(reply, email.Trim());
    }

    internal static async Task<AccountOutcome> SignInAsync(string email, string password)
    {
        using var client = new ExoAccountClient();
        var reply = await client.SignInAsync(email.Trim(), password, AccountDevice.Id(AppInfo.Data));
        return Finish(reply, email.Trim());
    }

    /// <summary>Ends this PC's session at the service (when it can be reached) and forgets it here. Local data stays.</summary>
    internal static async Task SignOutAsync()
    {
        if (Current() is { } session)
        {
            try
            {
                using var client = new ExoAccountClient();
                await client.SignOutAsync(session.Token);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                /* the service can't be reached: the token is forgotten here anyway */
            }
        }
        Store.Clear();
    }

    internal static async Task<JsonNode> SessionsAsync()
    {
        if (Current() is not { } session) return new JsonObject { ["ok"] = true, ["sessions"] = new JsonArray() };
        using var client = new ExoAccountClient();
        var reply = await client.ListSessionsAsync(session.Token);
        if (!reply.Ok || reply.Body is not JsonObject body) return Outcome(reply);
        var found = AccountDevices.Parse(body["sessions"] as JsonArray ?? []);
        var rows = new JsonArray();
        foreach (var row in AccountDevices.Merge(found, AccountDevices.OfThisPc(AccountDevice.Id(AppInfo.Data))))
            rows.Add(new JsonObject
            {
                ["device"] = row.Device,
                ["created"] = row.Created,
                ["lastUsed"] = row.LastUsed,
                ["current"] = row.Current,
                ["thisPc"] = row.ThisPc,
            });
        return new JsonObject { ["ok"] = true, ["sessions"] = rows };
    }

    internal static async Task<AccountOutcome> RevokeAsync(string device)
    {
        if (Current() is not { } session) return new AccountOutcome(false, "Sign in first.", "signed-out", null, false, null);
        using var client = new ExoAccountClient();
        return From(await client.RemoveSessionAsync(session.Token, device));
    }

    internal static async Task<AccountOutcome> ChangePasswordAsync(string current, string next)
    {
        if (Current() is not { } session) return new AccountOutcome(false, "Sign in first.", "signed-out", null, false, null);
        using var client = new ExoAccountClient();
        var reply = await client.ChangePasswordAsync(session.Token, current, next);
        // Other PCs are signed out; this PC keeps its token, so the session here stays as it is.
        return From(reply);
    }

    internal static async Task<AccountOutcome> NewRecoveryCodeAsync(string password)
    {
        if (Current() is not { } session) return new AccountOutcome(false, "Sign in first.", "signed-out", null, false, null);
        using var client = new ExoAccountClient();
        var reply = await client.NewRecoveryCodeAsync(session.Token, password);
        if (!reply.Ok) return From(reply);
        var code = (reply.Body as JsonObject)?["recoveryCode"]?.GetValue<string>();
        return code is null ? From(new AccountReply(reply.Status, reply.Body, "bad-response", null)) : AccountOutcome.Done(code);
    }

    internal static async Task<AccountOutcome> DeleteAsync(string password)
    {
        if (Current() is not { } session) return new AccountOutcome(false, "Sign in first.", "signed-out", null, false, null);
        using var client = new ExoAccountClient();
        var reply = await client.DeleteAccountAsync(session.Token, password);
        if (reply.Status == 204 || reply.Ok)
        {
            Store.Clear();
            return AccountOutcome.Done();
        }
        return From(reply);
    }

    /// <summary>A forgotten password, with the recovery code: every PC is signed out, the data stays, and this PC gets a new
    /// code (CLIENT.md 4.1 E).</summary>
    internal static async Task<AccountOutcome> ResetAsync(string email, string code, string password)
    {
        using var client = new ExoAccountClient();
        return Finish(await client.ResetAsync(email.Trim(), code, password, AccountDevice.Id(AppInfo.Data)), email.Trim());
    }

    private static AccountOutcome Finish(AccountReply reply, string email)
    {
        var signIn = ExoAccountClient.ReadSignIn(reply);
        if (signIn is null) return From(reply);
        Store.Save(new AccountSession(signIn.Token, signIn.AccountId, email));
        return AccountOutcome.Done(signIn.RecoveryCode);
    }

    private static AccountOutcome From(AccountReply reply) =>
        reply.Ok ? AccountOutcome.Done() : AccountOutcome.From(reply);

    /// <summary>A failed answer as the page reads it: ok false, its sentence, its code, and whether it was offline.</summary>
    private static JsonObject Outcome(AccountReply reply)
    {
        var outcome = AccountOutcome.From(reply);
        return new JsonObject
        {
            ["ok"] = false,
            ["message"] = outcome.Message,
            ["error"] = outcome.Error,
            ["offline"] = outcome.Offline,
            ["retryAfter"] = outcome.RetryAfterSeconds,
        };
    }
}
