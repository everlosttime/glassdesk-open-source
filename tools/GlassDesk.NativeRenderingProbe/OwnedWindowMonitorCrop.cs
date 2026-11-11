using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;

namespace GlassDesk.NativeRenderingProbe;

// Diagnostic readback only. Keep the owned HWND and its styles unchanged and
// export only its crop; never persist another application's monitor pixels.
internal static class OwnedWindowMonitorCrop
{
    internal static CapturePixels Capture(nint window,int expectedWidth,int expectedHeight)
    {
        GetWindowThreadProcessId(window,out var pid);
        if(pid!=Environment.ProcessId)throw new InvalidOperationException("Only an owned diagnostic window may be cropped.");
        var bounds=DisplayService.GetWindowPixelBounds(window)??throw new InvalidOperationException("Owned window bounds are unavailable.");
        var monitor=MonitorFromWindow(window,1);var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfoW(monitor,ref info))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if(bounds.Width!=expectedWidth || bounds.Height!=expectedHeight || bounds.X<info.Monitor.Left || bounds.Y<info.Monitor.Top
            || bounds.Right>info.Monitor.Right || bounds.Bottom>info.Monitor.Bottom)
            throw new InvalidOperationException("The owned diagnostic window must fit one monitor at its exact physical size.");
        var frame=global::MonitorCaptureReference.Capture(window)??throw new InvalidOperationException("No diagnostic monitor frame.");
        var left=bounds.X-info.Monitor.Left;var top=bounds.Y-info.Monitor.Top;
        if(left+expectedWidth>frame.Width || top+expectedHeight>frame.Height)throw new InvalidOperationException("Monitor frame does not cover the owned window.");
        var pixels=new byte[checked(expectedWidth*expectedHeight*4)];
        for(var y=0;y<expectedHeight;y++)frame.Pixels.AsSpan(((top+y)*frame.Width+left)*4,expectedWidth*4).CopyTo(pixels.AsSpan(y*expectedWidth*4));
        return new(expectedWidth,expectedHeight,pixels){SourceQpcTicks=frame.SourceQpcTicks};
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{internal int Size;internal Rect Monitor,Work;internal uint Flags;}
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
}
