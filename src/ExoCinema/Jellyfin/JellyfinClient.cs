using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoCinema.Library;

namespace ExoCinema.Jellyfin;

/// <summary>
/// A Jellyfin server (10.9 or later, the routes 12 still has), signed in as one user. Every request goes through Egress
/// as this server's, and none follows a redirect by itself, so neither the password nor the token can be sent on to
/// somewhere else. Items come back as Jellyfin's own JSON.
/// </summary>
internal sealed class JellyfinClient
{
    public const string ClientName = "Exo Cinema";

    // What lists leave out unless asked; everything else a card draws comes anyway. ItemFields names these two (12's
    // API refuses to name the rest of what the old list asked for).
    private const string Fields = "Overview,Genres";

    private static readonly HttpClient Http = new(Net.Egress.Checkpoint(Net.Egress.Purpose.Server, new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }))
    { Timeout = TimeSpan.FromSeconds(20) };

    public string Server { get; }
    public string DeviceId { get; }
    public string? Token { get; private set; }
    public string? UserId { get; private set; }

    public JellyfinClient(string server, string deviceId, string? token = null, string? userId = null)
    {
        Server = server;
        DeviceId = deviceId;
        Token = token;
        UserId = userId;
    }

    /// <summary>The server at the address typed, checked to be Jellyfin, with what it says of itself. An address that only
    /// sends on (http to https, a path) is followed, never from https down to http. Typed without its start it's tried
    /// over https; over http as well only at home (a server at "192.168.1.20:8096"), so nobody on the way can talk a
    /// public address down to plain http and read the password.</summary>
    public static async Task<(JellyfinClient Client, JsonNode Info)> ConnectAsync(string typed, string deviceId, CancellationToken ct = default)
    {
        var server = Normalize(typed) ?? throw new JellyfinException("That isn't a server's address.");
        try { return await ProbeAsync(server, deviceId, ct); }
        catch (JellyfinException) when (!typed.Contains("://", StringComparison.Ordinal) && AtHome(new Uri(server)))
        {
            return await ProbeAsync("http://" + server["https://".Length..], deviceId, ct);
        }
    }

    /// <summary>The address as typed (with or without https:// or /web), as the server's base address; null when it isn't one.</summary>
    internal static string? Normalize(string typed)
    {
        var url = typed.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Host.Length == 0) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    private static async Task<(JellyfinClient, JsonNode)> ProbeAsync(string server, string deviceId, CancellationToken ct)
    {
        const string route = "/System/Info/Public";
        for (var hop = 0; ; hop++)
        {
            var at = new JellyfinClient(server, deviceId);
            Net.Egress.TryServer(server);
            using var req = new HttpRequestMessage(HttpMethod.Get, server + route);
            using var res = await at.SendRawAsync(req, ct);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } to && hop < 3)
            {
                var next = new Uri(new Uri(server + "/"), to);
                if (!next.AbsolutePath.EndsWith(route, StringComparison.OrdinalIgnoreCase) || next.Scheme == "http" && server.StartsWith("https:", StringComparison.Ordinal))
                    throw NotJellyfin();
                server = next.GetLeftPart(UriPartial.Path)[..^route.Length];
                continue;
            }
            if ((int)res.StatusCode >= 500) throw ServerError(res.StatusCode);
            JsonNode? info = null;
            if (res.IsSuccessStatusCode)
                try { info = await JsonNode.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct); } catch (JsonException) { }
            if ((string?)info?["Id"] is not { Length: > 0 } || (string?)info["Version"] is not { Length: > 0 }
                || (string?)info["ProductName"] is not { } product || !product.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase))
                throw NotJellyfin();
            return (at, info);
        }
    }

    private static JellyfinException NotJellyfin() => new("That address isn't a Jellyfin server.", kind: "not Jellyfin");

    private static JellyfinException ServerError(HttpStatusCode status) => new($"The server answered with an error ({(int)status}).", status);

    // This PC, its network or a name only that network knows.
    internal static bool AtHome(Uri server)
    {
        if (server.IsLoopback) return true;
        if (IPAddress.TryParse(server.Host.Trim('[', ']'), out var ip))
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal
                || b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and < 32 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254);
        }
        return !server.Host.Contains('.') || server.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || server.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Jellyfin's own header: who the client is, and the token once signed in. The server lists the client among
    /// its devices by these.</summary>
    public string AuthHeader =>
        $"MediaBrowser Client=\"{ClientName}\", Device=\"{Environment.MachineName.Replace("\"", "")}\", DeviceId=\"{DeviceId}\", Version=\"{AppInfo.Version}\""
        + (Token is null ? "" : $", Token=\"{Token}\"");

    public async Task<JsonNode> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        var auth = await SendAsync(HttpMethod.Post, "/Users/AuthenticateByName", new JsonObject { ["Username"] = username, ["Pw"] = password }, ct);
        Token = (string?)auth["AccessToken"];
        UserId = (string?)auth["User"]?["Id"];
        if (Token is null || UserId is null) throw NotJellyfin();
        return auth;
    }

    /// <summary>Ends this PC's session on the server; signed out here whatever it answers.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { await SendAsync(HttpMethod.Post, "/Sessions/Logout", null, ct); } catch (JellyfinException) { }
        Token = null;
        UserId = null;
    }

    /// <summary>Who's signed in. <paramref name="limit"/> is how long one try may take (the default is the client's own).</summary>
    public Task<JsonNode> MeAsync(CancellationToken ct = default, TimeSpan? limit = null) => GetAsync("/Users/Me", ct, limit);

    public Task<JsonNode> ViewsAsync(CancellationToken ct = default) => GetAsync($"/UserViews?userId={UserId}", ct);

    public Task<JsonNode> ResumeAsync(int limit, CancellationToken ct = default) =>
        GetAsync($"/UserItems/Resume?userId={UserId}&limit={limit}&mediaTypes=Video&fields={Fields}", ct);

    public Task<JsonNode> NextUpAsync(int limit, CancellationToken ct = default) =>
        GetAsync($"/Shows/NextUp?userId={UserId}&limit={limit}&fields={Fields}&enableResumable=false&enableRewatching=false", ct);

    /// <summary>The episodes on the server airing from yesterday on, in air order (Jellyfin's own Upcoming): an episode
    /// that isn't out yet is listed too, so it's one the server has been told of.</summary>
    public Task<JsonNode> UpcomingAsync(int limit, CancellationToken ct = default) =>
        GetAsync($"/Shows/Upcoming?userId={UserId}&limit={limit}&enableImages=true&fields={Fields}", ct);
    /// <summary>A library's newest, as a plain list (unlike the others' Items); a show's new episodes count as the show.</summary>
    public Task<JsonNode> LatestAsync(string parentId, int limit, CancellationToken ct = default) =>
        GetAsync($"/Items/Latest?userId={UserId}&parentId={Id(parentId)}&limit={limit}&fields={Fields}&groupItems=true", ct);

    public Task<JsonNode> ItemsAsync(string parentId, string types, int start, int limit, CancellationToken ct = default) =>
        GetAsync($"/Items?userId={UserId}&parentId={Id(parentId)}&includeItemTypes={types}&recursive=true&sortBy=SortName&sortOrder=Ascending&startIndex={start}&limit={limit}&fields={Fields}&enableTotalRecordCount=false", ct);

    public Task<JsonNode> FavoritesAsync(int limit, CancellationToken ct = default) =>
        GetAsync($"/Items?userId={UserId}&isFavorite=true&includeItemTypes=Movie,Series&recursive=true&sortBy=SortName&sortOrder=Ascending&limit={limit}&fields={Fields}&enableTotalRecordCount=false", ct);

    /// <summary>The films and episodes played on this account, the last played first (History).</summary>
    public Task<JsonNode> PlayedAsync(int limit, CancellationToken ct = default) =>
        GetAsync($"/Items?userId={UserId}&isPlayed=true&includeItemTypes=Movie,Episode&recursive=true&sortBy=DatePlayed&sortOrder=Descending&limit={limit}&fields={Fields}&enableTotalRecordCount=false", ct);

    /// <summary>The server's quick search: each match's Id, Name, Type, ProductionYear and picture tags ("SearchHints"),
    /// without the rest of the item. On a large or remote library it answers several times sooner than the item search.</summary>
    public Task<JsonNode> SearchHintsAsync(string term, int limit, CancellationToken ct = default) =>
        GetAsync($"/Search/Hints?userId={UserId}&searchTerm={Uri.EscapeDataString(term)}&includeItemTypes=Movie,Series&limit={limit}", ct);

    public Task<JsonNode> SearchAsync(string term, int limit, CancellationToken ct = default) =>
        GetAsync($"/Items?userId={UserId}&searchTerm={Uri.EscapeDataString(term)}&includeItemTypes=Movie,Series&recursive=true&limit={limit}&enableTotalRecordCount=false", ct);

    public Task<JsonNode> ItemAsync(string id, CancellationToken ct = default) => GetAsync($"/Items/{Id(id)}?userId={UserId}", ct);

    /// <summary>A show's episodes on the server, in order.</summary>
    public Task<JsonNode> EpisodesAsync(string seriesId, CancellationToken ct = default) =>
        GetAsync($"/Shows/{Id(seriesId)}/Episodes?userId={UserId}&isMissing=false&fields={Fields}", ct);

    /// <summary>An episode and the ones after it in the show's order.</summary>
    public Task<JsonNode> EpisodesFromAsync(string seriesId, string startItemId, int limit, CancellationToken ct = default) =>
        GetAsync($"/Shows/{Id(seriesId)}/Episodes?userId={UserId}&isMissing=false&startItemId={Id(startItemId)}&limit={limit}&fields={Fields}", ct);

    /// <summary>The item's versions on the server. Exo Cinema plays the original file (mpv plays nearly every container
    /// and codec), so the server is told it may play any as it is, and never asked to convert one.</summary>
    public Task<JsonNode> PlaybackInfoAsync(string itemId, long startTicks, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/Items/{Id(itemId)}/PlaybackInfo?userId={UserId}", new JsonObject
        {
            ["UserId"] = UserId,
            ["StartTimeTicks"] = startTicks,
            // Never: the server would open the source first, and links made for the first connection that opens them
            // would then fail for this PC.
            ["AutoOpenLiveStream"] = false,
            ["EnableDirectPlay"] = true,
            ["EnableDirectStream"] = true,
            ["EnableTranscoding"] = false,
            ["DeviceProfile"] = new JsonObject
            {
                ["Name"] = ClientName,
                ["MaxStreamingBitrate"] = 400_000_000,
                ["MaxStaticBitrate"] = 400_000_000,
                ["DirectPlayProfiles"] = new JsonArray(new JsonObject { ["Type"] = "Video" }),
                ["SubtitleProfiles"] = new JsonArray(
                    new JsonObject { ["Format"] = "srt", ["Method"] = "External" },
                    new JsonObject { ["Format"] = "ass", ["Method"] = "External" },
                    new JsonObject { ["Format"] = "vtt", ["Method"] = "External" },
                    new JsonObject { ["Format"] = "srt", ["Method"] = "Embed" },
                    new JsonObject { ["Format"] = "ass", ["Method"] = "Embed" },
                    new JsonObject { ["Format"] = "pgssub", ["Method"] = "Embed" },
                    new JsonObject { ["Format"] = "dvdsub", ["Method"] = "Embed" }),
            },
        }, ct);

    /// <summary>The address the player opens: the original file on this server, the token travelling in its Authorization
    /// header, never in the address. A version kept at another address (Protocol Http, IsRemote) is opened there, as
    /// Jellyfin's own clients do, so a link made for the first connection that opens it is made for this one.</summary>
    public string StreamUrl(string itemId, JsonNode source) =>
        (bool?)source["IsRemote"] == true && (string?)source["Protocol"] == "Http"
            && Uri.TryCreate((string?)source["Path"], UriKind.Absolute, out var remote) && remote.Scheme is "https" or "http"
            ? remote.AbsoluteUri
            : ServerStreamUrl(itemId, source);

    /// <summary>The version as this server itself hands it over. For a version kept at another address the server
    /// fetches it and passes it on, so a link the host made for the server's connection plays (without seeking: the
    /// server passes such a file on in one piece).</summary>
    public string ServerStreamUrl(string itemId, JsonNode source) =>
        $"{Server}/Videos/{Id(itemId)}/stream?static=true&mediaSourceId={Uri.EscapeDataString((string?)source["Id"] ?? itemId)}";

    /// <summary>A picture on this server. The server hands pictures to anyone with the address, so it carries no token and
    /// goes through the picture cache (Library/ArtCache.cs). The tag changes with the picture.</summary>
    public string? Image(string? itemId, string type, string? tag) =>
        itemId is null || tag is null ? null : $"{Server}/Items/{Id(itemId)}/Images/{type}?tag={Uri.EscapeDataString(tag)}&maxWidth=1920";

    /// <summary>Whether an address is on this server, so it may carry the token.</summary>
    public bool IsOwnUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && Uri.TryCreate(Server, UriKind.Absolute, out var s)
        && u.Scheme == s.Scheme && string.Equals(u.Host, s.Host, StringComparison.OrdinalIgnoreCase) && u.Port == s.Port;

    public Task ReportStartAsync(JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/Sessions/Playing", body, ct);
    public Task ReportProgressAsync(JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/Sessions/Playing/Progress", body, ct);
    public Task ReportStoppedAsync(JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/Sessions/Playing/Stopped", body, ct);

    public Task SetPlayedAsync(string itemId, bool played, CancellationToken ct = default) =>
        SendAsync(played ? HttpMethod.Post : HttpMethod.Delete, $"/UserPlayedItems/{Id(itemId)}?userId={UserId}", null, ct);

    public Task SetFavoriteAsync(string itemId, bool on, CancellationToken ct = default) =>
        SendAsync(on ? HttpMethod.Post : HttpMethod.Delete, $"/UserFavoriteItems/{Id(itemId)}?userId={UserId}", null, ct);

    private static string Id(string id) => Uri.EscapeDataString(id);

    // A read (GET) gets one more try after a moment when the connection failed, reset, or timed out. Nothing else is
    // retried: a write may have happened on the server already.
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(500);

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct, TimeSpan? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        var retried = false;
        while (true)
        {
            try { return await SendAsync(HttpMethod.Get, path, null, ct, limit); }
            catch (JellyfinException e) when (e.Transient && !retried && !ct.IsCancellationRequested)
            {
                retried = true;
                await Task.Delay(RetryWait, ct);
            }
            catch (JellyfinException e)
            {
                e.ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                e.Retried = retried;
                throw;
            }
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, TimeSpan? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var req = new HttpRequestMessage(method, Server + path);
            if (body != null) req.Content = JsonContent.Create(body);
            using var res = await SendRawAsync(req, ct, limit);
            if (!res.IsSuccessStatusCode)
            {
                throw res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new JellyfinException(Token is null ? "Wrong username or password." : "Signed out by the server. Connect again.", res.StatusCode),
                    HttpStatusCode.Forbidden => new JellyfinException("This account isn't allowed to do that.", res.StatusCode),
                    HttpStatusCode.NotFound => new JellyfinException("Not found on the server.", res.StatusCode),
                    _ => ServerError(res.StatusCode),
                };
            }

            var text = await res.Content.ReadAsStringAsync(ct);
            try { return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject(); }
            catch (JsonException) { throw NotJellyfin(); }
        }
        catch (JellyfinException e) when (e.ElapsedMs is null)
        {
            e.ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage req, CancellationToken ct, TimeSpan? limit = null)
    {
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader);
        req.Headers.Accept.ParseAdd("application/json");
        using var cut = limit is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cut is not null) cut.CancelAfter(limit.Value);
        try { return await Http.SendAsync(req, cut?.Token ?? ct); }
        catch (Net.Egress.Refusal)
        {
            throw new JellyfinException($"Can't reach {new Uri(Server).Host}: Exo Cinema doesn't connect there.", kind: "refused by Egress");
        }
        catch (HttpRequestException e)
        {
            // A connection that drops or resets shows up under several of these, so the wire-level cause counts too.
            var reset = e.InnerException is IOException or System.Net.Sockets.SocketException;
            var (kind, why, transient) = e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => ("name resolution failed", "the address wasn't found", false),
                HttpRequestError.SecureConnectionError => ("TLS error", "its certificate isn't trusted", false),
                HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError => ("connection error", "it didn't answer", true),
                _ when reset => ("connection error", "it didn't answer", true),
                _ => ("protocol error", e.Message, false),
            };
            throw new JellyfinException($"Can't reach {new Uri(Server).Host}: {why}", kind: kind, transient: transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var ms = (limit ?? Http.Timeout).TotalMilliseconds;
            throw new JellyfinException($"{new Uri(Server).Host} took too long to answer.", kind: $"timeout after {ms:0} ms", transient: true);
        }
    }
}

/// <summary>A failure the page shows as it is, with what the server answered. <see cref="Kind"/> and the timings are for
/// app.log, which never gets the address or the token.</summary>
internal sealed class JellyfinException(string message, HttpStatusCode? status = null, string? kind = null, bool transient = false) : LibraryException(message)
{
    public HttpStatusCode? Status { get; } = status;
    /// <summary>What went wrong, in words app.log can keep: "timeout after 10000 ms", "connection error", "HTTP 500"...</summary>
    public string Kind { get; } = kind ?? (status is { } s ? $"HTTP {(int)s}" : "error");
    /// <summary>A failure a retry of a read may get past: the connection failed or reset, or it timed out.</summary>
    public bool Transient { get; } = transient;
    /// <summary>How long the call took, from the start of the call (all its tries).</summary>
    public long? ElapsedMs { get; set; }
    /// <summary>Whether a read was tried again before this failed.</summary>
    public bool Retried { get; set; }
    /// <summary>Whether this failure is already in app.log, so it isn't written twice.</summary>
    public bool Logged { get; set; }
    /// <summary>The one line app.log keeps for it: the kind, how long, and whether it was retried. Never the address.</summary>
    public string Diagnosis => $"{Kind}{(ElapsedMs is { } ms ? $", {ms} ms" : "")}{(Retried ? ", retried once" : "")}";
}
