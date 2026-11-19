using System.Text.Json.Nodes;

namespace ExoCinema.Jellyfin;

/// <summary>A title's versions on the server (its MediaSources), best for this PC first: what the source sheet and Play pick.</summary>
internal static class SourceRank
{
    /// <summary>
    /// Best first, for this PC: with the link's measured speed, a version whose bitrate fits it (LinkFit) before one that
    /// would stop to buffer; then the most detailed picture this PC is built for (above
    /// 1080p only on a dedicated graphics card that decodes it itself: PictureRules.HeightCap); then one the card decodes;
    /// then the highest bitrate. Only an order: a version is played as it is, never transcoded.
    /// </summary>
    public static IEnumerable<JsonNode> Rank(List<JsonNode> sources, GraphicsCard card, double? linkMbps)
    {
        static JsonNode? Video(JsonNode s) => (s["MediaStreams"] as JsonArray)?.FirstOrDefault(m => (string?)m?["Type"] == "Video");
        static string? Codec(JsonNode s) => (string?)Video(s)?["Codec"];
        // HDR is 10-bit whether the bit depth is given or not.
        static bool TenBit(JsonNode s) => ((int?)Video(s)?["BitDepth"] ?? 8) >= 10 || (string?)Video(s)?["VideoRange"] == "HDR";
        // The server's average bitrate for the version, in Mb/s (Jellyfin writes it in bits a second).
        static double? Mbps(JsonNode s) => (long?)s["Bitrate"] is { } bits && bits > 0 ? bits / 1e6 : null;
        return sources
            .OrderByDescending(s => LinkFit.Fits(Mbps(s), linkMbps))
            .ThenByDescending(s => ((int?)Video(s)?["Height"] ?? 0) is var h && h <= PictureRules.HeightCap(card, Codec(s), TenBit(s)) ? h : -h)
            .ThenByDescending(s => card.Decodes(Codec(s), TenBit(s)))
            .ThenByDescending(s => (long?)s["Bitrate"] ?? 0);
    }
}
