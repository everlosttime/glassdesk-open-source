using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LayoutStoreTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private LayoutStore CreateStore() =>
        new(_temp.Paths.DockLayoutFile, _temp.Paths.LauncherLayoutFile);

    /// <summary>构造一个存储：含一个程序坞固定项、一个散放槽位和一个只含一项的分组。</summary>
    private static LayoutStore Populate(LayoutStore store, out string groupId)
    {
        store.Dock.PinnedItemIds.AddRange(["app-a", "app-b", "app-c"]);

        groupId = "group-1";

        store.Launcher.Pages.Add(new LauncherPage
        {
            Id = "page-1",
            Slots =
            [
                new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = "app-b" },
                new LauncherSlot
                {
                    Kind = LauncherSlotKind.Group,
                    Group = new LauncherGroup
                    {
                        Id = groupId,
                        Name = "工具",
                        ItemIds = ["app-c", "app-d"],
                    },
                },
                new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = "app-e" },
            ],
        });

        return store;
    }

    [Fact]
    public void UnpinFromDock_RemovesOnlyTheDockPin()
    {
        var store = Populate(CreateStore(), out _);

        Assert.True(store.UnpinFromDock("app-b"));

        // 启动台中的引用必须保留：从程序坞移除不等于删除。
        Assert.DoesNotContain("app-b", store.Dock.PinnedItemIds);
        Assert.Contains(store.Launcher.Pages[0].Slots,
            slot => slot.Kind == LauncherSlotKind.App && slot.ItemId == "app-b");
    }

    [Fact]
    public void UnpinFromDock_UnknownId_ReportsNoChange()
    {
        var store = Populate(CreateStore(), out _);

        Assert.False(store.UnpinFromDock("not-present"));
    }

    [Fact]
    public void PurgeItem_RemovesDockPinLooseSlotAndGroupMembership()
    {
        var store = Populate(CreateStore(), out _);

        var removed = store.PurgeItem("app-c");

        // 一处程序坞固定项，加上一处分组成员关系。
        Assert.Equal(2, removed);
        Assert.DoesNotContain("app-c", store.Dock.PinnedItemIds);

        var group = store.Launcher.Pages[0].Slots
            .Single(slot => slot.Kind == LauncherSlotKind.Group).Group!;
        Assert.DoesNotContain("app-c", group.ItemIds);
    }

    [Fact]
    public void PurgeItem_RemovesTheLooseSlotAndClosesTheGap()
    {
        var store = Populate(CreateStore(), out _);

        store.PurgeItem("app-b");

        // 位置即索引，因此网格会自动重排，无需额外的压缩步骤。
        var slots = store.Launcher.Pages[0].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(LauncherSlotKind.Group, slots[0].Kind);
        Assert.Equal("app-e", slots[1].ItemId);
    }

    [Fact]
    public void PurgeItem_RemovesAGroupThatBecomesEmpty()
    {
        var store = Populate(CreateStore(), out var groupId);

        // app-d 是该分组里唯一没有同时出现在别处的成员。
        store.PurgeItem("app-c");
        store.PurgeItem("app-d");

        var slots = store.Launcher.Pages[0].Slots;
        Assert.DoesNotContain(slots, slot => slot.Group?.Id == groupId);
    }

    [Fact]
    public void PurgeItem_KeepsGroupsThatStillHaveMembers()
    {
        var store = Populate(CreateStore(), out var groupId);

        store.PurgeItem("app-c");

        var slots = store.Launcher.Pages[0].Slots;
        var group = slots.Single(slot => slot.Group?.Id == groupId).Group!;
        Assert.Equal(["app-d"], group.ItemIds);
    }

    [Fact]
    public void PurgeItem_LeavesUnrelatedEntriesAlone()
    {
        var store = Populate(CreateStore(), out _);

        store.PurgeItem("app-b");

        // 程序坞保留所有未被清理的项。
        Assert.Equal(["app-a", "app-c"], store.Dock.PinnedItemIds);

        // 分组保留了两个成员，无关的散放槽位也依然存在。
        // 只有 app-b 自己的槽位被移除，所以剩下两个槽位而不是三个。
        var slots = store.Launcher.Pages[0].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(["app-c", "app-d"], slots[0].Group!.ItemIds);
        Assert.Equal("app-e", slots[1].ItemId);
    }

    [Fact]
    public void PurgeItem_UnknownId_ChangesNothing()
    {
        var store = Populate(CreateStore(), out _);

        Assert.Equal(0, store.PurgeItem("never-existed"));
        Assert.Equal(3, store.Dock.PinnedItemIds.Count);
        Assert.Equal(3, store.Launcher.Pages[0].Slots.Count);
    }

    [Fact]
    public void RemoveEmptyPages_DropsTrailingBlankPages()
    {
        var store = Populate(CreateStore(), out _);
        store.Launcher.Pages.Add(new LauncherPage { Id = "page-2" });

        store.RemoveEmptyPages();

        Assert.Single(store.Launcher.Pages);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsBothLayouts()
    {
        var store = Populate(CreateStore(), out var groupId);
        store.SaveDock();
        store.SaveLauncher();

        var reloaded = CreateStore();
        reloaded.LoadDock();
        reloaded.LoadLauncher();

        Assert.Equal(["app-a", "app-b", "app-c"], reloaded.Dock.PinnedItemIds);

        var group = reloaded.Launcher.Pages[0].Slots
            .Single(slot => slot.Group?.Id == groupId).Group!;
        Assert.Equal("工具", group.Name);
        Assert.Equal(["app-c", "app-d"], group.ItemIds);
    }

    [Fact]
    public void SaveDock失败时共享布局恢复到上次已保存顺序()
    {
        var store = CreateStore();
        store.Dock.PinnedItemIds.AddRange(["app-a", "app-b"]);
        store.SaveDock();

        // 模拟另一个进程占用原子写入的临时文件；真正的磁盘保存必须失败。
        using (var blocker = new FileStream(
            store.DockFilePath + AtomicFile.TempSuffix,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            store.Dock.PinnedItemIds = ["app-b", "app-a"];
            Assert.Throws<StorageException>(() => store.SaveDock());
        }

        Assert.Equal(["app-a", "app-b"], store.Dock.PinnedItemIds);
        var reloaded = CreateStore();
        reloaded.LoadDock();
        Assert.Equal(store.Dock.PinnedItemIds, reloaded.Dock.PinnedItemIds);
    }

    [Fact]
    public void 取消固定保存失败时仍保持原固定项()
    {
        var store = CreateStore();
        store.Dock.PinnedItemIds.AddRange(["app-a", "app-b"]);
        store.SaveDock();

        using (var blocker = new FileStream(
            store.DockFilePath + AtomicFile.TempSuffix,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(store.UnpinFromDock("app-a"));
            Assert.Throws<StorageException>(() => store.SaveDock());
        }

        Assert.Equal(["app-a", "app-b"], store.Dock.PinnedItemIds);
    }

    [Fact]
    public void PurgeItem_SurvivesAReload()
    {
        var store = Populate(CreateStore(), out _);
        store.PurgeItem("app-c");
        store.SaveDock();
        store.SaveLauncher();

        var reloaded = CreateStore();
        reloaded.LoadDock();
        reloaded.LoadLauncher();

        Assert.DoesNotContain("app-c", reloaded.Dock.PinnedItemIds);
        var group = reloaded.Launcher.Pages[0].Slots
            .Single(slot => slot.Kind == LauncherSlotKind.Group).Group!;
        Assert.Equal(["app-d"], group.ItemIds);
    }

    [Fact]
    public void DeleteFromLibrary_RemovesTheRecordEverywhere()
    {
        // 整份文档层面的不变式，经由程序坞和启动台
        // 共用的门面来验证。
        var data = _temp.CreateStore();
        data.LoadAll();

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "记事本",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
        };

        data.Library.Add(entry);
        data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
        data.Layouts.Launcher.Pages.Add(new LauncherPage
        {
            Id = "page-1",
            Slots = [new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = entry.Id }],
        });

        Assert.True(data.DeleteFromLibrary(entry.Id));

        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();

        Assert.Null(reloaded.Library.Find(entry.Id));
        Assert.Empty(reloaded.Layouts.Dock.PinnedItemIds);
        Assert.Empty(reloaded.Layouts.Launcher.Pages[0].Slots);
    }
}
