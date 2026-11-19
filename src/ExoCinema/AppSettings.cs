using System.Text.Json;

namespace ExoCinema;

/// <summary>
/// What Exo Cinema remembers about itself, in settings.json beside the sign-in: whether the first run is done, the
/// last version whose What's New was seen, and the version Update now last started installing (so an update that
/// didn't take is offered with Not now from the start). The page keeps its own preferences.
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppInfo.Data, "settings.json");

    public bool Onboarded { get; set; }
    /// <summary>The one-time "Exo now has accounts" sheet was shown (or the first run made the choice).</summary>
    public bool AccountOffered { get; set; }
    public string? WhatsNewSeen { get; set; }
    public string? UpdateTried { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) is { } saved)
                return saved;
        }
        catch (JsonException) { }
        catch (IOException) { }

        // No file yet. An earlier Exo Cinema's sign-in file means it was used: no first run, and this version's What's
        // New once. Otherwise a new install: the first run, and nothing new to tell.
        var signedIn = File.Exists(Path.Combine(AppInfo.Data, "accounts.json"));
        var s = new AppSettings { Onboarded = signedIn, WhatsNewSeen = signedIn ? "0.0.0" : AppInfo.Version };
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppInfo.Data);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
