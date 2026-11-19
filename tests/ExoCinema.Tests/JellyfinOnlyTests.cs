using System.Text.RegularExpressions;

namespace ExoCinema.Tests;

/// <summary>
/// Jellyfin only (1.5): Exo Cinema takes its films from the Jellyfin server it is signed in to and from nowhere else.
/// The sources it played from before are gone from the code, the page and the list of places it connects; this keeps
/// them from coming back unnoticed.
/// </summary>
public sealed partial class JellyfinOnlyTests
{
    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PRIVACY.md")) && Directory.Exists(Path.Combine(dir.FullName, "src", "ExoCinema"))) return dir.FullName;
        throw new InvalidOperationException("The repository isn't above " + AppContext.BaseDirectory);
    }

    /// <summary>The only lines in src that may still name them, each with why.</summary>
    private static readonly (string File, string Holds, string Why)[] Justified =
    [
        ("MainWindow.App.cs", "aiostreams.elfhosted.com/stremio/configure", "AIOStreams' own set-up address, opened in the browser from the first run for a jlly.cc account"),
        ("MainWindow.App.cs", "AIOStreams' own set-up page", "the comment on that address"),
        ("AccountSync.cs", "addons", "the account service's own name for the list an account from before 1.5 holds; read so it isn't an error, used by nothing"),
        ("whats-new.json", "", "the notes of versions already published say what those versions did"),
    ];

    [Fact]
    public void Nothing_in_src_names_the_sources_Cinema_played_from_before()
    {
        var src = Path.Combine(Repo(), "src");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (Path.GetExtension(file) is not (".cs" or ".js" or ".css" or ".html" or ".json" or ".xaml" or ".csproj")) continue;
            var name = Path.GetFileName(file);
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (!Named().IsMatch(line)) continue;
                if (Justified.Any(j => j.File == name && line.Contains(j.Holds, StringComparison.Ordinal))) continue;
                found.Add($"{Path.GetRelativePath(src, file)}:{n}: {line.Trim()}");
            }
        }
        Assert.True(found.Count == 0, "Still named in src:\n" + string.Join('\n', found.Take(20)));
    }

    [Fact]
    public void The_page_has_no_screen_or_call_for_them()
    {
        var www = Path.Combine(Repo(), "src", "ExoCinema", "wwwroot");
        var page = string.Concat(new[] { "app.js", "onboarding.js", "player.js", "playerplus.js", "index.html" }.Select(f => File.ReadAllText(Path.Combine(www, f))));
        // Settings has no page for them, the first run no step, and the host is never asked for a list of them.
        Assert.DoesNotMatch(@"settings-page=.(addons|sources)", page);
        Assert.DoesNotContain("manifest.json", page);
        Assert.DoesNotMatch(@"jf\('(addons|addon-add|addon-remove|addon-move|prepare|prepare-next)'", page);
        // With no server signed in to, every room is the Jellyfin sign-in.
        var app = File.ReadAllText(Path.Combine(www, "app.js"));
        foreach (var room in new[] { "renderHome", "renderBrowse", "renderTitle", "renderSearch", "renderWatch" })
            Assert.Matches(new Regex(@"function " + room + @"\([^)]*\) \{\r?\n  drawRooms\(\)\r?\n  if \(!jellyfin\.signedIn\) return renderSignIn\(\)"), app);
    }

    /// <summary>
    /// The start-up fault of 1.4.12 (the log: "egress: refused a metadata request (not an allowed destination)", then
    /// "metadata meta didn't load"), found on 2026-11-12: reading the Watchlist, History and Continue watching asked the
    /// built-in public catalog to describe each title Exo Watch kept, a title of unknown kind as a film first; the
    /// catalog answers a show asked as a film (and any title it doesn't hold) with a redirect to a second host
    /// (cinemeta-live), which was no allowed destination, so the checkpoint refused it. 1.5 doesn't make the request:
    /// the catalog is gone, and those rows are the server's. What keeps it gone is here: every web client the app makes
    /// has one purpose, the library's and the pictures' are the signed-in server's alone, and only a film's own link
    /// may lead anywhere else.
    /// </summary>
    [Fact]
    public void Every_web_client_has_one_purpose_and_the_library_asks_only_the_server()
    {
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Account/ExoAccountClient.cs"] = "Account",
            ["AppUpdater.cs"] = "Updates",
            ["CrashReport.cs"] = "Reports",
            ["Jellyfin/JellyfinClient.cs"] = "Server",
            ["Library/ArtCache.cs"] = "Server",
            ["Mpv/PeekStream.cs"] = "Stream",
            ["Mpv/PlayStream.cs"] = "Stream",
            ["Net/StreamProbe.cs"] = "Stream",
        };
        var app = Path.Combine(Repo(), "src", "ExoCinema");
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetRelativePath(app, file).Replace('\\', '/');
            if (name == "Net/Egress.cs") continue;
            foreach (var line in File.ReadLines(file))
            {
                if (!line.Contains("Egress.Checkpoint(", StringComparison.Ordinal)) continue;
                var purpose = Checkpoint().Match(line);
                Assert.True(purpose.Success, $"{name} makes a web client whose purpose isn't written at the checkpoint: {line.Trim()}");
                Assert.True(!found.TryGetValue(name, out var other) || other == purpose.Groups[1].Value, $"{name} makes clients for two purposes");
                found[name] = purpose.Groups[1].Value;
            }
        }
        Assert.Equal(expected, found);
        // And the library itself, which reads the watch rows at start, makes no client at all: it asks JellyfinLibrary.
        Assert.DoesNotMatch(@"HttpClient|Egress\.", File.ReadAllText(Path.Combine(app, "Library", "CinemaLibrary.cs")));
    }

    [GeneratedRegex(@"Egress\.Checkpoint\((?:Net\.)?Egress\.Purpose\.(\w+),")]
    private static partial Regex Checkpoint();

    [GeneratedRegex(@"stremio|cinemeta|add-?on|metahub", RegexOptions.IgnoreCase)]
    private static partial Regex Named();
}
