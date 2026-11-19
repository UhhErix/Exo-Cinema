using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>
/// The film reader (Mpv/PlayStream.cs) against a host on this PC that goes quiet in the middle of a file, as a stream
/// host does when its link stalls. Found on 2026-11-12 in a real run: the read waited on the socket for good, so mpv's
/// stop never returned and the window froze (at Stop, at the next file, at closing). A read now ends when it is
/// cancelled, and a host that sends nothing is asked again from where it stopped.
/// </summary>
[Collection("Shared log")]
public sealed partial class PlayStreamTests : IDisposable
{
    // Bigger than what .NET would read off to keep a connection (1 MB): what is left of a film always is, so an answer
    // closed under a read lets go at once, as it does in a film.
    private const int FileSize = 3_000_000, FirstPart = 4_000;
    private readonly QuietHost _host = new();
    private readonly int _stallBefore = PlayStream.StallMs;

    public void Dispose()
    {
        PlayStream.StallMs = _stallBefore;
        _host.Dispose();
    }

    [Fact]
    public void A_read_waiting_on_a_quiet_host_ends_when_it_is_cancelled()
    {
        using var stream = PlayStream.OpenForTest(_host.Url)!;
        Assert.NotNull(stream);
        var buffer = new byte[FirstPart];
        var read = 0;
        while (read < FirstPart) read += Positive(stream.ReadForTest(buffer.AsSpan(0, FirstPart - read)));

        // The host sends nothing more. The next read waits, as mpv's stream thread does.
        var waiting = Task.Run(() => stream.ReadForTest(new byte[4096]));
        Assert.False(waiting.Wait(600), "the host was meant to be quiet");
        var cancelled = Stopwatch.StartNew();
        stream.CancelForTest();
        Assert.True(waiting.Wait(3000), "the read went on waiting after it was cancelled");
        Assert.Equal(-1, waiting.Result);
        Assert.True(cancelled.ElapsedMilliseconds < 1000, $"the read took {cancelled.ElapsedMilliseconds} ms to end");
        // And every read after it ends at once.
        Assert.Equal(-1, stream.ReadForTest(new byte[16]));
    }

    [Fact]
    public void A_host_that_goes_quiet_is_asked_again_from_where_it_stopped()
    {
        PlayStream.StallMs = 400;
        var said = new List<string>();
        using var stream = PlayStream.OpenForTest(_host.Url, line => { lock (said) said.Add(line); })!;
        Assert.NotNull(stream);
        var all = new byte[FileSize];
        var read = 0;
        var started = Stopwatch.StartNew();
        while (read < FileSize)
        {
            var got = stream.ReadForTest(all.AsSpan(read, Math.Min(65536, FileSize - read)));
            Assert.True(got > 0, $"the read gave up at {read} with {got}");
            read += got;
            Assert.True(started.ElapsedMilliseconds < 15_000, "the file never came");
        }
        // Every byte is the file's own, in order, across the second request.
        for (var i = 0; i < FileSize; i++) Assert.True(all[i] == (byte)(i % 251), $"byte {i} is wrong");
        Assert.Equal([0, FirstPart], _host.AskedFrom);
        lock (said) Assert.Contains(said, l => l.StartsWith("stream: nothing came for 0.4 s", StringComparison.Ordinal));
    }

    private static int Positive(int got)
    {
        Assert.True(got > 0, $"read gave {got}");
        return got;
    }

    /// <summary>Serves one file by byte range. The first request gets <see cref="FirstPart"/> bytes and then silence, the
    /// connection left open; a later request gets everything from where it asks.</summary>
    private sealed partial class QuietHost : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = [];
        public string Url { get; }
        public List<long> AskedFrom { get; } = [];

        public QuietHost()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/film.mkv";
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            for (var n = 0; ; n++)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
                lock (_held) _held.Add(client);
                _ = AnswerAsync(client, first: n == 0);
            }
        }

        private async Task AnswerAsync(TcpClient client, bool first)
        {
            try
            {
                var io = client.GetStream();
                var request = new StringBuilder();
                var one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    if (await io.ReadAsync(one) == 0) return;
                    else request.Append((char)one[0]);
                var from = long.Parse(Range().Match(request.ToString()).Groups[1].Value);
                lock (AskedFrom) AskedFrom.Add(from);
                var head = $"HTTP/1.1 206 Partial Content\r\nContent-Type: video/x-matroska\r\nAccept-Ranges: bytes\r\nContent-Range: bytes {from}-{FileSize - 1}/{FileSize}\r\nContent-Length: {FileSize - from}\r\n\r\n";
                await io.WriteAsync(Encoding.ASCII.GetBytes(head));
                var until = first ? from + FirstPart : FileSize;
                var body = new byte[until - from];
                for (var i = 0; i < body.Length; i++) body[i] = (byte)((from + i) % 251);
                await io.WriteAsync(body);
                await io.FlushAsync();
                // The first connection now says nothing and stays open.
                if (!first) client.Close();
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_held) foreach (var c in _held) c.Dispose();
        }

        [GeneratedRegex(@"Range: bytes=(\d+)-", RegexOptions.IgnoreCase)]
        private static partial Regex Range();
    }
}
