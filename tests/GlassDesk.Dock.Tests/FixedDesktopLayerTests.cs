using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class FixedDesktopLayerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTime Start = new(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AppearanceSelectionAppliesToBothBarsAndSurvivesReopeningWithoutChangingAutoHide()
    {
        StaTest.Run(() => WithSettings((data, settings, theme) =>
        {
            var combo = Assert.IsType<ComboBox>(settings.FindName("DesktopLayerModeCombo"));
            using var bars = new Bars();
            Assert.Equal(0, combo.SelectedIndex);
            combo.SelectedIndex = 1;
            Assert.False(bars.Dock.Topmost);
            Assert.False(bars.Status.Topmost);
            Assert.True(data.Settings.Current.Dock.AutoHide);
            var reloaded = new GlassDeskDataStore(data.Paths);
            reloaded.LoadAll();
            theme.Update(reloaded.Settings.Current.Appearance);
            var reopened = new SettingsWindow();
            try
            {
                reopened.AttachDataStore(reloaded, null);
                var selection = Assert.IsType<ComboBox>(reopened.FindName("DesktopLayerModeCombo"));
                Assert.Equal(1, selection.SelectedIndex);
                selection.SelectedIndex = 0;
                Assert.True(bars.Dock.Topmost);
                Assert.True(bars.Status.Topmost);
                Assert.True(reloaded.Settings.Current.Dock.AutoHide);
            }
            finally { reopened.Close(); }
        }));
    }

    [Fact]
    public void FailedSaveKeepsTheOriginalLayerAndSelection()
    {
        StaTest.Run(() => WithSettings((data, settings, _) =>
        {
            var combo = Assert.IsType<ComboBox>(settings.FindName("DesktopLayerModeCombo"));
            using var bars = new Bars();
            using var blocker = new FileStream(data.Paths.SettingsFile + AtomicFile.TempSuffix,
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            combo.SelectedIndex = 1;
            Assert.Equal(0, combo.SelectedIndex);
            Assert.True(bars.Dock.Topmost);
            Assert.True(bars.Status.Topmost);
            Assert.Contains("保存失败", Assert.IsType<TextBlock>(settings.FindName("AppearanceStatus")).Text);
        }));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void FixedDockHonorsDisplayModeAcrossCoverageAndFullscreen(bool autoHide, bool covered, bool fullscreen)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(FixedAppearance());
            using var bars = new Bars(show: false);
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            host.Update(new DockSettings { AutoHide = autoHide });
            var controller = new AutoHideController(300);
            typeof(DockWindow).GetField("_dockSettings", Private)!.SetValue(bars.Dock, host);
            typeof(DockWindow).GetField("_autoHide", Private)!.SetValue(bars.Dock, controller);
            bars.Dock.ToggleRevealLock();
            void Update(int milliseconds, bool pointer = false)
            {
                typeof(DockWindow).GetMethod("UpdateAutoHideState", Private)!
                    .Invoke(bars.Dock, [Start.AddMilliseconds(milliseconds), fullscreen, covered, pointer, false]);
            }
            Update(0, true);
            Assert.Equal(AutoHideState.Shown, controller.State);
            Update(10);
            Update(309);
            Assert.NotEqual(AutoHideState.Hidden, controller.State);
            Update(310);
            Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, controller.State);
            Update(400, true);
            Assert.Equal(AutoHideState.Shown, controller.State);
            Update(410);
            Update(700, true);
            Assert.Equal(AutoHideState.Shown, controller.State);
            Update(710);
            Update(1010);
            Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, controller.State);
            host.Update(new DockSettings { AutoHide = false });
            Update(1100);
            Assert.Equal(AutoHideState.Shown, controller.State);
            theme.Update(new AppearanceSettings { PinBarsToDesktop = false });
            typeof(DockWindow).GetMethod("UpdateAutoHideState", Private)!
                .Invoke(bars.Dock, [Start.AddSeconds(2), true, true, false, false]);
            Assert.Equal(AutoHideState.Hidden, controller.State);
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void FixedStatusBarHonorsDisplayModeAndStillHonorsDisableAndSleep(bool autoHide, bool covered, bool fullscreen)
    {
        var policy = new StatusBarVisibilityController();
        var dock = new DockSettings { AutoHide = autoHide };
        AutoHideState Update(int milliseconds, bool pointer = false, bool launcher = false)
            => policy.Update(dock, Start.AddMilliseconds(milliseconds), pointer,
                covered, fullscreen, launcher, true, true, false, fixedDesktopLayer: true);
        Assert.Equal(AutoHideState.Shown, Update(0, true));
        Update(10);
        Assert.NotEqual(AutoHideState.Hidden, Update(309));
        Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, Update(310));
        Assert.Equal(AutoHideState.Shown, Update(400, true));
        Update(410);
        Assert.Equal(AutoHideState.Shown, Update(700, true));
        Update(710);
        Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, Update(1010));
        Assert.Equal(AutoHideState.Shown, Update(1100, launcher: true));
        Update(1200);
        Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, Update(1500));
        dock.AutoHide = false;
        Assert.Equal(AutoHideState.Shown, Update(1600));
        Assert.Equal(AutoHideState.Hidden, policy.Update(dock, Start, false,
            true, true, false, false, false, false, fixedDesktopLayer: true));
        Assert.Equal(AutoHideState.Hidden, policy.Update(dock, Start, false,
            true, true, false, false, true, true, fixedDesktopLayer: true));
        Assert.Equal(AutoHideState.Shown, policy.Update(dock, Start, false,
            true, true, false, false, true, false, fixedDesktopLayer: true));
        Assert.Equal(AutoHideState.Hidden, policy.Update(dock, Start, false,
            true, true, false, false, true, false));
    }

    [Fact]
    public void AppearanceResetRestoresNormalLayerWithoutResettingDockChoices()
    {
        StaTest.Run(() => WithSettings((data, settings, theme) =>
        {
            var combo = Assert.IsType<ComboBox>(settings.FindName("DesktopLayerModeCombo"));
            using var bars = new Bars();
            combo.SelectedIndex = 1;
            var reset = SettingsPageEditor.ResetPage(data.Settings.Current, "外观");
            theme.Update(reset.Appearance);
            Assert.True(bars.Dock.Topmost);
            Assert.True(bars.Status.Topmost);
            Assert.True(reset.Dock.AutoHide);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedBarsStayBelowAnExistingWindowEvenAfterNativeRaiseAndReshow(bool prepareHandleBeforeShow)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(FixedAppearance());
            var cover = new Window { Width = 400, Height = 300, ShowActivated = false };
            cover.Show();
            try
            {
                using var bars = new Bars(prepareHandleBeforeShow: prepareHandleBeforeShow);
                var coverHandle = new WindowInteropHelper(cover).Handle;
                nint desktopIcons = 0;
                EnumWindows((window, _) =>
                {
                    if (FindWindowEx(window, 0, "SHELLDLL_DefView", null) == 0) return true;
                    desktopIcons = window;
                    return false;
                }, 0);
                foreach (var bar in new Window[] { bars.Dock, bars.Status })
                {
                    var handle = new WindowInteropHelper(bar).Handle;
                    Assert.False(bar.Topmost);
                    Assert.True(WindowStyles.IsNoActivate(handle));
                    AssertAbove(coverHandle, handle, $"Initial depth: {bar.Title}; owner={GetWindow(handle,4)}; desktop={DesktopWindowAttachment.FindDesktopHost()}; cover={coverHandle}; bar={handle}; attached={typeof(GlassDesk.UI.Controls.GlassWindow).GetProperty("IsDesktopAttached", Private)!.GetValue(bar)}");
                    if (GetShellWindow() is var shell && shell != 0) AssertAbove(handle, shell);
                    if (desktopIcons != 0) AssertAbove(handle, desktopIcons);
                    var foreground = GetForegroundWindow();
                    WindowStyles.BringToTopWithoutActivation(handle);
                    Assert.Equal(foreground, GetForegroundWindow());
                    Assert.Equal(0, GetWindowLong(handle, -20) & 8);
                    AssertAbove(coverHandle, handle);
                    bar.Hide();
                    ((GlassDesk.UI.Controls.GlassWindow)bar).Show();
                    AssertAbove(coverHandle, handle);
                }
            }
            finally { cover.Close(); }
        });
    }

    private static AppearanceSettings FixedAppearance() => JsonSerializer.Deserialize<AppearanceSettings>(
        "{\"PinBarsToDesktop\":true,\"ReduceMotion\":true}")!;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NativeDesktopGroupRaiseKeepsBothBarsAboveDesktopAndRestoresApplicationLayering(bool prepareHandleBeforeShow, bool plain)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var appearance = FixedAppearance();
            if (plain) appearance.GlassMaterial = GlassDesk.Core.Models.GlassMaterialMode.PlainGlass;
            theme.Update(appearance);
            Bars? bars = null;
            var cover = new Window { Title = "GlassDesk Show Desktop regression", Width = 500, Height = 350 };
            var minimizeCount = 0;
            cover.StateChanged += (_, _) => { if (cover.WindowState == WindowState.Minimized) minimizeCount++; };
            try
            {
                cover.Show(); cover.Activate(); Pump(300);
                var coverHandle = new WindowInteropHelper(cover).Handle;
                ActivateCover(coverHandle);
                bars = new Bars(prepareHandleBeforeShow: prepareHandleBeforeShow);
                ActivateCover(coverHandle);
                foreach(var bar in new Window[]{bars.Dock,bars.Status})
                {
                    Assert.False(bar.Topmost);
                    Assert.Equal(0,GetWindowLong(new WindowInteropHelper(bar).Handle,-20)&8);
                    Assert.Equal(DesktopWindowAttachment.FindDesktopHost(),GetWindow(new WindowInteropHelper(bar).Handle,4));
                }
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    var previousMinimizeCount = minimizeCount;
                    cover.WindowState = WindowState.Minimized;
                    _ = SetWindowPos(GetShellWindow(), 0, 0, 0, 0, 0, 0x13);
                    ActivateCover(GetShellWindow());
                    Pump(300);
                    Assert.True(WindowControl.IsMinimized(coverHandle) || IsAbove(GetShellWindow(), coverHandle),
                        $"Desktop cycle={cycle}; minimized events={minimizeCount-previousMinimizeCount}; foreground={GetForegroundWindow()}; cover={coverHandle}. Keeping the bars visible must not cancel Show Desktop and restore the application.");
                    foreach (var bar in new Window[] { bars.Dock, bars.Status })
                    {
                        var handle = new WindowInteropHelper(bar).Handle;
                        Assert.False(WindowControl.IsMinimized(handle));
                        Assert.True(bar.IsVisible);
                        Assert.False(bar.Topmost);
                        Assert.Equal(0, GetWindowLong(handle, -20) & 8);
                        Assert.NotEqual(handle, GetForegroundWindow());
                        AssertAbove(handle, GetShellWindow(), "Show Desktop raised the Shell above a bar.");
                        EnumWindows((window, _) =>
                        {
                            if (FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0)
                                AssertAbove(handle, window, "Show Desktop raised the desktop icon host above a bar.");
                            return true;
                        }, 0);
                    }
                    cover.WindowState = WindowState.Normal;
                    _ = SetWindowPos(coverHandle, 0, 0, 0, 0, 0, 0x13);
                    ActivateCover(coverHandle);
                    Pump(200);
                    foreach (var bar in new Window[] { bars.Dock, bars.Status })
                        AssertAbove(coverHandle, new WindowInteropHelper(bar).Handle, "A bar stayed above an application after leaving the desktop.");
                }
            }
            finally
            {
                cover.Close(); bars?.Dispose();
            }
        });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void ActivateCover(nint window)
    {
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = current != foreground && AttachThreadInput(current, foreground, true);
        try { _ = SetForegroundWindow(window); }
        finally { if (attached) _ = AttachThreadInput(current, foreground, false); }
        Pump(100);
        Assert.Equal(window, GetForegroundWindow());
    }

    [Fact]
    public void NativeDesktopRaiseNeverPresentsAnIntentionallyConcealedBar()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(FixedAppearance());
            using var bars = new Bars();
            bars.Dock.HideSurface(); bars.Status.HideSurface();
            var cover = new Window { Width = 300, Height = 200 };
            try
            {
                cover.Show(); cover.Activate(); Pump(200);
                cover.WindowState = WindowState.Minimized;
                SetWindowPos(GetShellWindow(), 0, 0, 0, 0, 0, 0x13); Pump(200);
                Assert.False(bars.Dock.IsSurfacePresented); Assert.False(bars.Status.IsSurfacePresented);
                cover.WindowState = WindowState.Normal;
                SetWindowPos(new WindowInteropHelper(cover).Handle, 0, 0, 0, 0, 0, 0x13); Pump(200);
                Assert.False(bars.Dock.IsSurfacePresented); Assert.False(bars.Status.IsSurfacePresented);
            }
            finally
            {
                cover.Close();
            }
        });
    }

    private static void AssertAbove(nint upper, nint lower, string? reason = null)
    {
        for (var current = GetWindow(upper, 2); current != 0; current = GetWindow(current, 2))
            if (current == lower) return;
        Assert.Fail(reason ?? "The bars must remain below ordinary application windows.");
    }

    private static bool IsAbove(nint upper, nint lower)
    {
        for (var current = GetWindow(upper, 2); current != 0; current = GetWindow(current, 2))
            if (current == lower) return true;
        return false;
    }

    private static void WithSettings(Action<GlassDeskDataStore, SettingsWindow, ThemeService> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-desktop-layer-" + Guid.NewGuid().ToString("N"));
        using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        var settings = new SettingsWindow();
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Settings.Current.Dock.AutoHide = true;
            DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher).Update(data.Settings.Current.Dock);
            theme.Update(data.Settings.Current.Appearance);
            settings.AttachDataStore(data, null);
            body(data, settings, theme);
        }
        finally
        {
            settings.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Bars : IDisposable
    {
        public DockWindow Dock { get; } = new();
        public StatusBarWindow Status { get; } = new();

        public Bars(bool show = true, bool prepareHandleBeforeShow = false)
        {
            var session = new PrimaryDisplaySession(DisplayService.Primary()!);
            Dock.BindPrimaryDisplay(session);
            Status.BindPrimaryDisplay(session);
            if (prepareHandleBeforeShow)
            {
                _ = new WindowInteropHelper(Dock).EnsureHandle();
                _ = new WindowInteropHelper(Status).EnsureHandle();
            }
            if (show) { Dock.Show(); Status.Show(); }
        }

        public void Dispose() { Status.Close(); Dock.Close(); }
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
}
