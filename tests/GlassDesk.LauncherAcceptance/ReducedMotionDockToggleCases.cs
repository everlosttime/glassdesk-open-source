using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyReducedMotionDockToggle(string[] args)
    {
        var root = Path.GetFullPath(Argument(args, "--data-dir") ?? throw new ArgumentException("--data-dir required"));
        var real = Path.GetFullPath(AppPaths.ForCurrentUser().RootDirectory);
        if (root.Equals(real, StringComparison.OrdinalIgnoreCase) || root.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A disposable profile is required.");
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
        using var services = new AppServiceHost(data);
        using var theme = ThemeService.Initialize(app.Dispatcher);
        theme.Update(data.Settings.Current.Appearance);
        var dockSettings = DockSettingsHost.Initialize(app.Dispatcher);
        dockSettings.Update(data.Settings.Current.Dock);
        var dock = new DockWindow();
        var launcher = new LauncherWindow();
        var settings = new SettingsWindow();
        var display = new PrimaryDisplaySession(DisplayService.Primary() ?? throw new InvalidOperationException("Primary display unavailable."));
        dock.BindPrimaryDisplay(display); launcher.BindPrimaryDisplay(display); settings.BindPrimaryDisplay(display);
        var toggle = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "GlassDesk.dll"))
            .GetType("GlassDesk.App.AppHost", true)!.GetMethod("ToggleLauncherFromDock", BindingFlags.Static | BindingFlags.NonPublic)!;
        var openRequests = 0;
        var events = new List<string>();
        var launcherButton = (Button)dock.FindName("LauncherButton")!;
        launcherButton.LostMouseCapture += (_, _) => events.Add("button-lost-capture");
        void Open(PixelRect? source)
        {
            openRequests++; events.Add("open");
            dock.SetLauncherContainerState(true);
            launcher.ShowWithTransition(source ?? dock.GetLauncherButtonPixelBounds());
            launcher.Activate();
        }
        launcher.ContainerAnchorProvider = dock.GetLauncherButtonPixelBounds;
        launcher.IsVisibleChanged += (_, e) => { events.Add("visible=" + e.NewValue); dock.SetLauncherContainerState(e.NewValue is true); };
        launcher.Deactivated += (_, _) => events.Add("deactivated");
        try
        {
            launcher.AttachServices(services, true);
            launcher.ApplySettings(data.Settings.Current.Launcher);
            dock.AttachServices(services, dockSettings, source => toggle.Invoke(null, [launcher, source, (Action<PixelRect?>)Open]));
            settings.AttachDataStore(data, null);
            var reduce = (CheckBox)settings.FindName("ReduceMotionCheck")!;
            foreach (var reduced in new[] { true, false })
            {
                reduce.IsChecked = reduced;
                Pump(TimeSpan.FromMilliseconds(100));
                Check("减少动画选项生效 " + reduced, "dock-toggle/settings", theme.AnimationsEnabled != reduced,
                    "AnimationsEnabled=" + !reduced, theme.AnimationsEnabled.ToString());
                dock.Show(); Pump(TimeSpan.FromMilliseconds(150));
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    ClickDock(); Pump(TimeSpan.FromMilliseconds(800));
                    Check($"鼠标打开 reduced={reduced} cycle={cycle}", "dock-toggle/open", launcher.IsVisible,
                        "visible", string.Join(", ", events));
                    var before = openRequests;
                    ClickDock(); Pump(TimeSpan.FromMilliseconds(800));
                    Check($"鼠标关闭不重新打开 reduced={reduced} cycle={cycle}", "dock-toggle/close",
                        !launcher.IsVisible && openRequests == before, "hidden; no extra open", string.Join(", ", events));
                    launcher.Hide();
                }
                ClickDock(); Pump(TimeSpan.FromMilliseconds(800));
                var requestsBeforeEarlyClose = openRequests;
                MoveToDock(); IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(100));
                // Reproduce the deactivation path finishing before the release Click.
                // With motion reduced, CloseWithTransition hides synchronously.
                launcher.CloseWithTransition();
                IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(800));
                Check("按下到松开之间已关闭时不重新打开 reduced=" + reduced, "dock-toggle/early-close",
                    !launcher.IsVisible && openRequests == requestsBeforeEarlyClose,
                    "hidden; no extra open", string.Join(", ", events));
                launcher.Hide();
                ClickDock(); Pump(TimeSpan.FromMilliseconds(800));
                MoveToDock(); IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(100));
                var work = DisplayService.Primary()!.WorkArea;
                IosInput.MovePacket(new Point(work.X + 12, work.Y + 12));
                Pump(TimeSpan.FromMilliseconds(60)); IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(100));
                launcher.Hide();
                var requestsBeforeCancelInvoke = openRequests;
                events.Add("cancel-before-invoke: visible=" + launcher.IsVisible
                    + "; wasOpen=" + typeof(DockWindow).GetField("_launcherWasOpenOnPress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dock)
                    + "; releasePending=" + typeof(DockWindow).GetField("_launcherReleaseClickPending", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dock)
                    + "; captured=" + launcherButton.IsMouseCaptured + "; pressed=" + launcherButton.IsPressed);
                ((Button)dock.FindName("LauncherButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var shownByCancelInvoke = launcher.IsVisible;
                Pump(TimeSpan.FromMilliseconds(800));
                // Programmatic invocation has no native input activation grant.
                // A later foreground loss can legitimately close the launcher;
                // the cancelled gesture must still permit exactly one real reveal.
                Check("取消鼠标手势后按钮调用仍能打开 reduced=" + reduced, "dock-toggle/cancel",
                    shownByCancelInvoke && openRequests == requestsBeforeCancelInvoke + 1,
                    "one reveal request; visible immediately", $"shown={shownByCancelInvoke}; requests={openRequests - requestsBeforeCancelInvoke}; " + string.Join(", ", events));
                launcher.Hide();
            }
        }
        catch (Exception error) { Check("Dock点击验收", "dock-toggle", false, "no exception", error.ToString()); }
        finally
        {
            launcher.Close(); dock.Close(); settings.Close();
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = Cases.Count - _failed, failed = _failed,
                driver = "SendInput mouse clicks; production AppHost toggle, Dock, launcher and settings; disposable profile", events, cases = Cases },
                new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        return _failed == 0 ? 0 : 1;

        void ClickDock()
        {
            MoveToDock();
            IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(100)); IosInput.Mouse(4);
        }
        void MoveToDock()
        {
            var until = System.Diagnostics.Stopwatch.StartNew();
            var measured = dock.GetLauncherButtonPixelBounds();
            while (measured is null && until.Elapsed < TimeSpan.FromSeconds(2))
            {
                dock.UpdateLayout(); dock.RefreshDisplayPlacement(); Pump(TimeSpan.FromMilliseconds(40));
                measured = dock.GetLauncherButtonPixelBounds();
            }
            var bounds = measured ?? throw new InvalidOperationException($"Dock button has no bounds: visible={dock.IsVisible}; dockSize={dock.ActualWidth}x{dock.ActualHeight}; buttonSize={launcherButton.ActualWidth}x{launcherButton.ActualHeight}; buttonVisibility={launcherButton.Visibility}.");
            IosInput.MovePacket(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            Pump(TimeSpan.FromMilliseconds(60));
        }
    }
}
