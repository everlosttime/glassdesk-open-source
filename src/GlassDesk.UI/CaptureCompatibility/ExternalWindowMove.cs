using System.Runtime.InteropServices;

internal static class ExternalWindowMove
{
    internal static int ProcessId()
    {
        const uint InMoveSize = 2;
        var state = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        // Mouse-up, Esc and loss of foreground activate the normal cadence on
        // the next check. No low-level input hook or wall-clock timeout is needed.
        if (!GetGUIThreadInfo(0, ref state) || (state.Flags & InMoveSize) == 0 || state.MoveSize == 0) return 0;
        GetWindowThreadProcessId(state.MoveSize, out var process);
        return process != Environment.ProcessId ? (int)process : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
