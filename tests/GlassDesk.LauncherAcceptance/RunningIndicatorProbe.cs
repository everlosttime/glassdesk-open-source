using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;
using GlassDesk.AppServices;
using GlassDesk.Core.Serialization;
using GlassDesk.Platform.Shell;

internal static partial class Program
{
    private static int ProbeRunningIndicator(string[] args)
    {
        var libraryFile = Path.GetFullPath(Argument(args, "--library") ?? throw new ArgumentException("--library required"));
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        if (File.Exists(report)) throw new IOException("Fresh report required");
        var library = JsonSerializer.Deserialize(File.ReadAllText(libraryFile), GlassDeskJsonContext.Default.AppLibraryData)!;
        var entry = library.Items.Single(e => e.DisplayName == (Argument(args, "--app-name") ?? "QQ"));
        var snapshot = WindowLocator.EnumerateTopLevelWindows();
        var identity = WindowMatcher.ResolveAumid(entry);
        var matching = WindowMatcher.MatchWindows(entry, snapshot, identity);
        var sameImage = snapshot.Where(w => string.Equals(w.ImagePath, entry.Target, StringComparison.OrdinalIgnoreCase)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        object? dockState = null;
        if (args.Contains("--verify-dock"))
        {
            var isolated = Path.Combine(Path.GetDirectoryName(report)!, "isolated-profile");
            if (Directory.Exists(isolated)) throw new IOException("Fresh isolated profile required");
            var data = new GlassDeskDataStore(new AppPaths(isolated)); data.LoadAll();
            data.Library.Add(entry); data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
            using var theme = ThemeService.Initialize(app.Dispatcher); theme.Update(data.Settings.Current.Appearance);
            var settings = DockSettingsHost.Initialize(app.Dispatcher); settings.Update(data.Settings.Current.Dock);
            using var services = new AppServiceHost(data);
            services.Running.Reconcile();
            var dock = new DockWindow();
            try
            {
                dock.AttachServices(services, settings, () => { });
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var model = ((IEnumerable<DockTileModel>)typeof(DockWindow).GetField("_models", fields)!.GetValue(dock)!).Single(m => m.EntryId == entry.Id);
                var strip = (DependencyObject)typeof(DockWindow).GetField("_tileStrip", fields)!.GetValue(dock)!;
                var layout = (FrameworkElement)strip;
                layout.Measure(new Size(1200, 160)); layout.Arrange(new Rect(0, 0, 1200, 160)); layout.UpdateLayout();
                var indicator = Visuals(strip).OfType<Border>().Single(b =>
                    BindingOperations.GetBinding(b, UIElement.VisibilityProperty) is { } binding
                    && binding.Path?.Path == nameof(DockTileModel.RunningVisibility) && ReferenceEquals(binding.Source, model));
                dockState = new { running = services.Running.IsRunning(entry), modelVisibility = model.RunningVisibility.ToString(),
                    indicatorVisibility = indicator.Visibility.ToString(), brush = (indicator.Background as SolidColorBrush)?.Color.ToString(),
                    indicator.Width, indicator.Height, liveWindowShown = false,
                    dockAssembly = typeof(DockWindow).Assembly.Location };
            }
            finally { dock.Close(); app.Shutdown(); }
        }
        var modules = new[] { typeof(WindowMatcher).Assembly, typeof(WindowLocator).Assembly }
            .Select(a => new { a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) });
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            entry.Id, entry.DisplayName, entry.Target, entry.AppUserModelId, resolvedIdentity = identity,
            matchedHandles = matching.Select(w => w.Handle.ToInt64()),
            sameImage = sameImage.Select(w => new { hwnd = w.Handle.ToInt64(), w.ProcessId, w.IsMinimized,
                w.ImagePath, w.WindowAumid, w.ProcessAumid, titleLength = w.Title.Length }), modules, dockState,
            readOnlyLibrary = true, privacy = "No window titles, chat contents or process command lines recorded."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{entry.DisplayName}: same-image candidates={sameImage.Length}, matches={matching.Count}; {report}");
        return 0;
    }
}
