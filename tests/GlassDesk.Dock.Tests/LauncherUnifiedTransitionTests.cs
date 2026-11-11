using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Models;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherUnifiedTransitionTests
{
    [Fact]
    public void CompleteTransitionUsesOnePresenterAndReturnsInteractiveContent()
    {
        StaTest.Run(() =>
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_UNIFIED_TRANSITION");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_UNIFIED_TRANSITION", "1");
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.AndroidLiquidGlass, UseSystemBackdrop = true });
            var window = new LauncherWindow();
            typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", flags)!.SetValue(window, 1);
            try
            {
                var inspect = typeof(LauncherWindow).GetMethod("InspectUnifiedTransition", flags);
                Assert.NotNull(inspect);
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
                surface.CaptureRevealBackdrop = (_, w, h) =>
                {
                    var pixels = new byte[w * h * 4];
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i+1] = 100; pixels[i+2] = 120; pixels[i+3] = 255; }
                    return pixels;
                };
                var display = DisplayService.Primary()!; window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48));
                Pump(80);
                var unavailable = typeof(LiquidGlassSurface).GetField("_performanceSleeping", flags)!;
                unavailable.SetValue(surface, true); Pump(60); unavailable.SetValue(surface, false);
                Pump(1600);
                var metrics = JsonSerializer.SerializeToElement(inspect.Invoke(window, null));
                Assert.True(metrics.GetProperty("submitted").GetUInt64() > 10, metrics.ToString());
                Assert.True(metrics.GetProperty("sourceDeferrals").GetUInt64() > 0, metrics.ToString());
                Assert.Null(metrics.GetProperty("failure").GetString());
                Assert.True(metrics.GetProperty("handoffFences").GetUInt64() > 0, metrics.ToString());
                Assert.False((bool)typeof(LauncherWindow).GetField("_transformActive", flags)!.GetValue(window)!);
                Assert.Equal(1, ((FrameworkElement)window.FindName("ContainerTransformClipRoot")).Opacity);
                Assert.True(((FrameworkElement)window.FindName("LauncherContent")).IsHitTestVisible);
                var revision = typeof(LauncherWindow).GetField("_unifiedContentRevision", flags)!;
                var before = (long)revision.GetValue(window)!;
                window.RefreshDiagnostics(); Assert.Equal(before, revision.GetValue(window));
                typeof(LauncherWindow).GetField("_notice", flags)!.SetValue(window, "Diagnostic cache invalidation");
                window.RefreshDiagnostics(); Assert.Equal(before + 1, revision.GetValue(window));
                window.RefreshDiagnostics(); Assert.Equal(before + 1, revision.GetValue(window));
                typeof(LauncherWindow).GetField("_notice", flags)!.SetValue(window, null); window.RefreshDiagnostics();
                Assert.Equal(before + 2, revision.GetValue(window));
                var oldHost = (nint)metrics.GetProperty("host").GetInt64();
                unavailable.SetValue(surface, true);
                typeof(LauncherWindow).GetMethod("PrepareUnifiedTransition", flags)!.Invoke(window, null);
                unavailable.SetValue(surface, false);
                Assert.False(IsWindowVisible(oldHost));
                window.CloseWithTransition(); Pump(1000);
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48)); Pump(1000);
                metrics = JsonSerializer.SerializeToElement(inspect.Invoke(window, null));
                Assert.Null(metrics.GetProperty("failure").GetString());
                var host = (nint)metrics.GetProperty("host").GetInt64();
                var owner = new WindowInteropHelper(window).Handle;
                Assert.True(IsWindowVisible(host));
                window.WindowState = WindowState.Minimized; Pump(100);
                Assert.False(IsWindowVisible(host));
                window.WindowState = WindowState.Normal; Pump(100);
                Assert.True(IsWindowVisible(host));
                var cloak = 1;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(owner, 13, ref cloak, 4)); Pump(100);
                Assert.False(IsWindowVisible(host));
                cloak = 0;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(owner, 13, ref cloak, 4)); Pump(100);
                Assert.True(IsWindowVisible(host));
                var peer = CreateWindowExW(0x80, "STATIC", "Owned z-order test", 0x80000000, 0, 0, 20, 20, 0, 0, 0, 0);
                Assert.NotEqual(0, peer);
                try
                {
                    Assert.True(SetWindowPos(peer, owner, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40)); Pump(150);
                    Assert.Equal(host, GetWindow(owner, 2));
                }
                finally { DestroyWindow(peer); }
                typeof(LauncherWindow).GetMethod("AbortContainerTransform", flags)!.Invoke(window, null); Pump(80);
                Assert.False(IsWindowVisible(host));
                var shadow = (GlassShadow?)typeof(GlassWindow).GetField("_shadow", flags)!.GetValue(window);
                Assert.True(shadow?.IsVisible);
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48)); Pump(120);
                typeof(LauncherWindow).GetMethod("FailUnifiedTransition", flags)!.Invoke(window, [new COMException("Injected diagnostic device failure", unchecked((int)0x887A0005))]);
                Pump(1000);
                metrics = JsonSerializer.SerializeToElement(inspect.Invoke(window, null));
                Assert.False(metrics.GetProperty("active").GetBoolean());
                Assert.False(IsWindowVisible(host));
                Assert.Equal(1, ((System.Windows.Controls.Border)window.FindName("ContainerTransformClipRoot")).Child.Opacity);
                window.CloseWithTransition(); Pump(1000);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_UNIFIED_TRANSITION", previous); }
        });
    }
    [Fact]
    public void PendingHandoffKeepsTheCompleteFrameAcrossCompositionChangesAndClose()
    {
        StaTest.Run(() =>
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.AndroidLiquidGlass, UseSystemBackdrop = true });
            var window = new LauncherWindow();
            typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", flags)!.SetValue(window, 1);
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            surface.ReadBackdropScene = () => new GlassBackdropScene(); surface.IsBackdropActive = () => false;
            surface.CaptureRevealBackdrop = (_, w, h) =>
            {
                var pixels = new byte[w * h * 4];
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i+1] = 100; pixels[i+2] = 120; pixels[i+3] = 255; }
                return pixels;
            };
            var display = DisplayService.Primary()!; window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
            object? Call(string name, params object?[] values) => typeof(LauncherWindow).GetMethod(name, flags)!.Invoke(window, values);
            JsonElement Inspect() => JsonSerializer.SerializeToElement(Call("InspectUnifiedTransition"));
            try
            {
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48));
                Pump(80); Call("CompleteContainerTransform", 1d);
                Thread.Sleep(40); // Let the native queue drain without a WPF Dispatcher render.
                Call("OnUnifiedTransferRendering", null, EventArgs.Empty);
                Call("OnUnifiedTransferRendering", null, EventArgs.Empty);
                Assert.False(Inspect().GetProperty("shadowOnly").GetBoolean());
                Call("OnUnifiedCompositionChanged", null, EventArgs.Empty);
                Assert.False(Inspect().GetProperty("shadowOnly").GetBoolean());
                Assert.Equal(0ul, Inspect().GetProperty("handoffFences").GetUInt64());
                window.CloseWithTransition(); Pump(1000);
                Assert.False(window.IsVisible);
                Assert.Null(Inspect().GetProperty("failure").GetString());
            }
            finally { window.Close(); }
        });
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int value, int size);
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
