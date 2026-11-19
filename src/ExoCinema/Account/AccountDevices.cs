namespace ExoCinema.Account;

/// <summary>One signed-in device of the account, as the service lists it, with whether it is this PC.</summary>
internal sealed record SessionRow(string Device, long Created, long LastUsed, bool Current, bool ThisPc = false);

/// <summary>
/// The PCs of the account (CLIENT.md 4.3). Each Exo app on this PC keeps its own device ID in its own folder, so one PC can
/// hold several sessions: here they are one PC and show as one "This PC" row. This PC is every device ID the Exo apps on it
/// keep, read from their folders; every other device is another PC.
/// </summary>
internal static class AccountDevices
{
    private static readonly string[] Apps = ["ExoLauncher", "ExoCinema", "ExoBrowser"];

    /// <summary>This PC's device IDs: this app's own, and the ones the other Exo apps keep in their folders.</summary>
    internal static HashSet<string> OfThisPc(string ownId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { ownId };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var app in Apps)
        {
            try
            {
                var saved = File.ReadAllText(Path.Combine(local, app, AccountDevice.FileName)).Trim();
                if (AccountDevice.IsValid(saved)) ids.Add(saved);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return ids;
    }

    /// <summary>
    /// The service's session rows (CLIENT.md 3, GET /v1/sessions), oldest first. A row without a valid device ID is dropped:
    /// it can't be removed or named, so it isn't shown.
    /// </summary>
    internal static List<SessionRow> Parse(IEnumerable<System.Text.Json.Nodes.JsonNode?> rows)
    {
        var found = new List<SessionRow>();
        foreach (var row in rows)
        {
            if (row is not System.Text.Json.Nodes.JsonObject item) continue;
            var device = (string?)item["device"] ?? "";
            if (!AccountDevice.IsValid(device)) continue;
            found.Add(new SessionRow(device, (long?)item["created"] ?? 0, (long?)item["lastUsed"] ?? 0, (bool?)item["current"] ?? false));
        }
        return found;
    }

    /// <summary>
    /// The rows with this PC's sessions merged into one, first; the other PCs follow in the service's order. A session is
    /// this PC's when it is the current one or its device is one of <paramref name="mine"/>.
    /// </summary>
    internal static IReadOnlyList<SessionRow> Merge(IReadOnlyList<SessionRow> rows, IReadOnlySet<string> mine)
    {
        bool Here(SessionRow row) => row.Current || mine.Contains(row.Device);
        var here = rows.Where(Here).ToList();
        if (here.Count == 0) return rows;
        var others = rows.Where(row => !Here(row)).ToList();
        var latest = here.MaxBy(row => row.LastUsed)!;
        var merged = new SessionRow(latest.Device, here.Min(row => row.Created), here.Max(row => row.LastUsed), here.Any(row => row.Current), ThisPc: true);
        return [merged, .. others];
    }
}
