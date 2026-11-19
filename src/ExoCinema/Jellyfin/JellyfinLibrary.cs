using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ExoCinema.Library;
using ExoCinema.Net;

namespace ExoCinema.Jellyfin;

/// <summary>
/// The signed-in Jellyfin server, which is Exo Cinema's library: its titles in the shapes the page draws, each by an id
/// starting <c>jf:</c>, and playback of their original files. The server is the record: where a title was left, whether
/// it was played and whether it's a favorite are read from it each time and written back to it, so every Jellyfin app on
/// the account sees the same. Reports where playback is at its start, every 30 s, on a seek, on pause and at its stop.
/// </summary>
internal sealed class JellyfinLibrary(IPlayerSurface player, AccountStore accounts, Func<GraphicsCard> card)
{
    private const string Prefix = "jf:";
    private const int RowSize = 24, RoomPage = 60, SearchSize = 20, Favorites = 200, Played = 100;

    internal const int ReportEveryMs = 30_000;

    /// <summary>Whether an id names one of the server's titles (<c>jf:</c> and its item id).</summary>
    internal static bool IsItem(string? id) => id?.StartsWith(Prefix, StringComparison.Ordinal) == true && id.Length > Prefix.Length;

    public async Task<JsonNode?> HandleAsync(string op, JsonNode? args)
    {
        var id = Text(args, "id") ?? "";
        switch (op)
        {
            case "jellyfin": return await AccountAsync();
            case "jellyfin-connect": return await ConnectAsync(Text(args, "server") ?? "", Text(args, "name") ?? "", Text(args, "password") ?? "");
            case "jellyfin-sign-out": await SignOutAsync(); return null;
        }
        var c = Client ?? throw new JellyfinException("Not signed in to Jellyfin.");
        // A title's call names one of the server's titles, or nothing is asked: an id kept from before 1.5 (another
        // source's) is not on the server.
        if (op is "meta" or "sources" or "play" or "next" or "played" or "watch-save" && !IsItem(id))
            throw new LibraryException("This isn't on your Jellyfin server.");
        return op switch
        {
            "catalog" => await CatalogAsync(c, Text(args, "type") ?? "", Text(args, "id") ?? "", (int?)args?["skip"] ?? 0),
            "meta" => await MetaAsync(c, Bare(id)),
            "sources" => await SourcesAsync(c, Bare(id)),
            "play" => await PlayAsync(c, id, (bool?)args?["resume"] ?? true, (int?)args?["source"]),
            "next" => await NextAsync(c, id),
            "played" => await PlayedAsync(c, id, (bool?)args?["on"] ?? true),
            "watch-save" => await SaveAsync(c, id, (bool?)args?["on"] ?? true),
            _ => throw new LibraryException($"Unknown call {op}."),
        };
    }

    private static string? Text(JsonNode? args, string key) => args?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Bare(string id) => id[Prefix.Length..];

    // ---- The account

    private bool _restored;
    private JellyfinClient? _client;
    private string _serverName = "";
    private string _userName = "";

    private JellyfinClient? Client
    {
        get
        {
            if (_restored) return _client;
            _restored = true;
            if (accounts.Current() is { } saved)
                Use(new JellyfinClient(saved.Account.Server, accounts.DeviceId, saved.Token, saved.Account.UserId), saved.Account.ServerName, saved.Account.UserName);
            return _client;
        }
    }

    private void Use(JellyfinClient? client, string serverName, string userName)
    {
        _client = client;
        _serverName = serverName;
        _userName = userName;
        Net.Egress.SignedInTo(client?.Server);
    }

    /// <summary>The server's address, for app.log to leave out.</summary>
    public string? Address => Client?.Server;

    /// <summary>The token, for app.log to leave out.</summary>
    public string? Token => Client?.Token;

    public bool SignedIn => Client is not null;

    private JsonObject Described(bool offline = false) => new()
    {
        ["server"] = _client!.Server,
        ["host"] = new Uri(_client.Server).Host,
        ["serverName"] = _serverName,
        ["userName"] = _userName,
        ["encrypted"] = _client.Server.StartsWith("https:", StringComparison.Ordinal) || new Uri(_client.Server).IsLoopback,
        ["offline"] = offline,
    };

    /// <summary>Who's signed in, asked of the server: a token it no longer takes ends the account here. A server that
    /// doesn't answer (twice: the read is tried again once) is said to be offline; a slow answer is not, it just takes
    /// as long as it takes (<see cref="AccountLimit"/> for each try). The page asks again on a backoff while it's offline.</summary>
    private async Task<JsonNode?> AccountAsync()
    {
        if (Client is not { } c) return null;
        try
        {
            if ((string?)(await c.MeAsync(limit: AccountLimit))["Name"] is { Length: > 0 } name) _userName = name;
            return Described();
        }
        catch (JellyfinException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            accounts.Forget();
            Use(null, "", "");
            return new JsonObject { ["expired"] = true };
        }
        catch (JellyfinException e)
        {
            Note("account", e);
            return Described(offline: true);
        }
    }
    /// <summary>How long one try at the account may take before it counts as failed. Well past a slow answer; the page
    /// shows "Checking…" meanwhile.</summary>
    private static readonly TimeSpan AccountLimit = TimeSpan.FromSeconds(10);
    /// <summary>Writes a failure to app.log once, as its kind and timings (never the address or the token).</summary>
    public void Note(string what, JellyfinException e)
    {
        if (e.Logged) return;
        e.Logged = true;
        Log?.Invoke($"jellyfin {what}: {e.Diagnosis}");
    }

    /// <summary>Connect: the address must be a Jellyfin server, it must take the name and password, and the session it hands
    /// back must then read the account. Only with all three is the account kept and called connected; the server's public
    /// answer alone says nothing about any of the rest. Each failure says which it was (JellyfinClient).</summary>
    private async Task<JsonNode?> ConnectAsync(string server, string name, string password)
    {
        if (name.Trim().Length == 0) throw new JellyfinException("Enter your username.");
        var (c, info) = await JellyfinClient.ConnectAsync(server, accounts.DeviceId);
        // Open to the address found for the rest of this call: signing in, the check after it, and leaving if that fails.
        Net.Egress.TryServer(c.Server);
        await c.SignInAsync(name.Trim(), password);
        JsonNode me;
        try
        {
            me = await c.MeAsync();
            if ((string?)me["Name"] is not { Length: > 0 }) throw new JellyfinException("The server took the sign-in but didn't say who you are.");
        }
        catch (JellyfinException e)
        {
            // Nothing kept, and no session left behind on the server.
            await c.SignOutAsync();
            throw e.Status is { } status ? new JellyfinException($"The server took the sign-in, then answered with an error ({(int)status}).", status) : e;
        }
        var serverName = (string?)info["ServerName"] ?? new Uri(c.Server).Host;
        var userName = (string)me["Name"]!;
        await CancelPlayAsync();
        accounts.Save(c.Server, serverName, c.UserId!, userName, c.Token!);
        _restored = true;
        Use(c, serverName, userName);
        return Described();
    }

    private async Task SignOutAsync()
    {
        await CancelPlayAsync();
        var c = Client;
        accounts.Forget();
        if (c is not null) await c.SignOutAsync();
        Use(null, "", "");
    }

    // ---- Home's rows and the rooms

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(4);

    /// <summary>Each film and show library's newest for Home, and all of it for Movies and Shows (<c>room</c>: not drawn on
    /// Home). A server that doesn't answer throws: signed in, it is the library, so the caller says so.</summary>
    public async Task<List<JsonObject>> RowsAsync()
    {
        if (Client is not { } c) return [];
        var views = await c.ViewsAsync();
        var rows = new List<JsonObject>();
        JsonObject Row(string type, string id, string title, bool room) => new()
        {
            ["type"] = type,
            ["id"] = id,
            ["title"] = title,
            ["room"] = room,
        };
        foreach (var view in (views["Items"] as JsonArray ?? []).OfType<JsonNode>())
        {
            var kind = (string?)view["CollectionType"];
            if (kind is not (null or "movies" or "tvshows" or "mixed") || (string?)view["Id"] is not { } id) continue;
            var name = (string?)view["Name"] ?? "";
            rows.Add(Row("latest", $"latest/{id}", $"New in {name}", room: false));
            if (kind != "tvshows") rows.Add(Row("movie", $"library/{id}", name, room: true));
            if (kind != "movies") rows.Add(Row("series", $"library/{id}", name, room: true));
        }
        return rows;
    }

    private async Task<JsonNode> CatalogAsync(JellyfinClient c, string type, string id, int skip)
    {
        var view = id[(id.IndexOf('/') + 1)..];
        var items = id.StartsWith("latest/", StringComparison.Ordinal)
            ? await c.LatestAsync(view, RowSize) as JsonArray
            : (await c.ItemsAsync(view, type == "series" ? "Series" : "Movie", skip, RoomPage))["Items"] as JsonArray;
        return new JsonArray([.. (items ?? []).OfType<JsonNode>().Select(i => (JsonNode)Card(c, i))]);
    }

    /// <summary>A film or show in a catalog's shape. A new episode stands for its show.</summary>
    private static JsonObject Card(JellyfinClient c, JsonNode item)
    {
        var episode = (string?)item["Type"] == "Episode" && (string?)item["SeriesId"] is not null;
        var card = new JsonObject
        {
            ["id"] = Prefix + (episode ? (string?)item["SeriesId"] : (string?)item["Id"]),
            ["type"] = episode ? "series" : Kind(item),
            ["name"] = episode ? (string?)item["SeriesName"] ?? "" : (string?)item["Name"] ?? "",
            ["poster"] = Poster(c, item),
            ["background"] = Backdrop(c, item),
            ["logo"] = Logo(c, item),
            ["description"] = episode ? null : (string?)item["Overview"],
            ["year"] = (int?)item["ProductionYear"] is { } year ? year.ToString(CultureInfo.InvariantCulture) : null,
            ["runtime"] = episode ? null : Runtime(item),
            ["imdbRating"] = item["CommunityRating"] is JsonValue r && r.TryGetValue<double>(out var rating) ? rating.ToString("0.0", CultureInfo.InvariantCulture) : null,
        };
        if (!episode && item["Genres"] is JsonArray genres) card["genres"] = genres.DeepClone();
        return card;
    }

    private static string Kind(JsonNode item) => (string?)item["Type"] == "Series" ? "series" : "movie";

    private static string? Runtime(JsonNode item)
    {
        if ((long?)item["RunTimeTicks"] is not { } ticks || ticks <= 0) return null;
        var minutes = (int)Math.Round(ticks / 6e8);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    private static string? Poster(JellyfinClient c, JsonNode item) =>
        (string?)item["Type"] == "Episode"
            ? c.Image((string?)item["SeriesId"], "Primary", (string?)item["SeriesPrimaryImageTag"])
            : c.Image((string?)item["Id"], "Primary", (string?)item["ImageTags"]?["Primary"]);

    private static string? Backdrop(JellyfinClient c, JsonNode item) =>
        c.Image((string?)item["Id"], "Backdrop", (string?)(item["BackdropImageTags"] as JsonArray)?.FirstOrDefault())
        ?? c.Image((string?)item["ParentBackdropItemId"], "Backdrop", (string?)(item["ParentBackdropImageTags"] as JsonArray)?.FirstOrDefault());

    private static string? Logo(JellyfinClient c, JsonNode item) =>
        c.Image((string?)item["Id"], "Logo", (string?)item["ImageTags"]?["Logo"])
        ?? c.Image((string?)item["ParentLogoItemId"], "Logo", (string?)item["ParentLogoImageTag"]);

    private static long? PlayedAt(JsonNode? data)
    {
        if ((bool?)data?["Played"] != true) return null;
        // Marked played without a day (1: played, no day to say).
        return LastPlayed(data) is { } at && at > 1 ? at : 1;
    }

    private static long? LastPlayed(JsonNode? data) =>
        DateTimeOffset.TryParse((string?)data?["LastPlayedDate"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : null;

    private static JsonObject? PositionOf(JsonNode item) =>
        (long?)item["UserData"]?["PlaybackPositionTicks"] is > 0 and var ticks
            ? new JsonObject { ["ms"] = ticks / 10_000, ["runtime"] = (long?)item["RunTimeTicks"] / 10_000, ["at"] = LastPlayed(item["UserData"]) }
            : null;

    // ---- Continue Watching, Next up, the Watchlist, History and Airing soon

    /// <summary>Continue Watching, Next Up, favorites (the Watchlist), what was played (History) and Airing soon. A slow or
    /// missing server gives fewer of them, never a longer wait for the rest.</summary>
    public async Task<(List<JsonObject> Continue, List<JsonObject> NextUp, List<JsonObject> Favorites, List<JsonObject> Airing, List<JsonObject> History)> WatchAsync()
    {
        if (Client is not { } c) { WatchLate = false; return ([], [], [], [], []); }
        async Task<List<JsonObject>> Part(Func<Task<JsonNode>> ask, string what)
        {
            try { return [.. ((await ask())["Items"] as JsonArray ?? []).OfType<JsonNode>().Select(i => Entry(c, i))]; }
            catch (JellyfinException e)
            {
                Note(what, e);
                return [];
            }
        }
        async Task<List<JsonObject>> Upcoming(Func<Task<JsonNode>> ask)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var answer = await ask();
                var shown = AiringRow(c, answer, DateTimeOffset.UtcNow, TimeZoneInfo.Local);
                Log?.Invoke($"jellyfin airing: {(answer["Items"] as JsonArray)?.Count ?? 0} upcoming, {shown.Count} shown, {(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms");
                return shown;
            }
            catch (JellyfinException e)
            {
                Note("airing", e);
                return [];
            }
        }
        // A part still on its way when the wait is over is not dropped: the next read of Exo Watch takes the same
        // request (for a while after it started) and fills its row in, so no part waits on another.
        var going = Ask("continue", () => Part(() => c.ResumeAsync(RowSize), "continue"));
        var next = Ask("next-up", () => Part(() => c.NextUpAsync(RowSize), "next up"));
        var saved = Ask("favorites", () => Part(() => c.FavoritesAsync(Favorites), "favorites"));
        var airing = Ask("airing", () => Upcoming(() => c.UpcomingAsync(RowSize)));
        // History: what was played on the server, the last played first. One without a day it was played has no place
        // in a list by day, so it is left out.
        var played = Ask("played", async () => (await Part(() => c.PlayedAsync(Played), "history")).Where(e => (long?)e["at"] > 0).ToList());
        var all = Task.WhenAll(going, next, saved, airing, played);
        await Task.WhenAny(all, Task.Delay(Patience));
        WatchLate = !all.IsCompleted;
        if (!airing.IsCompleted) Log?.Invoke($"jellyfin airing: not in after {Patience.TotalMilliseconds:0} ms, shown on the next read");
        // A part's entries are copies: the same entries may go out in a read that comes after this one.
        static List<JsonObject> Done(Task<List<JsonObject>> t) => t.IsCompletedSuccessfully ? [.. t.Result.Select(o => (JsonObject)o.DeepClone())] : [];
        return (Done(going), Done(next), Done(saved), Done(airing), Done(played));
    }
    /// <summary>The parts still asked for: a read within this long of the ask gets the same answer, so a part that
    /// was late is picked up without asking again.</summary>
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, (Task<List<JsonObject>> Task, DateTime At)> _asks = [];
    private Task<List<JsonObject>> Ask(string key, Func<Task<List<JsonObject>>> start)
    {
        lock (_asks)
        {
            if (_asks.TryGetValue(key, out var held) && DateTime.UtcNow - held.At < AskFor) return held.Task;
            var task = start();
            _asks[key] = (task, DateTime.UtcNow);
            return task;
        }
    }
    /// <summary>Whether the last Exo Watch read left a part of Jellyfin's rooms still on its way (the page reads again
    /// shortly, to fill it in).</summary>
    public bool WatchLate { get; private set; }
    /// <summary>
    /// Airing soon: the server's upcoming episodes, each as a card for its show's page (never a play: an episode that
    /// hasn't aired has no file to play). Its day in this PC's time: Today, Tomorrow, Yesterday, the weekday within the
    /// week, else the date; and its local time, unless the server holds only the day.
    /// </summary>
    internal static List<JsonObject> AiringRow(JellyfinClient c, JsonNode? upcoming, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var rows = new List<JsonObject>();
        foreach (var item in (upcoming?["Items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)item["Id"] is not { Length: > 0 } id || (string?)item["SeriesId"] is not { Length: > 0 } series) continue;
            if (!DateTimeOffset.TryParse((string?)item["PremiereDate"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var air)) continue;
            var (day, time) = AirDay(air, zone);
            var background = Backdrop(c, item);
            rows.Add(new JsonObject
            {
                ["pid"] = Prefix + id,
                ["id"] = Prefix + id,
                ["showId"] = Prefix + series,
                ["type"] = "series",
                ["title"] = (string?)item["SeriesName"] ?? (string?)item["Name"] ?? "",
                ["source"] = "jellyfin",
                // Null when the server doesn't say (not 0, which is a real season: specials), so the card shows only what's known.
                ["season"] = (int?)item["ParentIndexNumber"],
                ["episode"] = (int?)item["IndexNumber"],
                ["airDay"] = AirWords(day, today),
                ["airTime"] = time,
                ["background"] = background,
                ["thumbnail"] = c.Image(id, "Primary", (string?)item["ImageTags"]?["Primary"]) ?? background,
            });
        }
        return rows;
    }
    /// <summary>When an episode airs, as its day and local time. Midnight UTC is the server's way of saying only the day
    /// (no time it knows), so that's the calendar day it names, with no time.</summary>
    internal static (DateOnly Day, string? Time) AirDay(DateTimeOffset air, TimeZoneInfo zone)
    {
        if (air.UtcDateTime.TimeOfDay == TimeSpan.Zero) return (DateOnly.FromDateTime(air.UtcDateTime), null);
        var local = TimeZoneInfo.ConvertTime(air, zone).DateTime;
        return (DateOnly.FromDateTime(local), local.ToString("HH:mm", CultureInfo.InvariantCulture));
    }
    /// <summary>A day as the card says it, from today: Today, Tomorrow, Yesterday, the weekday for the next six days, else
    /// the date ("Nov 12").</summary>
    internal static string AirWords(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => "Today",
        1 => "Tomorrow",
        -1 => "Yesterday",
        > 1 and <= 6 => DateTimeFormatInfo.InvariantInfo.GetAbbreviatedDayName(day.DayOfWeek),
        _ => day.ToString("MMM d", CultureInfo.InvariantCulture),
    };

    private static JsonObject Entry(JellyfinClient c, JsonNode item)
    {
        var episode = (string?)item["Type"] == "Episode";
        var (season, number) = ((int?)item["ParentIndexNumber"] ?? 0, (int?)item["IndexNumber"] ?? 0);
        var id = Prefix + (string?)item["Id"];
        var background = Backdrop(c, item);
        var entry = new JsonObject
        {
            ["pid"] = id,
            ["id"] = id,
            ["at"] = LastPlayed(item["UserData"]) ?? 0,
            ["title"] = episode ? $"{(string?)item["SeriesName"]} · S{season}:E{number} {(string?)item["Name"]}".TrimEnd() : (string?)item["Name"] ?? "",
            ["source"] = "jellyfin",
            ["type"] = episode ? "series" : Kind(item),
            ["poster"] = Poster(c, item),
            ["background"] = background,
            ["logo"] = Logo(c, item),
            ["position"] = PositionOf(item),
        };
        if (episode)
        {
            entry["showId"] = Prefix + (string?)item["SeriesId"];
            entry["season"] = season;
            entry["episode"] = number;
            entry["thumbnail"] = c.Image((string?)item["Id"], "Primary", (string?)item["ImageTags"]?["Primary"]) ?? background;
        }
        return entry;
    }

    // ---- A title's page, and search

    private static async Task<JsonNode> MetaAsync(JellyfinClient c, string id)
    {
        var item = await c.ItemAsync(id);
        var page = Card(c, item);
        page["saved"] = (bool?)item["UserData"]?["IsFavorite"] == true;
        page["watchedAt"] = PlayedAt(item["UserData"]);
        page["position"] = PositionOf(item);
        if ((string?)item["Type"] == "Series")
        {
            var background = (string?)page["background"];
            var episodes = ((await c.EpisodesAsync(id))["Items"] as JsonArray ?? []).OfType<JsonNode>();
            page["videos"] = new JsonArray([.. episodes.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = Prefix + (string?)e["Id"],
                ["season"] = (int?)e["ParentIndexNumber"] ?? 0,
                ["episode"] = (int?)e["IndexNumber"] ?? 0,
                ["name"] = (string?)e["Name"],
                ["overview"] = (string?)e["Overview"],
                ["thumbnail"] = c.Image((string?)e["Id"], "Primary", (string?)e["ImageTags"]?["Primary"]) ?? background,
                ["released"] = (string?)e["PremiereDate"],
                ["watchedAt"] = PlayedAt(e["UserData"]),
                ["position"] = PositionOf(e),
            })]);
        }
        return page;
    }

    /// <summary>Films and shows on the server whose names match, as a catalog's.</summary>
    public async Task<JsonArray> SearchAsync(string term, CancellationToken ct)
    {
        if (Client is not { } c) return [];
        var key = term.Trim().ToLowerInvariant();
        lock (_searches)
        {
            if (!ReferenceEquals(_searchesOf, c)) { _searches.Clear(); _searchesOf = c; }
            if (_searches.TryGetValue(key, out var kept) && Environment.TickCount64 - kept.At < SearchKeptMs)
                return (JsonArray)kept.Cards.DeepClone();
        }
        var started = Environment.TickCount64;
        JsonArray cards;
        try
        {
            // The quick search first; a server without it (or whose quick search fails) is asked the full way.
            try { cards = new JsonArray([.. HintItems(await c.SearchHintsAsync(term, SearchSize, ct)).Select(i => (JsonNode)Card(c, i))]); }
            catch (JellyfinException) when (!ct.IsCancellationRequested)
            {
                cards = new JsonArray([.. ((await c.SearchAsync(term, SearchSize, ct))["Items"] as JsonArray ?? []).OfType<JsonNode>().Select(i => (JsonNode)Card(c, i))]);
            }
        }
        catch (JellyfinException e)
        {
            Note("search", e);
            return [];
        }
        Log?.Invoke($"search: {cards.Count} from the server in {Environment.TickCount64 - started} ms");
        lock (_searches)
        {
            if (_searches.Count >= 64) _searches.Clear();
            _searches[key] = ((JsonArray)cards.DeepClone(), Environment.TickCount64);
        }
        return cards;
    }

    // A term's answer is kept for a few minutes: typing back to it, or opening the search page for it, is immediate.
    private const long SearchKeptMs = 3 * 60_000;
    private readonly Dictionary<string, (JsonArray Cards, long At)> _searches = [];
    private JellyfinClient? _searchesOf;

    /// <summary>The quick search's matches in an item's shape (Id, Name, Type, ProductionYear, ImageTags,
    /// BackdropImageTags), which is all a card in a search list shows.</summary>
    internal static IEnumerable<JsonNode> HintItems(JsonNode answer)
    {
        foreach (var hint in (answer["SearchHints"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (((string?)hint["Id"] ?? (string?)hint["ItemId"]) is not { Length: > 0 } id) continue;
            if ((string?)hint["Type"] is not ("Movie" or "Series")) continue;
            var item = new JsonObject
            {
                ["Id"] = id,
                ["Name"] = (string?)hint["Name"] ?? "",
                ["Type"] = (string?)hint["Type"],
                ["ProductionYear"] = hint["ProductionYear"]?.DeepClone(),
            };
            if ((string?)hint["PrimaryImageTag"] is { Length: > 0 } primary) item["ImageTags"] = new JsonObject { ["Primary"] = primary };
            if ((string?)hint["BackdropImageTag"] is { Length: > 0 } backdrop) item["BackdropImageTags"] = new JsonArray(backdrop);
            yield return item;
        }
    }

    private static async Task<JsonNode> PlayedAsync(JellyfinClient c, string id, bool on)
    {
        await c.SetPlayedAsync(Bare(id), on);
        return new JsonObject { ["watched"] = on };
    }

    private static async Task<JsonNode> SaveAsync(JellyfinClient c, string id, bool on)
    {
        await c.SetFavoriteAsync(Bare(id), on);
        return new JsonObject { ["saved"] = on };
    }

    // ---- Where it plays from

    // The link's measured speed (LinkSpeed): none measured keeps the order as it was.
    private List<JsonNode> Ranked(JsonNode info) => [.. SourceRank.Rank([.. (info["MediaSources"] as JsonArray ?? []).OfType<JsonNode>()], card(), LinkSpeed.Fresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))];

    /// <summary>An item's versions, best for this PC first, as the source sheet lists them; the sheet's numbers mean the
    /// same at Play.</summary>
    private async Task<JsonNode> SourcesAsync(JellyfinClient c, string id)
    {
        var ranked = Ranked(await c.PlaybackInfoAsync(id, 0));
        return new JsonArray([.. ranked.Select((s, i) =>
        {
            var streams = (s["MediaStreams"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            var video = streams.FirstOrDefault(m => (string?)m["Type"] == "Video");
            var audio = streams.FirstOrDefault(m => (string?)m["Type"] == "Audio" && (bool?)m["IsDefault"] == true) ?? streams.FirstOrDefault(m => (string?)m["Type"] == "Audio");
            return (JsonNode)new JsonObject
            {
                ["id"] = i,
                ["name"] = (string?)s["Name"],
                ["best"] = i == 0,
                ["size"] = (long?)s["Size"],
                ["bitrate"] = (long?)s["Bitrate"],
                ["height"] = (int?)video?["Height"],
                ["videoCodec"] = (string?)video?["Codec"],
                ["range"] = (string?)video?["VideoRangeType"],
                ["audioCodec"] = (string?)audio?["Codec"],
                ["channels"] = (int?)audio?["Channels"],
            };
        })]);
    }

    // ---- Playing

    private sealed class Playback
    {
        public required string ItemId;
        public required string MediaSourceId;
        public required string? PlaySessionId;
        public required JellyfinClient Client;
        public double Position;
        public bool Paused;
        public bool Started;
        /// <summary>Set when Play chose this version (not the sheet's pick): the versions it can move on to.</summary>
        public VersionFallback? Fallback;
    }

    /// <summary>The versions a Play chose from, ranked as the sheet lists them, and where the play is among them.</summary>
    private sealed class VersionFallback
    {
        public required JellyfinClient Client;
        public required string Bare;
        public required List<JsonNode> Versions;
        public required int Index;
        /// <summary>Versions asked for so far, the first included.</summary>
        public required int Tried;
        public required double Start;
        public required string Title;
        public required string? PlaySessionId;
        /// <summary>A version is being looked up for the next file: a second failure meanwhile is that one's, not another.</summary>
        public bool Busy;
    }

    private Playback? _now;
    private Timer? _progressTimer;
    private int _playGeneration;
    private List<(string Url, string Title, string Lang)> _pendingSubtitles = [];

    /// <summary>Most versions one Play asks for before it says so.</summary>
    internal const int VersionTries = 5;

    /// <summary>A play none of whose versions would open, with what to say: the failed version's name and the player's
    /// reason when there was just one, else the player's reason.</summary>
    public event Action<string>? PlayFailed;

    /// <summary>The player couldn't open the file before its first frame (why: its reason). A version Play chose moves on
    /// to the next one, from the same place, up to <see cref="VersionTries"/>; a picked one, or a play that has shown a
    /// frame, doesn't. True when this library takes the failure: the page then hears of it only through PlayFailed.</summary>
    public bool TryNextVersion(string why)
    {
        if (_now is not { Started: false, Fallback: { } f } p) return false;
        if (f.Busy) return true;
        f.Busy = true;
        _ = OpenNextAsync(p, f, why, _playGeneration);
        return true;
    }

    private async Task OpenNextAsync(Playback p, VersionFallback f, string why, int generation)
    {
        var reason = why;
        while (f.Tried < VersionTries && f.Index + 1 < f.Versions.Count)
        {
            var index = ++f.Index;
            f.Tried++;
            var source = f.Versions[index];
            var (url, notice) = await ResolveAsync(f.Client, f.Bare, source);
            // A newer Play (or Back) since the failure: this one is no longer wanted.
            if (generation != _playGeneration) return;
            if (notice is { } refused)
            {
                reason = StreamProbe.Plain(refused);
                Log?.Invoke($"play: version {index + 1} of {f.Versions.Count} refused, the next one is asked for");
                continue;
            }
            Log?.Invoke($"play: version {index + 1} of {f.Versions.Count} asked for after the one before wouldn't open");
            f.Busy = false;
            _pendingSubtitles = SubtitlesOf(f.Client, source);
            _now = new Playback
            {
                ItemId = p.ItemId,
                MediaSourceId = (string?)source["Id"] ?? f.Bare,
                PlaySessionId = f.PlaySessionId,
                Client = f.Client,
                Position = f.Start,
                Fallback = f,
            };
            // The token goes only to this server, never to another address a version points at.
            player.Play(url, f.Client.IsOwnUrl(url) ? f.Client.AuthHeader : null, f.Start, f.Title);
            return;
        }
        if (generation != _playGeneration) return;
        await CancelPlayAsync();
        PlayFailed?.Invoke(f.Versions.Count == 1 ? $"{(string?)f.Versions[0]["Name"] ?? ""}; {why}" : reason);
    }

    /// <summary>The address a version opens at, and whether a link at another address was refused (its notice).</summary>
    private static async Task<(string Url, string? Notice)> ResolveAsync(JellyfinClient c, string bare, JsonNode source)
    {
        var url = c.StreamUrl(bare, source);
        // A refused remote link plays back as a short notice video, which mpv would treat as the show. Peek first, at
        // the server's own address too: a server that keeps its files elsewhere sends the player on from there.
        var own = c.IsOwnUrl(url);
        var probe = await StreamProbe.RemoteAsync(url, own: own ? (c.IsOwnUrl, c.AuthHeader) : null);
        if (probe.Notice is { } notice)
        {
            // The server asked for this link, so its host made it for the server's connection, not this PC's (a server
            // that gets its films from a stream service does this for every version). The server can still open it: the
            // version is played through the server, when the server passes it on rather than sending the player back.
            if (!own && StreamProbe.TiedToAnotherConnection(notice))
            {
                var through = c.ServerStreamUrl(bare, source);
                var again = await StreamProbe.RemoteAsync(through, own: (c.IsOwnUrl, c.AuthHeader));
                if (again.Notice is null)
                {
                    AppLog.Shared.Write("play: the link was made for the server's connection; played through the server");
                    return (through, null);
                }
            }
            return (url, notice);
        }
        // The server's own address is opened as it is, with the sign-in it needs.
        if (own) return (url, null);
        // The player opens where the link leads, not the redirect, which it would otherwise ask again per request.
        return (probe.Target ?? url, null);
    }

    /// <summary>Subtitle files beside the video (an .srt or .ass the server holds), added once mpv has opened it.</summary>
    private static List<(string Url, string Title, string Lang)> SubtitlesOf(JellyfinClient c, JsonNode source) =>
        [.. (source["MediaStreams"] as JsonArray ?? []).OfType<JsonNode>()
            .Where(s => (string?)s["Type"] == "Subtitle" && (bool?)s["IsExternal"] == true && (string?)s["DeliveryUrl"] is { Length: > 0 })
            .Select(s => (c.Server + (string)s["DeliveryUrl"]!, (string?)s["DisplayTitle"] ?? (string?)s["Title"] ?? "", (string?)s["Language"] ?? ""))];

    /// <summary>Plays the original file of the version picked in the source sheet, else the best for this PC, from where
    /// the server says it was left.</summary>
    private async Task<JsonNode?> PlayAsync(JellyfinClient c, string id, bool resume, int? picked)
    {
        // Stop (Back) during the lookups below cancels this play: it must not start behind the library.
        await CancelPlayAsync();
        var generation = _playGeneration;
        var asked = Environment.TickCount64;
        var bare = Bare(id);
        var item = await c.ItemAsync(bare);
        var episode = (string?)item["Type"] == "Episode";
        var startTicks = resume ? (long?)item["UserData"]?["PlaybackPositionTicks"] ?? 0 : 0;
        // The series (an episode's name and picture) is asked for while the server answers for the file, not after it.
        var series = episode && (string?)item["SeriesId"] is { } seriesId ? c.ItemAsync(seriesId) : null;
        var infoAt = Environment.TickCount64;
        var info = await c.PlaybackInfoAsync(bare, startTicks);
        var infoMs = Environment.TickCount64 - infoAt;
        var show = series is null ? null : await series;
        Log?.Invoke($"play: the item and the file's info in {Environment.TickCount64 - asked} ms (the file's info {infoMs} ms)");
        var sources = Ranked(info);
        if (sources.Count == 0) throw new JellyfinException("The server has no file for this item.");
        // A version Play chose is passed over for the next when a remote link is refused; the sheet's pick is never moved.
        var chosen = picked is { } p && p >= 0 && p < sources.Count ? p : -1;
        var index = Math.Max(chosen, 0);
        var tried = 0;
        string url;
        while (true)
        {
            var (opened, notice) = await ResolveAsync(c, bare, sources[index]);
            tried++;
            if (notice is null) { url = opened; break; }
            if (chosen >= 0 || index + 1 >= sources.Count || tried >= VersionTries)
            {
                // Every version asked for was refused: reported, so the cause can be found (how many versions, at which
                // hop, and whether it was about the connection are in the log lines; no address is).
                Log?.Invoke($"play: {tried} of {sources.Count} versions refused{(StreamProbe.TiedToAnotherConnection(notice) ? ", each made for another connection" : "")}");
                CrashReport.Keep("PlaybackFailed", $"PlaybackFailed: {tried} of {sources.Count} versions were refused by their host{(StreamProbe.TiedToAnotherConnection(notice) ? " (made for another connection)" : "")}",
                    AppLog.Shared.Tail(200).Split('\n'), AppInfo.Data, sendNow: true);
                throw new JellyfinException(StreamProbe.Plain(notice));
            }
            Log?.Invoke($"play: version {index + 1} of {sources.Count} refused, the next one is asked for");
            index++;
        }
        if (generation != _playGeneration) return null;
        var source = sources[index];
        var start = startTicks / 1e7;
        var name = (string?)item["Name"] ?? "";
        var (season, number) = ((int?)item["ParentIndexNumber"] ?? 0, (int?)item["IndexNumber"] ?? 0);
        var showName = episode ? (string?)item["SeriesName"] ?? (string?)show?["Name"] ?? "" : null;
        var title = episode ? $"{showName} · S{season}:E{number} {name}".TrimEnd() : name;
        var sessionId = (string?)info["PlaySessionId"];
        _now = new Playback
        {
            ItemId = bare,
            MediaSourceId = (string?)source["Id"] ?? bare,
            PlaySessionId = sessionId,
            Client = c,
            Position = start,
            Fallback = chosen < 0
                ? new VersionFallback { Client = c, Bare = bare, Versions = sources, Index = index, Tried = tried, Start = start, Title = title, PlaySessionId = sessionId }
                : null,
        };
        _pendingSubtitles = SubtitlesOf(c, source);
        // The token goes only to this server, never to another address a version points at.
        player.Play(url, c.IsOwnUrl(url) ? c.AuthHeader : null, start, title);
        var whole = show ?? item;
        return new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["id"] = id,
                ["type"] = episode ? "series" : Kind(item),
                ["name"] = name,
                ["showName"] = showName,
                ["showId"] = episode ? Prefix + (string?)item["SeriesId"] : null,
                ["season"] = episode ? season : null,
                ["episode"] = episode ? number : null,
                ["genres"] = whole["Genres"]?.DeepClone(),
                ["country"] = (string?)(whole["ProductionLocations"] as JsonArray)?.FirstOrDefault(),
                ["poster"] = Poster(c, item),
                ["background"] = Backdrop(c, item),
                ["logo"] = Logo(c, item),
                ["thumbnail"] = episode ? c.Image(bare, "Primary", (string?)item["ImageTags"]?["Primary"]) : null,
            },
            ["start"] = start,
        };
    }

    /// <summary>The episode after this one, or null for a film or a show's last on the server.</summary>
    private static async Task<JsonNode?> NextAsync(JellyfinClient c, string id)
    {
        var item = await c.ItemAsync(Bare(id));
        if ((string?)item["Type"] != "Episode" || (string?)item["SeriesId"] is not { } series) return null;
        var list = (await c.EpisodesFromAsync(series, Bare(id), 2))["Items"] as JsonArray;
        if (list is not { Count: > 1 } || list[1] is not { } next) return null;
        return new JsonObject
        {
            ["id"] = Prefix + (string?)next["Id"],
            ["type"] = "series",
            ["name"] = (string?)next["Name"],
            ["season"] = (int?)next["ParentIndexNumber"] ?? 0,
            ["episode"] = (int?)next["IndexNumber"] ?? 0,
            ["thumbnail"] = c.Image((string?)next["Id"], "Primary", (string?)next["ImageTags"]?["Primary"]),
            ["showName"] = (string?)next["SeriesName"],
            ["showId"] = Prefix + series,
        };
    }

    /// <summary>The current file's subtitle files, once: the player adds them when the file has opened.</summary>
    public List<(string Url, string Title, string Lang)> TakePendingSubtitles()
    {
        var subs = _pendingSubtitles;
        _pendingSubtitles = [];
        return subs;
    }

    /// <summary>A play still on its way called off, and one under way reported stopped.</summary>
    public async Task CancelPlayAsync()
    {
        _playGeneration++;
        var p = _now;
        _now = null;
        _progressTimer?.Dispose();
        _progressTimer = null;
        if (p is { Started: true }) await SafeAsync(p.Client.ReportStoppedAsync(Report(p)));
    }

    // From the player. These arrive on the UI thread.

    public void OnPlaybackRestart()
    {
        if (_now is not { } p) return;
        if (p.Started)
        {
            _ = SafeAsync(p.Client.ReportProgressAsync(Report(p)));
            return;
        }
        p.Started = true;
        _ = SafeAsync(p.Client.ReportStartAsync(Report(p)));
        _progressTimer = new Timer(_ => { if (_now == p) _ = SafeAsync(p.Client.ReportProgressAsync(Report(p))); }, null, ReportEveryMs, ReportEveryMs);
    }

    public void OnTime(double seconds)
    {
        if (_now is { } p) p.Position = seconds;
    }

    public void OnPause(bool paused)
    {
        if (_now is not { } p || p.Paused == paused) return;
        p.Paused = paused;
        if (p.Started) _ = SafeAsync(p.Client.ReportProgressAsync(Report(p)));
    }

    public void OnEnded() => _ = CancelPlayAsync();

    private static JsonObject Report(Playback p) => new()
    {
        ["ItemId"] = p.ItemId,
        ["MediaSourceId"] = p.MediaSourceId,
        ["PlaySessionId"] = p.PlaySessionId,
        ["PositionTicks"] = (long)(p.Position * 1e7),
        ["IsPaused"] = p.Paused,
        ["IsMuted"] = false,
        ["PlayMethod"] = "DirectPlay",
        ["CanSeek"] = true,
    };

    private async Task SafeAsync(Task report)
    {
        try { await report; }
        catch (JellyfinException e) { Note("report", e); }
        catch (OperationCanceledException) { }
    }

    public event Action<string>? Log;
}
