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

public sealed class IndependentLauncherPagesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialPagesRenderIndependentlyAndNewDragPageIsSavedOnlyAfterDrop(bool commit)
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-page-drag-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            data.Settings.Current.Launcher.GroupModeEnabled = false;
            data.Settings.Current.Launcher.Rows = 2; data.Settings.Current.Launcher.Columns = 2;
            for (var i = 0; i < 4; i++) data.Library.Add(new AppEntry { Id = "a" + i, DisplayName = "App" + i, Target = "missing" + i });
            using var services = new AppServiceHost(data);
            data.Layouts.CommitLauncher(new LauncherLayout { SchemaVersion = 2, Pages = [
                new() { Id = "p1", Slots = [new() { ItemId = "a0" }, new() { ItemId = "a1" }] },
                new() { Id = "p2", Slots = [new() { ItemId = "a2" }, new() { ItemId = "a3" }] }] }, data.Layouts.LauncherVersion);
            var window = new LauncherWindow { ShowActivated = false };
            window.ApplySettings(data.Settings.Current.Launcher);
            try
            {
                ((LiquidGlassSurface)window.FindName("GlassSurface")).SetPerformanceSleeping(true);
                window.AttachServices(services, true); window.Show(); window.UpdateLayout();
                var grid = (UniformGrid)window.FindName("TileGrid");
                string[] Visible() => grid.Children.OfType<Button>().Select(b => (string)b.Tag).ToArray();
                Assert.Equal(["a0", "a1"], Visible());
                window.NavigatePage(1); Call("StopPageSlide"); window.UpdateLayout();
                Assert.Equal(["a2", "a3"], Visible());
                var source = grid.Children.OfType<Button>().First();
                Call("PrepareIconDrag", source, new Point(20, 20));
                window.NavigatePage(1); Call("StopPageSlide"); window.UpdateLayout();
                Assert.Empty(Visible());
                Assert.Equal("3 / 3", ((TextBlock)window.FindName("PageText")).Text);
                window.NavigatePage(1); // Holding navigation on a blank page cannot create unbounded pages.
                Assert.Equal("3 / 3", ((TextBlock)window.FindName("PageText")).Text);
                if (commit)
                {
                    // The mouse can remain in panel padding after edge navigation.
                    Call("PlaceIconAtPointer", new Point(grid.ActualWidth + 12, grid.ActualHeight / 4), false, true);
                    var edit = (LauncherEditSession)Field("_dragEdit")!;
                    Assert.True(new LauncherWorkspace(services).Commit(edit));
                    Set("_dropCommitted", true);
                }
                Call("FinishIconDrag"); window.UpdateLayout();
                Assert.Equal(commit ? 3 : 2, data.Layouts.Launcher.Pages.Count);
                Assert.Equal(commit ? new[] { "a3" } : ["a2", "a3"], data.Layouts.Launcher.Pages[1].Slots.Select(s => s.ItemId!));
                Assert.Equal(commit ? new[] { "a2" } : ["a2", "a3"], Visible());
                Assert.Equal(["a0", "a1"], data.Layouts.Launcher.Pages[0].Slots.Select(s => s.ItemId!));

                object? Field(string name) => typeof(LauncherWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
                void Set(string name, object value) => typeof(LauncherWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
                void Call(string name, params object[] args) => typeof(LauncherWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
            }
            finally { window.Close(); services.Dispose(); Directory.Delete(root, true); }
        });
    }
}
