using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class ElevatedLaunchMenuTests
{
    [Theory]
    [InlineData(AppItemKind.Program, "application.exe", true)]
    [InlineData(AppItemKind.Shortcut, "application.lnk", true)]
    [InlineData(AppItemKind.File, "document.txt", false)]
    [InlineData(AppItemKind.Url, "https://example.com", false)]
    [InlineData(AppItemKind.Program, "missing.exe", false)]
    public void BothMenusOfferElevationOnlyForSupportedExistingApplications(AppItemKind kind, string target, bool enabled)
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-menu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            if (kind != AppItemKind.Url)
            {
                target = Path.Combine(root, target);
                if (!target.EndsWith("missing.exe")) File.WriteAllText(target, "placeholder");
            }
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            data.Library.Add(new() { Id = "app", Kind = kind, DisplayName = "Test", Target = target });
            using var services = new AppServiceHost(data);
            var dock = new DockWindow(); var launcher = new LauncherWindow();
            try
            {
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher); host.Update(data.Settings.Current.Dock);
                dock.AttachServices(services, host, () => { }); launcher.AttachServices(services, true);
                var dockMenu = (ContextMenu)typeof(DockWindow).GetMethod("BuildEntryMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dock, ["app"])!;
                var launcherMenu = (ContextMenu)typeof(LauncherWindow).GetMethod("BuildTileMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(launcher, [new LauncherSlot { ItemId = "app" }])!;
                foreach (var menu in new[] { dockMenu, launcherMenu })
                {
                    var action = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "以管理员身份运行"));
                    Assert.Equal(enabled, action.IsEnabled);
                    Assert.Equal("打开", ((MenuItem)menu.Items[0]).Header);
                    Assert.Same(action, menu.Items[1]);
                }
            }
            finally { launcher.Close(); dock.Close(); Directory.Delete(root, true); }
        });
    }
}
