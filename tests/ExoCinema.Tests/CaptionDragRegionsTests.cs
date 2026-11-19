using ExoCinema.Ui;
using Windows.Graphics;

namespace ExoCinema.Tests;

/// <summary>
/// The Bar's caption regions in a window that isn't maximized: the page's controls (CSS pixels, as window.js measures
/// them) turned into physical client pixels at the page's zoom (0.9 for Text size Small on a 1080p screen).
/// </summary>
public sealed class CaptionDragRegionsTests
{
    // A 1440x900 window (1424x892 client) at 100% DPI, the page at zoom 0.9: the Bar's controls as window.js measured
    // them on the page (x, y, w, h in CSS pixels), and the maximize button.
    private const int ClientWidth = 1424;
    private const double Scale = 0.9;
    private static readonly RectInt32[] Holes =
    [
        new(10, 8, 200, 36), new(675, 8, 276, 36), new(678, 11, 84, 30), new(764, 11, 93, 30), new(859, 11, 90, 30),
        new(1417, 8, 155, 36), new(1420, 11, 30, 30), new(1463, 11, 34, 30), new(1535, 11, 34, 30),
    ];
    private static readonly RectInt32 Maximize = new(1499, 11, 34, 30);

    private static (RectInt32[] Caption, RectInt32[] Passthrough, RectInt32[] Maximize) Build()
    {
        CaptionDragRegions.Build(ClientWidth, Scale, Holes, Maximize, out var caption, out var passthrough, out var maximize);
        return (caption, passthrough, maximize);
    }

    private static bool Contains(RectInt32[] rects, int x, int y) => CaptionDragRegions.Contains(rects, x, y);

    [Fact]
    public void A_point_is_in_a_rect_up_to_but_not_on_its_far_edges()
    {
        RectInt32[] rects = [new(10, 20, 30, 40)];
        Assert.True(Contains(rects, 10, 20));
        Assert.True(Contains(rects, 39, 59));
        Assert.False(Contains(rects, 40, 30));
        Assert.False(Contains(rects, 20, 60));
        Assert.False(Contains([], 10, 20));
    }

    [Fact]
    public void Empty_bar_between_the_rooms_and_the_tools_drags()
    {
        var (caption, _, _) = Build();
        // CSS x 1000 is between the Rooms (ends at 951) and the Tools (start at 1417): 900 physical.
        Assert.True(Contains(caption, 900, 23));
        Assert.True(Contains(caption, 900, 7));
        Assert.True(Contains(caption, 900, 39));
    }

    [Fact]
    public void Controls_are_holes_not_caption_at_zoom_0_9()
    {
        var (caption, passthrough, _) = Build();
        // Search, the Movies tab, the gear, minimize and close, in physical pixels (CSS x 0.9).
        foreach (var (x, y) in new[] { (99, 23), (720, 23), (1291, 23), (1332, 25), (1397, 25) })
        {
            Assert.False(Contains(caption, x, y), $"({x},{y}) is a control, not caption");
            Assert.True(Contains(passthrough, x, y), $"({x},{y}) is passed to the page");
        }
    }

    [Fact]
    public void Maximize_is_its_own_region_and_not_caption()
    {
        var (caption, _, maximize) = Build();
        Assert.Single(maximize);
        Assert.True(Contains(maximize, 1364, 23));
        Assert.False(Contains(caption, 1364, 23));
    }

    [Fact]
    public void Regions_stay_inside_the_client_area_and_the_band()
    {
        var (caption, passthrough, maximize) = Build();
        int band = (int)Math.Round(52 * Scale);
        foreach (var r in caption.Concat(passthrough).Concat(maximize))
        {
            Assert.InRange(r.X, 0, ClientWidth);
            Assert.True(r.X + r.Width <= ClientWidth, $"{r} runs past the client width");
            Assert.True(r.Y >= 0 && r.Y + r.Height <= band, $"{r} is outside the band");
        }
    }
}
