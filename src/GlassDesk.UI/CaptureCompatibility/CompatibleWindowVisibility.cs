using System.Runtime.InteropServices;

internal static class CompatibleWindowVisibility
{
    internal static unsafe string? ExclusionReason(nint window)
    {
        var style = GetWindowLongPtrW(window, -20).ToInt64();
        if ((style & 0x00080000L) == 0) return null;
        var known = GetLayeredWindowAttributes(window, out _, out var alpha, out var flags);
        if (known && (flags & 2) != 0 && alpha == 0) return "ZeroOpacity";
        if ((style & 0x08080080L) != 0x08080080L) return null;
        Span<char> name = stackalloc char[16];
        fixed (char* buffer = name)
        {
            var length = GetClassNameW(window, buffer, name.Length);
            if (!name[..length].SequenceEqual("CEF-OSC-WIDGET")) return null;
        }
        if (IsMonitorSized(window)) return "ScreenSizedLayeredCefOverlay";
        return !known || (flags & 2) == 0 || (flags & 1) != 0 ? "UnsupportedLayeredCefOverlay" : null;
    }

    private static bool IsMonitorSized(nint window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(window, out var bounds) || !GetMonitorInfoW(MonitorFromWindow(window, 2), ref info)) return false;
        var width = Math.Max(0, Math.Min(bounds.Right, info.Bounds.Right) - Math.Max(bounds.Left, info.Bounds.Left));
        var height = Math.Max(0, Math.Min(bounds.Bottom, info.Bounds.Bottom) - Math.Max(bounds.Top, info.Bounds.Top));
        var total = (long)(info.Bounds.Right - info.Bounds.Left) * (info.Bounds.Bottom - info.Bounds.Top);
        return total > 0 && (long)width * height * 100 >= total * 98;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Bounds, WorkArea; public uint Flags; }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern unsafe int GetClassNameW(nint window, char* name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
