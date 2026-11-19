using System.Runtime.InteropServices;

namespace ExoCinema.Mpv;

/// <summary>The PC's physical memory now (GlobalMemoryStatusEx), for ReadAhead.Size.</summary>
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>The physical memory in bytes, total and free now; zeros when Windows can't say.</summary>
    public static (long Total, long Available) Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }
}
