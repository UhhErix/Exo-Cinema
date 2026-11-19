using System.Runtime.InteropServices;

namespace ExoCinema.Ui;

/// <summary>Keeps a child window hidden when its owner asks to show it: the show is taken out of its WM_WINDOWPOSCHANGING.</summary>
internal static class HiddenWindow
{
    internal const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    /// <summary>Takes SWP_SHOWWINDOW off the WINDOWPOS the message's lParam points at; the move and the size go on.</summary>
    internal static void RefuseShow(IntPtr windowPos)
    {
        var offset = Marshal.OffsetOf<WINDOWPOS>(nameof(WINDOWPOS.flags)).ToInt32();
        var flags = (uint)Marshal.ReadInt32(windowPos, offset);
        if ((flags & SWP_SHOWWINDOW) != 0) Marshal.WriteInt32(windowPos, offset, (int)(flags & ~SWP_SHOWWINDOW));
    }
}
