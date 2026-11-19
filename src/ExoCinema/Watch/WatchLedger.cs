using System.Globalization;
using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Sync;

namespace ExoCinema.Watch;

/// <summary>
/// Exo Watch: one record of what you watched, are watching and saved, across every service and PC (the user,
/// 2026-10-29: "make our own better trakt built in"). Fed by Cinema's own playback up to 1.4; from 1.5 Cinema plays from
/// a Jellyfin server, which keeps its own record, and this one is only read (Library/CinemaLibrary.cs). Kept on this PC
/// (WatchStore) and synced under its own sub-key, <c>cinema-watch</c> (platform plan §2.7, agreed with Exo
/// Launcher). Its items are SyncBatch's own, held and merged by <see cref="SyncedItems"/>, so they travel as they are:
/// <list type="bullet">
/// <item><c>watched</c>, one per watch: id <c>&lt;public id&gt;@&lt;watched-at ms&gt;</c>, so a rewatch is a new item and
/// the newest-wins merge keeps every one.</item>
/// <item><c>position</c>: id <c>&lt;public id&gt;</c>; where it was left, and its length.</item>
/// <item><c>saved</c>: id <c>&lt;public id&gt;</c>; on the watchlist. Removing it leaves a tombstone.</item>
/// </list>
/// A public id names the title the same way everywhere, never a file or a server: <c>imdb:tt0111161</c> for a film,
/// <c>imdb:tt0903747:s01e02</c> for an episode (WatchIds).
/// </summary>
internal sealed class WatchLedger
{
    public const string App = "cinema-watch";

    public WatchLedger() : this(new SyncedItems(App)) { }

    public WatchLedger(SyncedItems items) => Synced = items;

    /// <summary>The items as sync holds them: what goes out, and where another PC's come in.</summary>
    public SyncedItems Synced { get; }

    public event Action? Changed
    {
        add => Synced.Changed += value;
        remove => Synced.Changed -= value;
    }

    public IEnumerable<SyncBatch.Item> Items => Synced.Items;

    public IEnumerable<SyncBatch.Item> Live(string type) => Synced.Live(type);

    /// <summary>Watched at <paramref name="at"/>. A watch within half a day of one already kept for the same title is the
    /// same watch (Cinema and a service both telling it, or one sent twice), so nothing is added.</summary>
    public bool Watched(string publicId, string title, DateTime at, string source)
    {
        var ms = new DateTimeOffset(at.ToUniversalTime()).ToUnixTimeMilliseconds();
        var prefix = publicId + "@";
        if (Live("watched").Any(i => i.Id.StartsWith(prefix, StringComparison.Ordinal) && Math.Abs(WatchedAt(i) - ms) < TimeSpan.FromHours(12).TotalMilliseconds))
            return false;
        Synced.Put("watched", $"{publicId}@{ms}", new JsonObject { ["title"] = title, ["source"] = source });
        // Finished: where it was left no longer matters.
        if (Synced.Get("position", publicId) is { Body: { } p } && (long?)p["at"] <= ms) Synced.Put("position", publicId, null);
        return true;
    }

    /// <summary>Left part way: the position and the title's length, kept if newer than what is there.</summary>
    public bool Position(string publicId, string title, long positionMs, long? runtimeMs, DateTime at, string source)
    {
        var atMs = new DateTimeOffset(at.ToUniversalTime()).ToUnixTimeMilliseconds();
        if (Synced.Get("position", publicId) is { Body: { } old } && (long?)old["at"] >= atMs) return false;
        // No length known: left out, as sync's canonical JSON takes no nulls.
        var body = new JsonObject { ["title"] = title, ["ms"] = positionMs, ["at"] = atMs, ["source"] = source };
        if (runtimeMs is { } runtime) body["runtime"] = runtime;
        Synced.Put("position", publicId, body);
        return true;
    }

    public bool Saved(string publicId, string title, string source)
    {
        if (Synced.Get("saved", publicId) is { Body: not null }) return false;
        Synced.Put("saved", publicId, new JsonObject { ["title"] = title, ["source"] = source });
        return true;
    }

    public void Unsave(string publicId) => Synced.Put("saved", publicId, null);

    public static long WatchedAt(SyncBatch.Item watched) =>
        long.TryParse(watched.Id[(watched.Id.LastIndexOf('@') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : 0;

    /// <summary>Every item, tombstones included (they stop a deleted item coming back), for the file this PC keeps.</summary>
    public JsonArray ToJson() => Synced.ToJson();

    public static WatchLedger FromJson(JsonArray? items) => new(SyncedItems.FromJson(App, items));
}

/// <summary>Public ids: how the ledger names a title, the same on every PC and from every source.</summary>
internal static class WatchIds
{
    /// <summary>A film by its IMDb id, else TMDB's; null without either.</summary>
    public static string? Film(JsonNode? providerIds) =>
        Id(providerIds, "Imdb") is { } imdb ? $"imdb:{imdb}" : Id(providerIds, "Tmdb") is { } tmdb ? $"tmdb:movie:{tmdb}" : null;

    /// <summary>A show by its IMDb id, else TMDB's (a saved show, or the start of its episodes' ids).</summary>
    public static string? Show(JsonNode? seriesProviderIds) =>
        Id(seriesProviderIds, "Imdb") is { } imdb ? $"imdb:{imdb}" : Id(seriesProviderIds, "Tmdb") is { } tmdb ? $"tmdb:tv:{tmdb}" : null;

    /// <summary>An episode by its show's id, season and number; null without them.</summary>
    public static string? Episode(JsonNode? seriesProviderIds, int? season, int? number) =>
        season is { } s && number is { } n && Show(seriesProviderIds) is { } show ? $"{show}:s{s:00}e{n:00}" : null;

    private static string? Id(JsonNode? ids, string key) => (string?)ids?[key] is { Length: > 0 } v ? v.Trim() : null;
}
