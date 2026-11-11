using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.Core.Contracts;
using GlassDesk.Platform;
using System.Windows.Interop;
using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class NearbyUiTests
{
    [Fact]
    public void PanelDockToggleHidesAndShowRequestsKeepItOpenAtTopLeft()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-panel-toggle-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var services = new AppServiceHost(data);
            var panel = new NearbyTransferWindow(services.Nearby, () => Task.CompletedTask, () => { });
            try
            {
                Assert.True(panel.ShowPanel(toggle: true));
                var display = Assert.IsType<GlassDesk.Platform.Displays.DisplayInfo>(GlassDesk.Platform.Displays.DisplayService.Primary());
                var bounds = GlassDesk.Platform.Displays.DisplayService.GetWindowPixelBounds(new WindowInteropHelper(panel).Handle)!.Value;
                Assert.Equal(display.WorkArea.X + display.ToPixels(16), bounds.X);
                Assert.Equal(display.WorkArea.Y + display.ToPixels(16), bounds.Y);
                panel.Left += 80;
                var moved = GlassDesk.Platform.Displays.DisplayService.GetWindowPixelBounds(new WindowInteropHelper(panel).Handle)!.Value;
                Assert.Equal(bounds.X, moved.X);
                Assert.False(panel.ShowPanel(toggle: true));
                Assert.False(panel.IsVisible);
                Assert.True(panel.ShowPanel());
                Assert.True(panel.ShowPanel());
                Assert.True(panel.IsVisible);
                Assert.False(services.Nearby.State.Running);
            }
            finally { panel.CloseForShutdown(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void PanelUsesSlimScrollThumbAndItsContentsRemainScrollable()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-panel-scroll-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var services = new AppServiceHost(data);
            var panel = new NearbyTransferWindow(services.Nearby, () => Task.CompletedTask, () => { });
            try
            {
                panel.ShowPanel(); panel.UpdateLayout();
                var scroll = Assert.IsType<ScrollViewer>(panel.FindName("TransferScroll"));
                scroll.ApplyTemplate();
                var bar = Assert.IsType<System.Windows.Controls.Primitives.ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
                bar.ApplyTemplate();
                var track = Assert.IsType<System.Windows.Controls.Primitives.Track>(bar.Template.FindName("PART_Track", bar));
                track.Thumb.ApplyTemplate();
                Assert.Equal(6, Assert.IsType<Border>(track.Thumb.Template.FindName("Grip", track.Thumb)).Width);
                scroll.ScrollToEnd(); panel.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 2);
            }
            finally { panel.CloseForShutdown(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void ReopeningPanelRestartsRevealAndHidingClearsItsAnimation()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-panel-reveal-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var services = new AppServiceHost(data);
            var panel = new NearbyTransferWindow(services.Nearby, () => Task.CompletedTask, () => { });
            var appearance = GlassDesk.UI.Theming.ThemeService.Current?.Appearance;
            var reduceMotion = appearance?.ReduceMotion;
            if (appearance is not null) appearance.ReduceMotion = false;
            try
            {
                panel.ShowPanel();
                Assert.True(panel.HasAnimatedProperties);
                panel.ShowPanel(toggle: true);
                Assert.False(panel.HasAnimatedProperties);
                Assert.Equal(1, panel.Opacity);
                panel.ShowPanel();
                Assert.True(panel.HasAnimatedProperties);
            }
            finally
            {
                panel.CloseForShutdown();
                if (appearance is not null) appearance.ReduceMotion = reduceMotion!.Value;
                Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void NetworkWarningSurvivesTransferRefreshAndRepairIsExplicit()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-network-ui-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var services = new AppServiceHost(data);
            var panel = new NearbyTransferWindow(services.Nearby, () => Task.CompletedTask, () => { });
            var requests = 0;
            panel.RepairNetwork = () => { requests++; return Task.FromResult("接收规则已就绪"); };
            try
            {
                panel.UpdateNetworkStatus("防火墙规则需要修复"); panel.RefreshState();
                Assert.Contains("需要修复", Assert.IsType<TextBlock>(panel.FindName("NetworkStatusText")).Text);
                Assert.Equal(0, requests);
                Assert.IsType<Button>(panel.FindName("RepairConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, requests);
                Assert.Equal("接收规则已就绪", Assert.IsType<TextBlock>(panel.FindName("NetworkStatusText")).Text);
                Assert.True(Assert.IsType<Button>(panel.FindName("RepairConnectionButton")).IsEnabled);
            }
            finally { panel.CloseForShutdown(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void BackgroundStatusDoesNotEraseTheLastSettingsApplyError()
    {
        StaTest.Run(() =>
        {
            var settings = new SettingsWindow();
            try
            {
                var result = Assert.IsType<TextBlock>(settings.FindName("NearbySettingsStatus"));
                result.Text = "保存失败：接收目录无权限。";
                settings.UpdateNearbyStatus("后台已停止监听。");
                Assert.Equal("保存失败：接收目录无权限。", result.Text);
                Assert.Equal("后台已停止监听。", Assert.IsType<TextBlock>(settings.FindName("NearbyServiceStatus")).Text);
            }
            finally { settings.Close(); }
        });
    }

    [Fact]
    public void DockFileDropStillPinsAfterNearbyMovesToControlCenter()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-drop-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "Data"))); data.LoadAll();
                var path = Path.Combine(root, "图片.png"); File.WriteAllBytes(path, [1, 2, 3]);
                using var services = new AppServiceHost(data);
                dock.AttachServices(services, DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher), () => { });
                var payload = new DataObject(DataFormats.FileDrop, new[] { path });
                static DragEventArgs DropArgs(DataObject payload, UIElement target) => (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [payload, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point()], null)!;
                var ordinaryDrop = DropArgs(payload, dock); ordinaryDrop.RoutedEvent = DragDrop.DropEvent;
                dock.RaiseEvent(ordinaryDrop);
                Assert.Single(data.Library.Items); Assert.Single(data.Layouts.Dock.PinnedItemIds);
            }
            finally { dock.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void IncomingNoticeShowsSummaryWithoutTakingForegroundFocus()
    {
        StaTest.Run(() =>
        {
            var foreground = WindowControl.GetForegroundWindow();
            var invoked = 0;
            var notice = new NearbyIncomingNotice(new NearbyIncoming("test", "手机", [new NearbyFile("a", "photo.png", 3, "image/png")], "D:\\Received", DateTimeOffset.UtcNow.AddMinutes(2)), () => invoked++);
            try
            {
                notice.Show(); notice.UpdateLayout();
                Assert.True(WindowStyles.IsNoActivate(new WindowInteropHelper(notice).Handle));
                Assert.Equal(foreground, WindowControl.GetForegroundWindow());
                Assert.False(notice.ShowActivated);
                Assert.False(notice.ShowInTaskbar);
            }
            finally { notice.Close(); }
            Assert.Equal(0, invoked);
        });
    }

    [Fact]
    public void ClosingPanelKeepsServiceRunningAndPanelIsAnActivatableWindow()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-panel-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var services = new AppServiceHost(data);
            services.Nearby.StartAsync(data.Settings.Current.Nearby).GetAwaiter().GetResult();
            var panel = new NearbyTransferWindow(services.Nearby, () => Task.CompletedTask, () => { });
            try
            {
                panel.Show(); panel.SetRefreshSuppressed(true); panel.Close();
                Assert.False(panel.IsVisible);
                Assert.True(services.Nearby.State.Running);
                panel.Show();
                panel.SetRefreshSuppressed(false);
                Assert.True(panel.ShowActivated);
                Assert.False(panel.Topmost);
                Assert.NotNull(panel.FindName("AddMediaButton"));
            }
            finally { panel.CloseForShutdown(); services.Nearby.StopAsync().GetAwaiter().GetResult(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void DockEditsPreserveNearbyChoicesAndCustomIcon()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-nearby-ui-" + Guid.NewGuid().ToString("N"));
            var settings = new SettingsWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                data.Settings.Current.Nearby.Enabled = true;
                data.Settings.Current.Nearby.DeviceName = "我的媒体设备";
                data.Settings.Current.Dock.ShowNearbyEntry = false;
                data.Settings.Current.Dock.NearbyCustomIconPath = "keep.png";
                settings.AttachDataStore(data, null);
                Assert.Contains("附近传输", settings.PlannedPages);
                Assert.IsType<Slider>(settings.FindName("DockScaleSlider")).Value = 1.2;
                var reload = new SettingsStore(data.Paths.SettingsFile); reload.Load();
                Assert.True(reload.Current.Nearby.Enabled);
                Assert.Equal("我的媒体设备", reload.Current.Nearby.DeviceName);
                Assert.False(reload.Current.Dock.ShowNearbyEntry);
                Assert.Equal("keep.png", reload.Current.Dock.NearbyCustomIconPath);
            }
            finally { settings.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void ControlCenterOpensNearbyPanelWithoutEnablingNetworkOrKeepingADockEntry()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-nearby-entry-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            var bar = new StatusBarWindow();
            NearbyTransferWindow? panel = null;
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                panel = new NearbyTransferWindow(services.Nearby, () => Task.CompletedTask, () => { });
                panel.SetRefreshSuppressed(true);
                var configure = typeof(StatusBarWindow).GetMethod("ConfigureNearby");
                Assert.NotNull(configure);
                var clicked = 0;
                configure.Invoke(bar, [(Action)(() => { clicked++; panel.ShowPanel(); }), (Func<string?>)(() => null)]);
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
                var button = Assert.IsType<Button>(center.FindName("NearbyButton"));
                center.Show();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, clicked);
                Assert.True(panel.IsVisible);
                Assert.False(center.IsVisible);
                Assert.False(services.Nearby.State.Running);
                Assert.Null(dock.FindName("NearbyButton"));
                data.Settings.Current.Dock.ShowNearbyEntry = false; host.Update(data.Settings.Current.Dock);
                Assert.Null(dock.FindName("NearbyButton"));
                Assert.False(services.Nearby.State.Running);
            }
            finally { panel?.CloseForShutdown(); bar.Close(); dock.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }
}
