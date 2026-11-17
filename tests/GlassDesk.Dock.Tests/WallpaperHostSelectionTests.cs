using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

namespace GlassDesk.Dock.Tests;

public sealed class WallpaperHostSelectionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false, 22631)]
    [InlineData(true, false, 26100)]
    [InlineData(false, true, 22631)]
    [InlineData(true, true, 26100)]
    [InlineData(false, false, 19045)]
    public void WallpaperUsesItsOwnDesktopHostWithoutSamplingTheBarsOwner(bool nested, bool ownsBars, int build)
    {
        using var foreign = new Windows10DesktopBackgroundTests.ForeignWindows();
        StaTest.Run(() =>
        {
            using var host = new DesktopHostFixture(nested);
            var renderer = foreign.BottomHandle;
            var style = GetWindowLongW(renderer, -16);
            var parent = GetParent(renderer);
            try
            {
                SetWindowLongW(renderer, -16, (style | 0x40000000) & ~unchecked((int)0x80000000));
                SetParent(renderer, host.WallpaperHost);
                Assert.True(SetWindowPos(renderer, 0, 0, 0, 240, 160, 0x54));
                foreign.Send("black");
                using var capture = new DocumentedDesktopSceneCapture(new(300, 200, 240, 160), false, null,
                    () => Process.GetProcesses().Select(p => { using (p) return p.Id; }).Where(id => id != foreign.ProcessId).ToArray(),
                    ownsBars ? () => host.Root : GetShellWindow, build, desktopOnly: true);
                var selected = (nint)typeof(DocumentedDesktopSceneCapture).GetField("_liveWallpaper", Private)!.GetValue(capture)!;
                var expected = build < 22000 || ownsBars && !nested ? renderer : host.WallpaperHost;
                Assert.Equal(expected, selected);
                if (expected == host.WallpaperHost)
                {
                    // Model a GPU renderer that successfully reads as black, while
                    // its composing host has the actual changing wallpaper pixels.
                    // HWND discovery/parenting is real; only driver readback is injected.
                    uint currentColor = 0;
                    var surface = new LiveWallpaperSurface(240, 160, 240, 160, (window, dc) =>
                    {
                        var rect = new NativeRect { Right = 240, Bottom = 160 };
                        var brush = CreateSolidBrush(window == host.WallpaperHost ? currentColor : 0);
                        try { return FillRect(dc, ref rect, brush) != 0; }
                        finally { DeleteObject(brush); }
                    }, null);
                    SetField(capture, "_liveSurface", surface);
                    SetField(capture, "_liveWidth", 240);
                    SetField(capture, "_liveHeight", 160);
                    SetField(capture, "_livePixels", new byte[240 * 160 * 4]);
                    foreach (var color in new uint[] { 0x00C800, 0x0000C8 })
                    {
                        currentColor = color;
                        var barrier = Stopwatch.GetTimestamp();
                        CapturePixels? frame = null;
                        var clock = Stopwatch.StartNew();
                        while (frame is null && clock.Elapsed < TimeSpan.FromSeconds(2))
                            frame = capture.TryRead(TimeSpan.FromMilliseconds(16), sourceNotBeforeTicks: barrier);
                        Assert.NotNull(frame);
                        Assert.Equal(new byte[] { 0, color == 0x00C800 ? (byte)200 : (byte)0,
                            color == 0x0000C8 ? (byte)200 : (byte)0, 255 }, frame.Center);
                        Assert.True(frame.SourceQpcTicks >= barrier);
                    }
                }
            }
            finally
            {
                SetParent(renderer, parent);
                SetWindowLongW(renderer, -16, style);
                SetWindowPos(renderer, 0, 300, 200, 240, 160, 0x54);
            }
        });
    }

    private static void SetField(object target, string field, object value)
        => target.GetType().GetField(field, Private)!.SetValue(target, value);

    private sealed class DesktopHostFixture : IDisposable
    {
        private readonly WindowProcedure _procedure = DefWindowProcW;
        private readonly nint _instance = GetModuleHandleW(null);
        public nint Root { get; }
        public nint WallpaperHost { get; }
        public DesktopHostFixture(bool nested)
        {
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Instance = _instance, ClassName = "WorkerW" };
            Assert.NotEqual((ushort)0, RegisterClassExW(ref cls));
            Root = CreateWindowExW(8, "WorkerW", "GlassDesk wallpaper host fixture", 0x90000000,
                300, 200, 240, 160, 0, 0, _instance, 0);
            Assert.NotEqual(0, Root);
            WallpaperHost = nested ? CreateWindowExW(0, "WorkerW", "", 0x50000000,
                0, 0, 240, 160, Root, 0, _instance, 0) : Root;
            Assert.NotEqual(0, WallpaperHost);
        }
        public void Dispose()
        {
            DestroyWindow(Root);
            UnregisterClassW("WorkerW", _instance);
            GC.KeepAlive(_procedure);
        }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string cls, nint instance);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint w, uint m, nint a, nint b);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
