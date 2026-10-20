using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class AppLibraryStoreTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AppLibraryStore CreateStore() => new(_temp.Paths.AppLibraryFile);

    private static AppEntry Entry(string target, string? arguments = null, string? name = null) => new()
    {
        Id = AppLibraryStore.NewId(),
        DisplayName = name ?? Path.GetFileName(target),
        Kind = AppItemKind.Program,
        Target = target,
        Arguments = arguments,
    };

    [Fact]
    public void Add_NewEntry_Succeeds()
    {
        var store = CreateStore();
        store.Load();

        var result = store.Add(Entry(@"C:\Windows\notepad.exe"));

        Assert.Equal(AddEntryResult.Added, result);
        Assert.Single(store.Items);
    }

    [Fact]
    public void Add_SameTargetTwice_IsReportedAsDuplicate()
    {
        var store = CreateStore();
        store.Load();

        store.Add(Entry(@"C:\Windows\notepad.exe"));
        var second = store.Add(Entry(@"C:\Windows\notepad.exe"));

        Assert.Equal(AddEntryResult.Duplicate, second);
        Assert.Single(store.Items);
    }

    [Fact]
    public void Add_SameTargetDifferentCase_IsDuplicate()
    {
        var store = CreateStore();
        store.Load();

        store.Add(Entry(@"C:\Windows\notepad.exe"));
        var second = store.Add(Entry(@"c:\windows\NOTEPAD.EXE"));

        Assert.Equal(AddEntryResult.Duplicate, second);
    }

    [Fact]
    public void Add_SameTargetDifferentArguments_IsADistinctEntry()
    {
        // 指向同一程序、开关不同的两个快捷方式是两种不同的操作，
        // 把它们合并会让用户丢掉一个条目。
        var store = CreateStore();
        store.Load();

        store.Add(Entry(@"C:\Tools\app.exe", "--profile work"));
        var second = store.Add(Entry(@"C:\Tools\app.exe", "--profile personal"));

        Assert.Equal(AddEntryResult.Added, second);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Add_WithoutTarget_IsRejected()
    {
        var store = CreateStore();
        store.Load();

        var result = store.Add(new AppEntry { DisplayName = "没有目标" });

        Assert.Equal(AddEntryResult.Rejected, result);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Add_WithoutId_AssignsOne()
    {
        var store = CreateStore();
        store.Load();

        var entry = new AppEntry { DisplayName = "notepad", Target = @"C:\Windows\notepad.exe" };
        store.Add(entry);

        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.NotNull(store.Find(entry.Id));
    }

    [Fact]
    public void Remove_DropsTheRecord()
    {
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Windows\notepad.exe");
        store.Add(entry);

        Assert.True(store.Remove(entry.Id));
        Assert.Null(store.Find(entry.Id));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Remove_UnknownId_ReportsNoChange()
    {
        var store = CreateStore();
        store.Load();

        Assert.False(store.Remove("no-such-id"));
    }

    [Fact]
    public void SaveThenLoad_KeepsEntriesAndLookups()
    {
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\编辑器.exe", name: "编辑器");
        store.Add(entry);
        store.Save();

        var reloaded = CreateStore();
        var outcome = reloaded.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Single(reloaded.Items);
        Assert.NotNull(reloaded.Find(entry.Id));
        Assert.Equal("编辑器", reloaded.Find(entry.Id)!.DisplayName);
    }

    [Fact]
    public void Load_SkipsUnusableRecordsForLookupButKeepsThemOnDisk()
    {
        // 手工编辑后出现坏行的文件不应让整个应用库不可用，
        // 这一行也应留存下来，以便用户在阶段 3 修复它。
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                { "id": "good", "displayName": "正常", "kind": "Program", "target": "C:\\Windows\\notepad.exe" },
                { "id": "broken", "displayName": "缺目标", "kind": "Program", "target": "" }
              ]
            }
            """);

        var store = CreateStore();
        store.Load();

        Assert.NotNull(store.Find("good"));
        Assert.Null(store.Find("broken"));
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Update_ReplacesInPlaceAndKeepsPosition()
    {
        var store = CreateStore();
        store.Load();
        var first = Entry(@"C:\Tools\a.exe");
        var second = Entry(@"C:\Tools\b.exe");
        store.Add(first);
        store.Add(second);

        first.DisplayName = "重命名后的名称";
        Assert.True(store.Update(first));

        // 位置保持不变：更新后的记录仍是第一项。
        Assert.Equal(first.Id, store.Items[0].Id);
        Assert.Equal("重命名后的名称", store.Find(first.Id)!.DisplayName);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Update_ClearsTheOldDedupKey_EvenWhenTheEntryWasMutatedFirst()
    {
        // 回归（阶段 3 复核）：调用方先就地改目标、再调 Update——这正是重新定位的做法。
        // 旧版先取「现有记录」再算键，而现有记录就是那个已被改过的对象，
        // 于是算出来的是新键、删掉的是新键，旧键留在索引里，
        // 之后导入旧路径会被误判为重复。
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\旧位置.exe");
        store.Add(entry);

        entry.Target = @"C:\Tools\新位置.exe";
        Assert.True(store.Update(entry));

        var oldKey = AppEntryKey.Compute(Entry(@"C:\Tools\旧位置.exe"));
        var newKey = AppEntryKey.Compute(Entry(@"C:\Tools\新位置.exe"));

        Assert.Null(store.FindByKey(oldKey));
        Assert.NotNull(store.FindByKey(newKey));
    }

    [Fact]
    public void Remove_ClearsTheOldDedupKey_EvenWhenTheEntryWasMutatedFirst()
    {
        // 与上一条同一类陷阱，只是发生在 Remove 上。
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\旧位置.exe");
        store.Add(entry);

        entry.Target = @"C:\Tools\新位置.exe";
        Assert.True(store.Remove(entry.Id));

        Assert.Null(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\旧位置.exe"))));
        Assert.Null(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\新位置.exe"))));
    }

    [Fact]
    public void Update_KeepsWorking_WhenTheCallerPassesADifferentObjectWithTheSameId()
    {
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\a.exe");
        store.Add(entry);

        var replacement = Entry(@"C:\Tools\b.exe");
        replacement.Id = entry.Id;
        replacement.DisplayName = "替换对象";

        Assert.True(store.Update(replacement));

        Assert.Equal("替换对象", store.Find(entry.Id)!.DisplayName);
        Assert.Null(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\a.exe"))));
        Assert.NotNull(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\b.exe"))));
    }

    [Fact]
    public void Update_RejectsATargetThatAnotherEntryAlreadyOccupies()
    {
        // 两条记录共享同一个去重键的话，应用库里就有一对无法分辨的重复项，
        // 之后无论删掉哪一条都会把共享的键一起清掉，另一条从此不参与去重。
        var store = CreateStore();
        store.Load();
        var first = Entry(@"C:\Tools\a.exe");
        var second = Entry(@"C:\Tools\b.exe");
        store.Add(first);
        store.Add(second);

        second.Target = @"C:\Tools\a.exe";

        Assert.False(store.Update(second));

        // 索引必须原封不动：两个键仍各自指向正确的记录，
        // 不会因为这次被拒绝的更新而少一个或多一个。
        // （调用方拿到的对象已被它自己改过，它得自己把字段改回去——
        // 见 LibraryMaintenance.Relocate 的处理。）
        var firstByKey = store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\a.exe")));
        var secondByKey = store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\b.exe")));

        Assert.NotNull(firstByKey);
        Assert.NotNull(secondByKey);
        Assert.Equal(first.Id, firstByKey.Id);
        Assert.Equal(second.Id, secondByKey.Id);
    }

    [Fact]
    public void Add_BatchThenSingleSave_ProducesOneDocument()
    {
        // 批量导入不应每加一项就重写一次文件。
        var store = CreateStore();
        store.Load();

        for (var i = 0; i < 20; i++)
        {
            store.Add(Entry($@"C:\Tools\app{i}.exe"));
        }

        store.Save();

        Assert.True(File.Exists(_temp.Paths.AppLibraryFile));

        var reloaded = CreateStore();
        reloaded.Load();
        Assert.Equal(20, reloaded.Items.Count);
    }

    [Fact]
    public void Load_ToleratesNullEntries()
    {
        // 手工编辑过的文件可能出现 null 条目。它必须被容错并报告，
        // 而不是抛出 NullReferenceException 让程序起不来。
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                null,
                { "id": "good", "displayName": "正常", "kind": "Program", "target": "C:\\Windows\\notepad.exe" }
              ]
            }
            """);

        var store = CreateStore();
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.NotNull(store.Find("good"));
    }

    [Fact]
    public void Load_ReportsAnomalousEntries_WithoutDroppingThem()
    {
        // 异常条目要报出来供用户修复，但不能拦住启动。
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                null,
                { "id": "no-target", "displayName": "缺目标", "kind": "Program", "target": "" },
                { "id": "good", "displayName": "正常", "kind": "Program", "target": "C:\\Windows\\notepad.exe" }
              ]
            }
            """);

        var store = CreateStore();
        store.Load();

        Assert.Equal(2, store.AnomalousEntryCount);
        Assert.NotNull(store.Find("good"));
    }

    [Fact]
    public void Load_ToleratesACompletelyNullItemsArray()
    {
        File.WriteAllText(_temp.Paths.AppLibraryFile, """{"schemaVersion":1,"items":null}""");

        var store = CreateStore();
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void LoadAll_SurvivesANullLibraryEntry_AndReportsIt()
    {
        // 端到端走一遍启动时的加载路径。一份含有 null 条目的应用库不能让加载抛异常，
        // 而要在报告里说明有几条记录无法使用——用户得知道文件里有东西坏了。
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """{"schemaVersion":1,"items":[null]}""");

        var data = _temp.CreateStore();
        var report = data.LoadAll();

        Assert.True(report.HasNotices);
        Assert.Contains(report.Notices, notice => notice.Contains("无法使用", StringComparison.Ordinal));
        Assert.Equal(1, data.Library.AnomalousEntryCount);
    }

    [Fact]
    public void LoadAll_OnAHealthyLibrary_ReportsNothing()
    {
        // 容错不能变成误报：正常的库不该产生任何提示。
        var store = CreateStore();
        store.Load();
        store.Add(Entry(@"C:\Windows\notepad.exe"));
        store.Save();

        var report = _temp.CreateStore().LoadAll();

        Assert.False(report.HasNotices);
    }
}