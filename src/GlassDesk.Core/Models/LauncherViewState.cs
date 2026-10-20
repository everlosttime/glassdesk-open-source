namespace GlassDesk.Core.Models;

public enum LauncherEscapeAction { CloseGroup, ClearSearch, HideWindow }

/// <summary>与窗口无关的分页和分层退出状态。</summary>
public sealed class LauncherViewState
{
    private int _rows;
    private int _columns;
    private int _browseCount;
    private int _searchCount;
    private int _browsePage;
    private int _searchPage;
    private string _query = string.Empty;

    public LauncherViewState(int rows, int columns) => SetGrid(rows, columns);

    public string Query
    {
        get => _query;
        set
        {
            var next = value ?? string.Empty;
            if (next == _query) return;
            var wasSearching = IsSearching;
            _query = next;
            if (!wasSearching && IsSearching) _searchPage = 0;
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_query);
    public string? OpenGroupId { get; set; }
    public int Rows => _rows;
    public int Columns => _columns;
    public int Capacity => _rows * _columns;
    public int ItemCount => IsSearching ? _searchCount : _browseCount;
    public int Page => IsSearching ? _searchPage : _browsePage;
    public int PageCount => Math.Max(1, (ItemCount + Capacity - 1) / Capacity);
    public int VisibleCount => Math.Max(0, Math.Min(Capacity, ItemCount - (Page * Capacity)));

    public void SetGrid(int rows, int columns)
    {
        if (rows is < 2 or > 10 || columns is < 2 or > 12)
            throw new ArgumentOutOfRangeException(nameof(rows), "Grid must be 2–10 rows and 2–12 columns.");
        _rows = rows;
        _columns = columns;
        ClampPages();
    }

    public void SetItemCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (IsSearching) _searchCount = count;
        else _browseCount = count;
        ClampPages();
    }

    public void MoveToPage(int page)
    {
        var next = Math.Clamp(page, 0, PageCount - 1);
        if (IsSearching) _searchPage = next;
        else _browsePage = next;
    }

    public LauncherEscapeAction Escape()
    {
        if (OpenGroupId is not null)
        {
            OpenGroupId = null;
            return LauncherEscapeAction.CloseGroup;
        }
        if (IsSearching)
        {
            Query = string.Empty;
            return LauncherEscapeAction.ClearSearch;
        }
        return LauncherEscapeAction.HideWindow;
    }

    private void ClampPages()
    {
        _browsePage = Math.Min(_browsePage, Math.Max(0, (_browseCount + Capacity - 1) / Capacity - 1));
        _searchPage = Math.Min(_searchPage, Math.Max(0, (_searchCount + Capacity - 1) / Capacity - 1));
    }
}
