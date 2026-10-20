using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

/// <summary>
/// 把用户带来的东西收进共用应用库。
/// </summary>
/// <remarks>
/// 三条硬性行为：
/// <list type="number">
/// <item><b>逐条报告，不整批失败。</b>一次导入二十个文件，其中一个没有权限，
/// 另外十九个照常入库，失败的那一个带原因出现在结果里。</item>
/// <item><b>不回滚已有数据。</b>失败只影响失败的那一条，
/// 已经进库的条目与导入之前就在库里的条目都不受影响。</item>
/// <item><b>只写一次文件。</b>批量导入在一开始就把库读进来，全部处理完再保存。
/// 每导入一条保存一次既慢，又会在中途失败时留下半个批次。</item>
/// </list>
///
/// <para>
/// 图标提取失败<strong>不算导入失败</strong>：界面少一张图是外观问题，
/// 条目本身仍然可用，不该因此拒绝用户的条目。
/// </para>
/// </remarks>
public sealed class ImportService : IImportService
{
    private readonly AppLibraryStore _library;
    private readonly IconCache _icons;
    private readonly IImportObserver? _observer;
    private readonly LayoutStore? _layouts;
    private readonly Func<int> _launcherCapacity;

    public ImportService(AppLibraryStore library, IconCache icons, IImportObserver? observer = null)
        : this(library, icons, observer, null)
    {
    }

    public ImportService(AppLibraryStore library, IconCache icons, IImportObserver? observer,
        LayoutStore? layouts, Func<int>? launcherCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(icons);

        _library = library;
        _icons = icons;
        _observer = observer;
        _layouts = layouts;
        _launcherCapacity = launcherCapacity ?? (() => 35);
    }

    /// <summary>本次会话里实际写出的图标文件数，便于报告这次导入花了多少代价。</summary>
    public int IconsWritten { get; private set; }

    /// <summary>本次会话里复用了已有图标文件的次数。</summary>
    public int IconsReused { get; private set; }

    public ImportResult ImportItems(IReadOnlyList<ImportRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var added = new List<AppEntry>();
        var duplicates = new List<AppEntry>();
        var failures = new List<ImportFailure>();

        if (requests.Count == 0)
        {
            return new ImportResult();
        }

        IconsWritten = 0;
        IconsReused = 0;

        foreach (var request in requests)
        {
            if (request is null)
            {
                continue;
            }

            try
            {
                ImportOne(request, added, duplicates, failures);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 单条出意外不能带走整批。这里兜住的是「某一条来源触发了我们没预料到的
                // 情况」——权限、半途被删掉、外壳返回一个没见过的失败码。
                // 它属于「逐条报告」，不属于「整批失败」：
                // 用户拖进来的一批文件里有一个坏的，其余十九个必须照常入库。
                failures.Add(new ImportFailure
                {
                    Source = request.Source ?? string.Empty,
                    Reason = ImportFailureReason.Other,
                    Message = $"处理时出错：{ex.Message}",
                });
            }
        }

        if (added.Count > 0)
        {
            // 只在真的有新增时才落盘。全是重复项的一次导入不该改动文件，
            // 那样会让文件的修改时间无谓地变化。
            try { _library.Save(); }
            catch (StorageException)
            {
                foreach (var entry in added) _library.Remove(entry.Id);
                throw;
            }
        }

        var layoutChanged = false;
        if (_layouts is not null && added.Count + duplicates.Count > 0)
        {
            var capacity = Math.Max(1, _launcherCapacity());
            var draft = LauncherLayoutRules.Reconcile(_layouts.Launcher,
                _library.Items.Where(item => item.IsUsable).Select(item => item.Id), capacity);
            draft = LauncherLayoutEditor.AddShortcuts(draft,
                added.Concat(duplicates).Where(item => item.IsUsable).Select(item => item.Id), capacity);
            layoutChanged = !LauncherLayoutRules.Equivalent(_layouts.Launcher, draft);
            if (layoutChanged && !_layouts.CommitLauncher(draft, _layouts.LauncherVersion))
                throw new StorageException("启动台布局已变化，请重试添加。", _layouts.LauncherFilePath);
        }
        if (added.Count > 0 || layoutChanged) _observer?.OnLibraryChanged();

        return new ImportResult
        {
            Added = added,
            Duplicates = duplicates,
            Failures = failures,
        };
    }

    /// <summary>导入单个来源，结果写进传进来的三个列表。</summary>
    public void ImportOne(
        ImportRequest request,
        List<AppEntry> added,
        List<AppEntry> duplicates,
        List<ImportFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(duplicates);
        ArgumentNullException.ThrowIfNull(failures);

        var (entry, reason, message) = AppEntryFactory.TryCreate(request);

        if (entry is null)
        {
            failures.Add(new ImportFailure
            {
                Source = request.Source ?? string.Empty,
                Reason = reason,
                Message = message,
            });

            return;
        }

        // 先去重再提取图标：重复项不该为了一份不会被保存的图标而付出提取代价。
        var existing = _library.FindByKey(AppEntryKey.Compute(entry));
        if (existing is not null)
        {
            duplicates.Add(existing);
            return;
        }

        CacheIcon(entry);

        if (_library.Add(entry) != AddEntryResult.Added)
        {
            // 走到这里说明同一个批次里出现了重复来源（例如把一个文件夹拖了两次）。
            // 它同样是「已经存在」，不是错误。
            duplicates.Add(entry);
            return;
        }

        added.Add(entry);
    }

    /// <summary>给一条记录备好缓存图标；失败就留着没有图标，不影响条目本身。</summary>
    public void CacheIcon(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cached = _icons.EnsureFor(entry.IconSource);

        if (cached is null)
        {
            return;
        }

        if (cached.CreatedFile)
        {
            IconsWritten++;
        }
        else
        {
            IconsReused++;
        }

        // 图标来源保留原样，不换成缓存文件的路径：缓存是可以被清掉的，
        // 留着来源才能在清空之后重新提取。
    }
}

/// <summary>
/// 应用库发生变化时得到通知。
/// </summary>
/// <remarks>
/// 让服务不必知道 Dock 与启动台的存在。阶段 3 先用它把变化告知宿主；
/// 阶段 4/5 的窗口接管刷新时接上同一个口子。
/// </remarks>
public interface IImportObserver
{
    void OnLibraryChanged();
}
