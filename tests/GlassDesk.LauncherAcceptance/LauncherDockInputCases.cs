using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;

internal static partial class Program
{
    private static void VerifyLauncherDockInput(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string dockAssembly, string evidence)
    {
        // Load the preserved package Dock rather than rebuilding that product component.
        var type = Assembly.LoadFrom(Path.GetFullPath(dockAssembly)).GetType("GlassDesk.Dock.DockWindow", throwOnError: true)!;
        var dock = (GlassWindow)Activator.CreateInstance(type)!;
        using var services = new AppServiceHost(data);
        var settings = new DockSettingsHost(window.Dispatcher);
        settings.Update(data.Settings.Current.Dock);
        type.GetMethod("AttachServices", [typeof(AppServiceHost), typeof(DockSettingsHost), typeof(Action)])!
            .Invoke(dock, [services, settings, (Action)(() => { })]);
        var starts = 0; var finishes = 0;
        EventHandler started = (_, _) => { starts++; type.GetMethod("RaiseAboveLauncherDuringDrag")!.Invoke(dock, null); };
        EventHandler finished = (_, _) => { finishes++; window.RaiseAboveDockAfterDrag(); };
        window.InternalDragStarted += started; window.InternalDragFinished += finished;
        try
        {
            dock.Show(); Pump(TimeSpan.FromMilliseconds(350)); window.Activate();
            var source = (string)((Button)grid.Children[0]).Tag;
            var layout = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
            var target = ((FrameworkElement)dock.FindName("LauncherButton")!).PointToScreen(new Point(24, 32));
            IosInput.SetCursorPos((int)start.X, (int)start.Y); IosInput.Mouse(2);
            var script = Task.Run(async () =>
            {
                try { await Task.Delay(550); IosInput.SetCursorPos((int)target.X, (int)target.Y); await Task.Delay(450); }
                finally { IosInput.Mouse(4); }
            });
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(5)); Pump(TimeSpan.FromMilliseconds(300));
            var reloaded = new GlassDeskDataStore(data.Paths); reloaded.LoadAll();
            Check("真实启动台拖入保留的Dock组件并固定落盘", "regression/launcher-dock",
                reloaded.Layouts.Dock.PinnedItemIds.Contains(source), "真实Dock保存源图标Id", string.Join(",", reloaded.Layouts.Dock.PinnedItemIds));
            Check("拖入Dock不移动启动台布局且完整结束一次", "regression/launcher-dock",
                layout.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)) && starts == 1 && finishes == 1 && System.Windows.Input.Mouse.Captured is null,
                "启动台布局不变，start/finish各一次，捕获释放", $"unchanged={layout.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))},start={starts},finish={finishes},capture={System.Windows.Input.Mouse.Captured}");
            Capture(dock, Path.Combine(evidence, "native-launcher-dock.png"));
        }
        finally
        {
            IosInput.Mouse(4); dock.Close();
            window.InternalDragStarted -= started; window.InternalDragFinished -= finished;
        }
    }
}
