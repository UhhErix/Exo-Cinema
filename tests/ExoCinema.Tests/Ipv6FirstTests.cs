using System.Net;
using System.Net.Sockets;
using ExoCinema.Net;

namespace ExoCinema.Tests;

/// <summary>Stream hosts are reached over IPv6 first, and over IPv4 when IPv6 is no use.</summary>
public sealed class Ipv6FirstTests
{
    private static readonly IPAddress V4a = IPAddress.Parse("203.0.113.7"), V4b = IPAddress.Parse("203.0.113.8"),
        V6a = IPAddress.Parse("2001:db8::7"), V6b = IPAddress.Parse("2001:db8::8"), LinkLocal = IPAddress.Parse("fe80::1");

    [Fact]
    public void One_IPv6_address_is_tried_first_then_IPv4_then_the_rest() =>
        Assert.Equal([V6a, V4a, V4b, V6b], Ipv6First.Order([V4a, V6a, V4b, V6b], ipv6Usable: true));

    [Fact]
    public void After_IPv6_failed_IPv4_is_tried_first() =>
        Assert.Equal([V4a, V4b, V6a], Ipv6First.Order([V6a, V4a, V4b], ipv6Usable: false));

    [Fact]
    public void A_host_with_one_family_is_reached_by_it()
    {
        Assert.Equal([V4a], Ipv6First.Order([V4a], ipv6Usable: true));
        Assert.Equal([V6a], Ipv6First.Order([V6a], ipv6Usable: false));
        Assert.Empty(Ipv6First.Order([], ipv6Usable: true));
    }

    [Fact]
    public void A_link_local_address_is_never_first() =>
        Assert.Equal([V4a], Ipv6First.Order([LinkLocal, V4a], ipv6Usable: true).Take(1));

    [Fact]
    public async Task A_host_on_this_PC_is_connected_to_whichever_family_it_listens_on()
    {
        // Listening on IPv4 loopback only: "localhost" gives ::1 and 127.0.0.1, IPv6 is refused at once, IPv4 connects.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var handler = new SocketsHttpHandler { ConnectCallback = Ipv6First.ConnectAsync };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var accept = listener.AcceptAsync();
        var ask = http.GetAsync($"http://localhost:{port}/");
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(peer.Connected);
        peer.Close();
        await Assert.ThrowsAnyAsync<Exception>(() => ask);
    }
}
