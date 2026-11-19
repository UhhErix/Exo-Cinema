using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExoCinema.Account;

/// <summary>One answer from the account service. Status 0 is no answer (network failure or timeout).</summary>
internal sealed record AccountReply(int Status, JsonNode? Body, string? Error, int? RetryAfterSeconds)
{
    public bool Ok => Error is null && Status is >= 200 and < 300;
    public bool Unreachable => Status == 0;
}

/// <summary>How a type's conflicts land (CLIENT.md 7.3).</summary>
internal enum MergeKind { Log, Value, Object }

/// <summary>One item as the wire carries it: a PUT entry. A deletion has no value.</summary>
internal sealed record SyncItem(string Type, string Id, long Base, long Updated, string? Value, bool Deleted);

/// <summary>One item on this PC: the last version the server gave, the local change, and for an object the keys changed since.</summary>
internal sealed class SyncEntry
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>The last server version this PC saw (0 when none).</summary>
    public long Base { get; set; }
    /// <summary>The clock of the last local change. It decides only a value conflict (CLIENT.md 7.4).</summary>
    public long Updated { get; set; }
    public string? Value { get; set; }
    public bool Deleted { get; set; }
    /// <summary>An object: the top-level keys this PC changed since Base, so a conflict keeps the server's other keys.</summary>
    public List<string> Changed { get; set; } = [];
}

/// <summary>The queue and the local copy, saved as account-sync.json beside the settings (CLIENT.md 7.5). Data, not a secret.</summary>
internal sealed class SyncState
{
    public long Cursor { get; set; }
    public Dictionary<string, SyncEntry> Items { get; set; } = [];
    /// <summary>Keys waiting to go up.</summary>
    public HashSet<string> Dirty { get; set; } = [];
    /// <summary>Keys held on this PC: too big, or refused by the service. Not retried until the value changes.</summary>
    public HashSet<string> Held { get; set; } = [];
    /// <summary>Why each held key is held (too-large or refused), for Settings › Account.</summary>
    public Dictionary<string, string> HeldReasons { get; set; } = [];
    public DateTimeOffset LastChange { get; set; }
    public DateTimeOffset NextAttempt { get; set; }
    public int Failures { get; set; }
    public bool SignedOut { get; set; }
    /// <summary>The account is full: no new pushes until room is made; pulls go on.</summary>
    public bool PushStopped { get; set; }
    /// <summary>The last time a pass finished.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }
    /// <summary>The account this queue belongs to. A different account starts an empty queue.</summary>
    public string? AccountId { get; set; }
    /// <summary>Items per PUT. Halved when the service refuses a whole PUT as too big, back to the full batch after one lands.</summary>
    public int BatchLimit { get; set; } = AccountSyncRules.MaxBatchItems;
    /// <summary>The one-time import from the mailbox's kept stores has run for this account (CLIENT.md 9, 1.4.10).</summary>
    public bool MailboxImported { get; set; }
    /// <summary>Object conflicts in a row for each key. After three, the queue waits for a later retry.</summary>
    public Dictionary<string, int> ObjectTries { get; set; } = [];
}

/// <summary>The rules the sync engine applies, as pure functions the tests pin (CLIENT.md 7.2 to 7.4). Ported from Exo Launcher's AccountSync.cs.</summary>
internal static partial class AccountSyncRules
{
    internal const int MaxValueBytes = 64 * 1024;
    internal const int MaxBatchItems = 100;
    internal const long MaxBodyBytes = 8L * 1024 * 1024;
    internal const string App = "cinema";
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    internal const string HeldTooLarge = "too-large";
    internal const string HeldRefused = "refused";

    /// <summary>
    /// The types Exo Cinema syncs and how each merges. Anything else never becomes an item:
    /// <c>watch</c> is one watch of a title (log: id is its public id and time); <c>resume</c> where a title was left
    /// (value); <c>watchlist</c> the saved titles (value); <c>settings</c> the page's choices (object, id <c>cinema</c>);
    /// <c>languages</c> each show's audio and subtitle choice (value, id <c>imdb:…</c> or <c>movies</c>); <c>addons</c> the list of stream sources an account from before 1.5 holds (value, id <c>list</c>): kept as the service sends it, used by nothing here.
    /// </summary>
    internal static MergeKind? KindOf(string type) => type switch
    {
        "watch" => MergeKind.Log,
        "resume" or "watchlist" or "languages" or "addons" => MergeKind.Value,
        "settings" => MergeKind.Object,
        _ => null,
    };

    internal static bool ValidType(string type) => TypeRule().IsMatch(type);
    internal static bool ValidId(string id) => IdRule().IsMatch(id);
    internal static string Key(string type, string id) => type + "/" + id;

    /// <summary>Retry delay for the nth failure: 5 s doubling to 10 minutes, each plus or minus 20% (CLIENT.md 7.6).</summary>
    internal static TimeSpan Backoff(int failures, Random random)
    {
        var seconds = Math.Min(600, 5 * Math.Pow(2, Math.Max(0, failures - 1)));
        var jitter = 1 + (random.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    /// <summary>The top-level keys whose value differs between two objects (a missing key counts as changed).</summary>
    internal static List<string> ChangedKeys(string? before, string? after)
    {
        var old = before is null ? new JsonObject() : JsonNode.Parse(before)?.AsObject() ?? new JsonObject();
        var now = after is null ? new JsonObject() : JsonNode.Parse(after)?.AsObject() ?? new JsonObject();
        return now.Select(p => p.Key).Union(old.Select(p => p.Key)).Where(k => !JsonNode.DeepEquals(old[k], now[k])).ToList();
    }

    /// <summary>The server's object with this PC's changed keys laid over it (CLIENT.md 7.4, object).</summary>
    internal static string Overlay(string? server, string? local, IEnumerable<string> changed)
    {
        var result = server is null ? new JsonObject() : JsonNode.Parse(server)?.AsObject() ?? new JsonObject();
        var mine = local is null ? new JsonObject() : JsonNode.Parse(local)?.AsObject() ?? new JsonObject();
        foreach (var key in changed)
        {
            result.Remove(key);
            if (mine[key] is { } value) result[key] = JsonNode.Parse(value.ToJsonString());
        }
        return result.ToJsonString();
    }

    /// <summary>The PUT body for a batch: one entry per item, the app is cinema.</summary>
    internal static JsonObject PutBody(IEnumerable<SyncItem> batch)
    {
        var items = new JsonArray();
        foreach (var item in batch)
        {
            var entry = new JsonObject { ["app"] = App, ["type"] = item.Type, ["id"] = item.Id, ["base"] = item.Base, ["updated"] = item.Updated };
            if (item.Deleted) entry["deleted"] = true;
            else entry["value"] = item.Value;
            items.Add(entry);
        }
        return new JsonObject { ["items"] = items };
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex TypeRule();

    [GeneratedRegex("^[A-Za-z0-9._:~@-]{1,128}$")]
    private static partial Regex IdRule();
}

/// <summary>
/// The sync engine: local changes mark items dirty, pushes wait for a quiet two seconds, pulls apply the server's items in
/// order, and each PUT's answer is applied by CLIENT.md 7.4 and 7.6. The HTTP calls are the caller's, so the same code runs
/// against the fixtures in the tests and the live service.
/// </summary>
internal sealed class AccountSyncEngine(SyncState state, Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;
    private static readonly TimeSpan ObjectRetryWait = TimeSpan.FromMinutes(1);

    public SyncState State => state;

    /// <summary>Keys the last pulls brought from the server, and those new to this PC.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>Records a local change. A null value on a value type is a deletion. Unknown types and bad names are refused.</summary>
    public void Write(string type, string id, string? value, long updated, DateTimeOffset now)
    {
        var kind = AccountSyncRules.KindOf(type) ?? throw new ArgumentException("Not a synced type: " + type, nameof(type));
        if (!AccountSyncRules.ValidType(type) || !AccountSyncRules.ValidId(id)) throw new ArgumentException("Bad item name: " + type + "/" + id);
        var key = AccountSyncRules.Key(type, id);
        state.Items.TryGetValue(key, out var entry);
        if (entry is not null && entry.Value == value && entry.Deleted == (value is null)) return; // nothing changed: nothing to send

        switch (kind)
        {
            case MergeKind.Log:
                if (entry is not null) return; // one watch, one entry: a duplicate id is dropped on this PC
                entry = new SyncEntry { Type = type, Id = id, Updated = updated, Value = value };
                break;
            case MergeKind.Object:
                entry ??= new SyncEntry { Type = type, Id = id };
                entry.Changed = entry.Changed.Union(AccountSyncRules.ChangedKeys(entry.Value, value)).ToList();
                entry.Value = value;
                entry.Updated = updated;
                break;
            default:
                entry ??= new SyncEntry { Type = type, Id = id };
                entry.Value = value;
                entry.Deleted = value is null;
                entry.Updated = updated;
                break;
        }

        state.Items[key] = entry;
        state.Held.Remove(key);
        state.HeldReasons.Remove(key);
        state.Dirty.Add(key);
        state.LastChange = now;
    }

    /// <summary>The batch to send now, or null: nothing dirty, the debounce is running, a retry waits, or pushes are stopped.</summary>
    public List<SyncItem>? NextPush(DateTimeOffset now)
    {
        if (state.SignedOut || state.PushStopped || state.Dirty.Count == 0) return null;
        if (now < state.NextAttempt || now - state.LastChange < AccountSyncRules.Debounce) return null;

        var batch = new List<SyncItem>();
        long bytes = 0;
        foreach (var key in state.Dirty.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var entry = state.Items[key];
            var size = Encoding.UTF8.GetByteCount(entry.Value ?? "");
            if (size > AccountSyncRules.MaxValueBytes)
            {
                // Too big to send: it stays on this PC until its value changes (CLIENT.md 7.6).
                Hold(key, AccountSyncRules.HeldTooLarge);
                continue;
            }
            if (batch.Count == state.BatchLimit || bytes + size > AccountSyncRules.MaxBodyBytes) break;
            batch.Add(new SyncItem(entry.Type, entry.Id, entry.Base, entry.Updated, entry.Value, entry.Deleted));
            bytes += size;
        }
        return batch.Count == 0 ? null : batch;
    }

    /// <summary>Applies the answer to a PUT of <paramref name="sent"/> (CLIENT.md 7.4, 7.6).</summary>
    public void ApplyPut(IReadOnlyList<SyncItem>? sent, AccountReply reply, DateTimeOffset now)
    {
        if (reply.Unreachable || reply.Status >= 500) { Fail(now, null); return; }
        if (reply.Error == "signed-out") { state.SignedOut = true; return; }
        if (reply.Error == "account-full") { state.PushStopped = true; return; }
        if (reply.Status == 429) { Fail(now, reply.RetryAfterSeconds ?? 60); return; }
        if (reply.Error == "too-big")
        {
            // A refused whole PUT is split in halves until the one item that is too big stands alone; only that one is held.
            var count = sent?.Count ?? 0;
            if (count > 1) { state.BatchLimit = Math.Max(1, count / 2); return; }
            if (sent is { Count: 1 }) Hold(AccountSyncRules.Key(sent[0].Type, sent[0].Id), AccountSyncRules.HeldTooLarge);
            return;
        }
        if (!reply.Ok)
        {
            // bad-item and the other client bugs: the batch is held here and logged by the caller; the rest goes on.
            foreach (var item in sent ?? []) Hold(AccountSyncRules.Key(item.Type, item.Id), AccountSyncRules.HeldRefused);
            return;
        }

        state.Failures = 0;
        state.BatchLimit = AccountSyncRules.MaxBatchItems;
        state.LastSyncAt = now;
        var body = reply.Body as JsonObject;
        var sentUpdated = (sent ?? []).ToDictionary(item => AccountSyncRules.Key(item.Type, item.Id), item => item.Updated);

        foreach (var written in Array(body, "written"))
        {
            var key = AccountSyncRules.Key(Str(written, "type"), Str(written, "id"));
            if (!state.Items.TryGetValue(key, out var entry)) continue;
            entry.Base = Num(written, "version");
            state.ObjectTries.Remove(key);
            // A change made while the batch was on the way stays queued.
            if (sentUpdated.TryGetValue(key, out var updated) && entry.Updated == updated)
            {
                state.Dirty.Remove(key);
                entry.Changed.Clear();
            }
        }

        foreach (var conflict in Array(body, "conflicts"))
        {
            var key = AccountSyncRules.Key(Str(conflict, "type"), Str(conflict, "id"));
            if (!state.Items.TryGetValue(key, out var local)) continue;
            var server = new SyncEntry
            {
                Type = Str(conflict, "type"), Id = Str(conflict, "id"), Base = Num(conflict, "version"),
                Updated = Num(conflict, "updated"), Value = conflict["value"]?.GetValue<string>(), Deleted = conflict["value"] is null,
            };
            ResolveConflict(key, local, server, now);
        }
    }

    /// <summary>Applies one page of a pull (CLIENT.md 7.7, 7.4). Returns whether the server has more pages.</summary>
    public bool Pull(AccountReply reply, DateTimeOffset now)
    {
        if (reply.Error == "signed-out") { state.SignedOut = true; return false; }
        if (!reply.Ok || reply.Body is not JsonObject body) return false;

        foreach (var item in Array(body, "items"))
        {
            if (item["app"]?.GetValue<string>() != AccountSyncRules.App) continue;
            var type = Str(item, "type");
            if (AccountSyncRules.KindOf(type) is not { } kind) continue;
            var id = Str(item, "id");
            if (!AccountSyncRules.ValidType(type) || !AccountSyncRules.ValidId(id)) continue;
            var key = AccountSyncRules.Key(type, id);
            Applied.Add(key);
            var server = new SyncEntry
            {
                Type = type, Id = id, Base = Num(item, "version"), Updated = Num(item, "updated"),
                Value = item["value"]?.GetValue<string>(), Deleted = item["value"] is null,
            };
            state.Items.TryGetValue(key, out var local);
            var mine = state.Dirty.Contains(key) && local is not null;

            switch (kind)
            {
                case MergeKind.Log:
                    // Logs keep both copies by id: the server's is added, or this PC rebases its own. Nothing double counts.
                    if (local is null) state.Items[key] = server;
                    else local.Base = server.Base;
                    state.Dirty.Remove(key);
                    break;
                case MergeKind.Object when mine:
                    state.Items[key] = new SyncEntry
                    {
                        Type = type, Id = id, Base = server.Base, Updated = local!.Updated,
                        Value = AccountSyncRules.Overlay(server.Value, local.Value, local.Changed), Changed = local.Changed,
                    };
                    break;
                case MergeKind.Value when mine && local!.Updated > server.Updated:
                    Rebase(local!, server);
                    break;
                default:
                    state.Items[key] = server;
                    state.Dirty.Remove(key);
                    break;
            }
        }

        state.Cursor = Num(body, "cursor", state.Cursor);
        state.LastSyncAt = now; // a pull that landed is a sync, pushes or not
        return body["more"]?.GetValue<bool>() == true;
    }

    /// <summary>The keys the last page brought, cleared: the caller applies them to the stores.</summary>
    public List<string> TakeApplied()
    {
        var keys = Applied.ToList();
        Applied.Clear();
        return keys;
    }

    private void ResolveConflict(string key, SyncEntry local, SyncEntry server, DateTimeOffset now)
    {
        switch (AccountSyncRules.KindOf(local.Type))
        {
            case MergeKind.Log:
                // The entry is already on the server: drop the queued copy and keep the server's.
                state.Dirty.Remove(key);
                state.Items[key] = server;
                break;
            case MergeKind.Object:
                // Start from the server's object, overlay what this PC changed, and send it again on the new base.
                // Three conflicts in a row for one key: keep the queue and retry a minute later, not in a tight loop (CLIENT.md 7.4).
                state.Items[key] = new SyncEntry
                {
                    Type = local.Type, Id = local.Id, Base = server.Base, Updated = local.Updated,
                    Value = AccountSyncRules.Overlay(server.Value, local.Value, local.Changed), Changed = local.Changed,
                };
                state.Dirty.Add(key);
                state.ObjectTries.TryGetValue(key, out var tries);
                if (++tries >= 3)
                {
                    state.ObjectTries.Remove(key);
                    state.NextAttempt = now.Add(ObjectRetryWait);
                }
                else state.ObjectTries[key] = tries;
                break;
            default:
                // The newer local change wins and is sent again; otherwise the server's value stands.
                if (local.Updated > server.Updated) Rebase(local, server);
                else
                {
                    state.Items[key] = server;
                    state.Dirty.Remove(key);
                }
                break;
        }
    }

    /// <summary>Keeps this PC's change queued on the server's new version.</summary>
    private void Rebase(SyncEntry local, SyncEntry server)
    {
        local.Base = server.Base;
        state.Dirty.Add(AccountSyncRules.Key(local.Type, local.Id));
    }

    private void Hold(string key, string reason)
    {
        state.HeldReasons[key] = reason;
        state.Dirty.Remove(key);
        state.Held.Add(key);
    }

    private void Fail(DateTimeOffset now, int? seconds)
    {
        state.Failures++;
        state.NextAttempt = now + (seconds is int s ? TimeSpan.FromSeconds(s) : AccountSyncRules.Backoff(state.Failures, _random));
    }

    private static IEnumerable<JsonObject> Array(JsonObject? body, string name) =>
        body?[name] is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string Str(JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? "";

    private static long Num(JsonNode? node, string name, long fallback = 0) =>
        node?[name] is JsonValue value
            ? value.TryGetValue(out long number) ? number : value.TryGetValue(out int small) ? small : fallback
            : fallback;
}
