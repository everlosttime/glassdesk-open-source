using GlassDesk.Core.Models;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Storage;

/// <summary>向应用库添加一个条目的结果。</summary>
public enum AddEntryResult
{
    /// <summary>已创建新的记录。</summary>
    Added = 0,

    /// <summary>已存在目标与参数相同的条目。</summary>
    Duplicate = 1,

    /// <summary>条目缺少最低限度的字段，已被拒绝。</summary>
    Rejected = 2,
}

/// <summary>
/// 持有 <c>apps.json</c>——Dock 与启动台共用的唯一一份应用库。
/// </summary>
/// <remarks>
/// 列表旁边维护一份 id 索引，使 Dock 固定项与启动台槽位的查找
/// 不会随应用库增长而变慢。索引在每次加载时重建，并由各变更方法
/// 同步维护；除此之外不应直接访问 <see cref="Items"/>。
/// </remarks>
public sealed class AppLibraryStore
{
    private readonly JsonFileStore<AppLibraryData> _store;
    private readonly Dictionary<string, AppEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppEntry> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// 「条目对象 → 它上次的去重键」。
    /// </summary>
    /// <remarks>
    /// 必须按<strong>对象引用</strong>而不是内容来查，原因见 <see cref="Update"/>：
    /// 条目对象会先被调用方就地修改，再交给更新方法。按内容查是查不到旧键的，
    /// 而旧键找不回来，它就永远留在索引里。
    /// </remarks>
    private readonly Dictionary<AppEntry, string> _keyByEntry = new(ReferenceEqualityComparer.Instance);

    public AppLibraryStore(string path, TimeProvider? timeProvider = null)
    {
        _store = new JsonFileStore<AppLibraryData>(
            path,
            GlassDeskJsonContext.Default.AppLibraryData,
            timeProvider);
    }

    public string FilePath => _store.Path;

    public long Version { get; private set; }

    /// <summary>当前内容，按插入顺序排列。</summary>
    /// <remarks>
    /// 可能含有 null 元素——手工编辑过的文件里会有——调用方遍历前需要自行判空。
    /// 这类条目既不能被使用，也不能在保存时被悄悄丢掉。
    /// </remarks>
    public IReadOnlyList<AppEntry> Items => Data.Items;

    /// <summary>
    /// 上次加载时跳过的异常条目数量：null 元素，或缺少必需字段、无法使用的记录。
    /// </summary>
    /// <remarks>
    /// 报出来是为了让用户知道文件里有东西坏了，而不是让它无声无息地消失。
    /// 这些记录仍留在文档里，保存时会原样写回，供阶段 3 呈现出来修复。
    /// </remarks>
    public int AnomalousEntryCount { get; private set; }

    /// <summary>
    /// 上次加载时被改回当前数据目录的托管图标引用数量。
    /// </summary>
    /// <remarks>
    /// 自愈是在改用户数据，必须可见而不是悄悄发生；由
    /// <see cref="GlassDeskDataStore.LoadAll"/> 转成一条提示。
    /// </remarks>
    public int RepairedIconReferenceCount { get; private set; }

    private AppLibraryData Data { get; set; } = new();

    public LoadOutcome<AppLibraryData> Load()
    {
        var outcome = _store.Load(static () => new AppLibraryData());

        Data = outcome.Value;
        Data.Items ??= [];
        RebuildIndexes();
        RepairManagedIconReferences();
        Version++;

        return outcome;
    }

    /// <summary>以原子方式持久化应用库。</summary>
    /// <exception cref="StorageException">文件无法写入。</exception>
    public void Save() => _store.Save(Data);

    public AppEntry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return _byId.GetValueOrDefault(id);
    }

        /// <summary>查找目标与参数相同的已有条目。</summary>
    public AppEntry? FindByKey(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>
    /// 添加条目，除非已存在等价条目。
    /// </summary>
    /// <remarks>
    /// 不负责保存；由调用方决定何时持久化，这样批量导入
    /// 只写一次文件，而不是每项写一次。
    /// </remarks>
    public AddEntryResult Add(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 只有目标需要由调用方提供。id 由我们生成，若在这里强制要求提供，
        // 每个刚导入的条目都会被拒绝。
        if (!entry.HasLaunchTarget)
        {
            return AddEntryResult.Rejected;
        }

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = NewId();
        }

        if (_byId.ContainsKey(entry.Id))
        {
            return AddEntryResult.Duplicate;
        }

        var key = AppEntryKey.Compute(entry);
        if (_byKey.ContainsKey(key))
        {
            return AddEntryResult.Duplicate;
        }

        Data.Items.Add(entry);
        Index(entry, key);
        Version++;
        return AddEntryResult.Added;
    }

    /// <summary>
    /// 从应用库移除条目。调用方还必须清除各布局中对这个 id 的
    /// 引用——见 <see cref="LayoutStore.PurgeItem"/>。
    /// </summary>
    public bool Remove(string? id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return false;
        }

        Data.Items.Remove(entry);
        _byId.Remove(entry.Id);
        Deindex(entry);
        Version++;
        return true;
    }

    /// <summary>
    /// 就地替换已有记录，保留其位置与 id。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>调用方通常会先改条目再调这里</b>——重新定位就是先把 <c>Target</c>
    /// 改成新路径。所以旧键不能在进入本方法之后再算：那时对象已经被改过，
    /// 算出来的是新键，删掉的也是新键，旧键便永远留在索引里，
    /// 旧位置从此被误判为「已有条目」，合法的导入被拒。
    /// 旧键从 <see cref="_keyByEntry"/> 里按对象引用取回。
    /// </para>
    /// <para>
    /// 目标改成了另一条记录已经占用的键时，本方法返回 false 且<strong>索引原封不动</strong>：
    /// 强行改下去会让两条记录共享同一个键，应用库里从此有一对无法分辨的重复项，
    /// 之后无论删掉哪一条都会把共享的键一起清掉，另一条从此不参与去重。
    /// 需要把冲突解释给用户听的调用方（重新定位）应当先自己探测，
    /// 见 <c>LibraryMaintenance.Relocate</c>。
    /// </para>
    /// <para>
    /// 返回 false 时，<b>调用方递进来的那个对象已经被它自己改过了</b>，
    /// 索引不会替它改回去——调用方必须自己把每个动过的字段还原。
    /// 这正是「就地修改」这种形态的代价，也是 <see cref="_keyByEntry"/>
    /// 必须存在的原因。
    /// </para>
    /// </remarks>
    public bool Update(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var existing = Find(entry.Id);
        if (existing is null)
        {
            return false;
        }

        var index = Data.Items.IndexOf(existing);
        if (index < 0)
        {
            return false;
        }

        var newKey = AppEntryKey.Compute(entry);

        if (_byKey.TryGetValue(newKey, out var holder)
            && !ReferenceEquals(holder, existing)
            && !ReferenceEquals(holder, entry))
        {
            return false;
        }

        Deindex(existing);
        Data.Items[index] = entry;
        _byId[entry.Id] = entry;
        Index(entry, newKey);
        Version++;
        return true;
    }

    /// <summary>为新的应用库记录生成不透明且永久的 id。</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>把一条记录连同它的键登记进两张索引。</summary>
    private void Index(AppEntry entry, string key)
    {
        _byId[entry.Id] = entry;
        _byKey[key] = entry;
        _keyByEntry[entry] = key;
    }

    /// <summary>
    /// 把一条记录从两张索引里拿掉，并顺手清掉它占用的键。
    /// </summary>
    /// <remarks>
    /// 键按引用取回，因此即使条目已经被调用方改过内容，
    /// 它<strong>原先</strong>占用的那个键也能被清掉。
    /// </remarks>
    private void Deindex(AppEntry entry)
    {
        _byId.Remove(entry.Id);

        if (_keyByEntry.Remove(entry, out var key))
        {
            _byKey.Remove(key);
        }
    }

    private void RebuildIndexes()
    {        _byId.Clear();
        _byKey.Clear();
        _keyByEntry.Clear();
        AnomalousEntryCount = 0;

        // 对无法启动的记录只忽略、不丢弃：手工编辑过的文件因此保留内容，
        // 阶段 3 会把损坏行呈现出来供修复。
        foreach (var entry in Data.Items)
        {
            // System.Text.Json 会把 JSON 数组里的 null 反序列化成列表里的 null 元素。
            // 这里曾经直接取 entry.IsUsable，于是 {"items":[null]} 就抛
            // NullReferenceException，整个程序起不来——一份手改坏的配置文件不该有这个后果。
            if (entry is null || !entry.IsUsable)
            {
                AnomalousEntryCount++;
                continue;
            }

            var key = AppEntryKey.Compute(entry);

            // TryAdd 而不是赋值：手工编辑的文件里可能真有两条内容相同的记录。
            // 只索引第一条，让另一条保持可被 Find(id) 找到但不参与去重，
            // 而不是让后一条悄悄顶掉前一条。
            if (_byKey.TryAdd(key, entry))
            {
                _keyByEntry[entry] = key;
            }

            _byId[entry.Id] = entry;
        }
    }

    /// <summary>
    /// 把指向别处的托管自定义图标引用改到当前数据目录。
    /// </summary>
    /// <remarks>
    /// <see cref="AppEntry.CustomIconPath"/> 存的是绝对路径，指向 <c>icons/</c> 下
    /// 由我们复制的那份文件。数据目录整体挪到别处（换电脑、手动迁移，或恢复到
    /// 另一个目录）之后，这些引用仍指着旧位置——文件明明已经搬过来了，图标却失效。
    ///
    /// <para>
    /// <b>托管图标的位置以当前数据目录为准。</b>
    /// 第一版用的是「原路径不存在才改写」，在**同一台机器**上跨目录恢复时不生效：
    /// 旧目录还在，旧路径照样解析得通，于是引用一直指着旧目录，
    /// 删掉旧目录图标就没了。托管文件的归属不该取决于旧目录碰巧还在不在。
    /// </para>
    ///
    /// <para>
    /// 判据是文件名前缀（<see cref="AppPaths.ManagedCustomIconPrefix"/>）：
    /// 托管的自定义图标一律叫 <c>custom-*</c>，且只可能在我们自己的 <c>icons/</c> 里。
    /// 只有当前目录下<strong>确有这份文件</strong>才改写——没有就保持原样，
    /// 绝不凭空造一个路径出来；指向 icons/ 之外的路径不是我们托管的，
    /// 动它就是破坏用户数据。
    /// </para>
    ///
    /// <para>
    /// 这里只改内存里的对象，写盘仍由调用方决定（<see cref="Load"/> 从不写文件，
    /// 这是数据层一贯的约定）。恢复流程会特意为此再保存一次，见 <c>BackupService</c>。
    /// </para>
    /// </remarks>
    private void RepairManagedIconReferences()
    {
        RepairedIconReferenceCount = 0;

        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(_store.Path));

        if (string.IsNullOrEmpty(dataRoot))
        {
            return;
        }

        foreach (var entry in Data.Items)
        {
            // 手工编辑过的文件里可能有空元素。
            if (entry is null || !AppPaths.IsManagedCustomIconPath(entry.CustomIconPath))
            {
                continue;
            }

            var current = entry.CustomIconPath!;
            var candidate = AppPaths.ManagedCustomIconPath(dataRoot, Path.GetFileName(current));

            // 已经指着当前目录就不用动；当前目录里没有这份文件，也不该凭空指过去。
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                continue;
            }

            entry.CustomIconPath = candidate;
            RepairedIconReferenceCount++;
        }
    }
}
