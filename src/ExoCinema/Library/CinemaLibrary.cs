using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Jellyfin;
using ExoCinema.Watch;

namespace ExoCinema.Library;

/// <summary>
/// Exo Cinema's library (the page's <c>jf</c> calls): the Jellyfin server this PC is signed in to, and nothing else.
/// Home's rows, Movies and Shows, a title's page, search, the versions of a file and playback are all the server's
/// (<see cref="Jellyfin"/>); with no server signed in to there is no library, and the page shows the sign-in.
/// <para>Exo Watch (Watch/WatchLedger.cs) is what Cinema recorded before 1.5, when it played from other sources: its
/// History and Watchlist are still read and shown, each title by its name, beside the server's own. Nothing is added to
/// it or taken from it here: the server keeps where a title was left, what was played and what is a favorite.</para>
/// </summary>
internal sealed class CinemaLibrary(IPlayerSurface player, JellyfinLibrary jellyfin, Func<WatchLedger> ledger, Func<long> cacheSize, Func<long> cacheClear)
{
    /// <summary>The Jellyfin account and its titles.</summary>
    public JellyfinLibrary Jellyfin { get; } = jellyfin;

    public async Task<JsonNode?> HandleAsync(string op, JsonNode? args) => op switch
    {
        "rows" => new JsonArray([.. await Jellyfin.RowsAsync()]),
        "search" => await SearchAsync(Text(args, "term") ?? ""),
        "watch" => await WatchAsync(),
        "stop" => await StopAsync(),
        "cache-size" => new JsonObject { ["bytes"] = cacheSize() },
        "cache-clear" => new JsonObject { ["bytes"] = cacheClear() },
        _ => await Jellyfin.HandleAsync(op, args),
    };

    private static string? Text(JsonNode? args, string key) => args?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- Search

    private CancellationTokenSource? _searching;

    /// <summary>Films and shows on the server whose names match. Only the latest search is worth asking: one typed past is
    /// called off (null: the page drops its answer).</summary>
    private async Task<JsonNode?> SearchAsync(string term)
    {
        term = term.Trim();
        if (term.Length == 0) return new JsonArray();
        using var mine = new CancellationTokenSource();
        Interlocked.Exchange(ref _searching, mine)?.Cancel();
        try
        {
            var found = await Jellyfin.SearchAsync(term, mine.Token);
            return mine.IsCancellationRequested ? null : found;
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested) { return null; }
        finally { Interlocked.CompareExchange(ref _searching, null, mine); }
    }

    private async Task<JsonNode?> StopAsync()
    {
        await Jellyfin.CancelPlayAsync();
        player.Stop();
        return null;
    }

    // ---- Continue Watching, Next up, Airing soon, the Watchlist and History

    private const int HistoryShown = 300, ContinueShown = 24;

    /// <summary>
    /// The rooms and rows about what you watch. The server's: Continue Watching, Next up, Airing soon, its favorites (the
    /// Watchlist) and what was played on it (History). Exo Watch's from before 1.5: the Watchlist and History it kept, each
    /// by its name with no id, so the page draws the name and looks it up on the server when it's pressed. Where Exo Watch
    /// left a title is not shown: only the server knows where its own file was left.
    /// </summary>
    private async Task<JsonObject> WatchAsync()
    {
        List<SyncBatch.Item> watched, saved;
        var kept = ledger();
        lock (kept.Synced)
        {
            watched = [.. kept.Live("watched").OrderByDescending(WatchLedger.WatchedAt).Take(HistoryShown)];
            saved = [.. kept.Live("saved").OrderByDescending(i => i.Modified)];
        }
        var server = await Jellyfin.WatchAsync();
        var history = server.History
            .Concat(watched.Select(w => Kept(w.Id[..w.Id.LastIndexOf('@')], w.Body, WatchLedger.WatchedAt(w))))
            .OrderByDescending(e => (long?)e["at"] ?? 0).Take(HistoryShown);
        return new JsonObject
        {
            ["continue"] = new JsonArray([.. server.Continue.OrderByDescending(e => (long?)e["at"] ?? 0).Take(ContinueShown)]),
            ["nextUp"] = new JsonArray([.. server.NextUp]),
            ["airing"] = new JsonArray([.. server.Airing]),
            ["watchlist"] = new JsonArray([.. server.Favorites, .. saved.Select(s => (JsonNode)Kept(s.Id, s.Body, s.Modified))]),
            ["history"] = new JsonArray([.. history]),
            // Some of the server's rooms were still on their way: the page reads again shortly to fill them in.
            ["late"] = Jellyfin.WatchLate,
        };
    }

    /// <summary>One of Exo Watch's, as it was kept: its public id (only to tell entries apart), its name and when.</summary>
    private static JsonObject Kept(string pid, JsonNode? body, long at) =>
        new() { ["pid"] = pid, ["at"] = at, ["title"] = (string?)body?["title"] ?? "", ["source"] = (string?)body?["source"] ?? "" };
}
