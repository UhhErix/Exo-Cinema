using System.Text.Json.Nodes;
using ExoCinema.Sync;

namespace ExoCinema.Tests;

/// <summary>Only the settings plan §2.7 lists sync, each in its shape; never the sign-in's token, and no longer the stream sources from before 1.5.</summary>
public sealed class CinemaSettingsTests
{
    private static JsonNode? J(string json) => JsonNode.Parse(json);

    [Fact]
    public void The_five_choices_sync_with_their_own_values()
    {
        Assert.True(CinemaSettings.Accept("setting", "material", J("\"solid\"")));
        Assert.True(CinemaSettings.Accept("setting", "motion", J("\"on\"")));
        Assert.True(CinemaSettings.Accept("setting", "source", J("\"ask\"")));
        Assert.True(CinemaSettings.Accept("setting", "autoplay", J("\"off\"")));
        Assert.True(CinemaSettings.Accept("setting", "autoplay", null)); // a delete
        Assert.True(CinemaSettings.Accept("setting", "smooth", J("\"on\"")));
        Assert.False(CinemaSettings.Accept("setting", "smooth", J("\"rife\"")));
        Assert.False(CinemaSettings.Accept("setting", "material", J("\"neon\"")));
        Assert.False(CinemaSettings.Accept("setting", "material", J("1")));
    }

    [Fact]
    public void Never_the_sign_in_s_token_or_anything_unlisted()
    {
        Assert.False(CinemaSettings.Accept("setting", "source-list", J("\"https://files.example/abc123/manifest.json\"")));
        Assert.False(CinemaSettings.Accept("account", "token", J("\"0123456789abcdef\"")));
        Assert.False(CinemaSettings.Accept("setting", "quality", J("\"best\"")));
        Assert.False(CinemaSettings.Accept("server", "last", J("""{"address":"https://media.example","name":"Home","token":"x"}""")));
    }

    [Fact]
    public void The_stream_sources_from_before_1_5_are_no_longer_taken_from_the_page_or_another_PC()
    {
        // As 1.4 kept them: one item per source (its address and its place), and the single one before the list.
        const string old = "addon";
        var url = "https://files.example/cfg/0f3c/eyJ0eXAi/manifest.json";
        Assert.False(CinemaSettings.Accept(old, "0123456789abcdef", J($$"""{"url":"{{url}}","at":2048}""")));
        Assert.False(CinemaSettings.Accept(old, "0123456789abcdef", null));
        Assert.False(CinemaSettings.Accept(old, "stream", J($"\"{url}\"")));
        Assert.DoesNotContain(old, CinemaSettings.Synced.Keys);
    }
    [Fact]
    public void Jellyfin_s_server_item_is_only_taken_away()
    {
        Assert.True(CinemaSettings.Accept("server", "last", null));
        Assert.False(CinemaSettings.Accept("server", "last", J("""{"address":"https://media.example:8920","name":"Home"}""")));
        Assert.False(CinemaSettings.Accept("server", "other", null));
    }

    [Fact]
    public void Languages_go_by_a_show_s_public_id_never_a_server_s_item_id()
    {
        Assert.True(CinemaSettings.Accept("languages", "imdb:tt0903747", J("""{"audio":"jpn","sub":"en-US"}""")));
        Assert.True(CinemaSettings.Accept("languages", "tmdb:tv:1396", J("""{"sub":"off"}""")));
        Assert.True(CinemaSettings.Accept("languages", "movies", J("""{"audio":"eng"}""")));
        Assert.False(CinemaSettings.Accept("languages", "4f1c9e2a7b3d4c5e8f9a0b1c2d3e4f5a", J("""{"audio":"jpn"}"""))); // a server's item id
        Assert.False(CinemaSettings.Accept("languages", "imdb:tt0903747", J("""{"audio":"https://x"}""")));
        Assert.False(CinemaSettings.Accept("languages", "imdb:tt0903747", J("""{"audio":"jpn","note":"x"}""")));
        Assert.False(CinemaSettings.Accept("languages", "imdb:tt0903747", J("{}")));
    }

    [Fact]
    public void An_old_import_item_is_refused_as_any_unknown_one_is()
    {
        // Imports were removed in 1.4.5: an import item an older copy synced is ignored, not an error.
        Assert.False(CinemaSettings.Accept("import", "jellyfin", J("""{"at":1761955200000}""")));
        Assert.False(CinemaSettings.Accept("import", "other", null));
        Assert.DoesNotContain("import", CinemaSettings.Synced.Keys.Select(k => k.Split('/')[0]));
    }
}
