namespace ExoCinema.Tests;

/// <summary>Help opens Exo's own help page, in the app's Installed apps entry and in Setup's (tools/ExoCinema.nsi).</summary>
public sealed class HelpLinkTests
{
    private const string Help = "https://getexo.app/help";
    private const string OldDiscord = "discord.gg/44mXWhaHP4";

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PRIVACY.md")) && Directory.Exists(Path.Combine(dir.FullName, "src", "ExoCinema"))) return dir.FullName;
        throw new InvalidOperationException("The repository isn't above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("src/ExoCinema/InstalledAppsEntry.cs")]
    [InlineData("tools/ExoCinema.nsi")]
    public void Help_link_is_the_help_page(string file)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), file));
        Assert.Contains(Help, text);
        Assert.DoesNotContain(OldDiscord, text);
    }
}
