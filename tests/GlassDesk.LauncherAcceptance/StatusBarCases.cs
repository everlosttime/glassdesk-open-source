using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    // Exercise production CreateWindows/performance wiring with an isolated data store, without acquiring
    // the installed instance's mutex or applying desktop/taskbar changes from its interaction controller.
    private static int VerifyStatusBar(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var evidence = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(evidence);
        var data = new GlassDeskDataStore(new AppPaths(Path.Combine(evidence, "Data")));
        data.LoadAll();
        data.Settings.Current.Dock.ShowStatusBar = true;
        data.Settings.Current.Dock.AutoHide = false;
        data.Settings.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "GlassDesk.dll"));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml") });
        var hostType = assembly.GetType("GlassDesk.App.AppHost", true)!;
        var options = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!.GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()]);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var host = (IDisposable)Activator.CreateInstance(hostType, instance, null, [app, options], null)!;
        var services = new AppServiceHost(data);
        var display = new PrimaryDisplaySession(DisplayService.Primary()!);
        var settingsHost = DockSettingsHost.Initialize(app.Dispatcher);
        settingsHost.Update(data.Settings.Current.Dock);
        foreach (var (name, value) in new (string, object)[] { ("_data", data), ("_services", services),
            ("_dockSettings", settingsHost), ("_primaryDisplaySession", display) })
            hostType.GetField(name, instance)!.SetValue(host, value);
        StatusBarWindow? bar = null;
        try
        {
            hostType.GetMethod("CreateWindows", instance)!.Invoke(host, null);
            hostType.GetMethod("InitializePerformanceCoordinator", instance)!.Invoke(host, null);
            if (args.Contains("--information-bar-integration"))
            {
                hostType.GetMethod("CreateMessageWindow", instance)!.Invoke(host, null);
                hostType.GetMethod("InitializeInteractionController", instance)!.Invoke(host, null);
            }
            bar = (StatusBarWindow)hostType.GetField("_statusBar", instance)!.GetValue(host)!;
            var settings = (SettingsWindow)hostType.GetField("_settings", instance)!.GetValue(host)!;
            // Pin visibility for material/layout capture regardless of the operator's foreground window.
            bar.IsLauncherActive = () => true;
            bar.ShowInTaskbar = true;
            bar.Title = "GlassDesk 信息栏 本地验收";
            if (args.Contains("--information-bar-integration"))
                return VerifyInformationBarIntegration(bar, settings, data, host, hostType, report, evidence);
            if (args.Contains("--status-popover-animation"))
                return VerifyStatusPopoverAnimation(bar, settings, data, report, evidence);
            if (args.Contains("--control-center-layout"))
                return VerifyControlCenterLayout(bar, report, evidence, args.Contains("--audio-output-switch"));
            if (args.Contains("--status-bar-power-network"))
            {
                Pump(TimeSpan.FromSeconds(3));
                const BindingFlags stateFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                ((DispatcherTimer)typeof(StatusBarWindow).GetField("_visibilityTimer", stateFlags)!.GetValue(bar)!).Stop();
                typeof(StatusBarWindow).GetMethod("StopSampling", stateFlags)!.Invoke(bar, null);
                var apply = typeof(StatusBarWindow).GetMethod("ApplySystemStatus", stateFlags)!;
                var native = GlassDesk.Platform.SystemStatusReader.Read();
                apply.Invoke(bar, [native with { BatteryPercent = 100, IsCharging = true, HasBattery = false, NetworkLabel = "联网" }]);
                bar.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(150));
                Check("无电池即使返回100%也隐藏电量，有线连接显示网线图标", "status/power-network",
                    ((Button)bar.FindName("BatteryButton")!).Visibility == Visibility.Collapsed
                    && ((Viewbox)bar.FindName("NetworkWiredGlyph")!).Visibility == Visibility.Visible
                    && ((TextBlock)bar.FindName("NetworkWifiGlyph")!).Visibility == Visibility.Collapsed,
                    "battery hidden, wired shown, wifi hidden", "see desktop-wired-synthetic.png");
                Capture(bar, Path.Combine(evidence, "desktop-wired-synthetic.png"));
                apply.Invoke(bar, [native with { BatteryPercent = 72, IsCharging = false, HasBattery = true, NetworkLabel = "Wi-Fi" }]);
                bar.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(150));
                Check("有电池显示电量，Wi-Fi图标恢复", "status/power-network",
                    ((Button)bar.FindName("BatteryButton")!).Visibility == Visibility.Visible
                    && ((TextBlock)bar.FindName("BatteryText")!).Text == "72%"
                    && ((TextBlock)bar.FindName("NetworkWifiGlyph")!).Visibility == Visibility.Visible
                    && ((Viewbox)bar.FindName("NetworkWiredGlyph")!).Visibility == Visibility.Collapsed,
                    "72%, wifi shown, wired hidden", "see laptop-wifi-synthetic.png");
                Capture(bar, Path.Combine(evidence, "laptop-wifi-synthetic.png"));
                apply.Invoke(bar, [native]); bar.UpdateLayout();
                Capture(bar, Path.Combine(evidence, "native-status.png"));
                File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, native, cases = Cases,
                    note = "First two screenshots explicitly simulate desktop/wired and battery/Wi-Fi states. Native screenshot uses local readings." },
                    new JsonSerializerOptions { WriteIndented = true }));
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--status-bar-live")) return VerifyStatusBarLive(bar, report, evidence, args);
            if (args.Contains("--status-bar-jitter")) return VerifyStatusBarJitter(bar, report, evidence);
            if (args.Contains("--status-bar-reveal")) return VerifyStatusBarReveal(bar, report, evidence, args);
            Check("宿主创建并绑定信息栏", "status/host", ReferenceEquals(display, bar.PrimaryDisplaySession), "shared primary session", bar.PrimaryDisplaySession?.Display.ToString() ?? "null");
            Pump(TimeSpan.FromSeconds(4));
            Check("信息栏显示且时钟更新", "status/clock", bar.IsVisible && ((TextBlock)bar.FindName("TimeText")!).Text.Contains(':'), "visible time", ((TextBlock)bar.FindName("TimeText")!).Text);
            Capture(bar, Path.Combine(evidence, "status-bar.png"));
            var surface = (LiquidGlassSurface)bar.FindName("GlassSurface")!;
            Check("信息栏使用实时玻璃或桌面快照", "status/glass", surface.BackdropSource is not (GlassBackdropSource.Transparent or GlassBackdropSource.Basic), "captured desktop material", surface.BackdropSource.ToString());
            var fanApply = typeof(StatusBarWindow).GetMethod("ApplyFanStatus", instance)!;
            fanApply.Invoke(bar, [30000]);
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(bar, Path.Combine(evidence, "status-bar-fan-layout-synthetic.png"));
            Check("风扇展开后仍在主屏工作区内", "status/layout", DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(bar).Handle) is { } bounds && display.ContainsInWorkArea(bounds), "inside primary", bar.Width.ToString());
            fanApply.Invoke(bar, [null]);
            ((CheckBox)settings.FindName("ShowStatusBarCheckBox")!).IsChecked = false;
            Check("设置关闭立即隐藏并停止捕获", "status/disable", !bar.IsVisible && !bar.IsPerformanceCaptureRequested, "hidden and idle", $"visible={bar.IsVisible},capture={bar.IsPerformanceCaptureRequested}");
            var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
            Check("关闭设置写入磁盘", "status/persistence", !reload.Current.Dock.ShowStatusBar, "false", reload.Current.Dock.ShowStatusBar.ToString());
            hostType.GetMethod("SetManualGameSleep", instance)!.Invoke(host, [true]);
            ((CheckBox)settings.FindName("ShowStatusBarCheckBox")!).IsChecked = true;
            Pump(TimeSpan.FromMilliseconds(200));
            Check("休眠中重新启用不误显示", "status/sleep", !bar.IsVisible && !bar.IsPerformanceCaptureRequested, "hidden", bar.IsVisible.ToString());
            hostType.GetMethod("SetManualGameSleep", instance)!.Invoke(host, [false]);
            Check("退出休眠后重新采样显示", "status/wake", PumpUntil(() => bar.IsVisible && bar.IsPerformanceCaptureRequested, TimeSpan.FromSeconds(4)), "visible and capture admitted", bar.IsVisible.ToString());
            ((CheckBox)settings.FindName("ShowStatusBarCheckBox")!).IsChecked = false;
            bar.StopForPrimaryDisplayChange();
            display.TryRefreshPrimary(null);
            bar.RefreshDisplayPlacement();
            display.TryRebindPrimary(DisplayService.Primary());
            bar.RefreshDisplayPlacement();
            Check("主屏重连不会复活已关闭的信息栏", "status/display", !bar.IsVisible, "hidden", bar.IsVisible.ToString());
            ((CheckBox)settings.FindName("ShowStatusBarCheckBox")!).IsChecked = true;
            Pump(TimeSpan.FromSeconds(4));
            Capture(bar, Path.Combine(evidence, "status-bar-final.png"));
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cases = Cases,
                note = "Fan-layout screenshot uses a synthetic 30000 RPM value; other readings are native. Preview pins visibility only.",
                display = display.Display }, new JsonSerializerOptions { WriteIndented = true }));
            if (Argument(args, "--hold-until") is { } stop)
            {
                var until = DateTime.UtcNow.AddMinutes(15);
                while (!File.Exists(stop) && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(200));
            }
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            hostType.GetField("_shuttingDown", instance)!.SetValue(host, true);
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            host.Dispose();
        }
    }

    private static int VerifyInformationBarIntegration(StatusBarWindow bar, SettingsWindow settings,
        GlassDeskDataStore data, IDisposable host, Type hostType, string report, string evidence)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var cursor = DisplayService.GetCursorPixelPoint();
        var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", instance)!.GetValue(bar)!;
        var rectField = typeof(StatusBarWindow).GetField("_restingRect", instance)!;
        PixelRect Rect() => (PixelRect)rectField.GetValue(bar)!;
        var edge = (ComboBox)settings.FindName("StatusBarEdgeCombo");
        var scale = (Slider)settings.FindName("StatusBarScaleSlider");
        try
        {
            Pump(TimeSpan.FromSeconds(2));
            edge.SelectedIndex = 1;
            scale.Value = 1.25;
            Pump(TimeSpan.FromSeconds(1));
            var bottom = Rect();
            Check("信息栏大小和下边缘设置即时生效", "status/integration", Math.Abs(bar.Width - 475) < 1
                && bottom.Bottom < bar.PrimaryDisplaySession!.Display.WorkArea.Bottom
                && bottom.Y > bar.PrimaryDisplaySession.Display.WorkArea.Y + 100,
                "125%, bottom edge", bottom.ToString());
            ((Button)bar.FindName("ControlCenterArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(700));
            var popup = DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(center).Handle)!.Value;
            Check("底部信息栏的控制中心向上展开", "status/integration", popup.Bottom < bottom.Y,
                "popup above information bar", popup.ToString());
            Capture(center, Path.Combine(evidence, "control-center-bottom.png"));
            NativeStatusClick((Button)center.FindName("SettingsButton"));
            Check("控制中心齿轮打开实际设置窗口", "status/integration", settings.IsVisible,
                "visible settings", settings.IsVisible.ToString());
            settings.NavigateToPage("信息栏");
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(settings, Path.Combine(evidence, "information-bar-settings.png"));
            settings.Hide();
            center.Hide();
            ((Button)bar.FindName("ControlCenterArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(500));
            NativeStatusClick((Button)center.FindName("GameSleepButton"));
            Check("控制中心游戏休眠隐藏信息栏和弹窗", "status/integration",
                (bool)hostType.GetProperty("RuntimeManualGameSleep", instance)!.GetValue(host)!
                && !bar.IsVisible && !center.IsVisible, "sleeping, hidden bar and popup", bar.IsVisible.ToString());
            hostType.GetMethod("SetManualGameSleep", instance)!.Invoke(host, [false]);
            Pump(TimeSpan.FromSeconds(1));
            Check("托盘使用的恢复动作恢复信息栏", "status/integration", bar.IsVisible, "visible", bar.IsVisible.ToString());

            var offsetSlider = (Slider)settings.FindName("StatusBarOffsetXSlider");
            offsetSlider.Value = -200;
            Pump(TimeSpan.FromMilliseconds(300));
            var before = Rect();
            var savedSettings = new SettingsStore(data.Settings.FilePath);
            savedSettings.Load();
            Check("水平偏移滑条立即移动信息栏并保存", "status/integration",
                data.Settings.Current.Dock.StatusBarOffsetX == -200 && savedSettings.Current.Dock.StatusBarOffsetX == -200
                && before.X < bottom.X - 200, "offset -200 DIP, moved left", before.ToString());
            var x = before.X + before.Width / 2;
            var y = before.Y + 3;
            IosInput.SetCursorPos(x, y);
            Pump(TimeSpan.FromMilliseconds(100));
            var hit = StatusWindowFromPoint(((long)y << 32) | (uint)x);
            if (hit != new System.Windows.Interop.WindowInteropHelper(bar).Handle)
                throw new InvalidOperationException("Information bar drag target is covered; refusing native input.");
            IosInput.Mouse(2);
            Pump(TimeSpan.FromMilliseconds(80));
            for (var i = 1; i <= 5; i++)
            {
                IosInput.SetCursorPos(x - 20 * i, y);
                Pump(TimeSpan.FromMilliseconds(80));
            }
            // Native drag gestures must no longer change or save the position.
            IosInput.SetCursorPos(x - 100, y - 100);
            Pump(TimeSpan.FromMilliseconds(80));
            IosInput.Mouse(4);
            Pump(TimeSpan.FromMilliseconds(500));
            var after = Rect();
            var reload = new SettingsStore(data.Settings.FilePath);
            reload.Load();
            Check("鼠标拖动不再改变信息栏位置", "status/integration", before == after
                && reload.Current.Dock.StatusBarOffsetX == -200,
                "unchanged bounds and saved offset", $"before={before}, after={after}, offset={reload.Current.Dock.StatusBarOffsetX}");
            edge.SelectedIndex = 0;
            Pump(TimeSpan.FromMilliseconds(500));
            Check("切回上边缘保留滑条偏移", "status/integration", Math.Abs(Rect().X - after.X) <= 1 && Rect().Y < after.Y,
                "same horizontal position, top edge", Rect().ToString());
            var savedOffset = data.Settings.Current.Dock.StatusBarOffsetX;
            using (var blocker = new FileStream(data.Paths.SettingsFile + AtomicFile.TempSuffix,
                       FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                offsetSlider.Value = savedOffset - 20;
                Check("水平偏移保存失败时恢复原位置且宿主继续运行", "status/integration",
                    data.Settings.Current.Dock.StatusBarOffsetX == savedOffset && offsetSlider.Value == savedOffset && bar.IsVisible,
                    "original offset and visible bar", data.Settings.Current.Dock.StatusBarOffsetX.ToString());
            }
            Capture(bar, Path.Combine(evidence, "information-bar-top.png"));
            var interaction = (TextBlock)settings.FindName("InteractionSettingsStatus");
            if (!interaction.Text.Contains("已就绪")) throw new InvalidOperationException("Hotkey initialization failed: " + interaction.Text);
            void PressSleepKey(ushort key, bool controlAlt)
            {
                if (controlAlt) { IosInput.Key(0x11, false); IosInput.Key(0x12, false); }
                IosInput.Key(key, false); Pump(TimeSpan.FromMilliseconds(60)); IosInput.Key(key, true);
                if (controlAlt) { IosInput.Key(0x12, true); IosInput.Key(0x11, true); }
                Pump(TimeSpan.FromMilliseconds(700));
            }
            bool Sleeping() => (bool)hostType.GetProperty("RuntimeManualGameSleep", instance)!.GetValue(host)!;
            PressSleepKey(0x47, true);
            Check("默认Ctrl+Alt+G开启游戏休眠", "status/hotkey", Sleeping() && !bar.IsVisible, "sleeping and hidden", Sleeping().ToString());
            PressSleepKey(0x47, true);
            Check("休眠后Ctrl+Alt+G仍可恢复", "status/hotkey", !Sleeping() && bar.IsVisible, "awake and visible", Sleeping().ToString());
            settings.Show(); settings.NavigateToPage("快捷操作"); Pump(TimeSpan.FromMilliseconds(300));
            Capture(settings, Path.Combine(evidence, "shortcuts.png"));
            var recorder = (ShortcutRecorder)settings.FindName("ToggleGameSleepHotkeyBox");
            recorder.BeginRecording(); recorder.CaptureKey(System.Windows.Input.Key.F8, System.Windows.Input.ModifierKeys.None);
            settings.Hide();
            PressSleepKey(0x77, false);
            Check("修改绑定后F8可以开启休眠", "status/hotkey", Sleeping(), "sleeping", Sleeping().ToString());
            PressSleepKey(0x77, false);
            Check("修改绑定后F8可以恢复", "status/hotkey", !Sleeping(), "awake", Sleeping().ToString());
        }
        catch (Exception error) { Check("信息栏整合验收", "status/integration", false, "no exception", error.ToString()); }
        finally
        {
            IosInput.Mouse(4);
            if (cursor is { } point) IosInput.SetCursorPos(point.X, point.Y);
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cases = Cases,
                note = "Isolated real AppHost, native buttons and hotkeys; offset slider persistence and save rollback; native drag has no effect. Original cursor restored." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;
    }

    private static int VerifyControlCenterLayout(StatusBarWindow bar, string report, string evidence, bool switchAudio)
    {
        const BindingFlags state = BindingFlags.Instance | BindingFlags.NonPublic;
        var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", state)!.GetValue(bar)!;
        var player = (MediaPlaybackControl)center.FindName("MediaPlayer");
        player.Stop();
        player.ApplySnapshot(new GlassDesk.Platform.MediaSessionSnapshot(true, "本机布局验收歌曲", "测试歌手", false, true, true, true, null));
        Pump(TimeSpan.FromSeconds(2));
        ((Button)bar.FindName("ControlCenterArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(TimeSpan.FromSeconds(2));
        Check("信息栏显示音乐摘要", "control-center/music-summary", ((TextBlock)bar.FindName("MusicTitleText")).Text == "本机布局验收歌曲", "本机布局验收歌曲", ((TextBlock)bar.FindName("MusicTitleText")).Text);
        Check("控制中心内嵌播放器", "control-center/player", center.IsVisible && player.IsVisible && Grid.GetRow(player) > Grid.GetRow((UIElement)center.FindName("VolumePanel")), "player below volume", $"center={center.IsVisible},player={player.IsVisible}");
        Check("没有独立音乐窗口", "control-center/no-side-tab", !Application.Current.Windows.Cast<Window>().Any(window => window is MediaCapsuleWindow), "no media capsule window", Application.Current.Windows.Count.ToString());
        Capture(bar, Path.Combine(evidence, "status-music-summary.png"));
        Capture(center, Path.Combine(evidence, "control-center.png"));
        foreach (var name in new[] { "NetworkText", "VolumeText", "MusicTitleText" })
        {
            center.Hide();
            NativeStatusClick((FrameworkElement)bar.FindName(name));
            Check(name + "不打开控制中心", "control-center/read-only", !center.IsVisible, "hidden", center.IsVisible.ToString());
        }
        NativeStatusClick((FrameworkElement)bar.FindName("ControlCenterArrowGlyph"));
        Check("仅右上箭头打开控制中心", "control-center/entry", center.IsVisible, "visible", center.IsVisible.ToString());
        var picker = (DevicePickerWindow)typeof(ControlCenterWindow).GetField("_devicePicker", state)!.GetValue(center)!;
        foreach (var (buttonName, glyphName, fileName) in new[]
        {
            ("WifiButton", "WifiArrowGlyph", "wifi-list.png"),
            ("BluetoothButton", "BluetoothArrowGlyph", "bluetooth-list.png"),
            ("OutputDeviceArrowButton", "OutputDeviceArrowGlyph", "audio-outputs.png"),
        })
        {
            ((Button)center.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromSeconds(2));
            var open = picker.IsVisible && center.IsVisible && ((TextBlock)center.FindName(glyphName)).Text == "\uE70E";
            Check(buttonName + "展开列表且箭头向上", "control-center/list-open", open, "visible list and up arrow", $"picker={picker.IsVisible},center={center.IsVisible}");
            Capture(center, Path.Combine(evidence, "expanded-" + fileName));
            if (picker.IsVisible && picker.ActualWidth > 0) Capture(picker, Path.Combine(evidence, fileName));
            ((Button)center.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(300));
            Check(buttonName + "再次点击收起且箭头向下", "control-center/list-close", !picker.IsVisible && ((TextBlock)center.FindName(glyphName)).Text == "\uE70D", "hidden list and down arrow", picker.IsVisible.ToString());
        }
        foreach (var (labelName, glyphName) in new[] { ("WifiStateText", "WifiArrowGlyph"), ("BluetoothIconBubble", "BluetoothArrowGlyph"), ("OutputDeviceNameText", "OutputDeviceArrowGlyph") })
        {
            NativeStatusClick((FrameworkElement)center.FindName(labelName));
            Check(labelName + "整块入口可展开", "control-center/whole-entry", picker.IsVisible && ((TextBlock)center.FindName(glyphName)).Text == "\uE70E", "visible", picker.IsVisible.ToString());
            NativeStatusClick((FrameworkElement)center.FindName(glyphName));
            Check(glyphName + "再次点击收起", "control-center/whole-entry", !picker.IsVisible, "hidden", picker.IsVisible.ToString());
        }
        if (switchAudio) VerifyNativeOutputSwitch(center, picker);
        ((Button)center.FindName("WifiButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        center.Hide(); Pump(TimeSpan.FromSeconds(2));
        Check("关闭后列表不会异步复活", "control-center/late-result", !picker.IsVisible && ((TextBlock)center.FindName("WifiArrowGlyph")).Text == "\uE70D", "hidden list and down arrow", picker.IsVisible.ToString());
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cases = Cases,
            note = "Music metadata is synthetic. Native mouse input checks read-only bar labels and whole device entries. " + (switchAudio ? "Real output devices switched through row clicks and original output restored in finally." : "Output devices are only enumerated.") }, new JsonSerializerOptions { WriteIndented = true }));
        return _failed == 0 ? 0 : 1;
    }

    private static void NativeStatusClick(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        _ = IosInput.SetCursorPos((int)point.X, (int)point.Y);
        Pump(TimeSpan.FromMilliseconds(50));
        var hit = StatusWindowFromPoint(((long)(int)point.Y << 32) | (uint)(int)point.X);
        if (hit != new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(element)).Handle)
            throw new InvalidOperationException($"Native test target {element.Name} at {point} is covered; hit={hit:X} {DisplayService.GetWindowPixelBounds(hit)}, expected={new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(element)).Handle:X} {DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(element)).Handle)}; refusing to click it.");
        IosInput.Mouse(2);
        Pump(TimeSpan.FromMilliseconds(30));
        IosInput.Mouse(4);
        Pump(TimeSpan.FromMilliseconds(500));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint StatusWindowFromPoint(long point);

    private static void VerifyNativeOutputSwitch(ControlCenterWindow center, DevicePickerWindow picker)
    {
        var before = GlassDesk.Platform.AudioOutputController.GetDevices();
        var original = before.Single(device => device.IsDefault);
        try
        {
            picker.Hide();
            center.Activate();
            Pump(TimeSpan.FromMilliseconds(300));
            NativeStatusClick((FrameworkElement)center.FindName("OutputDeviceNameText"));
            var list = (ListBox)picker.FindName("SelectorList");
            foreach (var output in before.Where(device => !device.IsDefault))
            {
                object? FindRow() => list.Items.Cast<object>().FirstOrDefault(item =>
                    item.GetType().GetProperty("Value")?.GetValue(item) is GlassDesk.Platform.AudioOutputDevice device && device.Id == output.Id);
                if (!PumpUntil(() => FindRow() is not null, TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Output list did not load.");
                var item = FindRow()!;
                list.ScrollIntoView(item); list.UpdateLayout();
                Pump(TimeSpan.FromMilliseconds(300));
                Console.WriteLine($"Click output {output.Name}, center={center.IsVisible}, picker={picker.IsVisible}");
                NativeStatusClick((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item));
                var changed = PumpUntil(() => GlassDesk.Platform.AudioOutputController.GetDevices().Any(device => device.Id == output.Id && device.IsDefault), TimeSpan.FromSeconds(5));
                Check("鼠标切换输出：" + output.Name, "control-center/audio-switch", changed, output.Id,
                    GlassDesk.Platform.AudioOutputController.GetDevices().FirstOrDefault(device => device.IsDefault)?.Id ?? "none");
                Check("输出名称同步：" + output.Name, "control-center/audio-label", PumpUntil(() => ((TextBlock)center.FindName("OutputDeviceNameText")).Text == output.Name, TimeSpan.FromSeconds(5)), output.Name, ((TextBlock)center.FindName("OutputDeviceNameText")).Text);
                Check("切换后面板保持展开", "control-center/audio-visible", center.IsVisible && picker.IsVisible, "visible", $"{center.IsVisible}/{picker.IsVisible}");
            }
        }
        finally
        {
            var restored = GlassDesk.Platform.AudioOutputController.SetDefault(original.Id);
            Check("恢复原输出设备", "control-center/audio-restore", restored && GlassDesk.Platform.AudioOutputController.GetDevices().Any(device => device.Id == original.Id && device.IsDefault), original.Name, GlassDesk.Platform.AudioOutputController.LastDiagnostic);
            picker.Hide();
        }
    }

    private static int VerifyStatusBarLive(StatusBarWindow bar, string report, string evidence, string[] args)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var target = typeof(StatusBarWindow).GetField("_targetVisible", instance)!;
        var resting = (PixelRect)typeof(StatusBarWindow).GetField("_restingRect", instance)!.GetValue(bar)!;
        var command = Path.Combine(evidence, "edge-command.txt");
        var ready = Path.Combine(evidence, "edge-ready.json");
        var observer = new Border();
        System.Windows.Data.BindingOperations.SetBinding(observer, FrameworkElement.TagProperty,
            new System.Windows.Data.Binding { Source = bar, Path = new PropertyPath(progress) });
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(Border));
        var watch = Stopwatch.StartNew();
        var phase = "warmup";
        var events = new List<object>();
        var phaseValues = new List<double>();
        var allPassed = true;
        EventHandler changed = (_, _) =>
        {
            var value = (double)observer.Tag;
            phaseValues.Add(value);
            events.Add(new { atMs = watch.Elapsed.TotalMilliseconds, phase, progress = value,
                target = (bool)target.GetValue(bar)!, visible = bar.IsVisible });
        };
        descriptor.AddValueChanged(observer, changed);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--fullscreen-fixture", "--fixture-status-bar-edge", "--fixture-command", command,
            "--fixture-ready", ready }) start.ArgumentList.Add(value);
        using var fixture = Process.Start(start)!;
        try
        {
            if (!PumpUntil(() => File.Exists(ready), TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Native edge fixture did not start");
            bar.IsLauncherActive = () => false;
            bar.ShowInTaskbar = false;
            for (var cycle = 0; cycle < 5; cycle++)
            {
                phase = $"{cycle}-uncovered";
                WriteFixtureCommand(command, "edge:-40");
                Pump(TimeSpan.FromMilliseconds(650));
                var shown = bar.IsVisible && (double)bar.GetValue(progress) > .99;
                phase = $"{cycle}-covered"; phaseValues.Clear();
                WriteFixtureCommand(command, "edge:40");
                Pump(TimeSpan.FromMilliseconds(700));
                var coverage = GlassDesk.Platform.Shell.ForegroundCoverage.IsCovered(resting);
                var reversals = phaseValues.Zip(phaseValues.Skip(1)).Count(pair => pair.Second > pair.First + .001);
                var passed = shown && coverage && !bar.IsVisible && reversals == 0;
                allPassed &= passed;
                Check($"原生窗口遮挡第{cycle + 1}次收起没有闪回", "status/live-edge", passed,
                    "shown then native coverage then concealed, no upward jump", $"shown={shown},covered={coverage},visible={bar.IsVisible},reverse={reversals}");
            }
            phase = "final-uncovered";
            WriteFixtureCommand(command, "edge:-40"); Pump(TimeSpan.FromMilliseconds(700));
            Capture(bar, Path.Combine(evidence, "native-final.png"));
            if (Argument(args, "--hold-until") is { } stop)
            {
                phase = "desktop-mouse-drag";
                var until = DateTime.UtcNow.AddMinutes(5);
                while (!File.Exists(stop) && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(100));
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = allPassed, cases = Cases, events,
                note = "Separate foreground HWND moved across the real resting rectangle. Production visibility timer, ForegroundCoverage and cursor queries remain active. Binding observes every effective progress change, including synchronous endpoint resets; no visibility intents supplied." }, new JsonSerializerOptions { WriteIndented = true }));
            return allPassed ? 0 : 1;
        }
        finally
        {
            descriptor.RemoveValueChanged(observer, changed);
            if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(5000); }
        }
    }

    private static int VerifyStatusBarJitter(StatusBarWindow bar, string report, string evidence)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var policy = (StatusBarVisibilityController)typeof(StatusBarWindow).GetField("_visibility", instance)!.GetValue(bar)!;
        var apply = typeof(StatusBarWindow).GetMethod("SetTargetVisible", instance)!;
        var monitor = (DispatcherTimer)typeof(StatusBarWindow).GetField("_visibilityTimer", instance)!.GetValue(bar)!;
        var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var settings = new GlassDesk.Core.Models.Settings.DockSettings();
        Pump(TimeSpan.FromSeconds(3));
        monitor.Stop();
        var observations = new List<object>();
        var cycles = new List<object>();
        var timeline = Stopwatch.StartNew();
        DateTime Now() => DateTime.UtcNow;
        bool Intent(bool pointer, bool covered, bool launcher = false)
        {
            var visible = policy.Update(settings, Now(), pointer, covered, false, launcher, false, true, false) != AutoHideState.Hidden;
            apply.Invoke(bar, [visible]);
            return visible;
        }
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Intent(false, false); Pump(TimeSpan.FromMilliseconds(300));
            Intent(true, false);
            var hiding = Stopwatch.StartNew();
            var hiddenIntent = !Intent(true, true);
            var previous = (double)bar.GetValue(progress);
            var reverseSteps = 0;
            var unintendedReveals = 0;
            var nextPoll = 100L;
            while (hiding.ElapsedMilliseconds < 420)
            {
                Pump(TimeSpan.FromMilliseconds(12));
                if (hiding.ElapsedMilliseconds >= nextPoll)
                { nextPoll += 100; if (Intent(true, true)) unintendedReveals++; }
                var value = (double)bar.GetValue(progress);
                if (value > previous + .005) reverseSteps++;
                observations.Add(new { cycle, phase = "cover-stationary-pointer", atMs = timeline.Elapsed.TotalMilliseconds, progress = value });
                previous = value;
            }
            var concealed = !bar.IsVisible;
            Intent(false, true); Intent(true, true);
            var showing = Stopwatch.StartNew();
            previous = (double)bar.GetValue(progress);
            var revealReverseSteps = 0;
            while (showing.ElapsedMilliseconds < 350)
            {
                Pump(TimeSpan.FromMilliseconds(12));
                var value = (double)bar.GetValue(progress);
                if (value < previous - .005) revealReverseSteps++;
                observations.Add(new { cycle, phase = "deliberate-pointer-reentry", atMs = timeline.Elapsed.TotalMilliseconds, progress = value });
                previous = value;
            }
            Intent(true, false); var retainedHover = Intent(true, true);
            cycles.Add(new { cycle, hiddenIntent, unintendedReveals, reverseSteps, concealed, revealReverseSteps, retainedHover });
            Check($"遮挡第{cycle + 1}次静止鼠标不反向弹回", "status/jitter-cover", hiddenIntent && unintendedReveals == 0 && reverseSteps == 0 && concealed,
                "one downward progress to hidden", $"unintended={unintendedReveals},reverse={reverseSteps},hidden={concealed}");
            Check($"第{cycle + 1}次重新进入展开保持单向", "status/jitter-reentry", revealReverseSteps == 0 && bar.IsVisible, "one upward progress to visible", $"reverse={revealReverseSteps},visible={bar.IsVisible}");
            Check($"第{cycle + 1}次遮挡状态波动保持悬停意图", "status/jitter-hover", retainedHover, "retained", retainedHover.ToString());
        }
        Capture(bar, Path.Combine(evidence, "stable-endpoint.png"));
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cycles, observations, cases = Cases,
            note = "Exact stationary-pointer and coverage sequences supplied to the production controller; actual WPF window progress is sampled. Native desktop capture, no synthetic pixels. This is not a manual mouse acceptance recording." },
            new JsonSerializerOptions { WriteIndented = true }));
        return _failed == 0 ? 0 : 1;
    }

    private static int VerifyStatusBarReveal(StatusBarWindow bar, string report, string evidence, string[] args)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var surface = (LiquidGlassSurface)bar.FindName("GlassSurface")!;
        var setVisible = typeof(StatusBarWindow).GetMethod("SetTargetVisible", instance)!;
        var monitor = (DispatcherTimer)typeof(StatusBarWindow).GetField("_visibilityTimer", instance)!.GetValue(bar)!;
        var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var material = typeof(LiquidGlassSurface).GetField("_diagnosticMaterial", instance)!;
        Pump(TimeSpan.FromSeconds(3));
        monitor.Stop();
        var cycles = new List<object>();
        for (var cycle = 0; cycle < 6; cycle++)
        {
            var before = CaptureProcessFamily.WorkerProcesses.Order().ToArray();
            setVisible.Invoke(bar, [false]);
            var hiding = Stopwatch.StartNew();
            var ambientFrames = 0;
            var observations = new List<object>();
            while (hiding.ElapsedMilliseconds < 180)
            {
                Pump(TimeSpan.FromMilliseconds(12));
                var value = (double)bar.GetValue(progress);
                if (!bar.IsVisible || value <= .01) continue;
                var file = Path.Combine(evidence, $"cycle-{cycle}-hide-{hiding.ElapsedMilliseconds:000}.png");
                Capture(bar, file);
                var source = (string?)material.GetValue(surface);
                if (source is "Ambient" or "Basic") ambientFrames++;
                observations.Add(new { milliseconds = hiding.ElapsedMilliseconds, progress = value, source, file = Path.GetFileName(file) });
            }
            Pump(TimeSpan.FromMilliseconds(400));
            var reveal = Stopwatch.StartNew();
            setVisible.Invoke(bar, [true]);
            double? firstPixelsMs = null;
            var nextPoll = 100L;
            while (reveal.ElapsedMilliseconds < 4500)
            {
                Pump(TimeSpan.FromMilliseconds(12));
                if (reveal.ElapsedMilliseconds >= nextPoll)
                { nextPoll += 100; setVisible.Invoke(bar, [true]); }
                var value = (double)bar.GetValue(progress);
                if (value > .01) firstPixelsMs ??= reveal.Elapsed.TotalMilliseconds;
                if (value >= .999) break;
            }
            var completeMs = reveal.Elapsed.TotalMilliseconds;
            Pump(TimeSpan.FromMilliseconds(250));
            var after = CaptureProcessFamily.WorkerProcesses.Order().ToArray();
            var sameWorkers = before.SequenceEqual(after);
            cycles.Add(new { cycle, firstPixelsMs, completeMs, ambientFrames, sameWorkers, before, after, observations });
            Check($"第{cycle + 1}次收起无暗色等待底板", "status/reveal-hide", ambientFrames == 0, "0", ambientFrames.ToString());
            Check($"第{cycle + 1}次唤出在250ms内出现", "status/reveal-latency", firstPixelsMs is <= 250, "<=250ms", firstPixelsMs?.ToString("F1") ?? "none");
            Check($"第{cycle + 1}次普通显隐复用捕获进程", "status/reveal-workers", sameWorkers, string.Join(',', before), string.Join(',', after));
        }
        object? coldIdle = null;
        if (args.Contains("--status-bar-cold-idle"))
        {
            setVisible.Invoke(bar, [false]);
            Pump(TimeSpan.FromSeconds(32));
            var before = CaptureProcessFamily.WorkerProcesses.Order().ToArray();
            var elapsed = Stopwatch.StartNew();
            setVisible.Invoke(bar, [true]);
            double? firstPixelsMs = null;
            var feedbackSamples = 0;
            var nextPoll = 100L;
            while (elapsed.ElapsedMilliseconds < 700)
            {
                Pump(TimeSpan.FromMilliseconds(12));
                if (elapsed.ElapsedMilliseconds >= nextPoll) { nextPoll += 100; setVisible.Invoke(bar, [true]); }
                if ((double)bar.GetValue(progress) > .01) firstPixelsMs ??= elapsed.Elapsed.TotalMilliseconds;
                if (elapsed.ElapsedMilliseconds >= 150 && ReadStartingCursor()) feedbackSamples++;
            }
            coldIdle = new { idleSeconds = 32, firstPixelsMs, feedbackSamples, before,
                after = CaptureProcessFamily.WorkerProcesses.Order().ToArray() };
            Check("捕获会话空闲释放后唤出仍在250ms内出现", "status/cold-latency", firstPixelsMs is <= 250, "<=250ms", firstPixelsMs?.ToString("F1") ?? "none");
            Check("捕获助手冷启动没有持续蓝圈", "status/cold-cursor", feedbackSamples == 0, "0 starting-cursor samples after 150ms", feedbackSamples.ToString());
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cycles, coldIdle, cases = Cases,
            note = "Production WPF visibility path, 100ms policy admission, native desktop capture. No synthetic pixels; pointer intent supplied directly for repeatability." }, new JsonSerializerOptions { WriteIndented = true }));
        if (Argument(args, "--hold-until") is { } stop)
        {
            var until = DateTime.UtcNow.AddMinutes(15);
            while (!File.Exists(stop) && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(200));
        }
        return _failed == 0 ? 0 : 1;
    }

    private static bool ReadStartingCursor()
    {
        var info = new RevealCursor { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<RevealCursor>() };
        return GetRevealCursorInfo(ref info) && info.Cursor == LoadRevealCursor(0, (nint)32650);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RevealCursor
    {
        public uint Size, Flags;
        public nint Cursor;
        public int X, Y;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorInfo")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetRevealCursorInfo(ref RevealCursor cursor);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadRevealCursor(nint instance, nint name);
}
