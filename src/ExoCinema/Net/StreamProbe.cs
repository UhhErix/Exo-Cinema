using System.Net;

namespace ExoCinema.Net;

/// <summary>
/// One look at a stream link before the player opens it: whether the host refused it (and plays a notice
/// video instead), where it redirects to, and whether it serves byte ranges (without them nothing can seek in it or
/// start it where it was left). Every request goes through Egress as a stream's.
/// </summary>
internal static class StreamProbe
{
    private static readonly HttpClient Head = new(Egress.Checkpoint(Egress.Purpose.Stream, new SocketsHttpHandler { AllowAutoRedirect = false }))
    { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly HttpClient Ranges = new(Egress.Checkpoint(Egress.Purpose.Stream, new SocketsHttpHandler { MaxAutomaticRedirections = 5 }))
    { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The notice a refused link plays (some hosts tie a link to the connection that asked for it and answer anyone else
    /// with a notice video), or the address it redirects to, which the player can open directly instead of asking the
    /// redirect again on every request.
    /// </summary>
    public static async Task<(string? Notice, string? Target)> RemoteAsync(string url, CancellationToken ct = default,
        (Func<string, bool> IsOwn, string Authorization)? own = null)
    {
        // A link can pass through several hosts before the file (the server, a service behind it, the file's
        // host), and the refusal can come from any of them: every hop is looked at, not only the first.
        string? target = null;
        var at = url;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            Uri? loc;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, at);
                // A server's sign-in goes only to that server, never to an address it sends on to.
                if (own is { } o && Uri.TryCreate(at, UriKind.Absolute, out var here) && o.IsOwn(here.AbsoluteUri))
                    req.Headers.TryAddWithoutValidation("Authorization", o.Authorization);
                using var res = await Head.SendAsync(req, ct);
                loc = res.Headers.Location;
                if (loc is { IsAbsoluteUri: false }) loc = new Uri(new Uri(at), loc);
            }
            catch (Exception e)
            {
                // A link that doesn't answer a look is played anyway; the log says what kind of failure it was.
                if (e is not OperationCanceledException) AppLog.Shared.Write($"stream: the link didn't answer its {(hop == 0 ? "first look" : $"look at hop {hop + 1}")} ({AppLog.Kind(e)})");
                break;
            }
            if (loc is null || loc.Scheme is not ("https" or "http")) break;
            if (NoticeOf(loc) is { } notice)
            {
                AppLog.Shared.Write($"stream: refused at hop {hop + 1} of the link{(TiedToAnotherConnection(notice) ? " (made for another connection)" : "")}");
                return (notice, null);
            }
            // The player opens where the first hop leads, as before; the later hops are only looked at.
            target ??= loc.AbsoluteUri;
            at = loc.AbsoluteUri;
        }
        return (null, target);
    }

    /// <summary>How many redirects of a link are looked at before it's played.</summary>
    internal const int MaxHops = 6;

    /// <summary>The notice a redirect carries when it leads to a notice video instead of the file: a "slate." host, or
    /// any host whose address carries a notice about the connection in "body"; null for an ordinary redirect.</summary>
    internal static string? NoticeOf(Uri location)
    {
        string? body = null;
        foreach (var pair in location.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == "body" && kv.Length == 2)
            {
                try { body = string.Join(' ', Uri.UnescapeDataString(kv[1].Replace('+', ' ')).Split(null as char[], StringSplitOptions.RemoveEmptyEntries)); }
                catch (UriFormatException) { }
                break;
            }
        }
        var slate = location.Host.StartsWith("slate.", StringComparison.OrdinalIgnoreCase);
        if (slate) return body is { Length: > 0 } ? body : "The stream host tied this link to another connection. Open the title again to ask for a fresh one.";
        return body is not null && TiedToAnotherConnection(body) ? body : null;
    }

    /// <summary>Whether a host's notice says the link belongs to another connection (it names the address it was made
    /// for and the one asking now).</summary>
    public static bool TiedToAnotherConnection(string notice) =>
        notice.Contains("Expected IP", StringComparison.OrdinalIgnoreCase)
        || notice.Contains("Current IP", StringComparison.OrdinalIgnoreCase)
        || notice.Contains("connection that requested", StringComparison.OrdinalIgnoreCase)
        || notice.Contains("another connection", StringComparison.OrdinalIgnoreCase);

    /// <summary>A refusal as the player says it: a notice about the connection in plain words, without the addresses
    /// the host printed; any other notice as the host wrote it.</summary>
    public static string Plain(string notice) => TiedToAnotherConnection(notice)
        ? "The stream host made this link for another connection (your server's, or a VPN's) and won't play it on this one. If you use a VPN or proxy, turn it off and play again."
        : notice;

    /// <summary>Whether a stream serves byte ranges (mpv: "Cannot seek in this stream" without); null when the host
    /// doesn't answer plainly.</summary>
    public static async Task<bool?> ServesRangesAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            // Headers only: a host that ignores the range would send the whole file.
            using var res = await Ranges.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return res.StatusCode switch { HttpStatusCode.PartialContent => true, HttpStatusCode.OK => false, _ => null };
        }
        catch { return null; }
    }
}
