using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace ExoCinema.Mpv;

/// <summary>
/// How the seek bar's picture maker (FramePeek) reads a video from its host. Its mpv opens exopeek://N, and every read
/// is served from a piece of the file asked for with a byte range on one kept-open connection. FFmpeg's own reader
/// opens a new connection for each jump and closes the old one only after it, so every picture paid a new TLS
/// handshake, and a host that allows two connections per link (the player holds one) refused the jump. Here a jump is
/// one more request on the same connection, and only what the picture needs is read: a piece starts at 256 KB and
/// doubles while reading straight on, up to 4 MB. A link that redirects (a debrid service's) is asked at the address it
/// sent us to, and the link again when that stops answering. The video's address never reaches mpv or its log.
/// </summary>
internal sealed unsafe class PeekStream : IDisposable
{
    public const string Protocol = "exopeek";
    private const int First = 256 << 10, Most = 4 << 20;
    private const int LoadingFailed = -13, Unsupported = -18, Generic = -20; // MPV_ERROR_*

    private static readonly ConcurrentDictionary<int, (string Url, string? Auth)> Sources = new();
    private static int _lastSource;

    /// <summary>Lets an mpv open exopeek:// addresses.</summary>
    public static int AddTo(IntPtr ctx) => MpvNative.StreamCbAddRo(ctx, Protocol, IntPtr.Zero, &Open);

    /// <summary>The address mpv opens a video at.</summary>
    public static string Register(string url, string? auth)
    {
        var n = Interlocked.Increment(ref _lastSource);
        Sources[n] = (url, auth);
        return $"{Protocol}://{n}";
    }

    public static void Unregister(string path)
    {
        if (int.TryParse(path.AsSpan(Protocol.Length + 3), out var n)) Sources.TryRemove(n, out _);
    }

    private readonly Uri _link;
    private readonly string? _auth;
    private Uri? _landed; // where the link redirected to
    // A connection of its own, closed with the stream: letting go of the video lets go of the host.
    private readonly HttpMessageInvoker _http = new(Net.Egress.Checkpoint(Net.Egress.Purpose.Stream, new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 1,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    }));
    private readonly CancellationTokenSource _cancel = new();
    private long _size = -1, _pos;
    private byte[] _piece = [];
    private long _pieceAt;
    private int _pieceLength, _next = First;

    private PeekStream(string url, string? auth)
    {
        _link = new Uri(url);
        _auth = auth;
    }

    private int ReadInto(Span<byte> buffer)
    {
        if (_pos >= _size) return 0;
        if (_pos < _pieceAt || _pos >= _pieceAt + _pieceLength)
        {
            // Reading straight on from the last piece: a bigger one; after a jump, a small one again.
            _next = _pieceLength > 0 && _pos == _pieceAt + _pieceLength ? Math.Min(_next * 2, Most) : First;
            if (!Fetch(_pos, (int)Math.Min(_next, _size - _pos))) return -1;
        }
        var from = (int)(_pos - _pieceAt);
        var n = Math.Min(buffer.Length, _pieceLength - from);
        _piece.AsSpan(from, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    // One piece of the file, read whole so the connection is free for the next request.
    private bool Fetch(long at, int length)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var target = attempt == 0 && _landed != null ? _landed : _link;
            using var ask = new HttpRequestMessage(HttpMethod.Get, target);
            ask.Headers.Range = new RangeHeaderValue(at, at + length - 1);
            // As the player's own requests say.
            ask.Headers.TryAddWithoutValidation("User-Agent", "libmpv");
            if (_auth != null && target.Authority == _link.Authority) ask.Headers.TryAddWithoutValidation("Authorization", _auth);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            timeout.CancelAfter(15_000);
            try
            {
                using var answer = _http.Send(ask, timeout.Token);
                if (answer.StatusCode != HttpStatusCode.PartialContent || answer.Content.Headers.ContentRange is not { From: { } from, Length: { } total } || from != at)
                {
                    // The redirect's address stopped answering (they expire): the link again.
                    if (target == _landed) { _landed = null; continue; }
                    return false;
                }
                _size = total;
                if (answer.RequestMessage?.RequestUri is { } landed && landed != target) _landed = landed;
                // The reads below wait on the socket with no end of their own: the cancel, or the 15 s this piece has,
                // closes the answer, which ends the wait (a host gone quiet otherwise held mpv's stop for good).
                using var end = timeout.Token.Register(answer.Dispose);
                using var body = answer.Content.ReadAsStream(timeout.Token);
                if (_piece.Length < length) _piece = new byte[Math.Min(Most, Math.Max(length, _piece.Length * 2))];
                var got = 0;
                while (got < length)
                {
                    var r = body.Read(_piece, got, length - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got == 0) return false;
                _pieceAt = at;
                _pieceLength = got;
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (_cancel.IsCancellationRequested || target != _landed) return false;
                _landed = null;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _http.Dispose();
        _cancel.Dispose();
    }

    private static PeekStream Of(IntPtr cookie) => (PeekStream)GCHandle.FromIntPtr(cookie).Target!;

    [UnmanagedCallersOnly]
    private static int Open(IntPtr user, byte* uri, MpvNative.StreamCbInfo* info)
    {
        try
        {
            var path = Marshal.PtrToStringUTF8((IntPtr)uri) ?? "";
            if (!path.StartsWith(Protocol + "://", StringComparison.Ordinal) || !int.TryParse(path.AsSpan(Protocol.Length + 3), out var n) || !Sources.TryGetValue(n, out var source))
                return LoadingFailed;
            var stream = new PeekStream(source.Url, source.Auth);
            // The first piece, which also says how big the file is.
            if (!stream.Fetch(0, First))
            {
                stream.Dispose();
                return LoadingFailed;
            }
            info->Cookie = GCHandle.ToIntPtr(GCHandle.Alloc(stream));
            info->Read = &Read;
            info->Seek = &Seek;
            info->Size = &Size;
            info->Close = &Close;
            info->Cancel = &Cancel;
            return 0;
        }
        catch
        {
            return LoadingFailed;
        }
    }

    [UnmanagedCallersOnly]
    private static long Read(IntPtr cookie, byte* buffer, ulong count)
    {
        try { return Of(cookie).ReadInto(new Span<byte>(buffer, (int)Math.Min(count, int.MaxValue))); }
        catch { return -1; }
    }

    // A jump only moves the place; the next read asks for the piece there.
    [UnmanagedCallersOnly]
    private static long Seek(IntPtr cookie, long offset)
    {
        try
        {
            var s = Of(cookie);
            if (offset < 0 || offset > s._size) return Generic;
            s._pos = offset;
            return offset;
        }
        catch
        {
            return Generic;
        }
    }

    [UnmanagedCallersOnly]
    private static long Size(IntPtr cookie)
    {
        try { return Of(cookie)._size is > 0 and var size ? size : Unsupported; }
        catch { return Unsupported; }
    }

    [UnmanagedCallersOnly]
    private static void Close(IntPtr cookie)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(cookie);
            var s = (PeekStream)handle.Target!;
            handle.Free();
            s.Dispose();
        }
        catch { }
    }

    // From another thread: the read under way, and any after it, stop.
    [UnmanagedCallersOnly]
    private static void Cancel(IntPtr cookie)
    {
        try { Of(cookie)._cancel.Cancel(); }
        catch { }
    }
}
