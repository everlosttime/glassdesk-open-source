using System.Diagnostics;
using System.Runtime.InteropServices;

using GlassDesk.Platform.Displays;
namespace GlassDesk.Platform.Shell;
public static class ForegroundCoverage
{
    public static bool IsCovered(PixelRect target)
    {
        if (target.Width <= 0 || target.Height <= 0) return false;
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (GetWindowThreadProcessId(hwnd,out uint pid) == 0) return false;
        if (pid == Environment.ProcessId) return false;
        var name = new char[256]; int length = GetClassName(hwnd,name,name.Length); if(length == 0) return false;
        if (new string(name,0,length) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (DwmGetWindowAttribute(hwnd,14,out int cloaked,sizeof(int)) == 0 && cloaked != 0) return false;
        Rect rect;
        if (DwmGetWindowAttribute(hwnd,9,out rect,Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd,out rect)) return false;
        return rect.Left < (long)target.X + target.Width && rect.Right > target.X && rect.Top < (long)target.Y + target.Height && rect.Bottom > target.Y;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd,[Out] char[] name,int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out Rect rect,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);
}
public static class TaskbarSettingsLink
{
    public static bool Open(out string? error)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
/// <summary>Never hides, moves or restarts the system taskbar.</summary>
public static class TaskbarEdgeInterception
{
    public static bool IsSupported => false;
    public static string Status => "不支持强制拦截系统任务栏边缘；请使用 Windows 任务栏设置。";
}
/// <summary>Documented folder flags; acquires a fresh desktop COM view on each operation.</summary>
public sealed class DesktopIconSession : IDisposable
{
    private bool? original, lastWritten;
    private bool disposed;
    public bool IsSupported => !disposed && AreIconsVisible().HasValue;
    public string Status { get; private set; } = "尚未探测桌面图标状态";
    public bool? AreIconsVisible()
    {
        if (disposed) return null;
        try
        {
            bool visible = DesktopFolderView.Read();
            if (lastWritten.HasValue && lastWritten != visible) { original = null; lastWritten = null; }
            Status = "桌面 IFolderView2 状态可读"; return visible;
        }
        catch (Exception ex) { Status = "桌面图标不支持：" + ex.Message; original = null; lastWritten = null; return null; }
    }
    public bool TrySetVisible(bool visible,out string? error)
    {
        error = null;
        var current = AreIconsVisible();
        if (!current.HasValue) { error = Status; return false; }
        if (current.Value == visible) return true;
        try
        {
            // Preserve the original before readback; a failed RPC may already have applied the write.
            original ??= current;
            lastWritten = visible;
            DesktopFolderView.Write(visible);
            var readback = AreIconsVisible();
            if (readback != visible) { error = "桌面图标写入后的状态核对失败"; return false; }
            return true;
        }
        catch(Exception ex) { error = ex.Message; return false; }
    }
    public bool TryToggle(out string? error)
    {
        var current = AreIconsVisible();
        if (!current.HasValue) { error = Status; return false; }
        return TrySetVisible(!current.Value,out error);
    }
    public void Dispose()
    {
        if (disposed) return;
        var current = AreIconsVisible();
        if (original.HasValue && lastWritten.HasValue && current == lastWritten)
        {
            try { DesktopFolderView.Write(original.Value); } catch { /* Explorer may have exited. */ }
        }
        disposed = true;
    }
}
internal static class DesktopFolderView
{
    private static readonly Guid BrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid FolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    internal static bool Read() => WithView(view => { Check(Slot<GetFlags>(view,25)(view,out uint flags)); return (flags & 0x1000) == 0; });
    internal static void Write(bool visible) => WithView(view => { Check(Slot<SetFlags>(view,24)(view,0x1000,visible ? 0u : 0x1000)); return true; });
    private static T WithView<T>(Func<nint,T> operation)
    {
        object? windows = null, desktop = null; nint browser=0, view=0, folder=0;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"),true)!);
            object location = 0, root = 0;
            desktop = ((dynamic)windows!).FindWindowSW(ref location,ref root,8,out int _,1);
            if (desktop == null) throw new InvalidOperationException("Explorer 未提供桌面视图");
            var service = (IServiceProvider)desktop;
            var sid = BrowserService; var iid = ShellBrowser;
            Check(service.QueryService(ref sid,ref iid,out browser));
            Check(Slot<QueryView>(browser,15)(browser,out view));
            iid = FolderView2; Check(Marshal.QueryInterface(view,in iid,out folder));
            return operation(folder);
        }
        finally
        {
            if(folder != 0) Marshal.Release(folder); if(view != 0) Marshal.Release(view); if(browser != 0) Marshal.Release(browser);
            if(desktop != null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if(windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
    }
    private static T Slot<T>(nint instance,int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),slot * IntPtr.Size));
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryView(nint self,out nint view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFlags(nint self,out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFlags(nint self,uint mask,uint flags);
    [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service,ref Guid iid,out nint result); }
}
