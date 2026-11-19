using System.Security.Cryptography;
using System.Text.Json;

namespace ExoCinema.Account;

/// <summary>The signed-in account on this PC: the session token, the account id and the email as typed.</summary>
internal sealed record AccountSession(string Token, string AccountId, string Email);

/// <summary>
/// The session token file (CLIENT.md section 6), the same rules as Exo Launcher's AccountSessionStore: sealed with Windows
/// DPAPI for this Windows user, written atomically, and never in settings, logs or a plain backup. A damaged or foreign
/// file reads as signed out and is kept, so local data is never deleted over it.
/// </summary>
internal sealed class AccountSessionStore
{
    internal const string FileName = "account-session.bin";
    private static readonly byte[] Entropy = "Exo Cinema account"u8.ToArray();
    private readonly string _path;

    internal AccountSessionStore(string dataDir) => _path = Path.Combine(dataDir, FileName);

    internal void Save(AccountSession session)
    {
        var blob = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(new Stored(session.Token, session.AccountId, session.Email)), Entropy, DataProtectionScope.CurrentUser);
        var temp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(temp, blob);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>The saved session, or null when there is none or it cannot be read on this PC. Never deletes.</summary>
    internal AccountSession? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser));
            if (stored is null || string.IsNullOrEmpty(stored.Token) || string.IsNullOrEmpty(stored.AccountId)) return null;
            return new AccountSession(stored.Token, stored.AccountId, stored.Email ?? "");
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal void Clear()
    {
        try { File.Delete(_path); }
        catch (IOException) { /* the next sign-in replaces it */ }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Stored(string Token, string AccountId, string? Email);
}

/// <summary>
/// The device id the service keys a session by (CLIENT.md 4.3): 16 random bytes as 32 lowercase hex, kept in a plain file
/// (it is not a secret), made once and reused on every start.
/// </summary>
internal static class AccountDevice
{
    internal const string FileName = "device-id.txt";

    internal static string Id(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            var saved = File.ReadAllText(path).Trim();
            if (IsValid(saved)) return saved;
        }
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(path, id);
        return id;
    }

    internal static bool IsValid(string id) =>
        id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
