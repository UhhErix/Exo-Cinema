using System.Text.Json.Nodes;
using ExoCinema.Account;

namespace ExoCinema.Tests;

/// <summary>
/// The PCs list in Settings › Account (CLIENT.md 4.3, 5). Ways it could fail with no error on either side:
/// - two Exo apps on one PC (their own device IDs) show as two PCs, so "This PC" is listed twice;
/// - this PC's current session is shown with a Remove button, so a person removes their own sign-in;
/// - a row with a malformed device ID is shown and can't be named or removed;
/// - the merge drops another PC, or changes the order the service gave.
/// </summary>
public sealed class AccountDevicesTests
{
    private const string Own = "0123456789abcdef0123456789abcdef";
    private const string Launcher = "fedcba9876543210fedcba9876543210";
    private const string Other = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static SessionRow Row(string device, long created, long used, bool current = false) => new(device, created, used, current);

    [Fact]
    public void Two_sessions_of_this_PC_merge_into_one_This_PC_row_first()
    {
        var rows = new List<SessionRow> { Row(Other, 100, 150), Row(Own, 200, 210, current: false), Row(Launcher, 50, 300) };
        var merged = AccountDevices.Merge(rows, new HashSet<string> { Own, Launcher });

        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].ThisPc);
        Assert.Equal(Launcher, merged[0].Device);          // the most recently used of this PC's sessions
        Assert.Equal(50, merged[0].Created);               // the earliest sign-in
        Assert.Equal(300, merged[0].LastUsed);             // the latest use
        Assert.False(merged[0].Current);
        Assert.Equal(Other, merged[1].Device);             // the other PC follows, unchanged
        Assert.False(merged[1].ThisPc);
    }

    [Fact]
    public void The_current_session_is_this_PC_even_when_its_device_is_not_on_this_PC_list()
    {
        var rows = new List<SessionRow> { Row(Other, 100, 150), Row(Own, 200, 210, current: true) };
        var merged = AccountDevices.Merge(rows, new HashSet<string>());

        Assert.True(merged[0].ThisPc);
        Assert.True(merged[0].Current);
        Assert.Equal(Own, merged[0].Device);
        Assert.Equal(Other, merged[1].Device);
    }

    [Fact]
    public void Without_a_session_of_this_PC_the_rows_come_back_as_the_service_sent_them()
    {
        var rows = new List<SessionRow> { Row(Other, 100, 150), Row(Launcher, 50, 60) };
        var merged = AccountDevices.Merge(rows, new HashSet<string> { Own });

        Assert.Equal(rows, merged);
        Assert.All(merged, row => Assert.False(row.ThisPc));
    }

    [Fact]
    public void A_row_with_a_bad_device_id_is_dropped_and_the_rest_keep_their_order()
    {
        // Parsed from JSON text, as the service answers: its numbers are longs, not C# int literals.
        var body = JsonNode.Parse("""
            [{"device":"NOT-HEX","created":1,"lastUsed":1,"current":false},
             {"device":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","created":2,"lastUsed":2,"current":false},
             "not an object",
             {"device":"0123456789abcdef0123456789abcdef","created":3,"lastUsed":3,"current":true}]
            """)!.AsArray();
        var parsed = AccountDevices.Parse(body);

        Assert.Equal([Other, Own], parsed.Select(row => row.Device));
        Assert.True(parsed[1].Current);
    }

    [Fact]
    public void The_service_sessions_fixture_merges_this_PC_and_keeps_the_other_PC()
    {
        // client-fixtures/account.json, case sessions-two: two sessions, the current one first. Its placeholders are
        // filled with the device IDs of this PC and another PC; the request never leaves this test.
        var body = JsonNode.Parse("""
            {"sessions":[{"device":"{{device-1}}","created":1794008166,"lastUsed":1794008166,"current":true},
                         {"device":"{{device-2}}","created":1794008173,"lastUsed":1794008173,"current":false}]}
            """)!.AsObject().ToJsonString().Replace("{{device-1}}", Own).Replace("{{device-2}}", Other);
        var merged = AccountDevices.Merge(AccountDevices.Parse(JsonNode.Parse(body)!["sessions"]!.AsArray()), new HashSet<string> { Own });

        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].ThisPc);
        Assert.Equal(Other, merged[1].Device);
        Assert.False(merged[1].Current);
    }
}
