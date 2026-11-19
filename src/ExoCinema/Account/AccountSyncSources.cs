using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Sync;

namespace ExoCinema.Account;

/// <summary>
/// The account sync's item sources (CLIENT.md 8, as Cinema holds it): what this PC keeps read as account items, the
/// queue written from them, and the items the service sends applied back to the same stores. Exo Watch's ledger holds the
/// watches, the resume points and the saved titles; the <c>cinema</c> settings store holds the page's choices and each
/// show's languages. Both are SyncedItems, so a pulled item goes in with its own Put, and a watch is one id: nothing is
/// counted twice.
/// <para>An account from before 1.5 also holds a list of stream sources (one item) that Cinema no longer uses. It is left
/// as it is on both sides: never read into this PC's stores, never written back, never deleted, so an older Exo Cinema
/// on another PC keeps working and nothing of it is lost.</para>
/// </summary>
internal static class AccountSyncSources
{
    internal sealed record Local(string Type, string Id, string Value, long Updated);

    /// <summary>
    /// What this PC holds now, as account items: <c>watch</c> per watch (id <c>public id@ms</c>), <c>resume</c> per title
    /// left part way, <c>watchlist</c> per saved title, <c>languages</c> per show (its id, or <c>movies</c>) and
    /// <c>settings</c>, one object of the choices (id <c>cinema</c>).
    /// </summary>
    internal static List<Local> Read(SyncedItems ledger, SyncedItems settings)
    {
        var items = new List<Local>();
        foreach (var watched in ledger.Live("watched")) items.Add(Item("watch", watched));
        foreach (var position in ledger.Live("position")) items.Add(Item("resume", position));
        foreach (var saved in ledger.Live("saved")) items.Add(Item("watchlist", saved));
        foreach (var languages in settings.Live("languages")) items.Add(Item("languages", languages));

        var choices = settings.Live("setting").OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        if (choices.Count > 0)
        {
            var body = new JsonObject();
            foreach (var choice in choices) body[choice.Id] = choice.Body!.DeepClone();
            items.Add(new Local("settings", "cinema", body.ToJsonString(), choices.Max(Seconds)));
        }
        return items;
    }

    /// <summary>
    /// Writes what this PC holds into the queue: a change where the value differs, a deletion for a resume point or saved
    /// title the stores no longer hold. A log id the queue already has is not written again (merged by id). Returns how many
    /// items were queued or changed in this call.
    /// </summary>
    internal static int Write(AccountSyncEngine engine, SyncState state, SyncedItems ledger, SyncedItems settings, DateTimeOffset now)
    {
        var before = state.Dirty.Count;
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in Read(ledger, settings))
        {
            wanted.Add(AccountSyncRules.Key(local.Type, local.Id));
            engine.Write(local.Type, local.Id, local.Value, local.Updated, now);
        }
        // A resume point, saved title or language the stores no longer hold goes as a deletion (a watch is history: never deleted).
        foreach (var entry in state.Items.Values.Where(e => e.Type is "resume" or "watchlist" or "languages" && !e.Deleted && e.Value is not null).ToList())
            if (!wanted.Contains(AccountSyncRules.Key(entry.Type, entry.Id)))
            {
                engine.Write(entry.Type, entry.Id, null, now.ToUnixTimeSeconds(), now);
            }
        return state.Dirty.Count - before;
    }

    /// <summary>
    /// Applies the items the pull brought (<paramref name="keys"/>, each as the state holds it) to the stores. Returns what
    /// was put, per store, so the page can draw it again.
    /// </summary>
    internal static (List<SyncBatch.Item> Ledger, List<SyncBatch.Item> Settings) Apply(IEnumerable<string> keys, SyncState state, SyncedItems ledger, SyncedItems settings)
    {
        var ledgerPut = new List<SyncBatch.Item>();
        var settingsPut = new List<SyncBatch.Item>();
        foreach (var key in keys.Distinct())
        {
            if (!state.Items.TryGetValue(key, out var entry)) continue;
            var body = entry.Deleted ? null : JsonNode.Parse(entry.Value ?? "null");
            switch (entry.Type)
            {
                case "watch":
                    // A watch's id is its public id and time ("<public id>@<ms>"): the server's copy is added once, never twice.
                    ledgerPut.Add(ledger.Put("watched", entry.Id, body));
                    break;
                case "resume":
                    ledgerPut.Add(ledger.Put("position", entry.Id, body));
                    break;
                case "watchlist":
                    ledgerPut.Add(ledger.Put("saved", entry.Id, body));
                    break;
                case "languages":
                    if (body is null || CinemaSettings.Accept("languages", entry.Id, body)) settingsPut.Add(settings.Put("languages", entry.Id, body?.DeepClone()));
                    break;
                case "settings" when body is JsonObject choices:
                    foreach (var (name, value) in choices)
                        if (value is not null && CinemaSettings.Accept("setting", name, value)) settingsPut.Add(settings.Put("setting", name, value.DeepClone()));
                    break;

            }
        }
        return (ledgerPut, settingsPut);
    }

    private static Local Item(string type, SyncBatch.Item item) =>
        new(type, item.Id, item.Body?.ToJsonString() ?? "null", Seconds(item));

    private static long Seconds(SyncBatch.Item item) => item.Modified / 1000;
}
