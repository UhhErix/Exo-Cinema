using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Account;
using ExoCinema.Sync;

namespace ExoCinema.Tests;

/// <summary>
/// The account sync (CLIENT.md 7, ported from Exo Launcher's AccountSync). Values and answers are the account service's
/// fixtures (client-fixtures/sync.json: sync-put-stale-base, sync-put-value-too-big, sync-get-after-0). Ways it could
/// fail with no error on either side:
/// - a watch entry sent twice, or pulled after this PC already has it, is counted twice;
/// - a PUT the service refuses as too big takes the whole batch with it, or is retried forever;
/// - a value conflict keeps this PC's older change, or drops a newer one;
/// - an object conflict drops the other keys this PC didn't change, or loops without its one-minute wait;
/// - signed out, or a full account, keeps pushing; a pulled setting this PC refuses gets in anyway.
/// </summary>
public sealed class AccountSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Watch = "imdb:tt0111161@1700000000000";
    private const string Own = "0123456789abcdef0123456789abcdef";

    private static AccountSyncEngine Engine(SyncState? state = null) => new(state ?? new SyncState(), new Random(7));

    private static AccountReply Ok(JsonNode body) => new(200, body, null, null);

    private static AccountReply Error(int status, string error, int? retry = null) => new(status, new JsonObject { ["error"] = error }, error, retry);

    private static JsonObject Put(string type, string id, long version, long updated, string value) =>
        new() { ["type"] = type, ["id"] = id, ["version"] = version, ["updated"] = updated, ["value"] = value };

    [Fact]
    public void A_watch_written_twice_is_one_entry_and_is_never_counted_twice()
    {
        var engine = Engine();
        engine.Write("watch", Watch, "{\"title\":\"Heat\"}", 1, T0);
        engine.Write("watch", Watch, "{\"title\":\"Heat (again)\"}", 2, T0);

        Assert.Single(engine.State.Items);
        Assert.Equal("{\"title\":\"Heat\"}", engine.State.Items[AccountSyncRules.Key("watch", Watch)].Value);
    }

    [Fact]
    public void A_pulled_watch_the_PC_already_has_is_applied_once()
    {
        var engine = Engine();
        engine.Write("watch", Watch, "{\"title\":\"Heat\"}", 1, T0);
        var page = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["app"] = "cinema", ["type"] = "watch", ["id"] = Watch, ["version"] = 3, ["updated"] = 1, ["value"] = "{\"title\":\"Heat\"}",
            }),
            ["cursor"] = 3,
            ["more"] = false,
        };
        engine.Pull(Ok(page), T0);

        Assert.Equal(new[] { AccountSyncRules.Key("watch", Watch) }, engine.TakeApplied());
        Assert.Equal(3, engine.State.Items[AccountSyncRules.Key("watch", Watch)].Base);
        Assert.Empty(engine.State.Dirty);
    }

    [Fact]
    public void A_push_waits_two_seconds_after_the_last_change()
    {
        var engine = Engine();
        engine.Write("resume", "imdb:tt0111161", "{\"ms\":5}", 1, T0);

        Assert.Null(engine.NextPush(T0.AddSeconds(1)));
        var batch = engine.NextPush(T0.AddSeconds(2));
        Assert.NotNull(batch);
        Assert.Equal("resume", batch[0].Type);
    }

    [Fact]
    public void A_PUT_body_names_the_app_cinema_and_a_deletion_has_no_value()
    {
        var body = AccountSyncRules.PutBody([
            new SyncItem("resume", "imdb:tt0111161", 2, 5, "{\"ms\":5}", false),
            new SyncItem("watchlist", "imdb:tt0111161", 0, 6, null, true),
        ]);
        var items = body["items"]!.AsArray();

        Assert.Equal("cinema", (string?)items[0]!["app"]);
        Assert.Equal("{\"ms\":5}", (string?)items[0]!["value"]);
        Assert.True((bool?)items[1]!["deleted"]);
        Assert.Null(items[1]!["value"]);
    }

    [Fact]
    public void A_whole_PUT_refused_as_too_big_is_halved_until_the_one_item_stands_alone_and_is_held()
    {
        var engine = Engine();
        for (var n = 0; n < 4; n++) engine.Write("resume", $"imdb:tt{n:0000001}", "{}", 1, T0);
        var batch = engine.NextPush(T0.AddSeconds(3))!;
        Assert.Equal(4, batch.Count);

        engine.ApplyPut(batch, Error(413, "too-big"), T0.AddSeconds(3));
        Assert.Equal(2, engine.State.BatchLimit);
        Assert.Equal(2, engine.NextPush(T0.AddSeconds(3))!.Count);

        engine.ApplyPut(engine.NextPush(T0.AddSeconds(3))!, Error(413, "too-big"), T0.AddSeconds(3));
        Assert.Equal(1, engine.State.BatchLimit);
        var single = engine.NextPush(T0.AddSeconds(3))!;
        engine.ApplyPut(single, Error(413, "too-big"), T0.AddSeconds(3));
        Assert.Single(engine.State.Held);
        Assert.Equal(AccountSyncRules.HeldTooLarge, engine.State.HeldReasons[single[0].Type + "/" + single[0].Id]);
    }

    [Fact]
    public void A_value_over_64_KiB_is_held_on_this_PC_and_never_sent()
    {
        var engine = Engine();
        engine.Write("resume", "imdb:tt0111161", new string('x', 64 * 1024 + 1), 1, T0);

        Assert.Null(engine.NextPush(T0.AddSeconds(3)));
        Assert.Single(engine.State.Held);
        Assert.Empty(engine.State.Dirty);
    }

    [Fact]
    public void A_full_account_stops_new_pushes_and_a_signed_out_session_stops_all()
    {
        var engine = Engine();
        engine.Write("settings", "cinema", "{\"material\":\"solid\"}", 1, T0);
        engine.ApplyPut(engine.NextPush(T0.AddSeconds(3)), Error(413, "account-full"), T0.AddSeconds(3));
        Assert.True(engine.State.PushStopped);
        Assert.Null(engine.NextPush(T0.AddSeconds(9)));

        var signedOut = Engine();
        signedOut.Write("settings", "cinema", "{\"material\":\"solid\"}", 1, T0);
        signedOut.ApplyPut(signedOut.NextPush(T0.AddSeconds(3)), Error(401, "signed-out"), T0.AddSeconds(3));
        Assert.True(signedOut.State.SignedOut);
        Assert.Null(signedOut.NextPush(T0.AddSeconds(9)));
    }

    [Fact]
    public void A_429_waits_the_Retry_After_and_a_server_error_backs_off_from_five_seconds()
    {
        var engine = Engine();
        engine.Write("resume", "imdb:tt0111161", "{}", 1, T0);
        engine.ApplyPut(engine.NextPush(T0.AddSeconds(3)), new AccountReply(429, null, "locked", 90), T0.AddSeconds(3));
        Assert.Equal(T0.AddSeconds(93), engine.State.NextAttempt);

        var server = Engine();
        server.Write("resume", "imdb:tt0111161", "{}", 1, T0);
        server.ApplyPut(server.NextPush(T0.AddSeconds(3)), new AccountReply(503, null, "server-error", null), T0.AddSeconds(3));
        Assert.Equal(1, server.State.Failures);
        var wait = server.State.NextAttempt - T0.AddSeconds(3);
        Assert.InRange(wait.TotalSeconds, 4, 6.01); // 5 s, plus or minus 20%
    }

    [Fact]
    public void A_bad_item_is_held_as_refused_and_the_rest_of_the_queue_goes_on()
    {
        var engine = Engine();
        engine.Write("resume", "imdb:tt0111161", "{}", 1, T0);
        var batch = engine.NextPush(T0.AddSeconds(3))!;
        engine.ApplyPut(batch, Error(400, "bad-item"), T0.AddSeconds(3));

        Assert.Single(engine.State.Held);
        Assert.Equal(AccountSyncRules.HeldRefused, engine.State.HeldReasons[AccountSyncRules.Key("resume", "imdb:tt0111161")]);
        Assert.Empty(engine.State.Dirty);
    }

    [Fact]
    public void A_value_conflict_keeps_the_newer_local_change_and_takes_the_server_value_when_the_server_is_newer()
    {
        // sync-put-stale-base: the server holds version 1 (updated ...092); this PC's change is newer (...097), so it wins.
        var engine = Engine();
        engine.Write("watchlist", "imdb:tt0111161", "{\"title\":\"local\"}", 1794008097, T0);
        var batch = engine.NextPush(T0.AddSeconds(3))!;
        var conflict = new JsonObject { ["type"] = "watchlist", ["id"] = "imdb:tt0111161", ["version"] = 1, ["updated"] = 1794008092, ["value"] = "{\"title\":\"server\"}" };
        engine.ApplyPut(batch, new AccountReply(200, new JsonObject { ["written"] = new JsonArray(), ["conflicts"] = new JsonArray(conflict) }, null, null), T0.AddSeconds(3));
        Assert.Contains(AccountSyncRules.Key("watchlist", "imdb:tt0111161"), engine.State.Dirty);
        Assert.Equal(1, engine.State.Items[AccountSyncRules.Key("watchlist", "imdb:tt0111161")].Base);

        // Older local change: the server's value stands and nothing is queued.
        var older = Engine();
        older.Write("watchlist", "imdb:tt0111161", "{\"title\":\"local\"}", 1794008090, T0);
        var older1 = older.NextPush(T0.AddSeconds(3))!;
        older.ApplyPut(older1, new AccountReply(200, new JsonObject { ["written"] = new JsonArray(), ["conflicts"] = new JsonArray(JsonNode.Parse(conflict.ToJsonString())) }, null, null), T0.AddSeconds(3));
        Assert.Empty(older.State.Dirty);
        Assert.Equal("{\"title\":\"server\"}", older.State.Items[AccountSyncRules.Key("watchlist", "imdb:tt0111161")].Value);
    }

    [Fact]
    public void An_object_conflict_overlays_only_the_keys_this_PC_changed_on_the_server_object()
    {
        // sync-put-stale-base's object: the server has {volume:70, captions:off}; this PC changed captions only.
        var engine = Engine();
        engine.Write("settings", "cinema", "{\"volume\":10,\"captions\":\"on\"}", 1794008097, T0);
        engine.State.Items[AccountSyncRules.Key("settings", "cinema")].Changed = ["captions"];
        var batch = engine.NextPush(T0.AddSeconds(3))!;
        var conflict = new JsonObject { ["type"] = "settings", ["id"] = "cinema", ["version"] = 1, ["updated"] = 1794008092, ["value"] = "{\"volume\":70,\"captions\":\"off\"}" };
        engine.ApplyPut(batch, new AccountReply(200, new JsonObject { ["written"] = new JsonArray(), ["conflicts"] = new JsonArray(conflict) }, null, null), T0.AddSeconds(3));

        var merged = JsonNode.Parse(engine.State.Items[AccountSyncRules.Key("settings", "cinema")].Value!)!.AsObject();
        Assert.Equal(70, (int?)merged["volume"]);      // the server's key, kept
        Assert.Equal("on", (string?)merged["captions"]); // this PC's key, laid over it
        Assert.Contains(AccountSyncRules.Key("settings", "cinema"), engine.State.Dirty);
        Assert.Equal(1, engine.State.Items[AccountSyncRules.Key("settings", "cinema")].Base);
    }

    [Fact]
    public void After_three_object_conflicts_in_a_row_the_queue_waits_a_minute_before_trying_again()
    {
        var engine = Engine();
        engine.Write("settings", "cinema", "{\"a\":1}", 1, T0);
        var key = AccountSyncRules.Key("settings", "cinema");
        var conflict = new JsonObject { ["type"] = "settings", ["id"] = "cinema", ["version"] = 5, ["updated"] = 1, ["value"] = "{\"a\":2}" };
        var stall = new AccountReply(200, new JsonObject { ["written"] = new JsonArray(), ["conflicts"] = new JsonArray(conflict) }, null, null);

        for (var round = 0; round < 3; round++)
        {
            var batch = engine.NextPush(T0.AddSeconds(3 + round * 4));
            if (batch is null) break;
            engine.ApplyPut(batch, stall, T0.AddSeconds(3 + round * 4));
        }
        Assert.Equal(T0.AddSeconds(3 + 2 * 4) + TimeSpan.FromMinutes(1), engine.State.NextAttempt);
        Assert.Null(engine.NextPush(T0.AddSeconds(3 + 2 * 4 + 30)));
        Assert.NotNull(engine.NextPush(T0.AddSeconds(3 + 2 * 4) + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(3)));
        Assert.Contains(key, engine.State.Dirty);
    }

    [Fact]
    public void The_service_sync_page_is_read_and_a_second_page_is_asked_for()
    {
        // sync-get-after-0 (fixture): two items, a cursor, and more.
        var engine = Engine();
        var page = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["app"] = "cinema", ["type"] = "settings", ["id"] = "cinema", ["version"] = 4, ["updated"] = 1794008102, ["value"] = "{\"volume\":80}" },
                new JsonObject { ["app"] = "cinema", ["type"] = "resume", ["id"] = "imdb:tt0111161", ["version"] = 5, ["updated"] = 1794008112, ["value"] = null },
                new JsonObject { ["app"] = "launcher", ["type"] = "playtime", ["id"] = "s.1", ["version"] = 6, ["updated"] = 1, ["value"] = "{}" }),
            ["cursor"] = 6,
            ["more"] = true,
        };
        Assert.True(engine.Pull(Ok(page), T0));
        Assert.Equal(6, engine.State.Cursor);
        Assert.Equal(new[] { "settings/cinema", "resume/imdb:tt0111161" }, engine.TakeApplied());
        Assert.True(engine.State.Items["resume/imdb:tt0111161"].Deleted);
    }

    [Fact]
    public void A_pull_answered_signed_out_ends_the_sync_and_keeps_the_cursor()
    {
        var engine = Engine();
        Assert.False(engine.Pull(Error(401, "signed-out"), T0));
        Assert.True(engine.State.SignedOut);
        Assert.Equal(0, engine.State.Cursor);
    }

    [Fact]
    public void Read_turns_this_PCs_watches_resume_points_and_settings_into_items()
    {
        var ledger = new SyncedItems("cinema-watch");
        var settings = new SyncedItems("cinema");
        ledger.Put("watched", Watch, new JsonObject { ["title"] = "Heat" });
        ledger.Put("position", "imdb:tt0111161", new JsonObject { ["ms"] = 5000, ["at"] = 1 });
        settings.Put("setting", "material", JsonValue.Create("solid"));
        settings.Put("setting", "motion", JsonValue.Create("off"));

        var items = AccountSyncSources.Read(ledger, settings);
        var byKey = items.ToDictionary(i => AccountSyncRules.Key(i.Type, i.Id), i => i.Value);

        Assert.Equal(Watch, items.Single(i => i.Type == "watch").Id);
        Assert.Equal("{\"ms\":5000,\"at\":1}", byKey["resume/imdb:tt0111161"]);
        var choices = JsonNode.Parse(byKey["settings/cinema"])!.AsObject();
        Assert.Equal("solid", (string?)choices["material"]);
        Assert.Equal("off", (string?)choices["motion"]);
        Assert.DoesNotContain(items, i => i.Type == "addons");
    }

    [Fact]
    public void A_pulled_setting_this_PC_does_not_accept_is_not_applied_and_an_accepted_one_is()
    {
        var settings = new SyncedItems("cinema");
        var state = new SyncState();
        var engine = new AccountSyncEngine(state, new Random(7));
        var page = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["app"] = "cinema", ["type"] = "settings", ["id"] = "cinema", ["version"] = 2, ["updated"] = 1,
                ["value"] = "{\"material\":\"solid\",\"bogus\":\"x\"}",
            }),
            ["cursor"] = 2,
            ["more"] = false,
        };
        engine.Pull(Ok(page), T0);
        var (_, put) = AccountSyncSources.Apply(engine.TakeApplied(), state, new SyncedItems("cinema-watch"), settings);

        Assert.Single(put);
        Assert.Equal("solid", (string?)settings.Get("setting", "material")?.Body);
        Assert.Null(settings.Get("setting", "bogus"));
    }

    [Fact]
    public void The_source_list_an_account_holds_from_before_1_5_is_read_without_failing_and_left_alone_on_both_sides()
    {
        // What a real account and a real PC hold from 1.4: the account's list item, and this PC's own copy of each source.
        const string url = "https://files.example/cfg/manifest.json";
        var ledger = new SyncedItems("cinema-watch");
        var settings = new SyncedItems("cinema");
        settings.Put("addon", "0123456789abcdef", new JsonObject { ["url"] = url, ["at"] = 1024 });
        settings.Put("setting", "autoplay", JsonValue.Create("off"));
        var state = new SyncState();
        var engine = new AccountSyncEngine(state, new Random(7));
        var page = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["app"] = "cinema", ["type"] = "addons", ["id"] = "list", ["version"] = 4, ["updated"] = 1,
                ["value"] = new JsonArray(new JsonObject { ["url"] = url, ["at"] = 1024 }, new JsonObject { ["url"] = "https://other.example/manifest.json", ["at"] = 2048 }).ToJsonString(),
            }),
            ["cursor"] = 4,
            ["more"] = false,
        };

        // Pulled: taken into the queue's state as the service sent it, and applied to nothing.
        engine.Pull(Ok(page), T0);
        var (ledgerPut, settingsPut) = AccountSyncSources.Apply(engine.TakeApplied(), state, ledger, settings);
        Assert.Empty(ledgerPut);
        Assert.Empty(settingsPut);
        Assert.Equal(url, (string?)settings.Get("addon", "0123456789abcdef")?.Body?["url"]);
        Assert.Single(settings.Live("addon"));

        // Written: this PC's choices go out; the list is neither changed nor deleted, here or on the account.
        var held = state.Items["addons/list"].Value;
        AccountSyncSources.Write(engine, state, ledger, settings, T0.AddSeconds(5));
        Assert.DoesNotContain(AccountSyncRules.Key("addons", "list"), state.Dirty);
        Assert.Equal(held, state.Items["addons/list"].Value);
        Assert.False(state.Items["addons/list"].Deleted);
        Assert.Contains(AccountSyncRules.Key("settings", "cinema"), state.Dirty);
    }
    [Fact]
    public void The_mailbox_import_queues_what_the_stores_hold_once_and_merges_a_watch_the_account_already_has_by_id()
    {
        // The one-time import (1.4.10): this PC's stores go into the queue. A watch the account already has, pulled
        // first, is not queued again: its id is the same entry, so nothing is counted twice.
        var ledger = new SyncedItems("cinema-watch");
        var settings = new SyncedItems("cinema");
        ledger.Put("watched", Watch, new JsonObject { ["title"] = "Heat" });
        ledger.Put("watched", "imdb:tt0111161@1700000000001", new JsonObject { ["title"] = "Shawshank" });
        settings.Put("languages", "imdb:tt0903747", new JsonObject { ["audio"] = "en", ["sub"] = "off" });
        var state = new SyncState();
        var engine = new AccountSyncEngine(state, new Random(7));

        // The account already holds the first watch (from a pull): this PC's copy of it is not queued again.
        state.Items[AccountSyncRules.Key("watch", Watch)] = new SyncEntry { Type = "watch", Id = Watch, Base = 2, Updated = 1, Value = "{\"title\":\"Heat\"}" };
        var queued = AccountSyncSources.Write(engine, state, ledger, settings, T0);

        Assert.Equal(2, queued); // the second watch and the language choice
        Assert.Contains(AccountSyncRules.Key("watch", "imdb:tt0111161@1700000000001"), state.Dirty);
        Assert.Contains(AccountSyncRules.Key("languages", "imdb:tt0903747"), state.Dirty);
        Assert.DoesNotContain(AccountSyncRules.Key("watch", Watch), state.Dirty);

        // A second pass with nothing new queues nothing.
        Assert.Equal(0, AccountSyncSources.Write(engine, state, ledger, settings, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_resume_point_or_language_the_stores_no_longer_hold_is_deleted_and_a_watch_never_is()
    {
        var ledger = new SyncedItems("cinema-watch");
        var settings = new SyncedItems("cinema");
        var state = new SyncState();
        var engine = new AccountSyncEngine(state, new Random(7));
        state.Items[AccountSyncRules.Key("resume", "imdb:tt0111161")] = new SyncEntry { Type = "resume", Id = "imdb:tt0111161", Base = 4, Updated = 1, Value = "{\"ms\":5}" };
        state.Items[AccountSyncRules.Key("languages", "imdb:tt0903747")] = new SyncEntry { Type = "languages", Id = "imdb:tt0903747", Base = 4, Updated = 1, Value = "{\"audio\":\"en\"}" };
        state.Items[AccountSyncRules.Key("watch", Watch)] = new SyncEntry { Type = "watch", Id = Watch, Base = 4, Updated = 1, Value = "{}" };

        AccountSyncSources.Write(engine, state, ledger, settings, T0);

        Assert.True(state.Items[AccountSyncRules.Key("resume", "imdb:tt0111161")].Deleted);
        Assert.True(state.Items[AccountSyncRules.Key("languages", "imdb:tt0903747")].Deleted);
        Assert.False(state.Items[AccountSyncRules.Key("watch", Watch)].Deleted);
    }

    [Fact]
    public void A_pulled_language_choice_is_put_into_the_settings_store_and_a_bad_one_is_not()
    {
        var settings = new SyncedItems("cinema");
        var state = new SyncState();
        state.Items[AccountSyncRules.Key("languages", "imdb:tt0903747")] = new SyncEntry
        {
            Type = "languages", Id = "imdb:tt0903747", Base = 3, Updated = 1, Value = "{\"audio\":\"en\",\"sub\":\"off\"}",
        };
        state.Items[AccountSyncRules.Key("languages", "imdb:tt0111161")] = new SyncEntry
        {
            Type = "languages", Id = "imdb:tt0111161", Base = 3, Updated = 1, Value = "{\"audio\":\"not a code\"}",
        };
        AccountSyncSources.Apply(state.Items.Keys.ToList(), state, new SyncedItems("cinema-watch"), settings);

        Assert.NotNull(settings.Get("languages", "imdb:tt0903747")?.Body);
        Assert.Null(settings.Get("languages", "imdb:tt0111161")?.Body);
    }

    [Fact]
    public void Languages_are_read_from_the_stores_as_account_items()
    {
        var ledger = new SyncedItems("cinema-watch");
        var settings = new SyncedItems("cinema");
        settings.Put("languages", "movies", new JsonObject { ["audio"] = "fr" });

        var item = AccountSyncSources.Read(ledger, settings).Single(i => i.Type == "languages");

        Assert.Equal("movies", item.Id);
        Assert.Equal("{\"audio\":\"fr\"}", item.Value);
    }
}
