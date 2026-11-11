using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static int VerifyDockBuiltinEntries(string[] args)
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var marker = Path.Combine(directory, ".full-host-acceptance-root");
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != directory)
            throw new InvalidOperationException("An explicitly marked test package is required.");
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var evidence = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(evidence);
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "GlassDesk.dll"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml") });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var optionsType = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!;
        var options = optionsType.GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()])!;
        var hostType = assembly.GetType("GlassDesk.App.AppHost", true)!;
        var host = (IDisposable)Activator.CreateInstance(hostType, flags, null, [application, options], null)!;
        SettingsWindow? settings = null;
        bool? originalIcons = null;
        try
        {
            var started = hostType.GetMethod("Start")!.Invoke(host, null)?.ToString();
            if (started != "Started") throw new InvalidOperationException("AppHost did not start: " + started);
            settings = (SettingsWindow)hostType.GetField("_settings", flags)!.GetValue(host)!;
            var dock = (DockWindow)hostType.GetField("_dock", flags)!.GetValue(host)!;
            var launcher = (LauncherWindow)hostType.GetField("_launcher", flags)!.GetValue(host)!;
            var data = (GlassDeskDataStore)hostType.GetField("_data", flags)!.GetValue(host)!;
            originalIcons = settings.DesktopIconsRead?.Invoke();
            if (args.Contains("--desktop-icon-continuity"))
            {
                VerifyDesktopIconToggleContinuity(host, hostType, dock, launcher, settings, evidence, Argument(args, "--fixture-exe"),
                    int.TryParse(Argument(args, "--window-cycles"), out var windowCycles) ? Math.Clamp(windowCycles, 1, 100) : 3);
                return _failed == 0 ? 0 : 1;
            }
            var beforeOrder = data.Layouts.Dock.PinnedItemIds.ToArray();
            hostType.GetMethod("ShowSettings", flags)!.Invoke(host, null);
            ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 1;
            ((Slider)settings.FindName("DockScaleSlider")!).Value = 1;
            Pump(TimeSpan.FromMilliseconds(300));
            var buttons = new[] { "LauncherButton", "ThisPcButton", "DesktopIconsButton" }
                .Select(name => (Button)dock.FindName(name)!).ToArray();
            var boxes = new[] { "ShowLauncherEntryCheckBox", "ShowThisPcEntryCheckBox", "ShowDesktopIconsEntryCheckBox" }
                .Select(name => (CheckBox)settings.FindName(name)!).ToArray();
            for (var mask = 0; mask < 8; mask++)
            {
                for (var i = 0; i < 3; i++) boxes[i].IsChecked = (mask & (1 << i)) != 0;
                Pump(TimeSpan.FromMilliseconds(100));
                var row = (StackPanel)buttons[0].Parent;
                double x = 0;
                var valid = true;
                for (var i = 0; i < 3; i++)
                {
                    var visible = (mask & (1 << i)) != 0;
                    valid &= buttons[i].Visibility == (visible ? Visibility.Visible : Visibility.Collapsed);
                    if (!visible) continue;
                    valid &= Math.Abs(buttons[i].TranslatePoint(new Point(), row).X - x) < 1;
                    x += buttons[i].ActualWidth + buttons[i].Margin.Right;
                }
                Check("内置图标组合 " + mask + " 立即生效且无空隙", "dock/entries", valid, "visible in fixed order", string.Join(",", buttons.Select(b => b.Visibility)));
            }
            Check("内置开关保留应用固定顺序", "dock/pins", beforeOrder.SequenceEqual(data.Layouts.Dock.PinnedItemIds), "unchanged", data.Layouts.Dock.PinnedItemIds.Count.ToString());
            Check("桌面图标动作可读", "dock/desktop", originalIcons.HasValue, "readable", originalIcons.ToString() ?? "null");
            if (originalIcons.HasValue)
            {
                data.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled = false;
                buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(TimeSpan.FromMilliseconds(150));
                Check("Dock 切换桌面图标独立于快捷键启用状态", "dock/desktop", settings.DesktopIconsRead?.Invoke() == !originalIcons.Value, "toggled", settings.DesktopIconsRead?.Invoke()?.ToString() ?? "null");
                buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(TimeSpan.FromMilliseconds(150));
                Check("第二次点击恢复桌面图标", "dock/desktop", settings.DesktopIconsRead?.Invoke() == originalIcons, "original state", settings.DesktopIconsRead?.Invoke()?.ToString() ?? "null");
            }
            boxes[0].IsChecked = false;
            settings.Hide();
            hostType.GetMethod("ShowLauncher", flags, null, Type.EmptyTypes, null)!.Invoke(host, null);
            Pump(TimeSpan.FromMilliseconds(700));
            Check("隐藏启动台入口仍可从其他途径打开启动台", "dock/launcher", launcher.IsVisible && dock.GetLauncherButtonPixelBounds() is null, "visible launcher, no hidden anchor", launcher.IsVisible.ToString());
            launcher.CloseWithTransition();
            Pump(TimeSpan.FromMilliseconds(600));
            boxes[0].IsChecked = true;
            var frame = (Slider)settings.FindName("DockFrameLengthSlider")!;
            frame.Value = frame.Minimum + 8;
            frame.Value = frame.Minimum;
            Pump(TimeSpan.FromMilliseconds(200));
            Check("短外框完整容纳三个固定入口", "dock/frame", dock.ActualWidth <= 191 && buttons[2].TranslatePoint(new Point(buttons[2].ActualWidth, 0), dock).X <= dock.ActualWidth, "entry inside short frame", dock.ActualWidth.ToString());
            data.Settings.Current.Dock.FrameLengthDip = 0;
            GlassDesk.UI.Settings.DockSettingsHost.Current!.Update(data.Settings.Current.Dock);
            settings.Hide();
            hostType.GetMethod("ShowLauncher", flags, null, Type.EmptyTypes, null)!.Invoke(host, null);
            Pump(TimeSpan.FromMilliseconds(700));
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(dock, Path.Combine(evidence, "dock-builtins.png"));
            launcher.CloseWithTransition();
            Pump(TimeSpan.FromMilliseconds(600));
            hostType.GetMethod("ShowSettings", flags)!.Invoke(host, null);
            ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 1;
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(settings, Path.Combine(evidence, "dock-settings.png"));
        }
        catch (Exception error) { Check("完整宿主内置入口验收", "dock/full-host", false, "no exception", error.ToString()); }
        finally
        {
            if (originalIcons.HasValue) settings?.DesktopIconsSet?.Invoke(originalIcons.Value);
            host.Dispose(); application.Shutdown();
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion,
                driver = "Complete production AppHost with copied isolated profile; real desktop icon state restored",
                passed = Cases.Count - _failed, failed = _failed, cases = Cases
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;
    }
}
