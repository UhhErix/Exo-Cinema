using ExoCinema.Jellyfin;

namespace ExoCinema.Tests;

/// <summary>What the start-up clean-up of earlier builds' files may touch, and the server address the Jellyfin sign-in reads (LegacyData, JellyfinClient.Normalize).</summary>
public sealed class LegacyDataTests
{
    [Fact]
    public void The_clean_up_names_only_the_old_import_folder_and_the_pre_1_5_sign_in()
    {
        var paths = LegacyData.Paths(@"C:\data");
        Assert.Equal([@"C:\data\import", @"C:\data\accounts.json"], paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("jellyfin.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_clean_up_leaves_today_s_Jellyfin_sign_in_alone()
    {
        var data = Path.Combine(Path.GetTempPath(), "exo-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "import"));
        File.WriteAllText(Path.Combine(data, "accounts.json"), "{}");
        File.WriteAllText(Path.Combine(data, "jellyfin.json"), "{\"keep\":true}");
        try
        {
            LegacyData.Clear(data);
            Assert.False(Directory.Exists(Path.Combine(data, "import")));
            Assert.False(File.Exists(Path.Combine(data, "accounts.json")));
            Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(data, "jellyfin.json")));
        }
        finally { Directory.Delete(data, true); }
    }

    [Theory]
    [InlineData("jlly.cc", "https://jlly.cc")]
    [InlineData("https://jlly.cc/web/", "https://jlly.cc")]
    [InlineData("  192.168.1.20:8096 ", "https://192.168.1.20:8096")]
    [InlineData("http://media.example/jellyfin/web", "http://media.example/jellyfin")]
    public void A_typed_server_address_is_read_as_the_base_address(string typed, string expected) =>
        Assert.Equal(expected, JellyfinClient.Normalize(typed));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://media.example")]
    [InlineData("https://me:pw@media.example")]
    public void Anything_that_is_not_a_server_address_is_refused(string typed) =>
        Assert.Null(JellyfinClient.Normalize(typed));
}
