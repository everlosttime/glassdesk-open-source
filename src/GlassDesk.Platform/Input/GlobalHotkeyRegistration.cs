using System.Runtime.InteropServices;
namespace GlassDesk.Platform.Input;
/// <summary>Owns registrations for one HWND. Use on the window message-loop thread.</summary>
public sealed class GlobalHotkeyRegistration(nint hwnd) : IDisposable
{
    private readonly HashSet<int> ids = [];
    private bool disposed;
    public bool TryRegister(int id, uint modifiers, uint virtualKey, out int error)
    {
        error = 0;
        if (disposed || id < 0 || id > 0xbfff || ids.Contains(id)) { error = 87; return false; }
        if (!RegisterHotKey(hwnd,id,modifiers,virtualKey)) { error = Marshal.GetLastWin32Error(); return false; }
        ids.Add(id); return true;
    }
    public bool Unregister(int id)
    {
        if (!ids.Contains(id)) return false;
        if (!UnregisterHotKey(hwnd,id)) return false;
        ids.Remove(id); return true;
    }
    public void Dispose() { if (disposed) return; foreach(var id in ids.ToArray()) Unregister(id); disposed = true; }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint hwnd,int id);
}
