using System.Text.Json;
using System.Text.Json.Nodes;
using Exo.Identity;

namespace ExoCinema.Account;

/// <summary>
/// The account sync on this PC (CLIENT.md 7): every two seconds one pass writes what changed in the stores, pushes the
/// queue after its quiet two seconds, and pulls the service's items after the cursor (on start, after sign-in, then each
/// minute). Pulled items go into the stores with <see cref="AccountSyncSources.Apply"/>. The queue is saved as
/// account-sync.json beside the settings: data, never the token. One pass runs at a time.
/// </summary>
internal sealed class AccountSyncHost : IAsyncDisposable
{
    private static readonly TimeSpan PullEvery = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly Func<AccountSession?> _session;
    private readonly SyncedItems _ledger, _settings;
    private readonly Action<SyncedItems, IReadOnlyList<SyncBatch.Item>> _taken;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly object _gate = new();
    private SyncState _state;
    private AccountSyncEngine _engine;
    private DateTimeOffset _nextPull;
    private string? _token;
    private Timer? _timer;

    internal AccountSyncHost(string dataDir, Func<AccountSession?> session, SyncedItems ledger, SyncedItems settings,
        Action<SyncedItems, IReadOnlyList<SyncBatch.Item>> taken, Action<string> log)
    {
        _path = Path.Combine(dataDir, "account-sync.json");
        _session = session;
        _ledger = ledger;
        _settings = settings;
        _taken = taken;
        _log = log;
        _state = Load();
        _engine = new AccountSyncEngine(_state);
    }

    internal void Start() => _timer = new Timer(_ => _ = PassAsync(false), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));

    /// <summary>Sync now: clears a stop (a full account, a session that ended) and runs a pass with a pull.</summary>
    internal Task SyncNowAsync()
    {
        lock (_gate)
        {
            _state.PushStopped = false;
            _state.SignedOut = false;
            _state.NextAttempt = default;
        }
        return PassAsync(true);
    }

    /// <summary>This PC forgot the account (delete): the cursor and the queue go, the local data stays.</summary>
    internal void Forget()
    {
        lock (_gate)
        {
            _state = new SyncState();
            _engine = new AccountSyncEngine(_state);
            _token = null;
            Save();
        }
    }

    /// <summary>What Settings › Account shows: the state of the queue, as plain fields for the page.</summary>
    internal JsonObject Status()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return new JsonObject
            {
                ["signedOut"] = _state.SignedOut,
                ["pushStopped"] = _state.PushStopped,
                ["pending"] = _state.Dirty.Count,
                ["held"] = _state.Held.Count,
                ["heldReasons"] = new JsonArray([.. _state.Held.Select(key => (JsonNode)(_state.HeldReasons.GetValueOrDefault(key) ?? AccountSyncRules.HeldRefused))]),
                ["lastSyncAt"] = _state.LastSyncAt is { } at ? at.ToUnixTimeMilliseconds() : 0,
                ["waiting"] = _state.NextAttempt > now,
            };
        }
    }

    /// <summary>One pass: write the stores' changes, pull when due, push the queue. A pass that finds one already running does nothing.</summary>
    internal async Task PassAsync(bool forcePull)
    {
        if (!await _pass.WaitAsync(0)) return;
        try
        {
            var session = _session();
            if (session is null) return;
            using var client = new ExoAccountClient();
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                if (_state.AccountId != session.AccountId)
                {
                    // Another account on this PC: its own queue, so nothing of one goes to the other.
                    _state = new SyncState { AccountId = session.AccountId };
                    _engine = new AccountSyncEngine(_state);
                    _nextPull = default;
                }
                if (session.Token != _token)
                {
                    // A new sign-in (or the first pass since start): a session that ended is tried again.
                    _token = session.Token;
                    _state.SignedOut = false;
                }
                var queued = AccountSyncSources.Write(_engine, _state, _ledger, _settings, now);
            if (!_state.MailboxImported)
            {
                // The first pass for this account: what the mailbox left on this PC is queued and merged by id, then this never runs again.
                _state.MailboxImported = true;
                _log($"account sync: mailbox import queued {queued} item(s)");
            }
            }
            if (forcePull || now >= _nextPull)
            {
                await PullAsync(client, session.Token);
                _nextPull = DateTimeOffset.UtcNow + PullEvery;
            }
            await PushAsync(client, session.Token);
            lock (_gate) Save();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            _log($"account sync: {e.GetType().Name}");
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task PullAsync(ExoAccountClient client, string token)
    {
        for (var page = 0; page < 200; page++)
        {
            long after;
            lock (_gate) after = _state.Cursor;
            var reply = await client.PullSyncAsync(token, after);
            bool more;
            lock (_gate) more = _engine.Pull(reply, DateTimeOffset.UtcNow);
            if (!reply.Ok)
            {
                _log("account sync pull: " + (reply.Error ?? reply.Status.ToString()));
                return;
            }
            (List<SyncBatch.Item> ledger, List<SyncBatch.Item> settings) taken;
            lock (_gate) taken = AccountSyncSources.Apply(_engine.TakeApplied(), _state, _ledger, _settings);
            if (taken.ledger.Count > 0) _taken(_ledger, taken.ledger);
            if (taken.settings.Count > 0) _taken(_settings, taken.settings);
            if (!more) return;
        }
    }

    private async Task PushAsync(ExoAccountClient client, string token)
    {
        for (var batchNo = 0; batchNo < 50; batchNo++)
        {
            List<SyncItem>? batch;
            lock (_gate) batch = _engine.NextPush(DateTimeOffset.UtcNow);
            if (batch is null) return;
            var reply = await client.PutSyncAsync(token, AccountSyncRules.PutBody(batch));
            lock (_gate) _engine.ApplyPut(batch, reply, DateTimeOffset.UtcNow);
            if (!reply.Ok && reply.Error is not "too-big")
            {
                _log("account sync push: " + (reply.Error ?? reply.Status.ToString()));
                return;
            }
        }
    }

    private SyncState Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_path)) is { } saved) return saved;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _log("account sync queue: starts empty (" + e.GetType().Name + ")");
        }
        return new SyncState();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
        File.Move(temp, _path, overwrite: true);
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        _pass.Dispose();
        return ValueTask.CompletedTask;
    }
}
