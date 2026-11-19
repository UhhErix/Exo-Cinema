using Exo.Identity;

namespace ExoCinema.Net;

/// <summary>
/// Where the Exo account service is reached (cloud/account/CLIENT.md): the live host by default. A test copy can name the
/// dev Worker with EXO_ACCOUNT_URL, only its root over https (the dev host, exo-account-dev) or a loopback http root; any
/// other value is ignored, so a release copy with no variable set always reaches the live host. Egress allows only that
/// one host while the variable names it.
/// </summary>
internal static class AccountEndpoint
{
    internal static readonly Uri Live = new("https://accounts.getexo.app/");

    /// <summary>The override the variable names, if it is one of the two allowed roots; null otherwise.</summary>
    internal static Uri? Override => Parse(Environment.GetEnvironmentVariable("EXO_ACCOUNT_URL"));

    internal static Uri Current => Override ?? Live;

    internal static Uri? Parse(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsAllowedRoot(uri) ? uri : null;

    /// <summary>The dev Worker's root over https, or a loopback http root: nothing else, no path, query or user info.</summary>
    internal static bool IsAllowedRoot(Uri uri) =>
        uri is { AbsolutePath: "/", Query: "", UserInfo: "", Fragment: "" } &&
        (uri.Scheme == Uri.UriSchemeHttps && uri.Host == DevServers.AccountHost && uri.IsDefaultPort ||
         uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
