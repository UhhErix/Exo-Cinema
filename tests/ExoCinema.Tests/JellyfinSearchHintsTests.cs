using System.Text.Json.Nodes;
using ExoCinema.Jellyfin;

namespace ExoCinema.Tests;

/// <summary>The server's quick search, read into the shape a card is made from.</summary>
public sealed class JellyfinSearchHintsTests
{
    [Fact]
    public void A_hint_becomes_an_item_with_its_name_year_and_pictures()
    {
        var answer = JsonNode.Parse("""
            {"SearchHints":[
              {"ItemId":"a1","Id":"a1","Name":"Dune","Type":"Movie","ProductionYear":2021,"PrimaryImageTag":"p1","BackdropImageTag":"b1"},
              {"ItemId":"a2","Name":"Dune: Prophecy","Type":"Series","ProductionYear":2024},
              {"Id":"a3","Name":"A person","Type":"Person"},
              {"Name":"No id","Type":"Movie"}
            ],"TotalRecordCount":4}
            """)!;
        var items = JellyfinLibrary.HintItems(answer).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("a1", (string?)items[0]["Id"]);
        Assert.Equal("Dune", (string?)items[0]["Name"]);
        Assert.Equal("Movie", (string?)items[0]["Type"]);
        Assert.Equal(2021, (int?)items[0]["ProductionYear"]);
        Assert.Equal("p1", (string?)items[0]["ImageTags"]?["Primary"]);
        Assert.Equal("b1", (string?)(items[0]["BackdropImageTags"] as JsonArray)?[0]);
        // A hint that gives only ItemId, and no pictures.
        Assert.Equal("a2", (string?)items[1]["Id"]);
        Assert.Equal("Series", (string?)items[1]["Type"]);
        Assert.Null(items[1]["ImageTags"]);
    }

    [Fact]
    public void An_answer_without_hints_is_no_matches() =>
        Assert.Empty(JellyfinLibrary.HintItems(JsonNode.Parse("{}")!));
}
