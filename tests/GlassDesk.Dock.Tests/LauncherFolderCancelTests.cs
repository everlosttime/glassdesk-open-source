using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherFolderCancelTests
{
    [Fact]
    public void CancellingMemberReorderRestoresVisibleOrderAndDisk()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-folder-cancel-" + Guid.NewGuid().ToString("N")));
            LauncherWindow? window = null;
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                for (var i = 0; i < 3; i++) data.Library.Add(new AppEntry { Id = "a" + i, DisplayName = "App " + i, Target = Path.Combine(root, "app" + i) });
                var layout = LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher, ["a0", "a1", "a2"], 35);
                layout = LauncherLayoutEditor.CreateGroup(layout, "a1", "a0", "g", 35);
                layout = LauncherLayoutEditor.AddToGroup(layout, "a2", "g", 35);
                Assert.True(data.Layouts.CommitLauncher(layout, data.Layouts.LauncherVersion));
                using var services = new AppServiceHost(data);
                window = new LauncherWindow(); ((LiquidGlassSurface)window.FindName("GlassSurface")).SetPerformanceSleeping(true);
                window.AttachServices(services, true); window.Show(); window.UpdateLayout();
                object? Call(string name, params object[] args) => typeof(LauncherWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
                Call("OnTileActivated", data.Layouts.Launcher.Pages[0].Slots[0]);
                typeof(LauncherWindow).GetField("_folderOpen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!.GetType().GetMethod("Snap")!.Invoke(
                    typeof(LauncherWindow).GetField("_folderOpen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window), [1d]);
                Call("ApplyFolderFrame"); window.UpdateLayout();
                var items = (UniformGrid)window.FindName("GroupItems"); var grid = (UniformGrid)window.FindName("TileGrid");
                var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
                var source = (Button)items.Children[0]; var target = (Button)items.Children[2];
                Call("PrepareIconDrag", source, new Point(20, 20));
                var position = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), grid);
                Assert.True((bool)Call("PreviewFolderMemberInside", position)!);
                Assert.Equal("a0", ((Button)items.Children[2]).Tag);
                Call("FinishIconDrag");
                Assert.Equal(["a0", "a1", "a2"], items.Children.OfType<Button>().Select(b => (string)b.Tag));
                Assert.Equal(before, File.ReadAllBytes(data.Layouts.LauncherFilePath));
            }
            finally
            {
                window?.Close();
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
