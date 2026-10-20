using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (GlassDesk.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        var results = new List<object>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")
        });
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-notice-test-" + Guid.NewGuid().ToString("n"));
        var report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "report.json");
        Directory.CreateDirectory(root);
        var failed = false;
        LauncherWindow? window = null;
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            using var host = new AppServiceHost(data);
            var input = Path.Combine(root, "Test app");
            Directory.CreateDirectory(input);
            var entry = host.Import.ImportItems([new GlassDesk.Core.Contracts.ImportRequest { Source = input }]).Added.Single();
            data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
            data.Layouts.SaveDock();
            window = new LauncherWindow();
            window.AttachServices(host, true);
            window.ShowActivated = false;
            window.Show();
            window.Hide();
            var status = (TextBlock)window.FindName("StatusText")!;
            var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
            var search = (TextBox)window.FindName("SearchBox")!;
            var trash = (FrameworkElement)window.FindName("TrashDropTarget")!;
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new LauncherWorkspace(host).BeginEdit());
            var payload = new DataObject("GlassDesk.Launcher.Slot", entry.Id);
            var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [payload, DragDropKeyStates.None, DragDropEffects.Move, trash, new Point(1, 1)], culture: null)!;
            drop.RoutedEvent = DragDrop.DropEvent;
            trash.RaiseEvent(drop);
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
            window.RefreshLibrary();
            Check("Production trash drop removes the launcher tile", grid.Children.Count == 0);
            search.Text = "Test app";
            Check("A removed shortcut is absent from search", grid.Children.Count == 0);
            search.Clear();
            Check("Trash preserves the pinned Dock item", data.Layouts.Dock.PinnedItemIds.Contains(entry.Id));
            Check("Saved layout has no hidden list", !File.ReadAllText(data.Paths.LauncherLayoutFile).Contains("excludedAppIds"));

            Add();
            Check("Adding the same source puts the tile back", grid.Children.Count == 1
                && data.Library.Items.Count == 1 && status.Visibility == Visibility.Visible);
            Pump(2400);
            Check("Feedback stays readable before three seconds", status.Visibility == Visibility.Visible);
            Pump(1000);
            Check("Add feedback disappears after three seconds", status.Visibility == Visibility.Collapsed);

            Add();
            Pump(2000);
            Add();
            Pump(1400);
            Check("A repeated success starts a fresh three seconds", status.Visibility == Visibility.Visible);
            Pump(2000);
            Check("Repeated success also expires", status.Visibility == Visibility.Collapsed);

            Add();
            Pump(2000);
            var showNotice = typeof(LauncherWindow).GetMethod("ShowNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var parameters = showNotice.GetParameters().Length == 1
                ? new object[] { "Read or save failed" } : new object[] { "Read or save failed", false };
            showNotice.Invoke(window, parameters);
            Pump(1500);
            Check("An older success cannot erase a newer error", status.Visibility == Visibility.Visible
                && status.Text == "Read or save failed");
            Pump(2000);
            Check("Errors remain available to read", status.Visibility == Visibility.Visible);

            void Add() => typeof(LauncherWindow).GetMethod("ImportPaths",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new List<string> { input }]);
        }
        catch (Exception ex)
        {
            failed = true;
            results.Add(new { name = "Unexpected exception", passed = false, details = ex.ToString() });
        }
        finally
        {
            window?.Close();
            app.Shutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = !failed, results },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        return failed ? 1 : 0;

        void Check(string name, bool passed)
        {
            results.Add(new { name, passed });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
            if (!passed) failed = true;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
