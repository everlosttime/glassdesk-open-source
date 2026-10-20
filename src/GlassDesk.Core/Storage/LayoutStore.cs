using GlassDesk.Core.Models;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Storage;

/// <summary>
/// 掌管两份布局文档：<c>dock-layout.json</c> 和 <c>launcher-layout.json</c>。
/// </summary>
/// <remarks>
/// 两者都独立于应用库，彼此之间也相互独立，因此启动台布局一旦损坏，不会让用户
/// 同时丢掉 Dock 或应用库。
///
/// 插槽按稠密的有序列表存储，而不是固定的网格坐标。位置
/// 就是索引，所以移除一个分组天然会合拢空档，无需
/// 参考启动台所做的那一趟空插槽压实。
/// </remarks>
public sealed class LayoutStore
{
    private readonly JsonFileStore<DockLayout> _dockStore;
    private readonly JsonFileStore<LauncherLayout> _launcherStore;
    private List<string> _lastSavedDockPins = [];
    private LauncherLayout _lastSavedLauncher = new();

    public LayoutStore(string dockLayoutPath, string launcherLayoutPath, TimeProvider? timeProvider = null)
    {
        _dockStore = new JsonFileStore<DockLayout>(
            dockLayoutPath,
            GlassDeskJsonContext.Default.DockLayout,
            timeProvider);

        _launcherStore = new JsonFileStore<LauncherLayout>(
            launcherLayoutPath,
            GlassDeskJsonContext.Default.LauncherLayout,
            timeProvider);
    }

    public DockLayout Dock { get; private set; } = new();

    public LauncherLayout Launcher { get; private set; } = new();

    public long LauncherVersion { get; private set; }

    public string DockFilePath => _dockStore.Path;

    public string LauncherFilePath => _launcherStore.Path;

    public LoadOutcome<DockLayout> LoadDock()
    {
        var outcome = _dockStore.Load(static () => new DockLayout());
        Dock = outcome.Value;
        Dock.PinnedItemIds ??= [];
        _lastSavedDockPins = [.. Dock.PinnedItemIds];
        return outcome;
    }

    public LoadOutcome<LauncherLayout> LoadLauncher()
    {
        var outcome = _launcherStore.Load(static () => new LauncherLayout());
        Launcher = outcome.Value;
        Launcher.Pages ??= [];
        _lastSavedLauncher = LauncherLayoutRules.DeepClone(Launcher);
        LauncherVersion++;
        return outcome;
    }

    /// <exception cref="StorageException">文件无法写入。</exception>
    public void SaveDock()
    {
        try
        {
            _dockStore.Save(Dock);
            _lastSavedDockPins = [.. Dock.PinnedItemIds];
        }
        catch
        {
            // Callers mutate the shared Dock object before saving. If the write fails,
            // leave every later reader with the last known persisted order.
            Dock.PinnedItemIds = [.. _lastSavedDockPins];
            throw;
        }
    }

    /// <exception cref="StorageException">文件无法写入。</exception>
    public void SaveLauncher()
    {
        try
        {
            _launcherStore.Save(Launcher);
            _lastSavedLauncher = LauncherLayoutRules.DeepClone(Launcher);
            LauncherVersion++;
        }
        catch
        {
            var lastSaved = LauncherLayoutRules.DeepClone(_lastSavedLauncher);
            Launcher.Pages = lastSaved.Pages;
            Launcher.ExcludedAppIds = lastSaved.ExcludedAppIds;
            Launcher.SchemaVersion = lastSaved.SchemaVersion;
            throw;
        }
    }

    /// <summary>Commits a detached preview once, refusing a stale layout revision.</summary>
    public bool CommitLauncher(LauncherLayout draft, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (expectedVersion != LauncherVersion) return false;
        if (LauncherLayoutRules.Equivalent(Launcher, draft)) return true;
        var committed = LauncherLayoutRules.DeepClone(draft);
        Launcher.Pages = committed.Pages;
        Launcher.ExcludedAppIds = committed.ExcludedAppIds;
        Launcher.SchemaVersion = committed.SchemaVersion;
        SaveLauncher();
        return true;
    }

    /// <summary>
    /// 只把应用从 Dock 栏移除。应用库中的记录仍然保留——这是
    /// 「从 Dock 移除」这一动作，而不是「删除」。
    /// </summary>
    public bool UnpinFromDock(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        return Dock.PinnedItemIds.Remove(itemId);
    }

    /// <summary>
    /// 清除两份布局中对该 id 的每一处引用。当记录从应用库
    /// 删除时调用它，也只在这种时候调用。
    /// </summary>
    /// <returns>被移除的引用数量。</returns>
    public int PurgeItem(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return 0;
        }

        var removed = 0;

        if (Dock.PinnedItemIds.Remove(itemId))
        {
            removed++;
        }

        foreach (var page in Launcher.Pages)
        {
            foreach (var slot in page.Slots)
            {
                if (slot.Kind != LauncherSlotKind.Group || slot.Group is null)
                {
                    continue;
                }

                removed += slot.Group.ItemIds.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
            }

            // 页保持原有顺序；网格会重新排布填上空档。
            removed += page.Slots.RemoveAll(slot =>
                slot.Kind == LauncherSlotKind.App &&
                string.Equals(slot.ItemId, itemId, StringComparison.Ordinal));
        }

        // 已经空无一物的分组没有理由继续留在网格上。
        removed += RemoveEmptyGroups();

        return removed;
    }

    /// <summary>丢弃不再包含任何应用的分组。</summary>
    public int RemoveEmptyGroups()
    {
        var removed = 0;

        foreach (var page in Launcher.Pages)
        {
            removed += page.Slots.RemoveAll(static slot =>
                slot.Kind == LauncherSlotKind.Group &&
                (slot.Group is null || slot.Group.ItemIds.Count == 0));
        }

        return removed;
    }

    /// <summary>移除空页，让网格末尾永远不会停在一张空白页上。</summary>
    public int RemoveEmptyPages()
    {
        return Launcher.Pages.RemoveAll(static page => page.Slots.Count == 0);
    }
}
