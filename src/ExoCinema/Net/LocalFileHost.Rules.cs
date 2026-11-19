using System.Globalization;
using System.Security.Cryptography;

namespace ExoCinema.Net;

// The host's rules, kept apart from WebView2 so the tests compile them as they are (LocalFileHostTests).
internal sealed partial class LocalFileHost
{
    internal static readonly string Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>A file's address: <paramref name="path"/> is already escaped, segment by segment.</summary>
    internal static string Url(string host, string path) => $"https://{host}/{Token}/{path}";

    internal enum RangeKind { Whole, Part, Unsatisfiable }

    /// <summary>
    /// A request the page may have made: no Origin or Referer, or Cinema's own. An img or video without crossorigin sends
    /// no Origin, so absence can't be refused; the token is what keeps other frames out, this only refuses a page that
    /// says who it is.
    /// </summary>
    internal static bool IsExoRequest(string? origin, string? referer) => IsPage(origin) && IsPage(referer);

    private static bool IsPage(string? value) =>
        value is null || Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IdnHost == "cinema.exo" && u.IsDefaultPort;

    /// <summary>
    /// The file a request's path names under <paramref name="root"/>: "/token/segment/…/name.ext", each segment
    /// unescaped on its own, with an extension the host serves. Null for a wrong or missing token, an empty, dot, dot-dot
    /// or Windows-aliased segment (trailing dot or space, a device name, a stream's colon), a separator or a control
    /// character hidden in a segment, and anything that resolves outside the root.
    /// </summary>
    internal static (string Path, string Type)? ResolveFile(string root, string absolutePath, string token, IReadOnlyDictionary<string, string> types)
    {
        var segments = absolutePath.Split('/');
        if (segments.Length < 3 || segments[0].Length != 0 ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(segments[1]), System.Text.Encoding.ASCII.GetBytes(token)))
            return null;
        var names = new string[segments.Length - 2];
        for (var i = 2; i < segments.Length; i++)
        {
            string name;
            try { name = Uri.UnescapeDataString(segments[i]); }
            catch (UriFormatException) { return null; }
            if (!IsPlainName(name)) return null;
            names[i - 2] = name;
        }
        if (!types.TryGetValue(Path.GetExtension(names[^1]), out var type)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var path = Path.GetFullPath(Path.Combine([full, .. names]));
            return path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? (path, type) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static readonly char[] Unsafe = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':'];
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && name[^1] is not ('.' or ' ') &&
        name.IndexOfAny(Unsafe) < 0 && !DeviceNames.Contains(name.Split('.')[0].TrimEnd(' '));

    /// <summary>
    /// One byte range ("bytes=a-b", "bytes=a-" or "bytes=-n") of a file of <paramref name="length"/> bytes, the end
    /// clamped to the file. A header that isn't one valid range (several ranges, another unit, junk) is ignored, as HTTP
    /// allows: the whole file. A start past the end, or an empty suffix, is unsatisfiable.
    /// </summary>
    internal static RangeKind ParseRange(string? header, long length, out long first, out long last)
    {
        first = 0;
        last = length - 1;
        if (string.IsNullOrWhiteSpace(header) || length <= 0) return RangeKind.Whole;
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return RangeKind.Whole;
        var spec = value[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0 || spec.Contains(',')) return RangeKind.Whole;
        var from = spec[..dash].Trim();
        var to = spec[(dash + 1)..].Trim();
        const NumberStyles digits = NumberStyles.None;
        if (from.Length == 0)
        {
            if (!long.TryParse(to, digits, CultureInfo.InvariantCulture, out var suffix)) return RangeKind.Whole;
            if (suffix == 0) return RangeKind.Unsatisfiable;
            first = Math.Max(0, length - suffix);
            return RangeKind.Part;
        }
        if (!long.TryParse(from, digits, CultureInfo.InvariantCulture, out var start)) return RangeKind.Whole;
        long end = length - 1;
        if (to.Length > 0 && (!long.TryParse(to, digits, CultureInfo.InvariantCulture, out end) || end < start)) return RangeKind.Whole;
        if (start >= length) return RangeKind.Unsatisfiable;
        first = start;
        last = Math.Min(end, length - 1);
        return RangeKind.Part;
    }
}
