using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyNoticeAndPages(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report")!);
        var evidence = Path.GetDirectoryName(report)!; Directory.CreateDirectory(evidence);
        var data = new GlassDeskDataStore(new AppPaths(Path.Combine(evidence, "Data"))); data.LoadAll();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
        var originalCursor = DisplayService.GetCursorPixelPoint(); var originalForeground = WindowControl.GetForegroundWindow();
        LauncherWindow? launcher = null; Window? notice = null; SettingsWindow? settings = null; StatusBarWindow? bar = null;
        nint hwnd = 0; HwndSource? source = null; HwndSourceHook? hook = null; bool registered = false;
        using var services = new AppServiceHost(data);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        try
        {
            var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "GlassDesk.dll"));
            var hostType = assembly.GetType("GlassDesk.App.AppHost", true)!;
            var options = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!.GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()]);
            var host = Activator.CreateInstance(hostType, flags, null, [app, options], null)!;
            hostType.GetField("_data", flags)!.SetValue(host, data);
            bool Snooze() => (bool)hostType.GetMethod("SnoozeUpdateNotice", flags)!.Invoke(host, null)!;
            const string config = "{\"cloudDownloads\":[{\"name\":\"测试网盘\",\"url\":\"https://example.com/download\"}]}";
            var noticeType = assembly.GetType("GlassDesk.App.Updates.CloudDownloadWindow", true)!;
            Window Notice() => (Window)Activator.CreateInstance(noticeType, config, new Version(9, 0, 0), "更新说明", null, null, (Func<bool>)Snooze)!;
            notice = Notice(); notice.Show(); Pump(TimeSpan.FromMilliseconds(180));
            var before = DateTimeOffset.UtcNow;
            ClickDockNative(FindNoticeButton(notice), false);
            var saved = new SettingsStore(data.Settings.FilePath); saved.Load();
            var until = saved.Current.UpdateNoticeSnoozedUntilUtc;
            Check("公告原生按钮保存七天免打扰并关闭", "notice", !notice.IsVisible && until >= before.AddDays(7) && until < DateTimeOffset.UtcNow.AddDays(7), "7 days persisted", until?.ToString() ?? "null");
            Check("重启读取后自动公告被抑制，手动检查仍可显示", "notice", !saved.Current.ShouldShowUpdateNotice(false, before) && saved.Current.ShouldShowUpdateNotice(true, before), "automatic suppressed/manual available", "persisted policy");
            Check("期限到达后自动公告恢复", "notice", saved.Current.ShouldShowUpdateNotice(false, until!.Value), "expires at boundary", until.ToString()!);
            notice = Notice(); notice.Show(); Pump(TimeSpan.FromMilliseconds(150));
            using (File.Open(data.Settings.FilePath + AtomicFile.TempSuffix, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                ClickDockNative(FindNoticeButton(notice), false);
            Check("保存失败不关闭公告并恢复旧期限", "notice", notice.IsVisible && data.Settings.Current.UpdateNoticeSnoozedUntilUtc == until,
                "visible and unchanged", data.Settings.Current.UpdateNoticeSnoozedUntilUtc.ToString()!);
            SaveNoticeImage(notice, Path.Combine(evidence, "notice-snooze.png")); notice.Close(); notice = null;

            settings = new SettingsWindow(); settings.AttachDataStore(data, null); _ = new WindowInteropHelper(settings).EnsureHandle();
            bar = new StatusBarWindow(); _ = new WindowInteropHelper(bar).EnsureHandle();
            var slider = (Slider)settings.FindName("StatusBarTextBrightnessSlider");
            foreach (var (value, channel) in new[] { (0d, (byte)0), (.5, (byte)128), (1d, (byte)255) })
            {
                slider.Value = value; Pump(TimeSpan.FromMilliseconds(40));
                var brush = (SolidColorBrush)((TextBlock)bar.FindName("TimeText")).Foreground;
                saved.Load();
                Check("信息栏明暗实时应用并保存 " + value, "brightness", brush.Color == Color.FromRgb(channel, channel, channel)
                    && saved.Current.Appearance.StatusBarTextBrightness == value, "matching grayscale and persisted", brush.Color.ToString());
            }
            using (File.Open(data.Settings.FilePath + AtomicFile.TempSuffix, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) slider.Value = .2;
            Check("信息栏明暗保存失败恢复", "brightness", slider.Value == 1 && data.Settings.Current.Appearance.StatusBarTextBrightness == 1,
                "white retained", slider.Value.ToString());
            settings.Show(); ((ListBox)settings.FindName("SettingsPageList")).SelectedItem = "信息栏";
            Pump(TimeSpan.FromMilliseconds(120)); SaveNoticeImage(settings, Path.Combine(evidence, "information-brightness-settings.png"));
            settings.Close(); settings = null; bar.Close(); bar = null;

            data.Settings.Current.Launcher.Rows = 2; data.Settings.Current.Launcher.Columns = 2;
            for (var i = 0; i < 4; i++) data.Library.Add(new AppEntry { Id = "a" + i, DisplayName = "测试应用 " + i, Target = "missing" + i, IsTargetMissing = true });
            data.Library.Save();
            data.Layouts.CommitLauncher(new LauncherLayout { SchemaVersion = 2, Pages = [
                new() { Id = "p1", Slots = [new() { ItemId = "a0" }, new() { ItemId = "a1" }] },
                new() { Id = "p2", Slots = [new() { ItemId = "a2" }, new() { ItemId = "a3" }] }] }, data.Layouts.LauncherVersion);
            launcher = new LauncherWindow(); ((LiquidGlassSurface)launcher.FindName("GlassSurface")).SetPerformanceSleeping(true);
            launcher.AttachServices(services, true); launcher.Show(); launcher.Activate(); Pump(TimeSpan.FromMilliseconds(300));
            hwnd = new WindowInteropHelper(launcher).Handle; source = HwndSource.FromHwnd(hwnd);
            var hotkeys = 0;
            hook = (nint _, int msg, nint wp, nint _, ref bool handled) =>
            {
                if (msg == 0x312 && wp.ToInt32() == 0x7717) { hotkeys++; launcher.NavigatePage(1); handled = true; }
                return 0;
            };
            source.AddHook(hook); registered = IosInput.RegisterHotKey(hwnd, 0x7717, 0x4000 | 2 | 4, 0x7e);
            if (!registered) throw new InvalidOperationException("Native test shortcut unavailable.");
            var grid = (UniformGrid)launcher.FindName("TileGrid");
            string[] Visible() => grid.Children.OfType<Button>().Select(b => (string)b.Tag).ToArray();
            Check("第一页独立显示未拉取第二页", "pages", Visible().SequenceEqual(["a0", "a1"]), "a0,a1", string.Join(',', Visible()));
            launcher.NavigatePage(1); Pump(TimeSpan.FromMilliseconds(320));
            Check("第二页独立显示", "pages", Visible().SequenceEqual(["a2", "a3"]), "a2,a3", string.Join(',', Visible()));
            Pickup();
            IosInput.Key(0x11, false); IosInput.Key(0x10, false); IosInput.Key(0x7e, false);
            IosInput.Key(0x7e, true); IosInput.Key(0x10, true); IosInput.Key(0x11, true);
            Pump(TimeSpan.FromMilliseconds(330));
            Check("真实快捷键在拖动时进入新页", "pages", hotkeys == 1 && ((TextBlock)launcher.FindName("PageText")).Text == "3 / 3", "3 / 3", ((TextBlock)launcher.FindName("PageText")).Text);
            IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(330));
            Check("松开鼠标保存到新页且原页独立补位", "pages", data.Layouts.Launcher.Pages.Count == 3 && data.Layouts.Launcher.Pages[1].Slots.Count == 1
                && data.Layouts.Launcher.Pages[2].Slots.Single().ItemId == "a2", "new page contains a2", string.Join(',', LauncherLayoutRules.AppIds(data.Layouts.Launcher)));
            SaveNoticeImage(launcher, Path.Combine(evidence, "launcher-new-page.png"));
            var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            Pickup();
            var panel = (FrameworkElement)launcher.FindName("LauncherPanelRoot");
            var edge = panel.PointToScreen(new Point(panel.ActualWidth - 12, panel.ActualHeight / 2));
            IosInput.SetCursorPos((int)edge.X, (int)edge.Y); Pump(TimeSpan.FromMilliseconds(1050));
            Check("鼠标停在启动台右边缘可开启空白页", "pages", ((TextBlock)launcher.FindName("PageText")).Text == "4 / 4", "4 / 4", ((TextBlock)launcher.FindName("PageText")).Text);
            Pump(TimeSpan.FromMilliseconds(800));
            Check("持续停留边缘不会无限新建页", "pages", ((TextBlock)launcher.FindName("PageText")).Text == "4 / 4", "4 / 4", ((TextBlock)launcher.FindName("PageText")).Text);
            var leftEdge = panel.PointToScreen(new Point(12, panel.ActualHeight / 2));
            IosInput.SetCursorPos((int)leftEdge.X, (int)leftEdge.Y); Pump(TimeSpan.FromMilliseconds(1000));
            Check("鼠标停在启动台左边缘可返回前页", "pages", ((TextBlock)launcher.FindName("PageText")).Text == "3 / 4", "3 / 4", ((TextBlock)launcher.FindName("PageText")).Text);
            IosInput.Key(0x1b, false); IosInput.Key(0x1b, true); Pump(TimeSpan.FromMilliseconds(80)); IosInput.Mouse(4);
            Check("Esc 取消新页保留原文件", "pages", bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)) && data.Layouts.Launcher.Pages.Count == 3,
                "unchanged layout", data.Layouts.Launcher.Pages.Count.ToString());
            // Repeat edge navigation and drop in the new page.
            Pickup(); IosInput.SetCursorPos((int)edge.X, (int)edge.Y); Pump(TimeSpan.FromMilliseconds(1000));
            IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(320));
            var workspace = new LauncherWorkspace(services); workspace.Reconcile(4);
            Check("边缘新页放下后保留并通过重新读取", "pages", data.Layouts.Launcher.Pages.Count == 4 && data.Layouts.Launcher.Pages[3].Slots.Single().ItemId == "a2",
                "page four persisted", data.Layouts.Launcher.Pages.Count.ToString());
            var reload = new LayoutStore(data.Paths.DockLayoutFile, data.Paths.LauncherLayoutFile); reload.LoadLauncher();
            Check("重启布局保持页面边界和全部图标", "pages", LauncherLayoutRules.Equivalent(data.Layouts.Launcher, reload.Launcher)
                && LauncherLayoutRules.AppIds(reload.Launcher).Distinct().Count() == 4, "same pages and unique IDs", reload.Launcher.Pages.Count.ToString());

            void Pickup()
            {
                launcher.Activate();
                var tile = grid.Children.OfType<Button>().First();
                var p = tile.PointToScreen(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2));
                IosInput.SetCursorPos((int)p.X, (int)p.Y); Pump(TimeSpan.FromMilliseconds(80));
                if (StatusWindowFromPoint(((long)(int)p.Y << 32) | (uint)(int)p.X) != hwnd) throw new InvalidOperationException("Launcher is covered; refusing native input.");
                IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(460));
                if (!(bool)typeof(LauncherWindow).GetField("_logicalIconDrag", flags)!.GetValue(launcher)!)
                    throw new InvalidOperationException("Native long press did not pick up the icon.");
            }
        }
        catch (Exception ex) { Check("Unexpected failure", "integration", false, "none", ex.ToString()); }
        finally
        {
            IosInput.Mouse(4); foreach (var key in new ushort[] { 0x11, 0x10, 0x7e, 0x1b }) IosInput.Key(key, true);
            if (registered) IosInput.UnregisterHotKey(hwnd, 0x7717); if (source is not null && hook is not null) source.RemoveHook(hook);
            notice?.Close(); settings?.Close(); bar?.Close(); launcher?.Close();
            if (originalCursor is { } cursor) IosInput.SetCursorPos(cursor.X, cursor.Y);
            if (originalForeground != 0) WindowLocator.TryBringToForeground(originalForeground);
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cases = Cases }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        return _failed == 0 ? 0 : 1;
    }

    private static Button FindNoticeButton(Window window)
        => DescendantsNotice(window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "7 天内不再弹出更新公告");

    private static IEnumerable<DependencyObject> DescendantsNotice(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in DescendantsNotice(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void SaveNoticeImage(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}
