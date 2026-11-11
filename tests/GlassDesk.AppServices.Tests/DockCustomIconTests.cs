using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class DockCustomIconTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("entry:launcher")]
    [InlineData("entry:this-pc")]
    [InlineData("entry:desktop-icons")]
    public void IconCopyPersistsAcrossReloadAndResetPreservesOriginalImage(string key)
    {
        WithHost((data, host, source) =>
        {
            var icons = Service(host);
            Assert.True(Set(icons, key, source));
            var managed = PathFor(icons, key)!;
            Assert.NotEqual(source, managed);
            Assert.True(File.Exists(managed));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(managed));
            var reloaded = new GlassDeskDataStore(data.Paths);
            reloaded.LoadAll();
            using var reopened = new AppServiceHost(reloaded);
            Assert.Equal(managed, PathFor(Service(reopened), key));
            Assert.True(Set(icons, key, null));
            Assert.Null(PathFor(icons, key));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(managed));
        });
    }

    [Theory]
    [InlineData("app")]
    [InlineData("entry:launcher")]
    public void SaveFailureKeepsPreviousIconAndRemovesUncommittedCopy(string key)
    {
        WithHost((data, host, source) =>
        {
            var icons = Service(host);
            Assert.True(Set(icons, key, source));
            var original = PathFor(icons, key);
            var previousFiles = Directory.GetFiles(data.Paths.IconCacheDirectory).Order().ToArray();
            var target = key == "app" ? data.Library.FilePath : data.Settings.FilePath;
            using (var blocker = new FileStream(target + AtomicFile.TempSuffix, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(Set(icons, key, source));
                Assert.False(Set(icons, key, null));
            }
            Assert.Equal(original, PathFor(icons, key));
            Assert.True(File.Exists(original));
            Assert.Equal(previousFiles, Directory.GetFiles(data.Paths.IconCacheDirectory).Order().ToArray());
        });
    }

    private static object Service(AppServiceHost host)
    {
        var property = typeof(AppServiceHost).GetProperty("DockIcons");
        Assert.NotNull(property);
        return property.GetValue(host)!;
    }
    private static bool Set(object service, string key, string? source) =>
        (bool)service.GetType().GetMethod("SetIcon")!.Invoke(service, [key, source])!;
    private static string? PathFor(object service, string key) =>
        (string?)service.GetType().GetMethod("GetIconPath")!.Invoke(service, [key]);
    private static void WithHost(Action<GlassDeskDataStore, AppServiceHost, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-dock-icon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            data.Library.Add(new AppEntry { Id = "app", DisplayName = "App", Kind = AppItemKind.Program, Target = @"C:\fixture.exe" });
            data.Library.Save(); data.Settings.Save();
            var source = Path.Combine(root, "source.png");
            File.WriteAllBytes(source, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z+YQAAAAASUVORK5CYII="));
            using var host = new AppServiceHost(data);
            action(data, host, source);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
