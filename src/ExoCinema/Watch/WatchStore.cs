using ExoCinema.Sync;

namespace ExoCinema.Watch;

/// <summary>
/// Where Exo Watch's ledger lives on this PC: <c>watch\ledger.dat</c> beside the settings (KeptItems: sealed for this
/// Windows user, written a moment after each change).
/// </summary>
internal static class WatchStore
{
    private static readonly KeptItems Kept = new(WatchLedger.App, Path.Combine("watch", "ledger.dat"), "Exo Cinema watch ledger v1");
    private static readonly Lazy<WatchLedger> Shared = new(() => new WatchLedger(Kept.Items));

    public static WatchLedger Ledger => Shared.Value;

    public static void Save() => Kept.Save();
}
