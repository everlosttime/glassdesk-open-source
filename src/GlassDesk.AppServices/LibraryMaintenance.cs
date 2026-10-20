using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

/// <summary>一次失效状态复查的结果。</summary>
/// <param name="Checked">检查过的条目数。</param>
/// <param name="Missing">目标找不到的条目数。</param>
/// <param name="NewlyBroken">这次新发现的失效条目。</param>
/// <param name="Recovered">目标又回来了、因此取消了失效标记的条目。</param>
/// <param name="Changed">状态有变化，调用方需要保存。</param>
public sealed record MissingTargetReport(
    int Checked,
    int Missing,
    IReadOnlyList<AppEntry> NewlyBroken,
    IReadOnlyList<AppEntry> Recovered,
    bool Changed);

/// <summary>重新定位一条记录的结果。</summary>
public enum RelocateResult
{
    /// <summary>已经改好并保存。</summary>
    Relocated = 0,

    /// <summary>没有这条记录。</summary>
    UnknownItem = 1,

    /// <summary>新位置不存在。</summary>
    TargetNotFound = 2,

    /// <summary>新位置已经被另一条记录占用；没有改动任何东西。</summary>
    ConflictsWithExisting = 3,

    /// <summary>这条记录不接受重新定位（网址）。</summary>
    NotRelocatable = 4,

    /// <summary>改得动内存但存不下去。</summary>
    SaveFailed = 5,
}

/// <summary>重新定位的结果，带上可展示的说明。</summary>
/// <param name="Result">结果分类。</param>
/// <param name="Message">给用户看的一句话。</param>
/// <param name="ItemId">与冲突的另一条记录（<see cref="RelocateResult.ConflictsWithExisting"/> 时有值）。</param>
public sealed record RelocationOutcome(RelocateResult Result, string Message, string? ItemId = null);

/// <summary>
/// 让应用库对「目标已经不在了」这件事保持诚实，并允许用户重新定位。
/// </summary>
/// <remarks>
/// R-07 要求失效的条目<strong>保留下来</strong>并显示为失效，而不是被悄悄丢掉。
/// 因此这里从不删除任何条目：它只翻转 <see cref="AppEntry.IsTargetMissing"/> 这一个标记。
/// 数据本身、布局对它的引用、图标全部原样留着——用户把移动硬盘插回去，
/// 下一次复查就会把标记清掉，条目恢复原样。
///
/// <para>
/// <b>网址永远不算失效。</b>一个网址能不能打开取决于网络，不取决于本机文件，
/// 拿文件存在性去判断它只会得到错误结论。
/// </para>
/// </remarks>
public sealed class LibraryMaintenance
{
    private readonly AppLibraryStore _library;
    private readonly IconCache _icons;
    private readonly IImportObserver? _observer;

    public LibraryMaintenance(AppLibraryStore library, IconCache icons, IImportObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(icons);

        _library = library;
        _icons = icons;
        _observer = observer;
    }

    /// <summary>
    /// 重新检查每一条记录的目标还在不在。
    /// </summary>
    /// <remarks>
    /// 只在状态真的变了的时候才保存。启动时无条件写一次应用库，会让文件的修改时间
    /// 每次开机都变，也让「上次改动是什么时候」这个线索失去意义。
    /// </remarks>
    public MissingTargetReport RefreshMissingTargets(bool persist = true)
    {
        var broken = new List<AppEntry>();
        var recovered = new List<AppEntry>();
        var missing = 0;
        var checkedCount = 0;

        foreach (var entry in _library.Items)
        {
            // 手工编辑过的文件里可能有空元素，它们既不能用也无从检查。
            if (entry is null || !entry.IsUsable)
            {
                continue;
            }

            checkedCount++;

            // 网址没有「文件在不在」这回事。
            if (entry.Kind == AppItemKind.Url)
            {
                continue;
            }

            var gone = LaunchService.IsTargetGone(entry);

            if (gone)
            {
                missing++;
            }

            if (gone == entry.IsTargetMissing)
            {
                continue;
            }

            entry.IsTargetMissing = gone;

            if (gone)
            {
                broken.Add(entry);
            }
            else
            {
                recovered.Add(entry);
            }
        }

        var changed = broken.Count > 0 || recovered.Count > 0;

        if (changed && persist)
        {
            _library.Save();
            _observer?.OnLibraryChanged();
        }

        return new MissingTargetReport(checkedCount, missing, broken, recovered, changed);
    }

    /// <summary>
    /// 把一条记录指到一个新位置。
    /// </summary>
    /// <remarks>
    /// 三件事一起做，缺一不可：改目标、取消失效标记、重新提取图标。
    /// 只改目标的话，界面上那条记录会带着旧图标继续显示为失效；
    /// 只清标记的话，记录会指向一个不存在的新位置却显示成正常。
    ///
    /// <para>
    /// 新位置已经被另一条记录占用时<strong>什么也不改</strong>并如实报冲突。
    /// 强行改下去会让两条记录的去重键相同——应用库里从此有一对无法分辨的重复项，
    /// 而这是阶段 2 花了 10 项测试去防的那类问题。
    /// </para>
    /// </remarks>
    public RelocationOutcome Relocate(string itemId, string newPath)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return new RelocationOutcome(RelocateResult.UnknownItem, "应用库里没有这一条记录。");
        }

        if (entry.Kind == AppItemKind.Url)
        {
            return new RelocationOutcome(RelocateResult.NotRelocatable, "网址不需要定位到文件。");
        }

        if (string.IsNullOrWhiteSpace(newPath))
        {
            return new RelocationOutcome(RelocateResult.TargetNotFound, "没有给出新位置。");
        }

        string full;

        try
        {
            full = Path.GetFullPath(newPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RelocationOutcome(RelocateResult.TargetNotFound, $"新位置无法解析：{newPath}");
        }

        if (!ExistsQuietly(full))
        {
            return new RelocationOutcome(RelocateResult.TargetNotFound, $"新位置不存在：{full}");
        }

        // 先看会不会撞上别的记录。检查用的是一条临时副本，因此原记录此刻还没被改动。
        var probe = CloneWithTarget(entry, full);
        var collided = _library.FindByKey(AppEntryKey.Compute(probe));

        if (collided is not null && !string.Equals(collided.Id, entry.Id, StringComparison.Ordinal))
        {
            return new RelocationOutcome(
                RelocateResult.ConflictsWithExisting,
                $"已经有另一条记录指向这个位置：{collided.DisplayName}",
                collided.Id);
        }

        var previousTarget = entry.Target;
        var previousKind = entry.Kind;
        var previousIconSource = entry.IconSource;
        var previousMissing = entry.IsTargetMissing;

        entry.Target = full;
        entry.Kind = ItemKindDetector.Detect(full, Directory.Exists(full));
        entry.IsTargetMissing = false;

        // 图标来源如果原先跟着旧目标走，就一起搬过去；如果它指向的是别处
        // （快捷方式自定义的图标位置），保持不动——那正是用户要的图标。
        if (string.IsNullOrWhiteSpace(previousIconSource)
            || string.Equals(previousIconSource, previousTarget, StringComparison.OrdinalIgnoreCase))
        {
            entry.IconSource = full;
        }

        if (!_library.Update(entry))
        {
            // 索引里找不到它，或者新位置撞上了别的记录。这里必须把**每一个**
            // 被改过的字段都还原——对象是就地修改的，漏一个就会留下半改的条目。
            // 漏掉 Kind 的那次是复核发现的：目标从文件改成文件夹、更新被拒之后，
            // 那条记录的类型仍是文件夹，指向的却还是原来的文件。
            entry.Target = previousTarget;
            entry.Kind = previousKind;
            entry.IconSource = previousIconSource;
            entry.IsTargetMissing = previousMissing;
            return new RelocationOutcome(RelocateResult.UnknownItem, "应用库索引里找不到这一条记录。");
        }

        try
        {
            _library.Save();
        }
        catch (StorageException ex)
        {
            entry.Target = previousTarget;
            entry.Kind = previousKind;
            entry.IconSource = previousIconSource;
            entry.IsTargetMissing = previousMissing;
            _library.Update(entry);
            return new RelocationOutcome(RelocateResult.SaveFailed, $"重新定位未能保存，原位置已恢复：{ex.Message}");
        }

        // 换了目标就重新提取图标。失败只是少一张图，不影响重新定位本身。
        CacheIcon(entry);

        _observer?.OnLibraryChanged();
        return new RelocationOutcome(RelocateResult.Relocated, $"已重新定位到：{full}");
    }

    /// <summary>给一条记录换一个自定义图标；传 null 表示撤掉自定义图标。</summary>
    public bool SetCustomIcon(string itemId, string? iconFilePath)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return false;
        }

        if (!_icons.ApplyCustomIcon(entry, iconFilePath))
        {
            return false;
        }

        _library.Update(entry);
        _library.Save();
        _observer?.OnLibraryChanged();
        return true;
    }

    private void CacheIcon(AppEntry entry)
    {
        var cached = _icons.EnsureFor(entry.IconSource);

        // 重新定位之后旧图标的缓存文件可能再也没人引用了。这里不主动删——
        // 「这个缓存文件还有没有别的记录在用」需要一次全库扫描才能答对，
        // 而代价（若干 KB 的孤儿文件）远小于答错（删掉别人正在用的图标）。
        _ = cached;
    }

    private static AppEntry CloneWithTarget(AppEntry entry, string target) => new()
    {
        Id = entry.Id,
        Target = target,
        Kind = ItemKindDetector.Detect(target, Directory.Exists(target)),
        Arguments = entry.Arguments,
    };

    private static bool ExistsQuietly(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
