using System.Text.Json.Nodes;
using Exo.Identity;
using ExoCinema.Sync;

namespace ExoCinema;

/// <summary>
/// The choices that travel with the Exo account (Sync/CinemaSettings.cs says which), kept in <c>sync\cinema.dat</c>
/// beside Exo Watch's ledger. The page tells each change as it's made; only what CinemaSettings accepts is kept. The
/// account sync (Account/AccountSyncHost.cs) carries them and Exo Watch's ledger; what another PC changed goes back to
/// the page as a <c>synced</c> message (app.js), and Exo Watch is read again.
/// <para>Before 1.4.10 an end-to-end mailbox carried these (and an Exo profile) between PCs. That sync was retired with
/// the account, and its code went in 1.5. What it left on a PC is still used or left alone: <c>sync\cinema.dat</c> and
/// <c>watch\ledger.dat</c> are the stores above, and the account sync's first pass for an account sends what they hold
/// (the one-time import, AccountSyncHost); <c>sync\profile.dat</c>, <c>sync\state.json</c> and <c>sync\carry.dat</c>
/// are read by nothing and are not deleted.</para>
/// </summary>
public sealed partial class MainWindow
{
    internal static readonly KeptItems SyncedSettings = new(CinemaSettings.App, Path.Combine("sync", "cinema.dat"), "Exo Cinema settings sync v1");

    // A choice made on the page: kept as the newest version, to go out at the next push.
    private void SyncPut(JsonNode msg)
    {
        if ((string?)msg["type"] is not { } type || (string?)msg["id"] is not { } id || !CinemaSettings.Accept(type, id, msg["body"]))
        {
            _log.Write($"sync: refused a {(string?)msg["type"]} the page sent");
            return;
        }
        lock (SyncedSettings.Items) SyncedSettings.Items.Put(type, id, msg["body"]?.DeepClone());
    }

    // Choices kept on the page from before sync existed, each kept as the oldest of all unless one is here already.
    private void SyncSeed(JsonNode msg)
    {
        lock (SyncedSettings.Items)
            foreach (var n in msg["items"] as JsonArray ?? [])
                if ((string?)n?["type"] is { } type && (string?)n["id"] is { } id && n["body"] is { } body && CinemaSettings.Accept(type, id, body))
                    SyncedSettings.Items.Seed(type, id, body.DeepClone());
    }

    // What the account sync brought from another PC: settings the page applies (only what CinemaSettings takes), and
    // Exo Watch it reads again.
    private void SyncTaken(SyncedItems holder, IReadOnlyList<SyncBatch.Item> items)
    {
        if (holder.App == CinemaSettings.App)
        {
            var accepted = new JsonArray([.. items.Where(i => CinemaSettings.Accept(i.Type, i.Id, i.Body))
                .Select(i => (JsonNode)new JsonObject { ["type"] = i.Type, ["id"] = i.Id, ["body"] = i.Body?.DeepClone() })]);
            if (accepted.Count > 0) OnUi(() => PostJson(new JsonObject { ["type"] = "synced", ["items"] = accepted }.ToJsonString()));
        }
        else if (holder.App == Watch.WatchLedger.App) OnUi(() => Post(new { type = "watch-synced" }));
    }
}
