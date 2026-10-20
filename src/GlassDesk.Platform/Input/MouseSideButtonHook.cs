using System.ComponentModel;
using System.Runtime.InteropServices;
namespace GlassDesk.Platform.Input;
/// <summary>Install and dispose on a message-loop thread. Callback must only decide and enqueue UI work.</summary>
public sealed class MouseSideButtonHook : IDisposable
{
    private readonly Func<int,bool> onButton;
    private readonly Func<int, bool>? onWheel;
    private readonly HookProc callback;
    private readonly bool[] consumed = new bool[3];
    private nint handle;
    private bool disposed;
    public MouseSideButtonHook(Func<int,bool> onButton, Func<int, bool>? onWheel = null) { this.onButton = onButton ?? throw new ArgumentNullException(nameof(onButton)); this.onWheel = onWheel; callback = Hook; }
    public bool TryStart(out string? error)
    {
        error = null;
        if (disposed) { error = "侧键监听已释放"; return false; }
        if (handle != 0) return true;
        handle = SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
        if (handle != 0) return true;
        error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false;
    }
    private bool HandleButton(int button, bool down, bool injected)
    {
        if (disposed || injected || button is < 1 or > 2) return false;
        if (!down) { bool result = consumed[button]; consumed[button] = false; return result; }
        if (consumed[button]) return true;
        try { return consumed[button] = onButton(button); } catch { return false; }
    }
    private nint Hook(int code,nint message,nint data)
    {
        try
        {
            if (code >= 0 && ((int)message == 0x20b || (int)message == 0x20c || (int)message == 0x20a))
            {
                var info = Marshal.PtrToStructure<MouseInfo>(data);
                if ((int)message == 0x20a)
                {
                    if (!disposed && (info.Flags & 3) == 0 && onWheel?.Invoke(unchecked((short)(info.MouseData >> 16))) == true) return 1;
                }
                else if (HandleButton((int)(info.MouseData >> 16),(int)message == 0x20b,(info.Flags & 3) != 0)) return 1;
            }
        }
        catch { /* Never let managed exceptions cross the native callback. */ }
        return CallNextHookEx(handle,code,message,data);
    }
    public void Dispose() { if (disposed) return; if (handle != 0) UnhookWindowsHookEx(handle); handle = 0; disposed = true; GC.KeepAlive(callback); }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInfo { public int X,Y; public uint MouseData,Flags,Time; public nuint ExtraInfo; }
    private delegate nint HookProc(int code,nint message,nint data);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWindowsHookEx(int kind,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook,int code,nint message,nint data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
