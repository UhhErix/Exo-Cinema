using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Exo.Identity;

namespace ExoCinema.Sync;

/// <summary>
/// A sync sub-key's items kept on this PC (SyncedItems): one file in Exo Cinema's data, sealed with Windows' data
/// protection for this Windows user (as the sign-ins are) until the Exo identity's app state key takes over. Read the
/// first time they're wanted, written a moment after each change, off the UI thread, and kept whole: a new file
/// replaces the old only once it is written.
/// </summary>
internal sealed class KeptItems(string app, string file, string purpose)
{
    private readonly byte[] _purpose = Encoding.UTF8.GetBytes(purpose);
    private readonly string _path = Path.Combine(AppInfo.Data, file);
    private Lazy<SyncedItems>? _items;
    private Timer? _save;

    public SyncedItems Items => (_items ??= new(Load)).Value;

    /// <summary>Starts over with none (this PC left its profile, or starts or joins one), kept at once.</summary>
    public void Reset()
    {
        _items = new(Watched(new SyncedItems(app)));
        _ = _items.Value;
        Save();
    }

    private SyncedItems Load()
    {
        SyncedItems items;
        try
        {
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), _purpose, DataProtectionScope.CurrentUser));
            items = SyncedItems.FromJson(app, JsonNode.Parse(json) as JsonArray);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException) { items = new SyncedItems(app); }
        return Watched(items);
    }

    private SyncedItems Watched(SyncedItems items)
    {
        items.Changed += () => (_save ??= new Timer(_ => Save())).Change(1500, Timeout.Infinite);
        return items;
    }

    public void Save()
    {
        if (_items is not { IsValueCreated: true } loaded) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] sealedBytes;
            lock (loaded.Value) sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(loaded.Value.ToJson().ToJsonString()), _purpose, DataProtectionScope.CurrentUser);
            var next = _path + ".new";
            File.WriteAllBytes(next, sealedBytes);
            File.Move(next, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { }
    }
}
