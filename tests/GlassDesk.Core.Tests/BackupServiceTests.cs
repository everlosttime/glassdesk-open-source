using System.Text.Json;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();
    private readonly TempDataDirectory _archive = new();
    private readonly TempDataDirectory _newHome = new();

    public void Dispose()
    {
        _temp.Dispose();
        _archive.Dispose();
        _newHome.Dispose();
    }

    /// <summary>用容易辨认的数据填充源目录，并返回该存储。</summary>
    private GlassDeskDataStore SeedSource()
    {
        var store = _temp.CreateStore();
        store.LoadAll();

        store.Settings.Current.Appearance.AccentColor = "#123456";
        store.Library.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "记事本",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
        });
        store.Layouts.Dock.PinnedItemIds.Add("dock-item");

        store.Settings.Save();
        store.Library.Save();
        store.Layouts.SaveDock();
        store.Layouts.SaveLauncher();

        return store;
    }

    [Fact]
    public void BackupTo_WritesManifestAndDocuments()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);

        var result = service.BackupTo(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.Contains("settings.json", result.Files);
        Assert.Contains("apps.json", result.Files);
        Assert.True(File.Exists(Path.Combine(_archive.Root, "manifest.json")));

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_archive.Root, "manifest.json")),
            GlassDeskJsonContext.Default.BackupManifest);

        Assert.NotNull(manifest);
        Assert.Equal("GlassDesk", manifest.Product);
        Assert.Equal(GlassDeskSettings.CurrentSchemaVersion, manifest.SchemaVersion);
    }

    [Fact]
    public void BackupTo_SkipsDocumentsThatWereNeverSaved()
    {
        // 在用户尚未做过任何自定义时创建的备份，文件更少是合理的。
        var service = new BackupService(_temp.Paths);

        var result = service.BackupTo(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void BackupAndRestore_IncludeCustomIconFiles()
    {
        // 回归（阶段 3 复核）：自定义图标不是可再生缓存，是从用户选的文件复制进来的。
        // 旧版备份只带四份 JSON，恢复之后 CustomIconPath 指向的文件不存在，
        // 用户看到的条目就真的没有图标了——而且备份当时还报「成功」。
        var store = SeedSource();

        var iconsDirectory = _temp.Paths.IconCacheDirectory;
        Directory.CreateDirectory(iconsDirectory);

        // 条目上记的是绝对路径，指向缓存目录里那个 custom- 开头的文件。
        var customIcon = Path.Combine(iconsDirectory, "custom-abc123.png");
        File.WriteAllBytes(customIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01]);

        var entry = store.Library.Items[0];
        entry.CustomIconPath = customIcon;
        store.Library.Update(entry);
        store.Library.Save();

        var service = new BackupService(_temp.Paths);
        Assert.True(service.BackupTo(_archive.Root).Succeeded);

        // 模拟「缓存被清掉」之后的恢复：图标文件已经不在了。
        Directory.Delete(iconsDirectory, recursive: true);
        Assert.False(File.Exists(customIcon));

        var restored = service.RestoreFrom(_archive.Root);

        Assert.True(restored.Succeeded, restored.Error);
        Assert.Contains(Path.Combine("icons", "custom-abc123.png"), restored.Files);

        // 必须回到原位，条目上的绝对路径才能继续指到东西。
        Assert.True(File.Exists(customIcon), "恢复之后自定义图标没有回来");
        Assert.Equal(
            File.ReadAllBytes(customIcon),
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01 });
    }

    [Fact]
    public void BackupTo_LeavesUnrelatedFilesInTheIconsDirectoryAlone()
    {
        // 备份只带走我们自己的 custom-* 文件；用户放进 icons/ 里的别的东西
        // 不该被顺带拷走，更不该被删。
        var store = SeedSource();
        var iconsDirectory = _temp.Paths.IconCacheDirectory;
        Directory.CreateDirectory(iconsDirectory);

        var unrelated = Path.Combine(iconsDirectory, "我的笔记.txt");
        File.WriteAllText(unrelated, "不是图标");

        var result = new BackupService(_temp.Paths).BackupTo(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(Path.Combine("icons", "我的笔记.txt"), result.Files);
        Assert.False(File.Exists(Path.Combine(_archive.Root, "icons", "我的笔记.txt")));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void RestoreIntoADifferentDirectory_RepointsManagedIconReferences()
    {
        // 回归（复核第二轮）：恢复到另一个数据目录。旧版把图标文件复制过去了，
        // 但 apps.json 里的 CustomIconPath 仍指向旧目录——删掉旧目录里的图片，
        // 恢复出来的引用就失效了。这影响换电脑与迁移数据目录。
        var source = SeedSource();
        var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x07 };

        Directory.CreateDirectory(_temp.Paths.IconCacheDirectory);
        var oldIcon = Path.Combine(_temp.Paths.IconCacheDirectory, "custom-moved.png");
        File.WriteAllBytes(oldIcon, iconBytes);

        var entry = source.Library.Items[0];
        entry.CustomIconPath = oldIcon;
        source.Library.Update(entry);
        source.Library.Save();

        Assert.True(new BackupService(_temp.Paths).BackupTo(_archive.Root).Succeeded);

        // 恢复到一个**不同的**数据目录，然后把旧目录整个删掉（换电脑）。
        var restore = new BackupService(_newHome.Paths).RestoreFrom(_archive.Root);

        Assert.True(restore.Succeeded, restore.Error);

        var iconInNewHome = Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-moved.png");
        Assert.True(File.Exists(iconInNewHome), "图标文件没有随恢复落到新目录");

        // 落盘的 apps.json 必须指向新目录，而不是旧目录。
        Assert.Equal(iconInNewHome, ReadStoredCustomIconPath(_newHome.Paths.AppLibraryFile));
        Assert.Equal(1, restore.RepairedIconReferences);

        // 旧目录删掉之后，新目录里的引用仍能读到图标。
        Directory.Delete(_temp.Root, recursive: true);
        Assert.False(File.Exists(oldIcon));
        Assert.Equal(iconBytes, File.ReadAllBytes(iconInNewHome));

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();
        Assert.Equal(iconInNewHome, reloaded.Find(entry.Id)!.CustomIconPath);
    }

    [Fact]
    public void Load_RepointsManagedIconReferences_WhenTheyNoLongerResolve()
    {
        // 数据目录被整棵搬走（不经备份）也是同一种情况：加载时发现托管的
        // 图标路径在原处不存在、而同名文件就在当前 icons/ 下，就指过去。
        var staleIcon = Path.Combine(_archive.Root, "icons", "custom-abc.png");

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "样例",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = staleIcon,
        };
        Assert.Equal(AddEntryResult.Added, store.Add(entry));
        store.Save();

        // 图标其实就在当前数据目录下——目录搬过来之后的样子。
        Directory.CreateDirectory(_newHome.Paths.IconCacheDirectory);
        var iconHere = Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-abc.png");
        File.WriteAllBytes(iconHere, [0x89, 0x50, 0x4E, 0x47]);

        Assert.False(File.Exists(staleIcon), "测试前提不成立：旧位置不应有文件");

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();

        Assert.Equal(iconHere, reloaded.Find(entry.Id)!.CustomIconPath);
        Assert.Equal(1, reloaded.RepairedIconReferenceCount);
        Assert.True(File.Exists(reloaded.Find(entry.Id)!.CustomIconPath));
    }

    [Fact]
    public void Load_ReportsRepairedIconReferencesThroughTheDataStore()
    {
        // 自愈是改了用户数据，必须让用户看得见，而不是悄悄改掉。
        var staleIcon = Path.Combine(_archive.Root, "icons", "custom-xyz.png");

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        Assert.Equal(AddEntryResult.Added, store.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "样例",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = staleIcon,
        }));
        store.Save();

        Directory.CreateDirectory(_newHome.Paths.IconCacheDirectory);
        File.WriteAllBytes(Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-xyz.png"), [0x89, 0x50]);

        var report = _newHome.CreateStore();
        var loadReport = report.LoadAll();

        Assert.Equal(1, report.Library.RepairedIconReferenceCount);
        Assert.Contains(loadReport.Notices, notice => notice.Contains("1 条", StringComparison.Ordinal) && notice.Contains("图标", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_LeavesAPathOutsideTheManagedDirectoryAlone()
    {
        // 指向 icons/ 之外的路径不是我们托管的，我们既无从判断它该指到哪儿，
        // 改写它就是破坏用户数据。保持原样，也不计入修复数。
        var external = Path.Combine(_archive.Root, "用户 自己放的.png");

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        Assert.Equal(AddEntryResult.Added, store.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "样例",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = external,
        }));
        store.Save();

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();

        Assert.Equal(external, reloaded.Find(store.Items[0]!.Id)!.CustomIconPath);
        Assert.Equal(0, reloaded.RepairedIconReferenceCount);
    }

    [Fact]
    public void Load_DoesNotTouchAReferenceThatAlreadyResolves()
    {
        // 指向当前目录且文件存在的引用是健康的，改写它只会白写一次文件。
        Directory.CreateDirectory(_newHome.Paths.IconCacheDirectory);
        var iconHere = Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-ok.png");
        File.WriteAllBytes(iconHere, [0x89, 0x50]);

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        Assert.Equal(AddEntryResult.Added, store.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "样例",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = iconHere,
        }));
        store.Save();

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();

        Assert.Equal(iconHere, reloaded.Find(store.Items[0]!.Id)!.CustomIconPath);
        Assert.Equal(0, reloaded.RepairedIconReferenceCount);
    }

    /// <summary>读出落盘的 apps.json 里第一条记录的自定义图标路径。</summary>
    private static string? ReadStoredCustomIconPath(string appLibraryFile)
    {
        var document = JsonSerializer.Deserialize(
            File.ReadAllText(appLibraryFile),
            GlassDeskJsonContext.Default.AppLibraryData);

        return document?.Items?.FirstOrDefault()?.CustomIconPath;
    }

    [Fact]
    public void RestoreFrom_LeavesUnrelatedFilesInTheArchiveAlone()
    {
        // 归档里除了我们自己的东西之外，可能还有别的文件。恢复只取
        // 四份文档与 icons/custom-*，其余一律不理。
        SeedSource();
        Assert.True(new BackupService(_temp.Paths).BackupTo(_archive.Root).Succeeded);

        // 备份完成之后，再往归档里塞几样不属于备份的东西。
        var archiveIcons = Path.Combine(_archive.Root, "icons");
        Directory.CreateDirectory(archiveIcons);
        File.WriteAllText(Path.Combine(archiveIcons, "custom-真的图标.png"), "内容");
        File.WriteAllText(Path.Combine(archiveIcons, "别人的文件.png"), "内容");
        File.WriteAllText(Path.Combine(_archive.Root, "随手放的.txt"), "内容");

        var result = new BackupService(_temp.Paths).RestoreFrom(_archive.Root);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(Path.Combine("icons", "custom-真的图标.png"), result.Files);
        Assert.DoesNotContain(Path.Combine("icons", "别人的文件.png"), result.Files);
        Assert.DoesNotContain("随手放的.txt", result.Files);
        Assert.False(File.Exists(Path.Combine(_temp.Paths.IconCacheDirectory, "别人的文件.png")));
        Assert.False(File.Exists(Path.Combine(_temp.Paths.RootDirectory, "随手放的.txt")));
    }

    [Fact]
    public void RestoreFrom_TakesTheSafetyCopyOfCustomIconsToo()
    {
        // 恢复前的快照也要带上自定义图标，否则「误操作的恢复」对图标不可回退。
        var store = SeedSource();
        var iconsDirectory = _temp.Paths.IconCacheDirectory;
        Directory.CreateDirectory(iconsDirectory);

        var customIcon = Path.Combine(iconsDirectory, "custom-safety.png");
        File.WriteAllBytes(customIcon, [0x89, 0x50, 0x4E, 0x47]);
        store.Library.Items[0].CustomIconPath = customIcon;
        store.Library.Update(store.Library.Items[0]);
        store.Library.Save();

        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);
        var restored = service.RestoreFrom(_archive.Root);

        Assert.True(restored.Succeeded);
        Assert.NotNull(restored.SafetyCopyDirectory);
        Assert.True(File.Exists(Path.Combine(restored.SafetyCopyDirectory!, "icons", "custom-safety.png")));
    }

    [Fact]
    public void RestoreFrom_ReturnsTheOriginalData()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        // 把正在使用的数据换成明显不同的一份。
        var live = _temp.CreateStore();
        live.LoadAll();
        live.Settings.Current.Appearance.AccentColor = "#FFFFFF";
        live.Library.Remove(live.Library.Items[0].Id);
        live.Settings.Save();
        live.Library.Save();

        var result = service.RestoreFrom(_archive.Root);
        Assert.True(result.Succeeded);

        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();

        Assert.Equal("#123456", reloaded.Settings.Current.Appearance.AccentColor);
        Assert.Single(reloaded.Library.Items);
        Assert.Equal(["dock-item"], reloaded.Layouts.Dock.PinnedItemIds);
    }

    [Fact]
    public void RestoreFrom_TakesASafetyCopyFirst()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        var result = service.RestoreFrom(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.SafetyCopyDirectory);
        Assert.True(Directory.Exists(result.SafetyCopyDirectory));
    }

    [Fact]
    public void RestoreFrom_RejectsAFolderWithoutAManifest()
    {
        var service = new BackupService(_temp.Paths);

        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.Contains("备份清单", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsAForeignManifest()
    {
        File.WriteAllText(
            Path.Combine(_archive.Root, "manifest.json"),
            """{"product":"SomeOtherApp","schemaVersion":1}""");

        var service = new BackupService(_temp.Paths);
        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.Contains("不属于本产品", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsABackupFromANewerSchema()
    {
        File.WriteAllText(
            Path.Combine(_archive.Root, "manifest.json"),
            """{"product":"GlassDesk","schemaVersion":99}""");

        var service = new BackupService(_temp.Paths);
        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.Contains("更新的版本", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsAMissingDirectory()
    {
        var service = new BackupService(_temp.Paths);

        var result = service.RestoreFrom(Path.Combine(_archive.Root, "does-not-exist"));

        Assert.False(result.Succeeded);
        Assert.Contains("不存在", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsABackupWhoseDocumentIsUnreadable()
    {
        // 备份清单本身是好的，但里面的设置文件是坏 JSON。这类备份必须整份拒绝：
        // 一旦开始拷贝，就会用坏数据覆盖用户当前正常的配置。
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        File.WriteAllText(Path.Combine(_archive.Root, "settings.json"), "{ 这不是合法 JSON");

        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RestoreFrom_LeavesCurrentDataUntouched_WhenTheBackupIsUnreadable()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        // 把当前数据改成与备份明显不同的内容。
        var live = _temp.CreateStore();
        live.LoadAll();
        live.Settings.Current.Appearance.AccentColor = "#ABCDEF";
        live.Settings.Save();

        File.WriteAllText(Path.Combine(_archive.Root, "settings.json"), "{ 坏掉的备份");

        var result = service.RestoreFrom(_archive.Root);
        Assert.False(result.Succeeded);

        // 关键断言：失败之后现有数据必须原封不动。
        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();
        Assert.Equal("#ABCDEF", reloaded.Settings.Current.Appearance.AccentColor);
    }

    [Fact]
    public void RestoreFrom_RejectsABackupWithAnUnreadableAppLibrary()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        File.WriteAllText(Path.Combine(_archive.Root, "apps.json"), "[[[ 坏掉的清单");

        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void RestoreFrom_AcceptsABackupThatIsEntirelyValid()
    {
        // 校验不能矫枉过正：完全正常的备份必须仍然可以恢复。
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        var live = _temp.CreateStore();
        live.LoadAll();
        live.Settings.Current.Appearance.AccentColor = "#000000";
        live.Settings.Save();

        var result = service.RestoreFrom(_archive.Root);

        Assert.True(result.Succeeded);

        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();
        Assert.Equal("#123456", reloaded.Settings.Current.Appearance.AccentColor);
    }
}