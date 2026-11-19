using System.Text.Json.Nodes;
using ExoCinema.Account;
using ExoCinema.Sync;

namespace ExoCinema;

/// <summary>
/// Settings › Account and the first run's account step (app.js): the live Exo account, one email and password for every
/// Exo app (cloud/account/CLIENT.md). The password goes to the service once and is never kept; the token stays in the DPAPI
/// file; the recovery code reaches the page once and is kept nowhere.
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonNode?> AccountAsync(string op, JsonNode? args)
    {
        switch (op)
        {
            case "account":
                var session = LiveAccount.Current();
                return new JsonObject
                {
                    ["enabled"] = true,
                    ["signedIn"] = session is not null,
                    ["email"] = session?.Email,
                    ["accountId"] = session?.AccountId,
                };
            case "account-create":
                return Signed(await LiveAccount.CreateAsync(Text(args, "email"), Text(args, "password")));
            case "account-signin":
                return Signed(await LiveAccount.SignInAsync(Text(args, "email"), Text(args, "password")));
            case "account-sync-status":
                return _accountSync?.Status() ?? new JsonObject();
            case "account-sync-now":
                if (_accountSync is { } syncNow) await syncNow.SyncNowAsync();
                return _accountSync?.Status() ?? new JsonObject();
            case "account-signout":
                await LiveAccount.SignOutAsync();
                return new JsonObject { ["ok"] = true };
            case "account-sessions":
                return await LiveAccount.SessionsAsync();
            case "account-revoke":
                return Answer(await LiveAccount.RevokeAsync(Text(args, "device")));
            case "account-password":
                return Answer(await LiveAccount.ChangePasswordAsync(Text(args, "current"), Text(args, "password")));
            case "account-recovery":
                return Answer(await LiveAccount.NewRecoveryCodeAsync(Text(args, "password")));
            case "account-delete":
                var deleted = await LiveAccount.DeleteAsync(Text(args, "password"));
                if (deleted.Ok) _accountSync?.Forget();
                return Answer(deleted);
            case "account-export":
                return await ExportAccountAsync();
            case "account-reset":
                return Signed(await LiveAccount.ResetAsync(Text(args, "email"), Text(args, "code"), Text(args, "password")));
            default:
                // The Profile room's own requests (profile.js): its service is parked, so each answers as off.
                return new JsonObject { ["enabled"] = false };
        }
    }

    private static string Text(JsonNode? args, string key) => (string?)args?[key] ?? "";

    /// <summary>The account sync (CLIENT.md 7): it runs whenever the account service is reached, from start and after sign-in.</summary>
    private AccountSyncHost? _accountSync;

    private void StartAccountSync()
    {
        _accountSync = new AccountSyncHost(AppInfo.Data, LiveAccount.Current, Watch.WatchStore.Ledger.Synced, SyncedSettings.Items, SyncTaken, _log.Write);
        _accountSync.Start();
    }

    /// <summary>A new sign-in or account: its first sync runs at once, with a pull.</summary>
    private JsonObject Signed(AccountOutcome outcome)
    {
        if (outcome.Ok) _ = _accountSync?.SyncNowAsync();
        return Answer(outcome);
    }

    /// <summary>The account's export, through the save dialog (CLIENT.md 7.7): every page read, then one JSON file the
    /// person picks. The file holds the account's items as plain text; nothing is kept here.</summary>
    private async Task<JsonNode?> ExportAccountAsync()
    {
        if (LiveAccount.Current() is not { } session) return new JsonObject { ["ok"] = false, ["message"] = "Sign in first." };
        using var client = new ExoAccountClient();
        var collected = await AccountExport.CollectAsync(client, session.Token, DateTimeOffset.Now);
        if (!collected.Ok || collected.File is null)
            return new JsonObject { ["ok"] = false, ["message"] = AccountMessages.For(collected.Error) };
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeChoices.Add("JSON", [".json"]);
        picker.SuggestedFileName = "Exo account data";
        if (await picker.PickSaveFileAsync() is not { } file) return new JsonObject { ["ok"] = true, ["cancelled"] = true };
        await File.WriteAllTextAsync(file.Path, collected.File.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>An account answer as the page reads it: the sentence, the code, whether it was offline, and the one-time
    /// recovery code (never the token or the password).</summary>
    private static JsonObject Answer(AccountOutcome outcome) => new()
    {
        ["ok"] = outcome.Ok,
        ["message"] = outcome.Message,
        ["error"] = outcome.Error,
        ["offline"] = outcome.Offline,
        ["retryAfter"] = outcome.RetryAfterSeconds,
        ["recoveryCode"] = outcome.RecoveryCode,
    };
}
