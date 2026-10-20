using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherBackgroundWorkTests
{
    [Fact]
    public void AnIconQueuedBeforeHideAppearsOnItsRetainedTileAfterResume()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-icon-resume-" + Guid.NewGuid().ToString("N")));
            LauncherWindow? window = null;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                var icon = Path.Combine(root, "custom.png");
                File.WriteAllBytes(icon, GlassDesk.Core.Imaging.PngWriter.EncodeBgra([0, 240, 0, 255], 1, 1));
                data.Library.Add(new AppEntry { Id = "icon-resume", DisplayName = "Icon resume", Target = "missing", IsTargetMissing = true, CustomIconPath = icon });
                using var services = new AppServiceHost(data);
                window = new LauncherWindow();
                ((LiquidGlassSurface)window.FindName("GlassSurface")!).SetPerformanceSleeping(true);
                var loader = typeof(LauncherWindow).GetField("_iconLoader", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                loader.GetType().GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(loader,
                    ["slow-before-icon", (Func<BitmapImage?>)(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return null; })]);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
                window.AttachServices(services, true); window.Show();
                var grid = (UniformGrid)window.FindName("TileGrid")!; var tile = grid.Children[0];
                window.Hide(); window.SetBackgroundWorkSuspended(true); release.Set();
                window.SetBackgroundWorkSuspended(false); window.PrepareToShow(); window.Show();
                Assert.Same(tile, grid.Children[0]);
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                var started = System.Diagnostics.Stopwatch.StartNew();
                timer.Tick += (_, _) => { if (HasImage(tile) || started.Elapsed.TotalSeconds >= 2) frame.Continue = false; };
                timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
                Assert.True(HasImage(tile), "A pending decode from the previous activation must be applied to the same retained tile once awake.");
            }
            finally
            {
                release.Set(); window?.Close();
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    private static bool HasImage(DependencyObject root)
    {
        if (root is Image { Source: BitmapSource }) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (HasImage(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    [Fact]
    public void UnchangedPageReusesItsVisibleTilesAcrossBackgroundPauseAndResume()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-page-resume-" + Guid.NewGuid().ToString("N")));
            LauncherWindow? window = null;
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                for (var i = 0; i < 6; i++) data.Library.Add(new AppEntry
                    { Id = "resume-" + i, DisplayName = "Resume " + i, Target = Path.Combine(root, "missing-" + i), IsTargetMissing = true });
                using var services = new AppServiceHost(data);
                window = new LauncherWindow();
                ((LiquidGlassSurface)window.FindName("GlassSurface")!).SetPerformanceSleeping(true);
                window.AttachServices(services, true); window.Show();
                var grid = (UniformGrid)window.FindName("TileGrid")!;
                var original = grid.Children.Cast<UIElement>().ToArray(); Assert.Equal(6, original.Length);
                window.Hide(); window.SetBackgroundWorkSuspended(true); window.SetBackgroundWorkSuspended(false);
                window.PrepareToShow(); window.Show();
                Assert.Equal(original.Length, grid.Children.Count);
                for (var i = 0; i < original.Length; i++) Assert.Same(original[i], grid.Children[i]);
            }
            finally
            {
                window?.Close();
                // This exact new test directory is a direct child of the system temp directory.
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                    throw new IOException("Unexpected test cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
