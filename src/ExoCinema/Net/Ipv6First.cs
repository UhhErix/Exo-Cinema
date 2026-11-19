using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ExoCinema.Net;

/// <summary>
/// Connections to stream hosts go over IPv6 when the host and this PC both have it, whatever order Windows would pick
/// (VPN software commonly tells Windows to prefer IPv4, and leaves it so). Some stream hosts refuse a link on IPv4 that
/// they play on IPv6: a server that gets its links from a stream service has them made for the server's own IPv4 address, and
/// the service checks that address only against IPv4 callers. The look at a link and the player both connect this way,
/// so they agree. IPv4 follows at once when IPv6 can't connect, and IPv6 is then left alone for a few minutes.
/// </summary>
internal static class Ipv6First
{
    private static readonly TimeSpan Ipv6Patience = TimeSpan.FromSeconds(2.5), LeftAloneFor = TimeSpan.FromMinutes(5);
    private static long _ipv6FailedAt; // Stopwatch time of the last IPv6 connect that failed; 0 for none

    /// <summary>The addresses in the order they are tried: one IPv6 address first (unless IPv6 just failed), then IPv4,
    /// then the rest.</summary>
    internal static List<IPAddress> Order(IReadOnlyList<IPAddress> addresses, bool ipv6Usable)
    {
        var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal).ToList();
        var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        return ipv6Usable ? [.. v6.Take(1), .. v4, .. v6.Skip(1)] : [.. v4, .. v6];
    }

    private static bool Ipv6Usable =>
        Socket.OSSupportsIPv6 && (Volatile.Read(ref _ipv6FailedAt) is var at && (at == 0 || Stopwatch.GetElapsedTime(at) > LeftAloneFor));

    /// <summary>A handler's ConnectCallback.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var (host, port) = (context.DnsEndPoint.Host, context.DnsEndPoint.Port);
        var order = Order(await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false), Ipv6Usable);
        Exception? last = null;
        for (var i = 0; i < order.Count; i++)
        {
            var address = order[i];
            var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // IPv6 gets a short wait while another address is left to try: a PC whose IPv6 goes nowhere moves on.
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (ipv6 && i + 1 < order.Count) patience.CancelAfter(Ipv6Patience);
                await socket.ConnectAsync(new IPEndPoint(address, port), patience.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = e;
                if (ipv6 && Interlocked.Exchange(ref _ipv6FailedAt, Stopwatch.GetTimestamp()) == 0)
                    AppLog.Shared.Write("stream: IPv6 didn't connect; IPv4 is used for a few minutes");
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("The stream host couldn't be reached.", last);
    }
}
