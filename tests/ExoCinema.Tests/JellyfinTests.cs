using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoCinema.Jellyfin;
using ExoCinema.Library;
using ExoCinema.Net;

namespace ExoCinema.Tests;

/// <summary>The Jellyfin account, against a small server on this PC that answers as Jellyfin's API describes: Connect's
/// results, the rows, the shapes the page draws, and what's reported back. No real server is signed in to.</summary>
public sealed class JellyfinTests : IAsyncLifetime
{
    private const string Password = "fake-password-for-tests", Token = "feedfacefeedfacefeedfacefeedface", UserId = "aaaa000000000000000000000000000a";

    private readonly FakeServer _server = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-jellyfin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePlayer _player = new();
    private JellyfinLibrary _library = null!;

    public ValueTask InitializeAsync()
    {
        Egress.SignedInTo(null);
        _library = New();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Egress.SignedInTo(null);
        _server.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        return ValueTask.CompletedTask;
    }

    private JellyfinLibrary New() => new(_player, new AccountStore(_folder), () => GraphicsCard.Unknown);

    private Task<JsonNode?> Connect(string? password = Password) =>
        _library.HandleAsync("jellyfin-connect", new JsonObject { ["server"] = _server.Url, ["name"] = "erix", ["password"] = password });

    private static async Task<string> Refused(Task task) => (await Assert.ThrowsAnyAsync<LibraryException>(() => task)).Message;

    // ---- Connect

    [Fact]
    public async Task Connect_succeeds_only_after_the_session_reads_the_account()
    {
        var me = await Connect();
        Assert.Equal("erix", (string?)me?["userName"]);
        Assert.Equal("JLLY test", (string?)me?["serverName"]);
        Assert.True(_server.Saw("POST", "/Users/AuthenticateByName"));
        var read = _server.Requests.Single(r => r.Path == "/Users/Me");
        Assert.Contains($"Token=\"{Token}\"", read.Auth);
        Assert.True(_library.SignedIn);
    }

    [Fact]
    public async Task The_public_answer_alone_never_connects()
    {
        _server.AuthStatus = HttpStatusCode.Unauthorized;
        Assert.Equal("Wrong username or password.", await Refused(Connect("wrong-for-the-test")));
        Assert.True(_server.Saw("GET", "/System/Info/Public"));
        Assert.False(_library.SignedIn);
    }

    [Fact]
    public async Task A_server_that_refuses_the_session_after_sign_in_is_not_connected_and_nothing_is_kept()
    {
        _server.MeStatus = HttpStatusCode.Unauthorized;
        var said = await Refused(Connect());
        Assert.Contains("took the sign-in", said);
        Assert.False(_library.SignedIn);
        Assert.Null(new AccountStore(_folder).Current());
        Assert.True(_server.Saw("POST", "/Sessions/Logout"));
    }

    [Fact]
    public async Task An_address_nothing_answers_at_says_it_can_t_be_reached()
    {
        var nobody = FreePort();
        var said = await Refused(_library.HandleAsync("jellyfin-connect", new JsonObject { ["server"] = $"http://localhost:{nobody}", ["name"] = "erix", ["password"] = Password }));
        Assert.StartsWith("Can't reach localhost", said);
    }

    [Fact]
    public async Task Something_that_isn_t_Jellyfin_says_so_before_the_password_goes_anywhere()
    {
        _server.PublicInfo = """{"Id":"1","Version":"4.8","ProductName":"Emby Server"}""";
        Assert.Equal("That address isn't a Jellyfin server.", await Refused(Connect()));
        _server.PublicInfo = "<html>a router's page</html>";
        Assert.Equal("That address isn't a Jellyfin server.", await Refused(Connect()));
        Assert.False(_server.Saw("POST", "/Users/AuthenticateByName"));
    }

    [Fact]
    public async Task A_server_error_says_it_was_the_server()
    {
        _server.AuthStatus = HttpStatusCode.InternalServerError;
        Assert.Equal("The server answered with an error (500).", await Refused(Connect()));
        _server.AuthStatus = HttpStatusCode.OK;
        _server.PublicStatus = HttpStatusCode.BadGateway;
        Assert.Equal("The server answered with an error (502).", await Refused(Connect()));
        Assert.False(_library.SignedIn);
    }

    [Fact]
    public async Task One_sign_in_lasts_across_restarts_with_only_the_sealed_token_kept()
    {
        await Connect();
        var file = File.ReadAllText(Path.Combine(_folder, "jellyfin.json"));
        Assert.DoesNotContain(Token, file);
        Assert.DoesNotContain(Password, file);

        Egress.SignedInTo(null);
        _library = New();
        Assert.True(_library.SignedIn);
        Assert.Equal(Token, _library.Token);
        var rows = await _library.RowsAsync();
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task A_token_the_server_no_longer_takes_ends_the_account_here()
    {
        await Connect();
        _server.MeStatus = HttpStatusCode.Unauthorized;
        var state = await _library.HandleAsync("jellyfin", null);
        Assert.True((bool?)state?["expired"]);
        Assert.False(_library.SignedIn);
        Assert.Null(new AccountStore(_folder).Current());
    }

    [Fact]
    public async Task A_server_that_is_down_leaves_the_account_and_says_offline()
    {
        await Connect();
        _server.MeStatus = HttpStatusCode.BadGateway;
        Assert.True((bool?)(await _library.HandleAsync("jellyfin", null))?["offline"]);
        Assert.True(_library.SignedIn);
    }

    // ---- Offline means unreachable: a slow answer isn't, one dropped connection isn't, two in a row are, and it recovers.
    private int MeCalls() => _server.Log.Count(r => r.Path == "/Users/Me");

    [Fact]
    public async Task An_answer_after_six_seconds_is_not_offline()
    {
        await Connect();
        _server.MeDelay = TimeSpan.FromSeconds(6);
        var state = await _library.HandleAsync("jellyfin", null);
        Assert.False((bool?)state?["offline"] ?? false);
        Assert.Equal("erix", (string?)state?["userName"]);
    }

    [Fact]
    public async Task One_dropped_connection_then_an_answer_is_not_offline()
    {
        await Connect();
        var before = MeCalls();
        _server.DropMe = 1;
        var state = await _library.HandleAsync("jellyfin", null);
        Assert.False((bool?)state?["offline"] ?? false);
        Assert.Equal("erix", (string?)state?["userName"]);
        Assert.Equal(2, MeCalls() - before);
    }

    [Fact]
    public async Task Two_failed_reads_in_a_row_are_offline_and_app_log_gets_one_line_with_no_address()
    {
        await Connect();
        var lines = new List<string>();
        _library.Log += lines.Add;
        var before = MeCalls();
        _server.DropMe = 2;
        var state = await _library.HandleAsync("jellyfin", null);
        Assert.True((bool?)state?["offline"]);
        Assert.Equal(2, MeCalls() - before);
        Assert.True(_library.SignedIn);
        var line = Assert.Single(lines);
        Assert.StartsWith("jellyfin account: connection error, ", line);
        Assert.EndsWith(" ms, retried once", line);
        Assert.DoesNotContain("localhost", line);
        Assert.DoesNotContain(Token, line);
    }

    [Fact]
    public async Task An_offline_server_recovers_by_itself_once_it_answers()
    {
        await Connect();
        _server.DropMe = 2;
        Assert.True((bool?)(await _library.HandleAsync("jellyfin", null))?["offline"]);
        var back = await _library.HandleAsync("jellyfin", null);
        Assert.False((bool?)back?["offline"] ?? false);
        Assert.Equal("erix", (string?)back?["userName"]);
    }

    [Fact]
    public async Task A_401_after_a_dropped_try_still_forgets_the_account()
    {
        await Connect();
        _server.DropMe = 1;
        _server.MeStatus = HttpStatusCode.Unauthorized;
        var state = await _library.HandleAsync("jellyfin", null);
        Assert.True((bool?)state?["expired"]);
        Assert.False(_library.SignedIn);
        Assert.Null(new AccountStore(_folder).Current());
    }

    [Fact]
    public async Task A_request_the_egress_checkpoint_refuses_is_offline_but_never_retried_or_sent()
    {
        await Connect();
        var lines = new List<string>();
        _library.Log += lines.Add;
        var before = MeCalls();
        Egress.SignedInTo(null);
        var state = await _library.HandleAsync("jellyfin", null);
        Assert.True((bool?)state?["offline"]);
        Assert.Equal(before, MeCalls());
        var line = Assert.Single(lines);
        Assert.StartsWith("jellyfin account: refused by Egress, ", line);
        Assert.DoesNotContain("localhost", line);
    }

    [Fact]
    public async Task Airing_soon_that_is_late_is_filled_in_on_the_next_read_not_dropped()
    {
        await Connect();
        _server.UpcomingDelay = TimeSpan.FromSeconds(6);
        var (_, _, _, first, _) = await _library.WatchAsync();
        Assert.Empty(first);
        Assert.True(_library.WatchLate);
        var (_, _, _, second, _) = await _library.WatchAsync();
        Assert.Equal("jf:up1", (string?)Assert.Single(second)["id"]);
        Assert.False(_library.WatchLate);
        Assert.Equal(1, _server.Log.Count(r => r.Path == "/Shows/Upcoming"));
    }

    [Fact]
    public async Task Rooms_read_twice_in_a_row_can_each_go_out_in_a_reply()
    {
        await Connect();
        var (going, _, _, airing, _) = await _library.WatchAsync();
        var first = new JsonArray([.. going, .. airing]);
        var (again, _, _, airingAgain, _) = await _library.WatchAsync();
        var second = new JsonArray([.. again, .. airingAgain]);
        Assert.Equal(2, first.Count);
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public async Task Airing_soon_logs_how_many_came_and_how_long_it_took()
    {
        await Connect();
        var lines = new List<string>();
        _library.Log += lines.Add;
        await _library.WatchAsync();
        Assert.Single(lines, l => l.StartsWith("jellyfin airing: 1 upcoming, 1 shown, ", StringComparison.Ordinal) && l.EndsWith(" ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Airing_soon_says_only_the_season_and_episode_the_server_gives()
    {
        var c = new JellyfinClient("https://jelly.example", "dev");
        var upcoming = JsonNode.Parse("""{"Items":[{"Id":"x1","SeriesId":"s1","SeriesName":"Show X","PremiereDate":"2026-11-06T02:30:00.0000000Z"},{"Id":"x2","SeriesId":"s1","SeriesName":"Show X","ParentIndexNumber":0,"IndexNumber":3,"PremiereDate":"2026-11-06T02:30:00.0000000Z"}]}""");
        var rows = JellyfinLibrary.AiringRow(c, upcoming, Now, Five);
        Assert.Null(rows[0]["season"]);
        Assert.Null(rows[0]["episode"]);
        Assert.Equal(0, (int?)rows[1]["season"]);
        Assert.Equal(3, (int?)rows[1]["episode"]);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_keeps_the_device()
    {
        await Connect();
        var device = new AccountStore(_folder).DeviceId;
        await _library.HandleAsync("jellyfin-sign-out", null);
        Assert.True(_server.Saw("POST", "/Sessions/Logout"));
        Assert.False(_library.SignedIn);
        Assert.Null(new AccountStore(_folder).Current());
        Assert.Equal(device, new AccountStore(_folder).DeviceId);
    }

    [Fact]
    public async Task The_password_goes_only_in_the_sign_in()
    {
        await Connect();
        await _library.RowsAsync();
        var with = _server.Requests.Where(r => r.Body.Contains(Password) || r.Query.Contains(Password) || r.Auth.Contains(Password)).ToList();
        Assert.Equal("/Users/AuthenticateByName", Assert.Single(with).Path);
    }

    // ---- Where it connects

    [Fact]
    public async Task Only_the_server_signed_in_to_is_reachable()
    {
        var there = new Uri(_server.Url + "/Items");
        Assert.False(Egress.Allowed(there, Egress.Purpose.Server));
        await Connect();
        Assert.True(Egress.Allowed(there, Egress.Purpose.Server));
        Assert.False(Egress.Allowed(new Uri("http://other.example/Items"), Egress.Purpose.Server));
        Assert.False(Egress.Allowed(new Uri($"https://localhost:{there.Port}/Items"), Egress.Purpose.Server));
        Assert.False(Egress.Allowed(new Uri($"http://localhost:{there.Port + 1}/Items"), Egress.Purpose.Server));
        Assert.False(Egress.Allowed(new Uri($"http://me:x@localhost:{there.Port}/Items"), Egress.Purpose.Server));
        await _library.HandleAsync("jellyfin-sign-out", null);
        Assert.False(Egress.Allowed(there, Egress.Purpose.Server));
    }

    [Fact]
    public void Exo_and_the_fixed_hosts_are_never_a_server_even_when_named_as_one()
    {
        foreach (var host in new[] { "https://updates.getexo.app", "https://getexo.app", "https://accounts.getexo.app" })
        {
            Egress.SignedInTo(host);
            Assert.False(Egress.Allowed(new Uri(host + "/x"), Egress.Purpose.Server), host);
        }
    }

    [Fact]
    public async Task Trying_an_address_opens_it_for_that_call_alone()
    {
        var there = new Uri(_server.Url + "/Items");
        await JellyfinClient.ConnectAsync(_server.Url, "device", TestContext.Current.CancellationToken);
        Assert.False(Egress.Allowed(there, Egress.Purpose.Server));
    }

    [Fact]
    public async Task A_redirect_is_never_followed_with_the_password_nor_down_to_http()
    {
        await Connect();
        await _library.HandleAsync("jellyfin-sign-out", null);
        _server.Requests.Clear();
        _server.RedirectSignInTo = "http://other.example/Users/AuthenticateByName";
        Assert.Equal("The server answered with an error (302).", await Refused(Connect()));
    }

    [Theory]
    [InlineData("http://192.168.1.20:8096", true)]
    [InlineData("http://10.0.0.2", true)]
    [InlineData("http://172.20.0.2", true)]
    [InlineData("http://jellyfin.local", true)]
    [InlineData("http://nas", true)]
    [InlineData("http://127.0.0.1:8096", true)]
    [InlineData("http://jlly.cc", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://172.32.0.2", false)]
    public void Only_a_server_at_home_is_tried_over_plain_http_by_itself(string server, bool home) => Assert.Equal(home, JellyfinClient.AtHome(new Uri(server)));

    // ---- What the page draws

    [Fact]
    public async Task Rows_are_each_library_s_newest_with_its_whole_list_for_the_rooms()
    {
        await Connect();
        var rows = await _library.RowsAsync();
        Assert.Contains(rows, r => (string?)r["title"] == "New in Films" && (string?)r["id"] == "latest/view-films" && (bool?)r["room"] == false);
        Assert.Contains(rows, r => (string?)r["type"] == "movie" && (string?)r["id"] == "library/view-films" && (bool?)r["room"] == true);
        Assert.Contains(rows, r => (string?)r["type"] == "series" && (string?)r["id"] == "library/view-shows" && (bool?)r["room"] == true);
        Assert.DoesNotContain(rows, r => ((string?)r["id"])!.Contains("view-music"));
    }

    [Fact]
    public async Task Cards_carry_jf_ids_and_pictures_without_the_token()
    {
        await Connect();
        var latest = (JsonArray)(await _library.HandleAsync("catalog", new JsonObject { ["type"] = "latest", ["id"] = "latest/view-films" }))!;
        var card = latest[0]!;
        Assert.Equal("jf:film1", (string?)card["id"]);
        Assert.Equal("movie", (string?)card["type"]);
        Assert.Equal("1 h 47 min", (string?)card["runtime"]);
        Assert.Equal(_server.Url + "/Items/film1/Images/Primary?tag=p1&maxWidth=1920", (string?)card["poster"]);
        Assert.DoesNotContain(Token, latest.ToJsonString());

        var room = await _library.HandleAsync("catalog", new JsonObject { ["type"] = "series", ["id"] = "library/view-shows", ["skip"] = 60 });
        Assert.Equal("show1", (string?)((JsonArray)room!)[0]!["id"] is { } id ? id[3..] : null);
        var asked = _server.Requests.Last(r => r.Path == "/Items");
        Assert.Contains("includeItemTypes=Series", asked.Query);
        Assert.Contains("startIndex=60", asked.Query);
    }

    [Fact]
    public async Task A_new_episode_in_a_latest_row_stands_for_its_show()
    {
        await Connect();
        var latest = (JsonArray)(await _library.HandleAsync("catalog", new JsonObject { ["type"] = "latest", ["id"] = "latest/view-shows" }))!;
        Assert.Equal("jf:show1", (string?)latest[0]!["id"]);
        Assert.Equal("series", (string?)latest[0]!["type"]);
        Assert.Equal("The Show", (string?)latest[0]!["name"]);
    }

    [Fact]
    public async Task The_watch_rows_and_rooms_are_the_server_s_continue_next_up_favorites_and_played()
    {
        await Connect();
        var (going, next, saved, airing, played) = await _library.WatchAsync();
        // History: what was played, by the day it was played; one marked played with no day has no place in it.
        var watched = Assert.Single(played);
        Assert.Equal("jf:ep1", (string?)watched["id"]);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T20:00:00Z").ToUnixTimeMilliseconds(), (long?)watched["at"]);
        var asked = _server.Requests.Last(r => r.Path == "/Items" && r.Query.Contains("isPlayed=true")).Query;
        Assert.Contains("sortBy=DatePlayed", asked);
        Assert.Contains("sortOrder=Descending", asked);
        Assert.Contains("includeItemTypes=Movie,Episode", asked);
        var e = Assert.Single(going);
        Assert.Equal("jf:ep2", (string?)e["id"]);
        Assert.Equal("jf:show1", (string?)e["showId"]);
        Assert.Equal("The Show · S1:E2 Second", (string?)e["title"]);
        Assert.Equal(600_000, (long?)e["position"]?["ms"]);
        Assert.Equal(2_400_000, (long?)e["position"]?["runtime"]);
        Assert.Equal("jf:ep3", (string?)Assert.Single(next)["id"]);
        Assert.Equal("jf:film1", (string?)Assert.Single(saved)["id"]);
        Assert.Equal("jf:up1", (string?)Assert.Single(airing)["id"]);
    }
    [Fact]
    public async Task Airing_soon_is_the_servers_upcoming_episodes_as_show_cards_that_never_play()
    {
        await Connect();
        var (_, _, _, airing, _) = await _library.WatchAsync();
        var card = Assert.Single(airing);
        var asked = _server.Requests.Last(r => r.Path == "/Shows/Upcoming").Query;
        Assert.Contains($"userId={UserId}", asked);
        Assert.Contains("limit=24", asked);
        Assert.Contains("enableImages=true", asked);
        Assert.Contains("fields=Overview,Genres", asked);
        Assert.Equal("series", (string?)card["type"]);
        Assert.Equal("jf:show1", (string?)card["showId"]);
        Assert.Equal("The Show", (string?)card["title"]);
        Assert.Equal(1, (int?)card["season"]);
        Assert.Equal(2, (int?)card["episode"]);
        Assert.False(string.IsNullOrEmpty((string?)card["airDay"]));
        Assert.Null(card["position"]);
    }
    [Fact]
    public async Task Airing_soon_is_not_drawn_when_the_server_fails_or_has_nothing()
    {
        await Connect();
        _server.UpcomingStatus = HttpStatusCode.InternalServerError;
        var (going, _, _, failed, _) = await _library.WatchAsync();
        Assert.Empty(failed);
        Assert.NotEmpty(going);
        _server.UpcomingStatus = HttpStatusCode.OK;
        _server.UpcomingEmpty = true;
        var (_, _, _, none, _) = await _library.WatchAsync();
        Assert.Empty(none);
    }

    // Thursday 5 Nov 2026 at noon UTC, in a zone five hours behind UTC (no time zone database needed).
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Five = TimeZoneInfo.CreateCustomTimeZone("Five behind", TimeSpan.FromHours(-5), "Five behind", "Five behind");
    [Theory]
    [InlineData(5, "Today")]
    [InlineData(6, "Tomorrow")]
    [InlineData(4, "Yesterday")]
    [InlineData(8, "Sun")]
    [InlineData(11, "Wed")]
    [InlineData(12, "Nov 12")]
    [InlineData(3, "Nov 3")]
    public void Air_days_say_today_tomorrow_yesterday_the_weekday_or_the_date(int day, string words) =>
        Assert.Equal(words, JellyfinLibrary.AirWords(new DateOnly(2026, 11, day), new DateOnly(2026, 11, 5)));
    [Fact]
    public void Air_time_is_the_local_time_and_a_date_only_airs_on_its_day_with_no_time()
    {
        // 02:30 UTC on the 6th is 21:30 on the 5th here: today, at 21:30.
        Assert.Equal((new DateOnly(2026, 11, 5), "21:30"), JellyfinLibrary.AirDay(DateTimeOffset.Parse("2026-11-06T02:30:00Z", System.Globalization.CultureInfo.InvariantCulture), Five));
        // Midnight UTC is the server's day only: the 6th, no time, not the 5th at 19:00 here.
        Assert.Equal((new DateOnly(2026, 11, 6), (string?)null), JellyfinLibrary.AirDay(DateTimeOffset.Parse("2026-11-06T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), Five));
    }
    [Fact]
    public void Airing_rows_are_parsed_from_the_server_and_a_bad_episode_is_left_out()
    {
        var c = new JellyfinClient("https://jelly.example", "dev");
        var upcoming = JsonNode.Parse("""
        {"Items":[
          {"Id":"a","SeriesId":"s1","SeriesName":"Show A","ParentIndexNumber":2,"IndexNumber":5,"PremiereDate":"2026-11-06T02:30:00.0000000Z"},
          {"Id":"b","SeriesId":"s2","SeriesName":"Show B","PremiereDate":"2026-11-08T00:00:00.0000000Z"},
          {"Id":"c","SeriesName":"No series id","PremiereDate":"2026-11-06T00:00:00.0000000Z"},
          {"Id":"d","SeriesId":"s3","SeriesName":"No date"}
        ]}
        """);
        var rows = JellyfinLibrary.AiringRow(c, upcoming, Now, Five);
        Assert.Equal(2, rows.Count);
        Assert.Equal("jf:a", (string?)rows[0]["id"]);
        Assert.Equal("jf:s1", (string?)rows[0]["showId"]);
        Assert.Equal(2, (int?)rows[0]["season"]);
        Assert.Equal(5, (int?)rows[0]["episode"]);
        Assert.Equal("Today", (string?)rows[0]["airDay"]);
        Assert.Equal("21:30", (string?)rows[0]["airTime"]);
        Assert.Equal("Sun", (string?)rows[1]["airDay"]);
        Assert.Null(rows[1]["airTime"]);
        Assert.Empty(JellyfinLibrary.AiringRow(c, null, Now, Five));
        Assert.Empty(JellyfinLibrary.AiringRow(c, JsonNode.Parse("{}"), Now, Five));
    }
    [Fact]
    public async Task A_show_s_page_has_its_episodes_with_the_server_s_played_and_position()
    {
        await Connect();
        var page = await _library.HandleAsync("meta", new JsonObject { ["type"] = "series", ["id"] = "jf:show1" });
        Assert.Equal("series", (string?)page?["type"]);
        Assert.True((bool?)page?["saved"]);
        var videos = (JsonArray)page!["videos"]!;
        Assert.Equal(3, videos.Count);
        Assert.NotNull(videos[0]!["watchedAt"]);
        Assert.Equal(600_000, (long?)videos[1]!["position"]?["ms"]);
        Assert.Null(videos[2]!["watchedAt"]);
    }

    [Fact]
    public async Task Marked_without_a_day_it_is_still_played()
    {
        await Connect();
        var page = await _library.HandleAsync("meta", new JsonObject { ["type"] = "movie", ["id"] = "jf:film2" });
        Assert.Equal(1, (long?)page?["watchedAt"]);
    }

    [Fact]
    public async Task Search_finds_the_server_s_films_and_shows()
    {
        await Connect();
        var found = await _library.SearchAsync("the show", CancellationToken.None);
        Assert.Equal("jf:show1", (string?)found[0]!["id"]);
        Assert.Contains("searchTerm=the%20show", _server.Requests.Last(r => r.Path == "/Items").Query);
    }

    // ---- Playing and reporting

    private async Task<JsonNode?> Play(string id = "jf:film1", bool resume = true, int? source = null) =>
        await _library.HandleAsync("play", new JsonObject { ["id"] = id, ["type"] = "movie", ["resume"] = resume, ["source"] = source });

    [Fact]
    public async Task Plays_the_original_file_from_the_server_s_position_never_asking_to_convert()
    {
        await Connect();
        var answer = await Play();
        Assert.Equal(_server.Url + "/Videos/film1/stream?static=true&mediaSourceId=src-big", _player.Url);
        Assert.Contains($"Token=\"{Token}\"", _player.Auth);
        Assert.Equal(1234.5, _player.Start);
        Assert.Equal(1234.5, (double?)answer?["start"]);
        Assert.DoesNotContain(Token, _player.Url);
        var info = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/Items/film1/PlaybackInfo").Body)!;
        Assert.False((bool?)info["EnableTranscoding"]);
        Assert.Null(info["DeviceProfile"]?["TranscodingProfiles"]);
        // Nothing of it for Exo Watch: the item has no public id.
        Assert.Null(answer?["item"]?["publicId"]);
    }

    [Fact]
    public async Task From_the_start_and_a_picked_version_are_honoured()
    {
        await Connect();
        await Play(resume: false, source: 1);
        Assert.Equal(0, _player.Start);
        Assert.EndsWith("mediaSourceId=src-small", _player.Url);
    }

    [Fact]
    public async Task A_version_at_another_address_never_gets_the_token()
    {
        await Connect();
        await Play("jf:remote");
        Assert.Null(_player.Auth);
    }

    // ---- A version that won't open before its first frame
    [Fact]
    public async Task A_version_that_wont_open_before_its_first_frame_moves_to_the_next_ranked_one()
    {
        await Connect();
        await Play();
        Assert.True(_library.TryNextVersion("loading failed"));
        await AwaitPlays(2);
        Assert.EndsWith("mediaSourceId=src-small", _player.Url);
        Assert.Equal(1234.5, _player.Start);
        Assert.Contains($"Token=\"{Token}\"", _player.Auth);
        // Its reports name the version that plays now.
        _library.OnPlaybackRestart();
        await _server.WaitFor("/Sessions/Playing", 1);
        var start = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/Sessions/Playing").Body)!;
        Assert.Equal("src-small", (string?)start["MediaSourceId"]);
    }

    [Fact]
    public async Task A_version_the_user_picked_in_the_sheet_never_moves()
    {
        await Connect();
        await Play(resume: false, source: 1);
        Assert.False(_library.TryNextVersion("loading failed"));
        Assert.Single(_player.Urls);
    }

    [Fact]
    public async Task A_failure_after_playback_began_does_not_move()
    {
        await Connect();
        await Play();
        _library.OnPlaybackRestart();
        Assert.False(_library.TryNextVersion("loading failed"));
        Assert.Single(_player.Urls);
    }

    [Fact]
    public async Task Versions_are_tried_up_to_the_bound_and_then_the_failure_is_said_once()
    {
        await Connect();
        var failures = new List<string>();
        _library.PlayFailed += failures.Add;
        await Play("jf:many");
        for (var k = 2; k <= JellyfinLibrary.VersionTries; k++)
        {
            Assert.True(_library.TryNextVersion("loading failed"));
            await AwaitPlays(k);
        }
        // Six versions are listed; the bound stops at five and says what the server said last.
        Assert.True(_library.TryNextVersion("loading failed"));
        Assert.Equal(JellyfinLibrary.VersionTries, _player.Urls.Count);
        Assert.Equal("loading failed", Assert.Single(failures));
    }

    [Fact]
    public async Task The_next_version_gets_the_token_only_when_it_is_on_this_server()
    {
        await Connect();
        await Play("jf:mixed");
        Assert.Contains($"Token=\"{Token}\"", _player.Auth);
        Assert.True(_library.TryNextVersion("loading failed"));
        await AwaitPlays(2);
        Assert.Contains("/file.mkv", _player.Url);
        Assert.Null(_player.Auth);
        Assert.DoesNotContain(Token, _player.Url);
    }

    [Fact]
    public async Task A_title_with_one_version_says_what_the_server_said_when_it_wont_open()
    {
        await Connect();
        var failures = new List<string>();
        _library.PlayFailed += failures.Add;
        await Play("jf:single");
        Assert.True(_library.TryNextVersion("loading failed"));
        Assert.Single(_player.Urls);
        Assert.Equal(
            "No streams · ⚠️ \"Wildwood\" no digital release yet (closest: Dec 8, 2026, 33d away); loading failed",
            Assert.Single(failures));
    }

    private async Task AwaitPlays(int count)
    {
        for (var k = 0; k < 500 && _player.Urls.Count < count; k++) await Task.Delay(10);
        Assert.Equal(count, _player.Urls.Count);
    }

    [Fact]
    public async Task Playback_is_reported_at_start_pause_and_stop_on_the_cadence_of_Exo_Watch()
    {
        Assert.Equal(30_000, JellyfinLibrary.ReportEveryMs);
        await Connect();
        await Play();
        _library.OnPlaybackRestart();
        await _server.WaitFor("/Sessions/Playing", 1);
        _library.OnTime(1300);
        _library.OnPause(true);
        await _server.WaitFor("/Sessions/Playing/Progress", 1);
        await _library.CancelPlayAsync();
        var start = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/Sessions/Playing").Body)!;
        Assert.Equal("film1", (string?)start["ItemId"]);
        Assert.Equal("src-big", (string?)start["MediaSourceId"]);
        Assert.Equal("session-1", (string?)start["PlaySessionId"]);
        Assert.Equal("DirectPlay", (string?)start["PlayMethod"]);
        Assert.Equal(12_345_000_000, (long?)start["PositionTicks"]);
        var paused = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/Sessions/Playing/Progress").Body)!;
        Assert.True((bool?)paused["IsPaused"]);
        Assert.Equal(13_000_000_000, (long?)paused["PositionTicks"]);
        var stopped = JsonNode.Parse(_server.Requests.Single(r => r.Path == "/Sessions/Playing/Stopped").Body)!;
        Assert.Equal(13_000_000_000, (long?)stopped["PositionTicks"]);
    }

    [Fact]
    public async Task A_play_that_never_started_reports_nothing()
    {
        await Connect();
        await Play();
        await _library.CancelPlayAsync();
        Assert.False(_server.Saw("POST", "/Sessions/Playing/Stopped"));
    }

    [Fact]
    public async Task Played_and_favorites_are_written_to_the_server()
    {
        await Connect();
        await _library.HandleAsync("played", new JsonObject { ["id"] = "jf:film1", ["on"] = true });
        await _library.HandleAsync("played", new JsonObject { ["id"] = "jf:film1", ["on"] = false });
        await _library.HandleAsync("watch-save", new JsonObject { ["id"] = "jf:film1", ["on"] = true });
        await _library.HandleAsync("watch-save", new JsonObject { ["id"] = "jf:film1", ["on"] = false });
        Assert.True(_server.Saw("POST", "/UserPlayedItems/film1"));
        Assert.True(_server.Saw("DELETE", "/UserPlayedItems/film1"));
        Assert.True(_server.Saw("POST", "/UserFavoriteItems/film1"));
        Assert.True(_server.Saw("DELETE", "/UserFavoriteItems/film1"));
    }

    [Fact]
    public async Task The_next_episode_comes_from_the_show_s_order()
    {
        await Connect();
        var next = await _library.HandleAsync("next", new JsonObject { ["id"] = "jf:ep2" });
        Assert.Equal("jf:ep3", (string?)next?["id"]);
        Assert.Equal("jf:show1", (string?)next?["showId"]);
        Assert.Null(await _library.HandleAsync("next", new JsonObject { ["id"] = "jf:film1" }));
    }

    // ---- Which calls are the server's, and what clears old sign-ins

    // A title Exo Watch kept before 1.5 is named by an IMDb id, which is no item of the server's: nothing is asked for it.
    [Theory]
    [InlineData("meta", "tt0111161")]
    [InlineData("play", "tt0903747:1:2")]
    [InlineData("sources", "tt0111161")]
    [InlineData("played", "tt0111161")]
    [InlineData("watch-save", "kitsu:46474")]
    [InlineData("next", "jf:")]
    public async Task A_title_that_isn_t_the_server_s_is_refused_and_the_server_is_not_asked(string op, string id)
    {
        await Connect();
        _server.Requests.Clear();
        Assert.Equal("This isn't on your Jellyfin server.", await Refused(_library.HandleAsync(op, new JsonObject { ["id"] = id })));
        Assert.Empty(_server.Requests);
        Assert.Null(_player.Url);
    }

    // ---- The library as the page asks it (Library/CinemaLibrary.cs): the server, and what Exo Watch kept before 1.5

    private CinemaLibrary Cinema(Watch.WatchLedger ledger) => new(_player, _library, () => ledger, () => 7, () => 0);

    [Fact]
    public async Task With_no_server_signed_in_there_is_no_library_and_nothing_is_asked_anywhere()
    {
        var cinema = Cinema(new Watch.WatchLedger());
        Assert.False(cinema.Jellyfin.SignedIn);
        Assert.Empty((JsonArray)(await cinema.HandleAsync("rows", null))!);
        Assert.Empty((JsonArray)(await cinema.HandleAsync("search", new JsonObject { ["term"] = "the film" }))!);
        var watch = (await cinema.HandleAsync("watch", null))!;
        foreach (var part in new[] { "continue", "nextUp", "airing", "watchlist", "history" }) Assert.Empty((JsonArray)watch[part]!);
        Assert.Equal("Not signed in to Jellyfin.", await Refused(cinema.HandleAsync("meta", new JsonObject { ["id"] = "tt0111161" })));
        Assert.Equal("Not signed in to Jellyfin.", await Refused(cinema.HandleAsync("catalog", new JsonObject { ["type"] = "movie", ["id"] = "library/view-films" })));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task What_Exo_Watch_kept_before_1_5_is_read_by_name_beside_the_server_s_and_left_as_it_was()
    {
        // As a PC that played from other sources left it: a film and an episode watched, a film saved, a film part way.
        var ledger = new Watch.WatchLedger();
        ledger.Watched("imdb:tt0111161", "The Shawshank Redemption", new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc), "cinema");
        ledger.Watched("imdb:tt0903747:s01e02", "Breaking Bad · S1:E2 Cat's in the Bag...", new DateTime(2026, 11, 3, 20, 0, 0, DateTimeKind.Utc), "cinema");
        ledger.Saved("tmdb:movie:603", "The Matrix", "cinema");
        ledger.Position("imdb:tt0133093", "The Matrix", 600_000, 8_160_000, new DateTime(2026, 11, 4, 20, 0, 0, DateTimeKind.Utc), "cinema");
        var before = ledger.ToJson().ToJsonString();
        await Connect();
        _server.Requests.Clear();

        var watch = (await Cinema(ledger).HandleAsync("watch", null))!;

        // History by day, newest first: the episode kept on 3 Nov, the server's on 1 Nov, the film kept on 1 Oct.
        var history = ((JsonArray)watch["history"]!).Select(e => (Id: (string?)e!["id"], Title: (string?)e["title"])).ToList();
        Assert.Equal([(null, "Breaking Bad · S1:E2 Cat's in the Bag..."), ("jf:ep1", "The Show · S1:E1 First"), (null, "The Shawshank Redemption")], history);
        // The Watchlist: the server's favorites, then what was saved before.
        var saved = ((JsonArray)watch["watchlist"]!).Select(e => (Id: (string?)e!["id"], Title: (string?)e["title"])).ToList();
        Assert.Equal([("jf:film1", "The Film"), (null, "The Matrix")], saved);
        // Continue watching is the server's alone: only it knows where its own file was left.
        Assert.Equal("jf:ep2", (string?)Assert.Single((JsonArray)watch["continue"]!)!["id"]);
        // Nothing was asked of anyone but the server, and nothing in the ledger changed.
        Assert.All(_server.Requests, r => Assert.True(r.Path is "/UserItems/Resume" or "/Shows/NextUp" or "/Shows/Upcoming" or "/Items", r.Path));
        Assert.Equal(before, ledger.ToJson().ToJsonString());
    }

    [Fact]
    public async Task Search_is_the_server_s_and_a_search_typed_past_is_dropped()
    {
        await Connect();
        var cinema = Cinema(new Watch.WatchLedger());
        var first = cinema.HandleAsync("search", new JsonObject { ["term"] = "the" });
        var second = await cinema.HandleAsync("search", new JsonObject { ["term"] = "the show" });
        Assert.Equal("jf:show1", (string?)Assert.Single((JsonArray)second!)!["id"]);
        // The earlier one answers null (called off) or its own list, never an error.
        Assert.True(await first is null or JsonArray);
        Assert.Equal(7, (long?)(await cinema.HandleAsync("cache-size", null))!["bytes"]);
    }

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
    public void The_cleanups_of_the_old_sign_in_can_t_reach_the_new_account()
    {
        // LegacyData.cs deletes accounts.json at every start; the account is in another file.
        Assert.Contains("accounts.json", Source("LegacyData.cs"));
        Assert.Contains("\"jellyfin.json\"", Source("Jellyfin", "AccountStore.cs"));
        Assert.DoesNotContain("accounts.json\")", Source("Jellyfin", "AccountStore.cs"));
        // The synced item MainWindow takes off every PC is nothing the account reads.
        Assert.Contains("Put(\"server\", \"last\", null)", Source("MainWindow.xaml.cs"));
        Assert.DoesNotContain("SyncedSettings", Source("Jellyfin", "JellyfinLibrary.cs"));
        Assert.DoesNotContain("SyncedItems", Source("Jellyfin", "JellyfinLibrary.cs"));
        // The page's sweep of what pre-1.5 sign-ins left spares what the server's titles keep.
        var sweep = Regex.Match(Source("wwwroot", "app.js"), @"if \(/(\^exo:\[0-9a-f\]\{32\}:[^\n]*?)/\.test\(key\)\) localStorage\.removeItem").Groups[1].Value;
        Assert.NotEmpty(sweep);
        var old = new Regex(sweep);
        Assert.Matches(old, "exo:0123456789abcdef0123456789abcdef:home");
        foreach (var key in new[] { "exo:lib:rows", "exo:lib:row:jf/latest/latest/abc", "exo:lib:meta:jf:abc", "exo:lib:watch" })
            Assert.DoesNotMatch(old, key);
    }

    [Fact]
    public void Addresses_with_the_token_are_cut_from_the_log()
    {
        var line = AppLog.Clean($"[ffmpeg] https://jlly.cc/Videos/abc/Subtitles/1/0/Stream.srt?ApiKey={Token}");
        Assert.DoesNotContain(Token, line);
        Assert.DoesNotContain("jlly.cc", line);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class FakePlayer : IPlayerSurface
    {
        public string? Url, Auth;
        public double Start = -1;
        public readonly List<string> Urls = [];

        public void Play(string url, string? authHeader, double startSeconds, string? title)
        {
            (Url, Auth, Start) = (url, authHeader, startSeconds);
            Urls.Add(url);
        }

        public void Stop() { }
    }

    /// <summary>Jellyfin's answers for one user and a small library, on this PC only.</summary>
    private sealed class FakeServer : IDisposable
    {
        public sealed record Request(string Method, string Path, string Query, string Auth, string Body);

        private readonly HttpListener _listener = new();
        public string Url { get; }
        public ConcurrentQueue<Request> Log { get; } = new();
        public RequestsView Requests => new(Log);
        public HttpStatusCode AuthStatus = HttpStatusCode.OK, MeStatus = HttpStatusCode.OK, PublicStatus = HttpStatusCode.OK, UpcomingStatus = HttpStatusCode.OK;
        public bool UpcomingEmpty;
        // Test knobs: a read of the account is cut off (the connection closed) this many times, and each answers after a delay.
        public int DropMe;
        public TimeSpan MeDelay, UpcomingDelay;
        public string PublicInfo = """{"Id":"abc","ServerName":"JLLY test","Version":"12.0.0","ProductName":"Jellyfin Server"}""";
        public string? RedirectSignInTo;

        public FakeServer()
        {
            var port = FreePort();
            Url = $"http://localhost:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public sealed class RequestsView(ConcurrentQueue<Request> log) : IEnumerable<Request>
        {
            public void Clear() => log.Clear();
            public IEnumerator<Request> GetEnumerator() => log.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => log.GetEnumerator();
        }

        public bool Saw(string method, string path) => Log.Any(r => r.Method == method && r.Path == path);

        public async Task WaitFor(string path, int count)
        {
            for (var i = 0; i < 200 && Log.Count(r => r.Path == path) < count; i++) await Task.Delay(25);
            Assert.True(Log.Count(r => r.Path == path) >= count, $"{path} was never reported");
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                try { await AnswerAsync(context); }
                catch (HttpListenerException) { }
            }
        }

        private async Task AnswerAsync(HttpListenerContext c)
        {
            using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var path = c.Request.Url!.AbsolutePath;
            var query = c.Request.Url.Query;
            var auth = c.Request.Headers["Authorization"] ?? "";
            Log.Enqueue(new Request(c.Request.HttpMethod, path, query, auth, body));
            if (path == "/Users/Me" && DropMe > 0) { DropMe--; c.Response.Abort(); return; }
            if (path == "/Users/Me") await Task.Delay(MeDelay);
            if (path == "/Shows/Upcoming") await Task.Delay(UpcomingDelay);
            var (status, json) = Answer(c, path, query, auth);
            c.Response.StatusCode = (int)status;
            c.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }

        private static string Items(params string[] items) => $$"""{"Items":[{{string.Join(',', items)}}]}""";

        private const string Film1 = """{"Id":"film1","Name":"The Film","Type":"Movie","ProductionYear":1994,"RunTimeTicks":64200000000,"CommunityRating":8.7,"Overview":"A film.","Genres":["Drama"],"ImageTags":{"Primary":"p1","Logo":"l1"},"BackdropImageTags":["b1"],"UserData":{"PlaybackPositionTicks":12345000000,"Played":false,"IsFavorite":true}}""";
        private const string Film2 = """{"Id":"film2","Name":"Marked","Type":"Movie","UserData":{"Played":true,"IsFavorite":false}}""";
        private const string Show1 = """{"Id":"show1","Name":"The Show","Type":"Series","ProductionYear":2008,"ImageTags":{"Primary":"p2"},"BackdropImageTags":["b2"],"Genres":["Crime"],"ProductionLocations":["United States"],"UserData":{"Played":false,"IsFavorite":true}}""";
        private const string Ep1 = """{"Id":"ep1","Name":"First","Type":"Episode","SeriesId":"show1","SeriesName":"The Show","SeriesPrimaryImageTag":"p2","ParentIndexNumber":1,"IndexNumber":1,"RunTimeTicks":24000000000,"ImageTags":{"Primary":"e1"},"UserData":{"Played":true,"LastPlayedDate":"2026-11-01T20:00:00.0000000Z"}}""";
        private const string Ep2 = """{"Id":"ep2","Name":"Second","Type":"Episode","SeriesId":"show1","SeriesName":"The Show","SeriesPrimaryImageTag":"p2","ParentIndexNumber":1,"IndexNumber":2,"RunTimeTicks":24000000000,"ImageTags":{"Primary":"e2"},"ParentBackdropItemId":"show1","ParentBackdropImageTags":["b2"],"UserData":{"PlaybackPositionTicks":6000000000,"Played":false,"LastPlayedDate":"2026-11-02T20:00:00.0000000Z"}}""";
        private const string Up1 = """{"Id":"up1","Name":"Upcoming","Type":"Episode","SeriesId":"show1","SeriesName":"The Show","ParentIndexNumber":1,"IndexNumber":2,"PremiereDate":"2026-11-06T02:30:00.0000000Z","ImageTags":{"Primary":"u1"}}""";
        private const string Ep3 = """{"Id":"ep3","Name":"Third","Type":"Episode","SeriesId":"show1","SeriesName":"The Show","ParentIndexNumber":1,"IndexNumber":3,"UserData":{"Played":false}}""";

        private (HttpStatusCode, string) Answer(HttpListenerContext c, string path, string query, string auth)
        {
            if (path == "/System/Info/Public") return (PublicStatus, PublicInfo);
            if (path == "/Users/AuthenticateByName")
            {
                if (RedirectSignInTo is { } to)
                {
                    c.Response.Headers["Location"] = to;
                    return (HttpStatusCode.Found, "");
                }
                return (AuthStatus, AuthStatus == HttpStatusCode.OK ? $$$"""{"AccessToken":"{{{Token}}}","User":{"Id":"{{{UserId}}}","Name":"erix"}}""" : "");
            }
            if (!auth.Contains($"Token=\"{Token}\"")) return (HttpStatusCode.Unauthorized, "");
            return path switch
            {
                "/Users/Me" => (MeStatus, MeStatus == HttpStatusCode.OK ? """{"Id":"me","Name":"erix"}""" : ""),
                "/Sessions/Logout" or "/Sessions/Playing" or "/Sessions/Playing/Progress" or "/Sessions/Playing/Stopped" => (HttpStatusCode.NoContent, ""),
                "/UserViews" => (HttpStatusCode.OK, Items(
                    """{"Id":"view-films","Name":"Films","CollectionType":"movies"}""",
                    """{"Id":"view-shows","Name":"Shows","CollectionType":"tvshows"}""",
                    """{"Id":"view-music","Name":"Music","CollectionType":"music"}""")),
                "/UserItems/Resume" => (HttpStatusCode.OK, Items(Ep2)),
                "/Shows/NextUp" => (HttpStatusCode.OK, Items(Ep3)),
                "/Shows/Upcoming" => (UpcomingStatus, UpcomingStatus != HttpStatusCode.OK ? "" : UpcomingEmpty ? Items() : Items(Up1)),
                "/Items/Latest" => (HttpStatusCode.OK, query.Contains("parentId=view-films") ? $"[{Film1}]" : $"[{Ep3}]"),
                "/Items" when query.Contains("isFavorite=true") => (HttpStatusCode.OK, Items(Film1)),
                "/Items" when query.Contains("isPlayed=true") => (HttpStatusCode.OK, Items(Ep1, Film2)),
                "/Items" when query.Contains("searchTerm") => (HttpStatusCode.OK, Items(Show1)),
                "/Items" => (HttpStatusCode.OK, Items(query.Contains("includeItemTypes=Series") ? Show1 : Film1)),
                "/Items/film1" or "/Items/remote" or "/Items/many" or "/Items/single" or "/Items/mixed" => (HttpStatusCode.OK, Film1),
                "/Items/film2" => (HttpStatusCode.OK, Film2),
                "/Items/show1" => (HttpStatusCode.OK, Show1),
                "/Items/ep2" => (HttpStatusCode.OK, Ep2),
                "/Shows/show1/Episodes" when query.Contains("startItemId=ep2") => (HttpStatusCode.OK, Items(Ep2, Ep3)),
                "/Shows/show1/Episodes" => (HttpStatusCode.OK, Items(Ep1, Ep2, Ep3)),
                "/Items/film1/PlaybackInfo" => (HttpStatusCode.OK, """{"PlaySessionId":"session-1","MediaSources":[{"Id":"src-small","Name":"720p","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":720,"Codec":"h264"}]},{"Id":"src-big","Name":"1080p","Bitrate":9000,"MediaStreams":[{"Type":"Video","Height":1080,"Codec":"h264"}]}]}"""),
                "/Items/remote/PlaybackInfo" => (HttpStatusCode.OK, $$"""{"PlaySessionId":"session-2","MediaSources":[{"Id":"r","IsRemote":true,"Protocol":"Http","Path":"http://localhost:{{new Uri(Url).Port + 1}}/file.mkv"}]}"""),
                "/Items/many/PlaybackInfo" => (HttpStatusCode.OK, """{"PlaySessionId":"session-3","MediaSources":[{"Id":"m1","Name":"Many 1","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":720,"Codec":"h264"}]},{"Id":"m2","Name":"Many 2","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":576,"Codec":"h264"}]},{"Id":"m3","Name":"Many 3","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":480,"Codec":"h264"}]},{"Id":"m4","Name":"Many 4","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":360,"Codec":"h264"}]},{"Id":"m5","Name":"Many 5","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":240,"Codec":"h264"}]},{"Id":"m6","Name":"Many 6","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":200,"Codec":"h264"}]}]}"""),
                // A notice listed as the only version, as a server's plugin lists a title with no digital release.
                "/Items/single/PlaybackInfo" => (HttpStatusCode.OK, """{"PlaySessionId":"session-4","MediaSources":[{"Id":"s1","Name":"No streams · ⚠️ \"Wildwood\" no digital release yet (closest: Dec 8, 2026, 33d away)","Bitrate":0,"MediaStreams":[]}]}"""),
                // This server's version first, then one at another address.
                "/Items/mixed/PlaybackInfo" => (HttpStatusCode.OK, $$"""{"PlaySessionId":"session-5","MediaSources":[{"Id":"own","Name":"720p","Bitrate":1000,"MediaStreams":[{"Type":"Video","Height":720,"Codec":"h264"}]},{"Id":"r","Name":"Remote","IsRemote":true,"Protocol":"Http","Path":"http://localhost:{{new Uri(Url).Port + 1}}/file.mkv"}]}"""),
                _ when path.StartsWith("/UserPlayedItems/", StringComparison.Ordinal) || path.StartsWith("/UserFavoriteItems/", StringComparison.Ordinal) => (HttpStatusCode.OK, "{}"),
                _ => (HttpStatusCode.NotFound, ""),
            };
        }

        public void Dispose() => _listener.Close();
    }
}
