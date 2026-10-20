using System.ComponentModel;
using System.Windows;

namespace GlassDesk.Dock;

/// <summary>
/// 一枚 Dock 磁贴的显示数据。
/// </summary>
/// <remarks>
/// 刻意做成了最小视图模型：图标与尺寸在构建时确定（设置变更时整行重建），
/// 而<strong>运行状态点</strong>用属性通知单独刷新——调和事件只改圆点，
/// 不重建磁贴，避免每次窗口生灭都重载一遍图标。
/// </remarks>
public sealed class DockTileModel : INotifyPropertyChanged
{
    private Visibility _runningVisibility = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>应用库条目 id。点击时据此走启动/激活。</summary>
    public string EntryId { get; init; } = string.Empty;

    /// <summary>内置入口的符号字形（Segoe Fluent Icons）。普通磁贴为 null（用图标）。</summary>
    public string? Glyph { get; init; }

    /// <summary>目标已失效（R-07）。失效磁贴的图标降透明显示。</summary>
    public bool IsMissing { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>悬停提示（D-06）：完整名称，不截断；失效条目附说明。</summary>
    public string ToolTipText { get; init; } = string.Empty;

    public System.Windows.Media.ImageSource? Icon { get; init; }

    /// <summary>运行状态点的颜色。来自当前调色板的前景色。</summary>
    public System.Windows.Media.Brush? DotBrush { get; init; }

    /// <summary>图标边长，DIP，来自程序坞设置。</summary>
    public double IconSize { get; init; } = 48;

    /// <summary>磁贴外边距，由图标间距设置推导。</summary>
    public Thickness TileMargin { get; init; } = new(4, 0, 4, 0);

    /// <summary>运行状态点（D-07）的可见性。调和事件单独刷新它。</summary>
    public Visibility RunningVisibility
    {
        get => _runningVisibility;
        set
        {
            if (_runningVisibility == value)
            {
                return;
            }

            _runningVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));
        }
    }
}
