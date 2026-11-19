using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExoCinema.Sync;

/// <summary>
/// What of Exo Cinema's settings syncs, under the sub-key <c>cinema</c> (platform plan §2.7), and nothing else: only
/// these items are taken from the page or from another PC, each checked for its shape.
/// <list type="bullet">
/// <item><c>setting</c>: Material, Reduce motion, Source, Play next episode and Smooth motion, by their keys, with one
/// of their values. Reduce motion is the person's need, wherever they sit.</item>
/// <item><c>languages</c>: the audio and subtitle languages last chosen for a show, by its public id (Exo Watch's,
/// never a server's item id), or <c>movies</c> for films; each a language code or <c>off</c>.</item>
/// <item><c>server</c> <c>last</c>: only its removal. It was the Jellyfin server's address and name, from when Cinema
/// played from Jellyfin; each PC takes it away from the profile (MainWindow).</item>
/// </list>
/// Never: what this PC decides from its own hardware and connection. And no longer: the stream sources Cinema took
/// films from before 1.5 (it plays from a Jellyfin server alone now). The items a PC kept for them stay in its store
/// untouched, and none is accepted from the page or from another PC.
/// </summary>
internal static partial class CinemaSettings
{
    public const string App = "cinema";

    // The page's own choices (app.js PREFS), and the values each can take.
    internal static IEnumerable<string> SettingKeys => Settings.Keys;

    private static readonly Dictionary<string, string[]> Settings = new()
    {
        ["material"] = ["glass", "solid"],
        ["motion"] = ["off", "on"],
        ["source"] = ["best", "ask"],
        ["autoplay"] = ["on", "off"],
        ["smooth"] = ["off", "on"],
    };

    /// <summary>
    /// Every choice Exo Cinema keeps, with why it follows you or stays on this PC (account plan item 15, as Exo Launcher
    /// tags its settings). A new one is neither until it's added here, and the settings test fails until it is.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Synced = new Dictionary<string, string>
    {
        ["setting/material"] = "Glass or Solid is how you like Exo to look, on any PC.",
        ["setting/motion"] = "Reduce motion is your need, wherever you sit.",
        ["setting/source"] = "Best or Ask is how you like to pick a version.",
        ["setting/autoplay"] = "Play next episode is how you watch shows.",
        ["setting/smooth"] = "Smooth motion is how you like films to move; each PC still turns it off where it can't keep up.",
        ["languages"] = "The audio and subtitles you pick for a show are yours, not the PC's.",
        ["server"] = "Only the old Jellyfin address's removal, so it leaves every PC's copy of the profile.",
    };

    /// <summary>What stays on this PC, and why.</summary>
    public static readonly IReadOnlyDictionary<string, string> ThisPc = new Dictionary<string, string>
    {
        ["Onboarded"] = "The first run happened on this PC; another PC has its own first run.",
        ["WhatsNewSeen"] = "Which version's What's New this copy showed.",
        ["UpdateTried"] = "Which version this copy last started installing.",
        ["AccountOffered"] = "Whether the one-time Exo account sheet was shown on this PC.",
        ["exo:volume"] = "Speakers and headphones differ from PC to PC.",
        ["exo:account-banner-hidden"] = "When this PC's account banner was last hidden: a week, then it comes back.",
        ["exo:lib:"] = "Rows kept to draw Home at once, not a choice.",
        ["exo:lib-kept"] = "Which version last cleared this PC's kept rows (1.5 did, once).",
        ["picture"] = "The picture's quality and decoder are picked from this PC's graphics card, power and screen, with no setting.",
        ["image-cache"] = "Pictures kept on this PC's disk.",
    };

    /// <summary>Whether an item is one Exo Cinema syncs, in its shape; a delete (null body) of one too.</summary>
    public static bool Accept(string type, string id, JsonNode? body) => type switch
    {
        "setting" => Settings.TryGetValue(id, out var values) && (body is null || Text(body) is { } v && values.Contains(v)),
        "languages" => (id == "movies" || ShowId().IsMatch(id)) && (body is null || Languages(body)),
        "server" => id == "last" && body is null,

        _ => false,
    };

    private static bool Languages(JsonNode body) =>
        body is JsonObject o && o.Count is > 0 and <= 2 && o.All(kv => kv.Key is "audio" or "sub" && Text(kv.Value) is { } code && LanguageCode().IsMatch(code));


    private static string? Text(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string?)v : null;

    [GeneratedRegex(@"^(imdb:tt\d{1,10}|tmdb:tv:\d{1,10})$")]
    private static partial Regex ShowId();


    [GeneratedRegex(@"^([a-z]{2,3}(-[A-Za-z0-9]{1,8})*|off)$")]
    private static partial Regex LanguageCode();
}
