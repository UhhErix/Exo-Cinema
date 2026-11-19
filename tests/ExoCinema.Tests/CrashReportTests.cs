using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace ExoCinema.Tests;

/// <summary>
/// Crash, freeze and error reports (CrashReport.cs): what a report holds once scrubbed, what the service refuses, that a
/// kept report is sent by default at the next start, and the daily limits.
/// </summary>
[Collection("Shared log")]
public sealed class CrashReportTests
{
    private const string UserName = "Erix";
    private const string Profile = @"C:\Users\Erix";
    private const string Machine = "DESKTOP-K3J9";
    private const string JellyfinServer = "https://jellyfin.example.net:8920";
    private const string JellyfinToken = "a1b2c3" + "d4e5f6" + "071829" + "3a4b5c" + "6d7e8f" + "90";
    private const string AddonBase = "https://aiostreams.example.org/stremio/4cdf0e6a-secret/eyJhbGciOiJIUzI1NiJ9";
    private const string Title = "Quantum Night";
    private static readonly string[] Secrets = [JellyfinServer, JellyfinToken, AddonBase, Title];

    // Realistic lines, as Cinema writes them: a server and an add-on named, a token, a title, a file, a path and a crash.
    // Nothing here is scrubbed; CrashReport must do that.
    private static readonly string[] Log =
    [
        "[2026-11-05T19:42:10.1230000Z] INFO Library: signed in to " + JellyfinServer + " for " + UserName + ".",
        "[2026-11-05T19:42:10.4560000Z] WARN Library: could not reach jellyfin.example.net:8920 with token " + JellyfinToken,
        "[2026-11-05T19:42:10.7000000Z] WARN Addon: stream didn't load (HTTP 500, 812 ms) from " + AddonBase.Replace("https://", ""),
        "[2026-11-05T19:42:11.0000000Z] INFO Player: Playing Quantum Night (2010).mkv from the library",
        "[2026-11-05T19:42:11.2000000Z] INFO Player: now " + Title + ", 42% watched",
        @"[2026-11-05T19:42:11.4000000Z] WARN Player: could not open C:\Users\Erix\Videos\Quantum Night\cut.mp4",
        "[2026-11-05T19:42:11.6000000Z] WARN Egress: refused a reports request (not an allowed destination)",
        "[2026-11-05T19:42:11.8000000Z] CRASH xaml: System.InvalidOperationException: boom at 10.0.0.12 on " + Machine,
        "[2026-11-05T19:42:12.0000000Z] ERROR mpv: end-file reason 4 (Failed to recognize file format.)",
        "",
    ];

    /// <summary>What the service refuses, copied from cloud/reports/src/validate.js (PERSONAL).</summary>
    private static readonly Regex[] ServicePatterns =
    [
        new(@"[A-Za-z]:\\"),
        new(@"\\\\[^\s\\]+\\"),
        new(@"(^|[^A-Za-z0-9])/(home|Users)/"),
        new(@"[a-z][a-z0-9+.-]{1,15}://", RegexOptions.IgnoreCase),
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}"),
        new(@"\d{15,}"),
        new(@"[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}"),
    ];

    private sealed class SmokeTestException(string message) : Exception(message);

    private static CrashReport.Payload Build(string[] lines, string error = "Playback failed: SmokeTestException") =>
        CrashReport.Build(error, string.Join('\n', lines), "1.6.0", "10.0.26300.0", UserName, Profile, Machine, Secrets);

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exo-cinema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A throwaway app.log and a clean set of kept reports for this test, so nothing reaches the owner's folder.</summary>
    private static IDisposable Isolated(out string folder)
    {
        folder = Folder();
        var previous = AppLog.Shared;
        AppLog.Shared = new AppLog(Path.Combine(folder, "app.log"));
        CrashReport.ForgetKeptThisRun();
        return new Restore(() => AppLog.Shared = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>A transport that answers each request with the status it is given and remembers what was posted.</summary>
    private sealed class FakeTransport(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Posts.Add((request.RequestUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status);
        }
    }

    private static IDisposable FakeNetwork(FakeTransport transport)
    {
        CrashReport.FakeTransport = () => transport;
        return new Restore(() => CrashReport.FakeTransport = null);
    }

    /// <summary>Writes one kept report file by hand, as an earlier run would have.</summary>
    private static void KeepFile(string folder, CrashReport.Payload payload)
    {
        var dir = CrashReport.PendingDir(folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"), CrashReport.ToJson(payload), Encoding.UTF8);
    }

    [Fact]
    public void The_excerpt_keeps_the_error_and_the_log_but_not_who_where_or_what_was_watched()
    {
        var payload = Build(Log);
        Assert.Contains("Egress: refused a reports request", payload.Excerpt);
        Assert.Contains("<hidden>", payload.Excerpt);
        Assert.Contains("<path>", payload.Excerpt);
        Assert.Contains("<user>", payload.Excerpt);
        foreach (var leak in new[] { "jellyfin.example.net", "example.org", "aiostreams", "4cdf0e6a", "eyJ", "a1b2c3d4", "Quantum", ".mkv", ".mp4", "Erix", @"C:\", "DESKTOP", "10.0.0.12", "://", "@" })
            Assert.DoesNotContain(leak, payload.Excerpt);
        Assert.True(CrashReport.Acceptable(payload));
    }

    [Fact]
    public void The_Jellyfin_address_and_token_are_taken_out_wherever_they_appear()
    {
        var scrubbed = CrashReport.Scrub("on jellyfin.example.net:8920 with a1b2c3d4e5f60718293a4b5c6d7e8f90 and " + JellyfinServer, UserName, Profile, Machine, Secrets);
        Assert.DoesNotContain("example.net", scrubbed);
        Assert.DoesNotContain("a1b2c3d4", scrubbed);
        Assert.Equal("on <hidden>:8920 with <hidden> and <hidden>", scrubbed);
    }

    [Fact]
    public void Add_on_addresses_and_their_keys_are_taken_out_in_every_form()
    {
        var scrubbed = CrashReport.Scrub(
            "fetched " + AddonBase + " then aiostreams.example.org/stremio/4cdf0e6a-secret/eyJhbGciOiJIUzI1NiJ9 and stremio://aiostreams.example.org/x",
            UserName, Profile, Machine, Secrets);
        Assert.DoesNotContain("aiostreams", scrubbed);
        Assert.DoesNotContain("4cdf0e6a", scrubbed);
        Assert.DoesNotContain("eyJ", scrubbed);
        Assert.DoesNotContain("example.org", scrubbed);
    }

    [Fact]
    public void Titles_and_file_names_are_taken_out_and_a_line_with_a_file_name_goes_whole()
    {
        var payload = Build(["[2026-11-05T19:42:11.0000000Z] INFO Player: Sintel.2010.1080p.mkv opened", "[2026-11-05T19:42:11.2000000Z] INFO Player: " + Title + " resumed"]);
        Assert.DoesNotContain("Sintel", payload.Excerpt);
        Assert.DoesNotContain("Quantum", payload.Excerpt);
        Assert.DoesNotContain(".mkv", payload.Excerpt);
        Assert.Contains("<file>", payload.Excerpt);
        Assert.Contains("resumed", payload.Excerpt);
    }

    [Fact]
    public void The_excerpt_passes_the_services_own_checks_line_by_line()
    {
        var payload = Build(Log);
        foreach (var line in payload.Excerpt.Split('\n'))
            foreach (var pattern in ServicePatterns) Assert.DoesNotMatch(pattern, line);
    }

    [Theory]
    [InlineData(@"Opened C:\Users\Erix\AppData\Local\Temp\x.exe")]
    [InlineData(@"Read D:\Games\Riot\VALORANT\x.log for the team")]
    [InlineData(@"Read %USERPROFILE%\Videos\x.exe")]
    [InlineData(@"Read <user>\AppData\Local\x")]
    [InlineData(@"Read \\DESKTOP-K3J9\share\x.txt")]
    [InlineData("Fetched https://api.example.com/v1?token=abc")]
    [InlineData("Fetched http://127.0.0.1:9364/achievements")]
    [InlineData("Signed in as erix@example.com")]
    [InlineData("Account id 76561198012345678 owns this")]
    [InlineData("signed in with eyJhbG" + "ciOiJI" + "UzI1Ni" + "J9.eyJ" + "zdWIiO" + "iIxMjM" + "0NTY3O" + "DkwIn0" + ".abcde" + "fghijk" + "lmnopq" + "rstuvw" + "x")]
    [InlineData("Read /home/erix/.config/exo.json and /Users/erix/x")]
    public void Each_kind_of_personal_data_is_taken_out(string text)
    {
        var scrubbed = CrashReport.Scrub(text, UserName, Profile, Machine, Secrets);
        foreach (var pattern in ServicePatterns) Assert.False(pattern.IsMatch(scrubbed), $"'{scrubbed}' still matches {pattern}");
        Assert.DoesNotContain("Erix", scrubbed);
    }

    [Theory]
    [InlineData(@"Launch C:\Users\Erix\Videos\Quantum Night\cut ok", "Quantum", "Night", "Videos")]
    [InlineData(@"Read \\DESKTOP-K3J9\My Share\Old Files\x.txt (done)", "Share", "Files", "Old")]
    [InlineData(@"Read /home/erix/My Documents/exo.json (done)", "Documents", "exo.json", "My")]
    public void A_path_with_spaces_is_taken_out_whole(string text, string first, string second, string third)
    {
        var scrubbed = CrashReport.Scrub(text, UserName, Profile, Machine, Secrets);
        Assert.Contains("<path>", scrubbed);
        foreach (var word in new[] { first, second, third }) Assert.DoesNotContain(word, scrubbed);
    }

    [Fact]
    public void The_machine_and_the_user_name_are_taken_out_wherever_they_are()
    {
        Assert.Equal("on <host> for <user> (<host>)", CrashReport.Scrub("on DESKTOP-K3J9 for Erix (desktop-k3j9)", UserName, Profile, Machine, []));
    }

    [Fact]
    public void An_error_named_in_the_text_becomes_the_error_type()
    {
        Assert.Equal("SmokeTestException", Build(Log, "Playback failed: SmokeTestException").ErrorType);
    }

    [Theory]
    [InlineData("Hang")]
    [InlineData("PlaybackFailed")]
    [InlineData("ServerUnreachable")]
    public void The_named_report_types_are_kept_as_given(string type)
    {
        var payload = CrashReport.Build("something", "[2026-11-05T19:42:10.1230000Z] WARN Player: slow.", "1.6.0", "10.0.26300.0", UserName, Profile, Machine, [], type);
        Assert.Equal(type, payload.ErrorType);
        Assert.True(CrashReport.Acceptable(payload));
    }

    [Fact]
    public void Without_a_named_error_the_type_is_AppError()
    {
        var payload = CrashReport.Build("Something broke", "[2026-11-05T19:42:10.1230000Z] WARN Player: slow.", "1.6.0", "10.0.26300.0", UserName, Profile, Machine, []);
        Assert.Equal("AppError", payload.ErrorType);
    }

    [Fact]
    public void Version_and_Windows_build_take_their_shape()
    {
        var payload = CrashReport.Build("x", "y", "1.6.0-beta.2", "10.0.26300.0", UserName, Profile, Machine, []);
        Assert.Equal("1.6.0", payload.Version);
        Assert.Equal("10.0.26300.0", payload.OsBuild);
        Assert.Equal("10.0", CrashReport.Build("x", "y", "1.6.0", "10.0", UserName, Profile, Machine, []).OsBuild);
        Assert.Equal("0.0.0", CrashReport.Build("x", "y", "dev", "10.0.26300.0", UserName, Profile, Machine, []).Version);
        Assert.Equal("cinema", payload.App);
    }

    [Fact]
    public void An_excerpt_never_goes_past_its_cap_even_when_lines_are_replaced()
    {
        // Short lines that the scrub replaces with a longer marker, many times over, must still stay under the cap.
        var lines = Enumerable.Range(0, 4000).Select(i => $"C:\\{i}").ToArray();
        var payload = CrashReport.Build("Playback failed: SmokeTestException", string.Join('\n', lines), "1.6.0", "10.0.26300.0", UserName, Profile, Machine, []);
        Assert.True(payload.Excerpt.Length <= CrashReport.MaxExcerpt);
        Assert.True(CrashReport.Acceptable(payload));
    }

    [Fact]
    public void Control_characters_never_fail_a_report()
    {
        var payload = Build(["[2026-11-05T19:42:10.1230000Z] WARN Player: bell\u0007 and a return\r here"]);
        Assert.False(Regex.IsMatch(payload.Excerpt, "[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]"));
        Assert.Contains("bell and a return here", payload.Excerpt);
        Assert.True(CrashReport.Acceptable(payload));
    }

    [Fact]
    public void The_JSON_holds_exactly_the_five_fields_with_no_install_id_and_the_endpoint_is_the_service_over_https()
    {
        var body = JsonNode.Parse(CrashReport.ToJson(Build(Log)))!.AsObject();
        Assert.Equal(new[] { "app", "errorType", "excerpt", "osBuild", "version" }, body.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal("cinema", body["app"]!.GetValue<string>());
        Assert.Equal("https://reports.getexo.app/v1/report", CrashReport.Endpoint);
    }

    [Fact]
    public async Task A_kept_report_is_sent_at_the_next_start_by_default()
    {
        using var _ = Isolated(out var folder);
        var transport = new FakeTransport(HttpStatusCode.Accepted);
        using var __ = FakeNetwork(transport);
        CrashReport.Keep(null, "xaml: SmokeTestException: boom", [], folder);
        Assert.Single(Directory.GetFiles(CrashReport.PendingDir(folder)));

        var sent = await CrashReport.SendPendingAsync(folder, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, sent);
        Assert.Single(transport.Posts);
        Assert.Equal("https://reports.getexo.app/v1/report", transport.Posts[0].Uri!.ToString());
        var body = JsonNode.Parse(transport.Posts[0].Body)!.AsObject();
        Assert.Equal("cinema", (string?)body["app"]);
        Assert.Contains("SmokeTest", (string?)body["errorType"]);
        Assert.Empty(Directory.GetFiles(CrashReport.PendingDir(folder)));
    }

    [Fact]
    public async Task A_test_copy_with_no_report_send_keeps_a_freeze_report_and_sends_none()
    {
        using var _ = Isolated(out var folder);
        var transport = new FakeTransport(HttpStatusCode.Accepted);
        using var __ = FakeNetwork(transport);
        var previous = Environment.GetEnvironmentVariable("EXO_CINEMA_NO_REPORT_SEND");
        Environment.SetEnvironmentVariable("EXO_CINEMA_NO_REPORT_SEND", "1");
        try
        {
            Assert.True(CrashReport.SendingOff);
            CrashReport.Keep("Hang", "Hang: the UI thread didn't answer for 60 s; running: start", [], folder, sendNow: true);
            await Task.Delay(200);
            Assert.Empty(transport.Posts);
            Assert.Single(Directory.GetFiles(CrashReport.PendingDir(folder)));
            Assert.Equal(0, await CrashReport.SendPendingAsync(folder, DateTime.UtcNow));
            Assert.Empty(transport.Posts);
            Assert.Single(Directory.GetFiles(CrashReport.PendingDir(folder)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXO_CINEMA_NO_REPORT_SEND", previous);
        }
    }

    [Fact]
    public async Task The_same_signature_is_sent_once_a_day()
    {
        using var _ = Isolated(out var folder);
        var transport = new FakeTransport(HttpStatusCode.Accepted);
        using var __ = FakeNetwork(transport);
        var day = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var payload = Build(Log);
        KeepFile(folder, payload);
        KeepFile(folder, payload);
        Assert.Equal(1, await CrashReport.SendPendingAsync(folder, day));

        KeepFile(folder, payload);
        Assert.Equal(0, await CrashReport.SendPendingAsync(folder, day.AddHours(3)));
        Assert.Single(transport.Posts);

        KeepFile(folder, payload);
        Assert.Equal(1, await CrashReport.SendPendingAsync(folder, day.AddDays(1)));
        Assert.Equal(2, transport.Posts.Count);
    }

    [Fact]
    public async Task No_more_than_five_reports_are_sent_in_a_day()
    {
        using var _ = Isolated(out var folder);
        var transport = new FakeTransport(HttpStatusCode.Accepted);
        using var __ = FakeNetwork(transport);
        for (var i = 0; i < 7; i++)
            KeepFile(folder, CrashReport.Build("Failed: SmokeTestException", $"[2026-11-05T19:42:10.1230000Z] WARN Player: case {(char)('a' + i)}", "1.6.0", "10.0.26300.0", UserName, Profile, Machine, []));

        Assert.Equal(5, await CrashReport.SendPendingAsync(folder, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(5, transport.Posts.Count);
        // The other two wait for tomorrow, where they are kept.
        Assert.Equal(2, Directory.GetFiles(CrashReport.PendingDir(folder)).Length);
        Assert.Equal(2, await CrashReport.SendPendingAsync(folder, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(7, transport.Posts.Count);
    }

    [Fact]
    public async Task A_refused_or_failed_report_is_logged_not_thrown()
    {
        using var _ = Isolated(out var folder);
        var transport = new FakeTransport(HttpStatusCode.UnprocessableEntity);
        using var __ = FakeNetwork(transport);
        CrashReport.Keep(null, "xaml: SmokeTestException: boom", [], folder);

        Assert.Equal(0, await CrashReport.SendPendingAsync(folder, DateTime.UtcNow));
        Assert.Single(transport.Posts);
        Assert.Contains(AppLog.Shared.Tail(50).Split('\n'), line => line.Contains("the service answered 422"));
    }

    [Fact]
    public async Task A_kept_report_that_no_longer_passes_the_check_is_not_sent()
    {
        using var _ = Isolated(out var folder);
        var transport = new FakeTransport(HttpStatusCode.Accepted);
        using var __ = FakeNetwork(transport);
        var dir = CrashReport.PendingDir(folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.json"), """{"app":"cinema","version":"1.6.0","osBuild":"10.0.26300.0","errorType":"SmokeTestException","excerpt":"mail pat@contoso.org"}""");

        Assert.Equal(0, await CrashReport.SendPendingAsync(folder, DateTime.UtcNow));
        Assert.Empty(transport.Posts);
    }

    /// <summary>A play whose failures carry the title in every message (a search, a server answer, a file's name) and in a
    /// folder: the title is in neither app.log nor the report's excerpt, and the failure is still named by its kind.</summary>
    [Fact]
    public void A_played_title_never_reaches_app_log_or_a_report_excerpt()
    {
        using var _ = Isolated(out var folder);
        AppLog.Shared.Write($"play: version 2 of 3 refused ({AppLog.Kind(new HttpRequestException($"GET {JellyfinServer}/Items?searchTerm={Title} failed"))})");
        AppLog.Shared.Write($"jellyfin search: {AppLog.Kind(new System.Text.Json.JsonException($"{Title} is not a film"))}");
        AppLog.Shared.Write($"stream: {AppLog.Kind(new InvalidDataException(Title))} ahead");
        AppLog.Shared.Write($"could not open D:\\Films\\{Title} (2010)\\cut.nfo");
        var lines = AppLog.Shared.Tail(200).Split('\n');
        Assert.DoesNotContain(Title, string.Join('\n', lines));
        Assert.Contains(lines, l => l.Contains("play: version 2 of 3 refused (no answer)", StringComparison.Ordinal));
        var payload = Build(lines);
        Assert.DoesNotContain(Title, payload.Excerpt);
        Assert.DoesNotContain(Title, payload.ErrorType);
    }

    [Fact]
    public void Keep_writes_nothing_to_the_owners_folder_and_keeps_one_signature_once_per_run()
    {
        using var _ = Isolated(out var folder);
        CrashReport.Keep("PlaybackFailed", "mpv ended the file with an error: Failed to recognize file format.", Log, folder);
        CrashReport.Keep("PlaybackFailed", "mpv ended the file with an error: Failed to recognize file format.", Log, folder);
        Assert.Single(Directory.GetFiles(CrashReport.PendingDir(folder)));
    }

    [Fact]
    public void The_watchdog_calls_a_freeze_after_five_seconds_and_not_before()
    {
        Assert.False(UiWatchdog.Stuck(TimeSpan.FromSeconds(4.9)));
        Assert.True(UiWatchdog.Stuck(TimeSpan.FromSeconds(5.1)));
    }

    [Fact]
    public void Samples_for_the_services_own_validator_are_written_when_asked()
    {
        // With EXO_CRASH_SAMPLES set to a folder, the samples go there for cloud/reports/src/validate.js to check (Node).
        var dir = Environment.GetEnvironmentVariable("EXO_CRASH_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var samples = new Dictionary<string, string>
        {
            ["log"] = CrashReport.ToJson(Build(Log)),
            ["stack"] = CrashReport.ToJson(Build(["   at ExoCinema.Player.Open(String path) in C:\\Users\\Erix\\Code\\x.cs:line 9", "Playback failed: SmokeTestException"])),
            ["quiet"] = CrashReport.ToJson(CrashReport.Build("Something broke", "[2026-11-05T19:42:10.1230000Z] WARN Player: slow.", "1.6.0", "10.0", UserName, Profile, Machine, [])),
            ["playback"] = CrashReport.ToJson(CrashReport.Build("PlaybackFailed", "mpv ended the file with an error: Failed to recognize file format.", "1.6.0", "10.0.26300.0", UserName, Profile, Machine, [], "PlaybackFailed")),
            ["hang"] = CrashReport.ToJson(CrashReport.Build("Hang: the UI thread didn't answer for 6 s; running: jf settings", "running: jf settings", "1.6.0", "10.0.26300.0", UserName, Profile, Machine, [], "Hang")),
            ["smoke"] = CrashReport.ToJson(CrashReport.Build("SmokeTestException: forced", "SmokeTestException: forced", "1.6.0", "10.0.26300.0", UserName, Profile, Machine, [])),
        };
        foreach (var (name, json) in samples) File.WriteAllText(Path.Combine(dir, name + ".json"), json, Encoding.UTF8);
    }
}
