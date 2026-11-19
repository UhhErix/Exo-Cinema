using System.Runtime.InteropServices;
using ExoCinema.Ui;

namespace ExoCinema.Tests;

/// <summary>
/// WinUI shows its content window again after a restore or a resize, and shown, it takes every click off the page. The
/// window's show request is refused in the message itself: the rest of the request stays as it came.
/// </summary>
public sealed class HiddenWindowTests
{
    private const uint Show = 0x0040, Hide = 0x0080, NoMove = 0x0002, NoSize = 0x0001, NoActivate = 0x0010;

    private static HiddenWindow.WINDOWPOS Refused(HiddenWindow.WINDOWPOS pos)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<HiddenWindow.WINDOWPOS>());
        try
        {
            Marshal.StructureToPtr(pos, memory, false);
            HiddenWindow.RefuseShow(memory);
            return Marshal.PtrToStructure<HiddenWindow.WINDOWPOS>(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void A_show_is_taken_out_and_the_other_flags_stay()
    {
        var after = Refused(new() { flags = Show | NoMove | NoSize | NoActivate });
        Assert.Equal(NoMove | NoSize | NoActivate, after.flags);
    }

    [Fact]
    public void The_size_and_place_of_a_show_still_go_through()
    {
        var after = Refused(new() { hwnd = 7, hwndInsertAfter = 9, x = 1, y = 2, cx = 884, cy = 592, flags = Show });
        Assert.Equal((new IntPtr(7), new IntPtr(9), 1, 2, 884, 592, 0u), (after.hwnd, after.hwndInsertAfter, after.x, after.y, after.cx, after.cy, after.flags));
    }

    [Fact]
    public void A_hide_is_left_alone()
    {
        Assert.Equal(Hide | NoActivate, Refused(new() { flags = Hide | NoActivate }).flags);
    }

    [Fact]
    public void A_move_with_no_show_is_left_alone()
    {
        Assert.Equal(NoActivate, Refused(new() { cx = 900, cy = 600, flags = NoActivate }).flags);
    }
}
