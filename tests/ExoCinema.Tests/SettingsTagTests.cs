using System.Text.RegularExpressions;
using ExoCinema.Sync;

namespace ExoCinema.Tests;

/// <summary>Every choice Exo Cinema keeps either syncs, with why, or stays on this PC, with why (account plan item 15).</summary>
public sealed partial class SettingsTagTests
{
    private static string Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine([dir.FullName, "src", "ExoCinema", .. parts]);
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        throw new InvalidOperationException("src\\ExoCinema not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_synced_setting_says_why()
    {
        foreach (var key in CinemaSettings.SettingKeys) Assert.True(CinemaSettings.Synced.ContainsKey($"setting/{key}"), $"setting/{key} has no reason");
        foreach (var type in new[] { "languages", "server" }) Assert.True(CinemaSettings.Synced.ContainsKey(type), type);
        Assert.All(CinemaSettings.Synced.Values.Concat(CinemaSettings.ThisPc.Values), why => Assert.False(string.IsNullOrWhiteSpace(why)));
    }

    [Fact]
    public void Every_page_choice_and_app_setting_is_synced_or_this_pc_only()
    {
        // The page's choices (app.js PREFS) sync; each is in CinemaSettings.
        var app = Source("wwwroot", "app.js");
        var prefs = Prefs().Match(app);
        Assert.True(prefs.Success);
        foreach (Match key in PrefKey().Matches(prefs.Groups[1].Value)) Assert.True(CinemaSettings.Synced.ContainsKey($"setting/{key.Groups[1].Value}"), key.Groups[1].Value);
        // Every other key the page keeps is tagged.
        foreach (var file in new[] { "app.js", "player.js", "profile.js", "boot.js" })
            foreach (Match m in StoredKey().Matches(Source("wwwroot", file)))
            {
                var key = m.Groups[1].Value;
                if (key is "exo:pref:" or "exo:lang:") continue;
                Assert.True(CinemaSettings.ThisPc.Keys.Any(k => key.StartsWith(k, StringComparison.Ordinal)), $"{file}: {key} is neither synced nor this-PC-only");
            }
        // Every setting the app itself keeps (AppSettings) is this PC's.
        foreach (Match m in AppSetting().Matches(Source("AppSettings.cs"))) Assert.True(CinemaSettings.ThisPc.ContainsKey(m.Groups[1].Value), m.Groups[1].Value);
    }

    [GeneratedRegex(@"const PREFS = \{(.*?)\n\}", RegexOptions.Singleline)]
    private static partial Regex Prefs();

    [GeneratedRegex(@"^\s*(\w+): \{ label", RegexOptions.Multiline)]
    private static partial Regex PrefKey();

    [GeneratedRegex(@"localStorage\.setItem\([`'](exo:[a-z-]+:?)")]
    private static partial Regex StoredKey();

    [GeneratedRegex(@"public (?:bool|string\??|int|long) (\w+) \{ get; set; \}")]
    private static partial Regex AppSetting();
}
