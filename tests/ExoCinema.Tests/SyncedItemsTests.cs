using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Sync;

namespace ExoCinema.Tests;

/// <summary>A sync sub-key's items as one PC holds them: SyncBatch's merge, what goes out, and the file it keeps.</summary>
public sealed class SyncedItemsTests
{
    private const string Desk = "0123456789abcdef0123456789abcdef", Laptop = "fedcba9876543210fedcba9876543210";

    private static SyncBatch.Item Item(string id, long modified, string device, string? value) =>
        new("setting", id, modified, device, value is null ? null : JsonValue.Create(value));

    [Fact]
    public void A_change_here_is_newer_than_the_last_and_the_same_value_is_no_change()
    {
        var held = new SyncedItems("cinema");
        var first = held.Put("setting", "material", JsonValue.Create("solid"));
        var again = held.Put("setting", "material", JsonValue.Create("solid"));
        var next = held.Put("setting", "material", JsonValue.Create("glass"));
        Assert.Same(first, again);
        Assert.True(next.Modified > first.Modified);
    }

    [Fact]
    public void Another_PC_wins_only_when_newer_or_tied_on_the_higher_device()
    {
        var held = new SyncedItems("cinema");
        held.Take([Item("source", 100, Desk, "ask")]);
        Assert.Empty(held.Take([Item("source", 99, Laptop, "best")]));   // older
        Assert.Single(held.Take([Item("source", 100, Laptop, "best")]));  // tie: the higher device
        Assert.Empty(held.Take([Item("source", 100, Desk, "ask")]));      // tie: the lower device
        Assert.Equal("best", (string?)held.Get("setting", "source")!.Body);
    }

    [Fact]
    public void Any_order_of_the_same_items_ends_the_same_and_as_SyncBatch_merges()
    {
        SyncBatch.Item[] items =
        [
            Item("material", 5, Desk, "solid"), Item("material", 7, Laptop, "glass"), Item("material", 7, Desk, "solid"),
            Item("autoplay", 3, Laptop, null), Item("autoplay", 2, Desk, "off"),
        ];
        var expected = SyncBatch.Merge([], items).Select(Key);
        foreach (var order in new[] { items, Enumerable.Reverse(items).ToArray(), items.OrderBy(i => i.Device).ToArray() })
        {
            var held = new SyncedItems("cinema");
            foreach (var item in order) held.Take([item]);
            Assert.Equal(expected, held.Items.OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).Select(Key));
        }
    }

    [Fact]
    public void Changes_go_out_once_stamped_with_this_PC_and_as_a_well_formed_batch()
    {
        var held = new SyncedItems("cinema");
        held.Put("setting", "material", JsonValue.Create("solid"));
        held.Put("setting", "autoplay", null);
        var outgoing = held.Outgoing(Desk);
        Assert.Equal(2, outgoing.Count);
        Assert.All(outgoing, i => Assert.Equal(Desk, i.Device));
        // The bytes a push carries, read back as any PC reads them.
        var back = SyncBatch.Read(SyncBatch.Write("cinema", outgoing), "cinema");
        Assert.Equal(outgoing.Select(Key), back!.Select(Key));
        held.Sent(outgoing);
        Assert.Empty(held.Outgoing(Desk));
        // Changed again after it was sent: it goes again.
        held.Put("setting", "material", JsonValue.Create("glass"));
        Assert.Single(held.Outgoing(Desk));
    }

    [Fact]
    public void A_change_from_another_PC_is_not_sent_back()
    {
        var held = new SyncedItems("cinema");
        held.Put("setting", "material", JsonValue.Create("solid"));
        held.Take([Item("material", long.MaxValue / 2, Laptop, "glass")]);
        Assert.Empty(held.Outgoing(Desk));
    }

    [Fact]
    public void A_choice_from_before_sync_loses_to_one_made_on_purpose_elsewhere()
    {
        var held = new SyncedItems("cinema");
        Assert.True(held.Seed("setting", "source", JsonValue.Create("ask")));
        Assert.False(held.Seed("setting", "source", JsonValue.Create("best")));
        Assert.Single(held.Take([Item("source", 1, Laptop, "best")]));
        Assert.Equal("best", (string?)held.Get("setting", "source")!.Body);
    }

    [Fact]
    public void A_snapshot_keeps_tombstones_for_90_days()
    {
        var held = new SyncedItems("cinema");
        var now = 1_000L * 24 * 3600 * 1000;
        held.Take([Item("material", now - SyncedItems.TombstoneMs + 1, Laptop, null), Item("motion", now - SyncedItems.TombstoneMs - 1, Laptop, null), Item("source", 1, Laptop, "ask")]);
        Assert.Equal(["material", "source"], held.Snapshot(Desk, now).Select(i => i.Id));
    }

    [Fact]
    public void The_file_keeps_versions_writers_and_what_has_still_to_go_out()
    {
        var held = new SyncedItems("cinema");
        held.Put("setting", "material", JsonValue.Create("solid"));
        held.Take([Item("source", 9, Laptop, "ask")]);
        var back = SyncedItems.FromJson("cinema", JsonNode.Parse(held.ToJson().ToJsonString()) as JsonArray);
        Assert.Equal(held.Items.OrderBy(i => i.Id).Select(Key), back.Items.OrderBy(i => i.Id).Select(Key));
        Assert.Equal(["material"], back.Outgoing(Desk).Select(i => i.Id));
    }

    [Fact]
    public void A_ledger_kept_before_sync_has_everything_still_to_go_out()
    {
        // Exo Watch's file as it was written before sync: no device, no "out".
        var old = JsonNode.Parse("""[{"type":"saved","id":"imdb:tt1","modified":5,"body":{"title":"A"},"deleted":null},{"type":"saved","id":"imdb:tt2","modified":6,"body":null,"deleted":true}]""") as JsonArray;
        var held = SyncedItems.FromJson("cinema-watch", old);
        var outgoing = held.Outgoing(Desk);
        Assert.Equal(["imdb:tt1", "imdb:tt2"], outgoing.Select(i => i.Id));
        Assert.Null(outgoing[1].Body);
        Assert.NotNull(SyncBatch.Read(SyncBatch.Write("cinema-watch", outgoing), "cinema-watch"));
    }

    private static string Key(SyncBatch.Item i) => SyncBatch.Json(i).ToJsonString();
}
