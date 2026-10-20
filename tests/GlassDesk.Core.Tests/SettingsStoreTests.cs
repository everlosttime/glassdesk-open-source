using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LoadPreservesDockAutoHideAndDisablesRemovedPointerAndFullscreenFeatures()
    {
        File.WriteAllText(_temp.Paths.SettingsFile,
            """{"dock":{"autoHide":true,"edgeRevealEnabled":true},"launcher":{"fullscreenMode":true},"shortcuts":{"hotCornersEnabled":true,"hotCornerTarget":"TopLeft","mouseSideButtonsEnabled":true}}""");
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        Assert.True(store.Current.Dock.AutoHide);
        Assert.False(store.Current.Dock.EdgeRevealEnabled);
        Assert.False(store.Current.Launcher.FullscreenMode);
        Assert.False(store.Current.Shortcuts.HotCornersEnabled);
        Assert.False(store.Current.Shortcuts.MouseSideButtonsEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockDisplayModeSurvivesSavingAndReloading(bool autoHide)
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Dock.AutoHide = autoHide;
        store.Save();
        var reloaded = new SettingsStore(_temp.Paths.SettingsFile);
        reloaded.Load();
        Assert.Equal(autoHide, reloaded.Current.Dock.AutoHide);
    }

    [Fact]
    public void Load_OnEmptyDirectory_ReportsFirstRun_WithSpecifiedDefaults()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);

        var outcome = store.Load();

        Assert.Equal(LoadStatus.FirstRun, outcome.Status);
        Assert.False(outcome.NeedsUserAttention);

        // 默认值来自当前液态玻璃外观规格，因此值得固定下来。
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
        Assert.Equal("#0A84FF", store.Current.Appearance.AccentColor);
        Assert.Equal(40, store.Current.Appearance.DockCornerRadius);
        Assert.Equal(80, store.Current.Appearance.LauncherCornerRadius);

        Assert.Equal(48, store.Current.Dock.IconSize);
        Assert.Equal(8, store.Current.Dock.IconSpacing);
        Assert.Equal(1.35, store.Current.Dock.MagnificationPeak);
        Assert.Equal(300, store.Current.Dock.AutoHideLeaveDelayMs);

        Assert.Equal(5, store.Current.Launcher.Rows);
        Assert.Equal(7, store.Current.Launcher.Columns);
        Assert.Equal(0.66, store.Current.Launcher.WindowWorkAreaRatio);
        Assert.False(store.Current.Launcher.FocusSearchOnOpen);
        Assert.True(store.Current.Shortcuts.NextPage.IsBound);
        Assert.Equal("WheelDown", store.Current.Shortcuts.NextPage.Key);
        Assert.True(store.Current.Shortcuts.PreviousPage.IsBound);
        Assert.Equal("WheelUp", store.Current.Shortcuts.PreviousPage.Key);

        // 有风险的交互默认关闭，等用户主动开启。
        Assert.False(store.Current.Shortcuts.HotCornersEnabled);
        Assert.False(store.Current.Shortcuts.MouseSideButtonsEnabled);
        Assert.False(store.Current.DesktopTaskbar.BlockTaskbarEdgeReveal);
        Assert.False(store.Current.DesktopTaskbar.TaskbarAutoHideEnabled);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Current.Appearance.AccentColor = "#FF3B30";
        store.Current.Dock.Placement = DockPlacement.TopCenter;
        store.Current.Dock.TargetDisplayId = "DISPLAY2";
        store.Save();

        var reloaded = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = reloaded.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Equal(ThemeMode.Dark, reloaded.Current.Appearance.Theme);
        Assert.Equal("#FF3B30", reloaded.Current.Appearance.AccentColor);
        Assert.Equal(DockPlacement.TopCenter, reloaded.Current.Dock.Placement);
        Assert.Equal("DISPLAY2", reloaded.Current.Dock.TargetDisplayId);
    }

    [Fact]
    public void Load_WhenMainFileIsCorruptAndBackupIsGood_RecoversAndPreserves()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Save();

        // 再保存一次，替换流程才有上一版本可以留作备份。
        store.Current.Appearance.AccentColor = "#34C759";
        store.Save();

        File.WriteAllText(_temp.Paths.SettingsFile, "{ not valid json at all");

        var damaged = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = damaged.Load();

        Assert.Equal(LoadStatus.RecoveredFromBackup, outcome.Status);
        Assert.True(outcome.NeedsUserAttention);
        Assert.NotNull(outcome.PreservedFile);
        Assert.True(File.Exists(outcome.PreservedFile));

        // 恢复出来的值就是损坏发生前磁盘上的那一份。
        Assert.Equal(ThemeMode.Dark, damaged.Current.Appearance.Theme);
    }

    [Fact]
    public void Load_WhenBothFilesAreCorrupt_UsesDefaultsAndKeepsTheEvidence()
    {
        File.WriteAllText(_temp.Paths.SettingsFile, "broken");
        File.WriteAllText(_temp.Paths.SettingsFile + AtomicFile.BackupSuffix, "also broken");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Unrecoverable, outcome.Status);
        Assert.NotNull(outcome.PreservedFile);
        Assert.True(File.Exists(outcome.PreservedFile));
        Assert.Equal("broken", File.ReadAllText(outcome.PreservedFile));

        // 仍然可用：配置损坏不应阻止产品启动。
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
    }

    [Fact]
    public void Load_TreatsEmptyFileAsCorrupt()
    {
        // 这是引入原子替换之前，保存过程被断电打断后留下来的形态。
        // 它不能被当作「空但合法」的文档解析。
        File.WriteAllText(_temp.Paths.SettingsFile, string.Empty);

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.NotEqual(LoadStatus.Loaded, outcome.Status);
        Assert.NotNull(outcome.PreservedFile);
    }

    [Fact]
    public void Load_WithNullSections_RepairsThemInsteadOfThrowing()
    {
        File.WriteAllText(
            _temp.Paths.SettingsFile,
            """{"schemaVersion":1,"appearance":null,"dock":null,"shortcuts":null}""");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.NotNull(store.Current.Appearance);
        Assert.NotNull(store.Current.Dock);
        Assert.NotNull(store.Current.Shortcuts);
        Assert.NotNull(store.Current.Shortcuts.ToggleLauncher);
    }

    [Fact]
    public void Load_StampsTheCurrentSchemaVersion()
    {
        File.WriteAllText(_temp.Paths.SettingsFile, """{"schemaVersion":0}""");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();

        Assert.Equal(GlassDeskSettings.CurrentSchemaVersion, store.Current.SchemaVersion);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        // 高级用户会手工编辑这个文件；因为琐碎的语法就拒绝加载未免太苛刻。
        File.WriteAllText(
            _temp.Paths.SettingsFile,
            """
            {
              // the accent colour
              "schemaVersion": 1,
              "appearance": { "theme": "Dark", },
            }
            """);

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
    }

    [Fact]
    public void Load_WrittenByNewerSchema_KeepsDataWithoutLosingIt()
    {
        File.WriteAllText(
            _temp.Paths.SettingsFile,
            """{"schemaVersion":99,"appearance":{"theme":"Dark","accentColor":"#123456"}}""");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        // 向前兼容的读取：保留能理解的部分，而不是拒绝启动。
        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
        Assert.Equal("#123456", store.Current.Appearance.AccentColor);
    }

    [Fact]
    public void Save_ProducesReadableJson_WithCamelCaseAndNamedEnums()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Save();

        var json = File.ReadAllText(_temp.Paths.SettingsFile);

        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"Dark\"", json, StringComparison.Ordinal);
    }
}
