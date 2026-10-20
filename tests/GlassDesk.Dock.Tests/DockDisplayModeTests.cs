using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockDisplayModeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTime Start = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayModeIsAppliedImmediatelyAndRestoredWhenSettingsReopen(bool autoHide)
    {
        StaTest.Run(() => WithSettings((data, window, host) =>
        {
            var combo = Assert.IsType<ComboBox>(window.FindName("DockDisplayModeCombo"));
            Assert.Equal(0, combo.SelectedIndex);
            combo.SelectedIndex = 1;
            Assert.True(host.Settings.AutoHide);
            combo.SelectedIndex = autoHide ? 1 : 0;
            Assert.Equal(autoHide, host.Settings.AutoHide);
            var reloaded = new GlassDeskDataStore(data.Paths);
            reloaded.LoadAll();
            Assert.Equal(autoHide, reloaded.Settings.Current.Dock.AutoHide);
            var reopened = new SettingsWindow();
            try
            {
                reopened.AttachDataStore(reloaded, null);
                Assert.Equal(autoHide ? 1 : 0,
                    Assert.IsType<ComboBox>(reopened.FindName("DockDisplayModeCombo")).SelectedIndex);
            }
            finally { reopened.Close(); }
        }));
    }

    [Fact]
    public void DisplayModeSaveFailureRestoresTheSelectionAndLiveSettings()
    {
        StaTest.Run(() => WithSettings((data, window, host) =>
        {
            var combo = Assert.IsType<ComboBox>(window.FindName("DockDisplayModeCombo"));
            using var blocker = new FileStream(data.Paths.SettingsFile + AtomicFile.TempSuffix,
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            combo.SelectedIndex = 1;
            Assert.Equal(0, combo.SelectedIndex);
            Assert.False(data.Settings.Current.Dock.AutoHide);
            Assert.False(host.Settings.AutoHide);
            Assert.Contains("保存失败", Assert.IsType<TextBlock>(window.FindName("DockSettingsStatus")).Text);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerLeaveHidesOnlyInAutomaticModeAndHoverRevealsIt(bool autoHide)
    {
        StaTest.Run(() => WithDock(autoHide, (window, controller) =>
        {
            Update(window, Start);
            Update(window, Start.AddMilliseconds(299));
            Assert.NotEqual(AutoHideState.Hidden, controller.State);
            Update(window, Start.AddMilliseconds(301));
            Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, controller.State);
            Update(window, Start.AddMilliseconds(400), pointerAtPosition: true);
            Assert.Equal(AutoHideState.Shown, controller.State);
            Update(window, Start.AddMilliseconds(900), pointerAtPosition: true, pointerVisible: true);
            Assert.Equal(AutoHideState.Shown, controller.State);
            Update(window, Start.AddMilliseconds(1000));
            Update(window, Start.AddMilliseconds(1301));
            Assert.Equal(autoHide ? AutoHideState.Hidden : AutoHideState.Shown, controller.State);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MenusAndDraggingKeepTheDockShownEvenWhenCovered(bool covered)
    {
        StaTest.Run(() => WithDock(true, (window, controller) =>
        {
            controller.PushSuppress(Start);
            Update(window, Start, covered: covered);
            Update(window, Start.AddSeconds(2), covered: covered);
            Assert.Equal(AutoHideState.Shown, controller.State);
            controller.PopSuppress(Start.AddSeconds(2));
            Update(window, Start.AddSeconds(2));
            Update(window, Start.AddMilliseconds(2301));
            Assert.Equal(AutoHideState.Hidden, controller.State);
        }));
    }

    [Fact]
    public void LauncherKeepsAutomaticDockShownAndItHidesAfterLauncherCloses()
    {
        StaTest.Run(() => WithDock(true, (window, controller) =>
        {
            controller.ForceHide(Start);
            window.SetLauncherContainerState(true);
            Update(window, Start.AddSeconds(1));
            Assert.Equal(AutoHideState.Shown, controller.State);
            window.SetLauncherContainerState(false);
            Update(window, Start.AddSeconds(2));
            Update(window, Start.AddMilliseconds(2301));
            Assert.Equal(AutoHideState.Hidden, controller.State);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraggingADockTileOutsideTheBarKeepsItShownUntilTheGestureEnds(bool covered)
    {
        StaTest.Run(() => WithDock(true, (window, controller) =>
        {
            var strip = (DockTileStrip)typeof(DockWindow).GetField("_tileStrip", Private)!.GetValue(window)!;
            var dragging = typeof(DockTileStrip).GetField("_dragActive", Private)!;
            dragging.SetValue(strip, true);
            Update(window, Start, covered: covered);
            Update(window, Start.AddSeconds(2), covered: covered);
            Assert.Equal(AutoHideState.Shown, controller.State);
            dragging.SetValue(strip, false);
            Update(window, Start.AddSeconds(3));
            Update(window, Start.AddMilliseconds(3301));
            Assert.Equal(AutoHideState.Hidden, controller.State);
        }));
    }

    private static void Update(DockWindow window, DateTime now, bool covered = false,
        bool pointerAtPosition = false, bool pointerVisible = false)
        => typeof(DockWindow).GetMethod("UpdateAutoHideState", Private)!
            .Invoke(window, [now, false, covered, pointerAtPosition, pointerVisible]);

    private static void WithDock(bool autoHide, Action<DockWindow, AutoHideController> body)
    {
        var window = new DockWindow();
        var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
        host.Update(new DockSettings { AutoHide = autoHide });
        var controller = new AutoHideController(300);
        typeof(DockWindow).GetField("_dockSettings", Private)!.SetValue(window, host);
        typeof(DockWindow).GetField("_autoHide", Private)!.SetValue(window, controller);
        try { body(window, controller); }
        finally { window.Close(); }
    }

    private static void WithSettings(Action<GlassDeskDataStore, SettingsWindow, DockSettingsHost> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-display-mode-" + Guid.NewGuid().ToString("N"));
        var window = new SettingsWindow();
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            window.AttachDataStore(data, null);
            body(data, window, host);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
