using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Input;

public static class KeyboardModifierState
{
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    public static uint Read() => (IsDown(0x12) ? 1u : 0) | (IsDown(0x11) ? 2u : 0)
        | (IsDown(0x10) ? 4u : 0) | (IsDown(0x5B) || IsDown(0x5C) ? 8u : 0);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
