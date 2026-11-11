using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherSearchRefreshTests
{
    [Fact]
    public void TextBoxSearchUsesCurrentNameAliasesAndRestoresBrowsing()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-search-refresh-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            var entry = new AppEntry { Id = "a", DisplayName = "启动台", Target = "missing-a", SearchAliases = ["old-alias"] };
            data.Library.Add(entry);
            data.Library.Add(new AppEntry { Id = "b", DisplayName = "Other", Target = "missing-b" });
            using var services = new AppServiceHost(data);
            var window = new LauncherWindow { ShowActivated = false };
            try
            {
                // This checks TextBox/render routing, not capture performance.
                ((LiquidGlassSurface)window.FindName("GlassSurface")).SetPerformanceSleeping(true);
                window.AttachServices(services, true); window.Show(); window.UpdateLayout();
                var search = (TextBox)window.FindName("SearchBox");
                var grid = (UniformGrid)window.FindName("TileGrid");
                string[] Visible() => grid.Children.OfType<Button>().Select(button => (string)button.Tag).ToArray();
                Assert.Equal(["a", "b"], Visible());
                search.Text = "qidongtai";
                Assert.Equal(["a"], Visible());
                search.Text = "qdt";
                Assert.Equal(["a"], Visible());
                search.Text = "old-alias";
                Assert.Equal(["a"], Visible());
                entry.DisplayName = "工具";
                entry.SearchAliases[0] = "new-alias";
                search.Text = "qdt";
                Assert.Empty(Visible());
                search.Text = "gj";
                Assert.Equal(["a"], Visible());
                search.Text = "old-alias";
                Assert.Empty(Visible());
                search.Text = "NEW-ALIAS";
                Assert.Equal(["a"], Visible());
                search.Clear();
                Assert.Equal(["a", "b"], Visible());
                Assert.Equal("1 / 1", ((TextBlock)window.FindName("PageText")).Text);
            }
            finally { window.Close(); services.Dispose(); Directory.Delete(root, true); }
        });
    }
}
