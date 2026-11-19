namespace ExoCinema.Net;

/// <summary>
/// Everywhere Exo Cinema connects (platform plan §2.8), in one place. Every web request it makes passes a checkpoint for
/// what the request is for, which lets it through only to where that may go:
/// <list type="bullet">
/// <item>Fixed destinations, by name, over https only: Exo's update address, for updates; Exo's
/// account service; Exo's crash-report service. Each is named in PRIVACY.md (EgressTests checks), so a new one can't
/// come in without it being said there.</item>
/// <item>Your Jellyfin server (Jellyfin/): only the one you're signed in to, and for a call that's connecting, the one
/// that call was given (<see cref="TryServer"/>), by its scheme, host and port; never a fixed host or Exo's own. The
/// library, its pictures and what is reported back all go there.</item>
/// <item>Where the server's films are kept, when that is another address (<see cref="Purpose.Stream"/>): the links the
/// server hands out for a file, over http or https, and never one of the fixed hosts or Exo's own, so nothing meant for
/// a file's host reaches Exo. These connections go over IPv6 first (Net/Ipv6First.cs).</item>
/// <item>Exo's account service, for the Exo account (Account/): the live host over https, or one on this PC
/// (loopback, <c>http://</c>) that EXO_ACCOUNT names; on a PC set up for Exo's own development (Exo.Identity's
/// DevServers: while %LOCALAPPDATA%\Exo\dev.json names it), the dev one over https.</item>
/// <item>Exo's crash-report service (CrashReport.cs), for the reports the app sends: its one host, over https, for the
/// one POST a report makes. Nothing else goes there.</item>
/// </list>
/// The player (mpv) and the page (WebView2) read only the addresses those answers give.
/// </summary>
internal static class Egress
{
    public enum Purpose { Updates, Stream, Account, Server, Reports }

    private static volatile Uri? _signedIn;
    private static readonly AsyncLocal<Uri?> Trying = new();

    /// <summary>The Jellyfin server now signed in to, or none: from here on the only one <see cref="Purpose.Server"/>
    /// reaches, besides one being tried.</summary>
    public static void SignedInTo(string? server) => _signedIn = server is null ? null : new Uri(server);

    /// <summary>Opens a server being connected to for the async call this is made in and what it awaits, and for nothing
    /// else running: typing an address opens it to the sign-in alone, and only while that goes on.</summary>
    public static void TryServer(string server) => Trying.Value = new Uri(server);

    /// <summary>The fixed destinations and what each is for.</summary>
    public static readonly IReadOnlyList<(string Host, Purpose For)> Fixed =
    [
        ("updates.getexo.app", Purpose.Updates),
        ("reports.getexo.app", Purpose.Reports),
        ("accounts.getexo.app", Purpose.Account),
    ];

    /// <summary>Whether a request for <paramref name="purpose"/> may go to <paramref name="uri"/>.</summary>
    public static bool Allowed(Uri? uri, Purpose purpose) => Allowed(uri, purpose, devServers: Exo.Identity.DevServers.Current is not null);

    internal static bool Allowed(Uri? uri, Purpose purpose, bool devServers)
    {
        if (uri is not { IsAbsoluteUri: true } || uri.Host.Length == 0) return false;
        var host = uri.IdnHost.TrimEnd('.');
        var fixedFor = Fixed.Where(f => string.Equals(f.Host, host, StringComparison.OrdinalIgnoreCase)).Select(f => f.For).ToList();
        var plain = uri.UserInfo.Length == 0 && uri.Query.Length == 0;
        return purpose switch
        {
            Purpose.Updates => uri.Scheme == Uri.UriSchemeHttps && fixedFor.Contains(Purpose.Updates),
            // The Exo account: the live service over https, or one on this PC, or the dev one.
            // A query only on the sync pull and the export, which page by cursor (CLIENT.md 7.6, 7.7); no other account route takes one.
            Purpose.Account => (uri.UserInfo.Length == 0 && (plain || uri.AbsolutePath is "/v1/sync" or "/v1/export")) && ((uri.Scheme == Uri.UriSchemeHttps && fixedFor.Contains(Purpose.Account)) || uri.Scheme == "http" && uri.IsLoopback || devServers && Dev(uri, "https", Exo.Identity.DevServers.AccountHost) ||
                AccountEndpoint.Override is { } test && uri.Scheme == test.Scheme && uri.Host.Equals(test.Host, StringComparison.OrdinalIgnoreCase)),
            // Crash and error reports: Exo's report service over https, with no query and no user info.
            Purpose.Reports => uri.Scheme == Uri.UriSchemeHttps && fixedFor.Contains(Purpose.Reports) && plain,
            // The Jellyfin server you signed in to or are signing in to, and no other.
            Purpose.Server => uri.UserInfo.Length == 0 && uri.Scheme is "http" or "https" && fixedFor.Count == 0 && !IsExo(host) && (IsServer(uri, _signedIn) || IsServer(uri, Trying.Value)),
            // Where a film is kept: any web address that is not a fixed host or Exo's own.
            Purpose.Stream => uri.Scheme is "http" or "https" && fixedFor.Count == 0 && !IsExo(host),
            _ => false,
        };
    }

    private static bool IsServer(Uri uri, Uri? server) =>
        server is not null && uri.Scheme == server.Scheme && uri.Port == server.Port && string.Equals(uri.IdnHost, server.IdnHost, StringComparison.OrdinalIgnoreCase);

    private static bool Dev(Uri uri, string scheme, string host) =>
        uri.Scheme == scheme && string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;

    private static bool IsExo(string host) =>
        host.Equals("getexo.app", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".getexo.app", StringComparison.OrdinalIgnoreCase);

    /// <summary>A handler that refuses, before anything is sent, a request <paramref name="purpose"/> may not make.</summary>
    public static HttpMessageHandler Checkpoint(Purpose purpose, HttpMessageHandler inner)
    {
        // A stream's connections (the look at a link, and the player's) go over IPv6 first: Net/Ipv6First.cs says why.
        if (purpose == Purpose.Stream && inner is SocketsHttpHandler { ConnectCallback: null } sockets) sockets.ConnectCallback = Ipv6First.ConnectAsync;
        return new Gate(purpose, inner);
    }

    private sealed class Gate(Purpose purpose, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Allowed(request.RequestUri, purpose) ? base.SendAsync(request, ct) : throw Refused(request);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) =>
            Allowed(request.RequestUri, purpose) ? base.Send(request, ct) : throw Refused(request);

        // The log names the purpose only, never the address: a server's or a file's host and path stay out of it.
        // A refusal is a Refusal (never retried, and callers can tell it apart); its text names no host either.
        private Refusal Refused(HttpRequestMessage request)
        {
            AppLog.Shared.Write($"egress: refused a {purpose.ToString().ToLowerInvariant()} request (not an allowed destination)");
            return new($"Exo Cinema doesn't connect there for {purpose.ToString().ToLowerInvariant()}.");
        }
    }

    /// <summary>A request the checkpoint refused before anything was sent. Not a server that can't be reached: it is never
    /// retried, and callers can tell it apart.</summary>
    public sealed class Refusal(string message) : HttpRequestException(message);
}
