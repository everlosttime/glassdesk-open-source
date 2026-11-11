using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyDockIdentityAndIcons(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report")!);
        var evidence = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(evidence);
        var data = new GlassDeskDataStore(new AppPaths(Path.Combine(evidence, "Data"))); data.LoadAll();
        var library = JsonSerializer.Deserialize(File.ReadAllText(@"D:\GlassDesk\Data\apps.json"), GlassDeskJsonContext.Default.AppLibraryData)!;
        var entries = library.Items.Where(entry => entry.DisplayName is "ChatGPT" or "Apple Music").ToArray();
        foreach (var entry in entries) { data.Library.Add(entry); data.Layouts.Dock.PinnedItemIds.Add(entry.Id); }
        data.Library.Save(); data.Layouts.SaveDock();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
        var settings = DockSettingsHost.Initialize(app.Dispatcher); settings.Update(data.Settings.Current.Dock);
        using var services = new AppServiceHost(data);
        var dock = new DockWindow();
        var cursor = DisplayService.GetCursorPixelPoint();
        var foreground = WindowControl.GetForegroundWindow();
        var originalStates = new Dictionary<nint, bool>();
        try
        {
            dock.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            services.Running.Start();
            dock.AttachServices(services, settings, () => { });
            services.LibraryChanged += (_, _) => dock.RefreshTiles();
            dock.Show(); dock.SetLauncherContainerState(true); Pump(TimeSpan.FromSeconds(1));
            foreach (var entry in args.Contains("--icons-only") ? [] : entries)
            {
                var target = WindowMatcher.FindWindows(entry).Single();
                originalStates[target.Handle] = target.IsMinimized;
                WindowLocator.TryBringToForeground(target.Handle); Pump(TimeSpan.FromMilliseconds(300));
                var body = Visuals(dock).OfType<FrameworkElement>().First(element => AutomationProperties.GetAutomationId(element) == "dock-tile:" + entry.Id);
                ClickDockNative(body, false); Pump(TimeSpan.FromMilliseconds(400));
                services.Running.Reconcile(); Pump(TimeSpan.FromMilliseconds(250));
                var model = ((IEnumerable<DockTileModel>)typeof(DockWindow).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dock)!).Single(item => item.EntryId == entry.Id);
                Check(entry.DisplayName + "点击最小化后仍显示运行圆点", "dock/live-app", WindowControl.IsMinimized(target.Handle)
                    && model.RunningVisibility == Visibility.Visible, "minimized and running dot", $"minimized={WindowControl.IsMinimized(target.Handle)},dot={model.RunningVisibility}");
                ClickDockNative(body, false); Pump(TimeSpan.FromMilliseconds(500));
                Check(entry.DisplayName + "再次点击恢复原窗口", "dock/live-app", !WindowControl.IsMinimized(target.Handle)
                    && WindowControl.GetForegroundWindow() == target.Handle, "restored original foreground window", WindowControl.GetForegroundWindow().ToString("X"));
            }

            var source = Path.Combine(evidence, "custom-icon.png");
            var encoder = new PngBitmapEncoder();
            var pixels = Enumerable.Range(0, 64 * 64).SelectMany(_ => new byte[] { 240, 145, 55, 255 }).ToArray();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 256)));
            using (var output = File.Create(source)) encoder.Save(output);
            foreach (var (id, name) in new[] { ("entry:launcher", "LauncherButton"), ("entry:this-pc", "ThisPcButton"), ("entry:desktop-icons", "DesktopIconsButton"), (entries[0].Id, "") })
            {
                var element = name.Length > 0 ? (FrameworkElement)dock.FindName(name)
                    : Visuals(dock).OfType<FrameworkElement>().First(item => AutomationProperties.GetAutomationId(item) == "dock-tile:" + id);
                ClickDockNative(element, true);
                var menu = PresentationSource.CurrentSources.OfType<HwndSource>().Where(item => item.RootVisual is not null)
                    .SelectMany(item => Visuals(item.RootVisual)).OfType<ContextMenu>().First(item => item.IsOpen);
                var custom = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "自定义图标…");
                var picker = Task.Run(() => FillDockIconPicker(source));
                ClickDockNative(custom, false);
                picker.GetAwaiter().GetResult();
                Pump(TimeSpan.FromMilliseconds(200));
                var managed = services.DockIcons.GetIconPath(id);
                Check(id + "右键选择图片后保存自定义图标", "dock/icon-picker", managed is not null && File.Exists(managed)
                    && managed != source, "managed icon copy", managed ?? "null");
            }
            Capture(dock, Path.Combine(evidence, "dock-custom-icons.png"));
            foreach (var id in new[] { "entry:launcher", "entry:this-pc", "entry:desktop-icons", entries[0].Id })
                Check(id + "恢复默认图标", "dock/icon-reset", services.DockIcons.SetIcon(id, null)
                    && services.DockIcons.GetIconPath(id) is null, "default icon", services.DockIcons.GetIconPath(id) ?? "default");
            Capture(dock, Path.Combine(evidence, "dock-default-icons.png"));
        }
        catch (Exception error) { Check("Dock窗口与图标验收", "dock/integration", false, "no exception", error.ToString()); }
        finally
        {
            foreach (var state in originalStates) if (state.Value) WindowControl.Minimize(state.Key); else WindowControl.Restore(state.Key);
            if (foreground != 0) WindowLocator.TryBringToForeground(foreground);
            if (cursor is { } point) IosInput.SetCursorPos(point.X, point.Y);
            dock.Close(); app.Shutdown();
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cases = Cases,
                note = "Native clicks on the real ChatGPT and Apple Music windows and isolated Dock; original window states restored. File picker and icon persistence use isolated data." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }

    private static void ClickDockNative(FrameworkElement element, bool right)
    {
        for (var i = 0; i < 2; i++)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            IosInput.SetCursorPos((int)point.X, (int)point.Y); Pump(TimeSpan.FromMilliseconds(180));
        }
        var target = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var expected = (PresentationSource.FromVisual(element) as HwndSource)!.Handle;
        if (StatusWindowFromPoint(((long)(int)target.Y << 32) | (uint)(int)target.X) != expected)
            throw new InvalidOperationException("Dock test target is covered; refusing native input.");
        IosInput.Mouse(right ? 8u : 2u); Pump(TimeSpan.FromMilliseconds(40)); IosInput.Mouse(right ? 16u : 4u);
        Pump(TimeSpan.FromMilliseconds(300));
    }

    private static void FillDockIconPicker(string source)
    {
        nint dialog = 0;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(12);
            while (dialog == 0 && DateTime.UtcNow < until)
            {
                DockPickerNative.EnumWindows((window, _) =>
                {
                    DockPickerNative.GetWindowThreadProcessId(window, out var process);
                    if (process == Environment.ProcessId && DockPickerNative.Text(window) == "选择自定义图标")
                    { dialog = window; return false; }
                    return true;
                }, 0);
                if (dialog == 0) Thread.Sleep(100);
            }
            if (dialog == 0) throw new InvalidOperationException("Icon picker did not open.");
            Thread.Sleep(700);
            var filenameParents = new List<nint>();
            nint edit = 0, open = 0;
            DockPickerNative.EnumChildWindows(dialog, (window, _) =>
            {
                var id = DockPickerNative.GetDlgCtrlID(window);
                if (id == 1148) filenameParents.Add(window);
                if (id == 1 && DockPickerNative.Class(window) == "Button") open = window;
                return true;
            }, 0);
            foreach (var parent in filenameParents)
            {
                if (DockPickerNative.Class(parent) == "Edit") edit = parent;
                DockPickerNative.EnumChildWindows(parent, (window, _) =>
                { if (DockPickerNative.Class(window) == "Edit") edit = window; return true; }, 0);
            }
            if (edit == 0 || open == 0) throw new InvalidOperationException("File picker controls were not found.");
            DockPickerNative.SetText(edit, 0x000C, 0, source);
            Thread.Sleep(150);
            if (!DockPickerNative.GetWindowRect(open, out var bounds)) throw new InvalidOperationException("Open button has no bounds.");
            var x = (bounds.Left + bounds.Right) / 2;
            var y = (bounds.Top + bounds.Bottom) / 2;
            var hit = StatusWindowFromPoint(((long)y << 32) | (uint)x);
            if (DockPickerNative.GetAncestor(hit, 2) != dialog) throw new InvalidOperationException("Open button is covered; refusing native input.");
            IosInput.SetCursorPos(x, y);
            IosInput.Mouse(2); Thread.Sleep(40); IosInput.Mouse(4);
        }
        catch
        {
            if (dialog != 0) DockPickerNative.PostMessage(dialog, 0x0010, 0, 0);
            throw;
        }
    }

    private static class DockPickerNative
    {
        public delegate bool Enumerate(nint window, nint parameter);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct NativeRect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out NativeRect bounds);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(Enumerate callback, nint parameter);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumChildWindows(nint parent, Enumerate callback, nint parameter);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetDlgCtrlID(nint window);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int capacity);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder text, int capacity);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern nint SetText(nint window, uint message, nint parameter, string text);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        public static string Text(nint window) { var text = new System.Text.StringBuilder(256); GetWindowText(window, text, text.Capacity); return text.ToString(); }
        public static string Class(nint window) { var text = new System.Text.StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString(); }
    }
}
