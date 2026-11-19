using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExoCinema.Jellyfin;

/// <summary>
/// The signed-in Jellyfin account and this PC's device id, in jellyfin.json (not accounts.json, the sign-in from before
/// 1.5 that MainWindow.Import.cs clears at every start). The token is sealed with Windows DPAPI for this Windows user;
/// the password is never kept. On this PC alone: nothing here goes through Exo sync.
/// </summary>
internal sealed class AccountStore(string folder)
{
    private static readonly byte[] Entropy = "Exo Cinema Jellyfin account"u8.ToArray();
    private readonly string _path = Path.Combine(folder, "jellyfin.json");

    internal sealed record Saved(string Server, string ServerName, string UserId, string UserName, string SealedToken);
    private sealed record Stored(string DeviceId, Saved? Account);

    /// <summary>Who this PC is to the server, kept across sign-outs so it stays one device there.</summary>
    public string DeviceId => Load().DeviceId;

    /// <summary>The account with its token; none when there is no account, or its token was sealed for someone else.</summary>
    public (Saved Account, string Token)? Current()
    {
        if (Load().Account is not { } a) return null;
        try { return (a, Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(a.SealedToken), Entropy, DataProtectionScope.CurrentUser))); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    public void Save(string server, string serverName, string userId, string userName, string token)
    {
        var sealedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        Write(Load() with { Account = new Saved(server, serverName, userId, userName, sealedToken) });
    }

    public void Forget() => Write(Load() with { Account = null });

    private Stored Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path)) is { DeviceId.Length: > 0 } stored) return stored;
        }
        catch (JsonException) { }
        var fresh = new Stored(Guid.NewGuid().ToString("N"), null);
        Write(fresh);
        return fresh;
    }

    private void Write(Stored stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
