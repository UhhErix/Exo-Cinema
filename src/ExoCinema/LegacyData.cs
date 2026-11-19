namespace ExoCinema;

/// <summary>
/// Files from earlier builds, cleared as the app starts: the browser data a test build kept for an import (Data\import),
/// and the Jellyfin sign-in from before 1.5 (accounts.json, its server's token). Today's sign-in is jellyfin.json, which
/// is never one of these.
/// </summary>
internal static class LegacyData
{
    /// <summary>What <see cref="Clear"/> removes under <paramref name="data"/>: a folder and a file, and nothing else.</summary>
    internal static string[] Paths(string data) => [Path.Combine(data, "import"), Path.Combine(data, "accounts.json")];

    internal static void Clear(string data)
    {
        foreach (var path in Paths(data))
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
