namespace ExoCinema;

/// <summary>
/// Whether a version's average bitrate fits this PC's connection: within the share of the link's measured speed that a
/// stream can take without stopping to buffer (LinkSpeed). The server's
/// versions (Jellyfin/SourceRank) rank by this measure. A bitrate not known, or a link not measured, fits.
/// </summary>
internal static class LinkFit
{
    /// <summary>The share of the link's measured speed a version may need.</summary>
    public const double Share = 0.85;

    /// <param name="needMbps">The version's average bitrate in Mb/s, or null when it isn't known.</param>
    /// <param name="linkMbps">The link's measured speed in Mb/s, or null before anything has been measured.</param>
    public static bool Fits(double? needMbps, double? linkMbps) =>
        linkMbps is not > 0 || needMbps is not { } need || need <= linkMbps * Share;
}
