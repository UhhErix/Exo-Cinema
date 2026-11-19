using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Watch;

namespace ExoCinema.Tests;

/// <summary>Exo Watch's ledger: sync's own items (platform plan §2.7), merged the way sync merges them.</summary>
public sealed class WatchLedgerTests
{
    private static readonly DateTime Day = new(2026, 10, 20, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Each_watch_is_its_own_item_and_a_repeat_of_the_same_watch_adds_nothing()
    {
        var ledger = new WatchLedger();
        Assert.True(ledger.Watched("imdb:tt0000001", "Paper Comet", Day, "netflix"));
        Assert.False(ledger.Watched("imdb:tt0000001", "Paper Comet", Day.AddHours(3), "cinema")); // the same evening, told twice
        Assert.True(ledger.Watched("imdb:tt0000001", "Paper Comet", Day.AddDays(30), "cinema")); // a rewatch
        var watches = ledger.Live("watched").ToList();
        Assert.Equal(2, watches.Count);
        Assert.All(watches, w => Assert.StartsWith("imdb:tt0000001@", w.Id));
        Assert.Contains(watches, w => WatchLedger.WatchedAt(w) == new DateTimeOffset(Day).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void A_position_is_kept_until_the_title_is_finished()
    {
        var ledger = new WatchLedger();
        Assert.True(ledger.Position("imdb:tt0000002:s01e02", "Night Garden · S1:E2", 600_000, 1_440_000, Day, "cinema"));
        Assert.False(ledger.Position("imdb:tt0000002:s01e02", "Night Garden · S1:E2", 300_000, 1_440_000, Day.AddHours(-1), "crunchyroll")); // older
        Assert.Single(ledger.Live("position"));
        ledger.Watched("imdb:tt0000002:s01e02", "Night Garden · S1:E2", Day.AddHours(1), "cinema");
        Assert.Empty(ledger.Live("position")); // finished: a tombstone, so it doesn't come back from another PC
        Assert.Contains(ledger.Items, i => i.Type == "position" && i.Body is null);
    }

    [Fact]
    public void Saved_titles_and_their_removal()
    {
        var ledger = new WatchLedger();
        Assert.True(ledger.Saved("imdb:tt0000003", "Blue Harbor", "crunchyroll"));
        Assert.False(ledger.Saved("imdb:tt0000003", "Blue Harbor", "netflix"));
        ledger.Unsave("imdb:tt0000003");
        Assert.Empty(ledger.Live("saved"));
    }

    [Fact]
    public void Modified_only_moves_forward_and_the_newest_wins_a_merge()
    {
        var ledger = new WatchLedger();
        ledger.Saved("imdb:tt1", "A", "x");
        ledger.Saved("imdb:tt2", "B", "x");
        var times = ledger.Items.Select(i => i.Modified).ToList();
        Assert.True(times[1] > times[0]);
        var old = new SyncBatch.Item("saved", "imdb:tt1", 1, "", null);
        ledger.Synced.Take([old]); // older than what is here: ignored
        Assert.Equal(2, ledger.Live("saved").Count());
        ledger.Synced.Take([old with { Modified = long.MaxValue }]);
        Assert.Single(ledger.Live("saved"));
    }

    [Fact]
    public void The_ledger_survives_its_file()
    {
        var ledger = new WatchLedger();
        ledger.Watched("imdb:tt0000001", "Paper Comet", Day, "netflix");
        ledger.Position("tmdb:tv:42:s02e03", "Show · S2:E3", 1000, null, Day, "cinema");
        ledger.Saved("imdb:tt0000003", "Blue Harbor", "crunchyroll");
        ledger.Unsave("imdb:tt0000003");
        var back = WatchLedger.FromJson(JsonNode.Parse(ledger.ToJson().ToJsonString()) as JsonArray);
        Assert.Equal(ledger.Items.OrderBy(i => i.Id).Select(i => (i.Type, i.Id, i.Modified, i.Body is null)), back.Items.OrderBy(i => i.Id).Select(i => (i.Type, i.Id, i.Modified, i.Body is null)));
        Assert.Equal("Paper Comet", (string?)back.Live("watched").Single().Body!["title"]);
    }

    [Fact]
    public void Public_ids_name_films_and_episodes_by_imdb_else_tmdb()
    {
        Assert.Equal("imdb:tt0111161", WatchIds.Film(JsonNode.Parse("""{"Imdb":"tt0111161","Tmdb":"278"}""")));
        Assert.Equal("tmdb:movie:278", WatchIds.Film(JsonNode.Parse("""{"Tmdb":"278"}""")));
        Assert.Null(WatchIds.Film(JsonNode.Parse("{}")));
        Assert.Equal("imdb:tt0903747:s01e02", WatchIds.Episode(JsonNode.Parse("""{"Imdb":"tt0903747"}"""), 1, 2));
        Assert.Equal("tmdb:tv:1396:s10e11", WatchIds.Episode(JsonNode.Parse("""{"Tmdb":"1396"}"""), 10, 11));
        Assert.Null(WatchIds.Episode(JsonNode.Parse("""{"Imdb":"tt0903747"}"""), null, 2));
    }
}
