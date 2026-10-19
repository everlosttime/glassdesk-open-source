using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>
/// Dock 栏窗口。
/// </summary>
/// <remarks>
/// 结构：窗口只做三件事——摆放（<see cref="PositionOnDisplay"/>，主屏物理像素）、
/// 自动隐藏（<see cref="AutoHideController"/> 状态机 + 主屏窗口内的内容滑动）与把服务交给
/// <see cref="DockTileStrip"/>（磁贴、放大、滚动、命中、点击）。
/// 窗口在 <see cref="OnSourceInitialized"/> 里给自己挂上 <c>WS_EX_NOACTIVATE</c>：
/// 点击 Dock 不把用户正在使用的应用顶到后台。代价是 Dock 弹出的东西
/// （右键菜单、文件夹面板、窗口选择列表）也不会随点击取得焦点，需要键盘交互时
/// 必须显式调 <see cref="WindowActivation.TryBringToForeground"/>。
/// </remarks>
public partial class DockWindow : GlassWindow
{
    /// <summary>启动失败提示在状态行上停留的时长。之后恢复成常规诊断文字。</summary>
    private static readonly TimeSpan FailureNoticeDuration = TimeSpan.FromSeconds(5);

    /// <summary>自动隐藏与滑动的公共帧间隔。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private AppServiceHost? _services;
    private DockSettingsHost? _dockSettings;
    private Action<PixelRect?>? _toggleLauncher;
    /// <summary>宿主提供与桌面图标快捷键相同的动作，不依赖快捷键是否启用。</summary>
    public Action? ToggleDesktopIcons { get; set; }
    private System.Windows.Threading.DispatcherTimer? _failureNoticeTimer;
    private readonly DockTileStrip _tileStrip;

    private readonly List<DockTileModel> _models = [];

    // ---- 自动隐藏（D-19） ----

    private AutoHideController? _autoHide;
    private System.Windows.Threading.DispatcherTimer? _frameTimer;
    private PixelRect _restingRect;
    private int _fullyHiddenOffsetPixels;
    private bool _revealLocked;
    private bool _edgeRevealedDuringSuppression;
    private bool _launcherContainerActive;
    private Rect? _slidePanelBoundsDip;
    private bool _backdropWasActive;
    private bool _panelWasConcealed;

    /// <summary>Keep the collapsed destination present until the expanded container has returned.</summary>
    public void SetLauncherContainerState(bool expanded)
    {
        _launcherContainerActive = expanded;
        if (expanded && !_performanceSleepRequested && _restingRect.Width > 0)
        {
            _autoHide?.ForceShown(DateTime.UtcNow);
            _slideOffsetPixels = 0;
            ApplySlide(_restingRect, 0);
        }
    }

    /// <summary>当前滑出量（物理像素）。0 = 完全在位；<see cref="SlideOutDistancePixels"/> = 完全隐藏。</summary>
    private double _slideOffsetPixels;

    internal bool IsOpticalSurfaceVisible => _slideOffsetPixels < Math.Max(1, _restingRect.Height - 4);
    internal PixelRect OpticalBackdropBounds => _restingRect;

    private DisplayInfo? CurrentPrimaryDisplay => PrimaryDisplaySession is { } session
        ? session.IsAvailable ? session.Display : null
        : DisplayService.Primary();

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(_slidePanelBoundsDip ?? bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(_slidePanelBoundsDip ?? bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    public DockWindow()
    {
        InitializeComponent();
        InitializeEntryHover();
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsOpticalSurfaceVisible;

        _tileStrip = new DockTileStrip();
        _tileStrip.TileClicked += OnTileClicked;
        _tileStrip.TileContextMenuRequested += OnTileContextMenuRequested;
        _tileStrip.ReorderFinished += (_, _) => RefreshTiles();
        _tileStrip.UnpinRequested += OnUnpinRequested;
        _tileStrip.SetReorderModel(new DockReorderModel(SaveDockOrder));
        TileStripHost.Content = _tileStrip;

        // 常驻的内置入口不依赖宿主：构造完就该出现在磁贴行上。
        RefreshTiles();

        // Show() may re-apply Topmost from XAML; restore the desktop layer after visibility changes.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(new Action(RestoreDesktopLayer),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>
    /// 排序提交的落盘路径：只在这里写一次（R-08），并且**只写应用库里的真实条目**。
    /// </summary>
    /// <remarks>
    /// 排序模型给过来的列表本应只含应用磁贴，这里再校验一遍：
    /// 去掉内置入口（<c>entry:*</c>）、去掉库里不存在的 id、去重、保持顺序。
    /// 布局文件记的是「用户固定了哪些应用、什么顺序」，混进伪 id 就不再表示这件事了。
    /// </remarks>
    private void SaveDockOrder(IReadOnlyList<string> order)
    {
        if (_services is null)
        {
            return;
        }

        var sanitized = DockLayoutPolicy.Sanitize(order, id => _services.Library.Find(id) is not null);

        _services.Layouts.Dock.PinnedItemIds = sanitized;
        _services.Layouts.SaveDock();
    }

    /// <summary>
    /// 清理布局里不属于应用库的 id；有变化才写盘。
    /// </summary>
    /// <remarks>
    /// 早先的版本把三个内置入口（<c>entry:launcher</c> 等）写进了布局，已在文件里的那些
    /// 要在加载时清掉，同时**保持原有应用顺序**。
    ///
    /// <para>
    /// 安全前提：只有当应用库**确实加载出了条目**时才清洗。
    /// 库为空可能是加载失败，此时所有 id 都「查不到」，照着清洗会把整份布局抹掉。
    /// </para>
    /// </remarks>
    public void SanitizePinnedLayout()
    {
        if (_services is null || _services.Library.Items.Count == 0)
        {
            return;
        }

        var pinned = _services.Layouts.Dock.PinnedItemIds;
        var sanitized = DockLayoutPolicy.Sanitize(pinned, id => _services.Library.Find(id) is not null);

        if (DockLayoutPolicy.SameOrder(pinned, sanitized))
        {
            return;
        }

        var dropped = pinned.Count - sanitized.Count;
        _services.Layouts.Dock.PinnedItemIds = sanitized;
        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            AppendLayoutNotice("清理旧固定项时无法保存；磁盘布局已保留，启动继续。请检查数据目录的写入权限。");
            return;
        }

        AppendLayoutNotice($"布局文件里清理了 {dropped} 个不属于应用库的固定项（内置入口或已失效的条目）。");
    }

    /// <summary>
    /// 拖出取消固定（D-03）：只取消固定，<strong>不删</strong>应用库记录——
    /// 启动台里这一项还在。
    /// </summary>
    /// <remarks>
    /// 内置入口不受这一路径影响：它们不是固定项，也没有拖拽处理器。
    /// 这里再挡一道，防止将来的调用方误把 <c>entry:*</c> 递进来。
    /// </remarks>
    private void OnUnpinRequested(object? sender, string entryId)
    {
        if (_services is null || DockLayoutPolicy.IsEntryTile(entryId))
        {
            return;
        }

        if (!_services.Layouts.UnpinFromDock(entryId))
        {
            return;
        }

        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            ShowFailureNotice("未能保存取消固定操作，原固定项已恢复。");
        }

        RefreshTiles();
    }

    /// <summary>把一条与布局有关的话写进启动日志，便于事后核对自愈行为。</summary>
    private void AppendLayoutNotice(string message)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            _services.Paths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(_services.Paths.LogDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Dock 布局：{message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记日志失败绝不能影响界面。
        }
    }

    /// <summary>悬停时接受可用的启动台应用条目或文件列表。</summary>
    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Effects = _services?.Library.Find(itemId) is { IsUsable: true }
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop, true)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 外部拖入导入（D-01）：资源管理器拖来的文件走导入服务，
    /// 成功的条目顺手固定到 Dock 尾部——否则用户拖完了什么都没看见。
    /// </summary>
    /// <remarks>
    /// 注册归属（修订计划第 5 条）：WPF 对窗口句柄自己注册 OLE 拖放目标
    /// （探针 C 实测证实），这里只用 WPF 的 Drop 事件，绝不再 RegisterDragDrop。
    /// </remarks>
    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Handled = true;
            PinLauncherItem(itemId, e);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop, true)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } sources)
        {
            return;
        }

        // 拖入期间自动隐藏计时被抑制，导入完成后恢复。
        _autoHide?.PushSuppress(DateTime.UtcNow);

        try
        {
            var requests = sources.Select(source => new ImportRequest { Source = source }).ToList();
            var result = _services.Import.ImportItems(requests);

            // 已固定的 id 与「本次新增/已在库」合并——重复条目也要固定：
            // 把一条从 Dock 移出的应用再拖回来时，导入报的是「重复」，
            // 只看 Added 会让这个手势完全没有反应（条目本来就在库里）。
            var pinned = _services.Layouts.Dock.PinnedItemIds;
            var merged = DockPinMerge.Merge(
                pinned,
                result.Added.Select(entry => entry.Id),
                result.Duplicates.Select(entry => entry.Id));

            var newlyPinned = merged.Count - pinned.Count;

            if (newlyPinned > 0)
            {
                _services.Layouts.Dock.PinnedItemIds = merged;
                try
                {
                    _services.Layouts.SaveDock();
                }
                catch (StorageException)
                {
                    ShowFailureNotice("已导入应用库，但未能保存 Dock 固定；原固定顺序已恢复。");
                    RefreshTiles();
                    return;
                }
            }

            ShowTemporaryNotice(result.Failures.Count == 0
                ? $"已固定 {newlyPinned} 项（新增 {result.Added.Count}，已在库 {result.Duplicates.Count}）。"
                : $"已固定 {newlyPinned} 项，{result.Failures.Count} 项失败。");

            RefreshTiles();
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
        }
    }

    private void PinLauncherItem(string itemId, DragEventArgs e)
    {
        var entry = _services?.Library.Find(itemId);
        if (entry is not { IsUsable: true })
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var pinned = _services!.Layouts.Dock.PinnedItemIds;
        var merged = DockPinMerge.Merge(pinned, [entry.Id], []);
        if (merged.Count == pinned.Count)
        {
            ShowTemporaryNotice("此应用已在 Dock 中。");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
            return;
        }

        _services.Layouts.Dock.PinnedItemIds = merged;
        try
        {
            _services.Layouts.SaveDock();
            ShowTemporaryNotice("已添加到 Dock。");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
        }
        catch (StorageException)
        {
            ShowFailureNotice("未能保存 Dock 布局，原有图标顺序已恢复。");
            e.Effects = DragDropEffects.None;
            RefreshTiles();
        }
    }

    /// <summary>生效中的程序坞设置；尚未附加宿主时是默认值。</summary>
    public DockSettings Settings => _dockSettings?.Settings ?? new DockSettings();

    /// <summary>
    /// 当前磁贴总数（固定项 + 三个内置入口）。诊断与测试用。
    /// </summary>
    public int TileCount => _models.Count;

    /// <summary>
    /// 把宿主建好的东西交给 Dock：应用服务入口、程序坞设置通知口与「切换启动台」回调。
    /// </summary>
    /// <remarks>
    /// 结构约束（开工说明 4.7）：Dock 经 <see cref="AppServiceHost"/> 使用应用服务，
    /// 绝不引用宿主工程 <c>GlassDesk.App</c>；设置变更经
    /// <see cref="DockSettingsHost.Changed"/> 推进来，Dock 不轮询设置文件；
    /// 运行状态经 <see cref="RunningAppTracker.Changed"/> 推进来，
    /// Dock 不做任何窗口查询。
    /// </remarks>
    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(toggleLauncher);
        AttachServices(services, settings, _ => toggleLauncher());
    }

    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action<PixelRect?> toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(toggleLauncher);

        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        _services = services;
        _dockSettings = settings;
        _toggleLauncher = toggleLauncher;

        _dockSettings.Changed += OnDockSettingsChanged;
        _services.Running.Changed += OnRunningChanged;

        // 布局里可能残留旧版本写进去的内置入口（entry:*）或已经不存在的 id，
        // 在第一次画磁贴之前清掉。
        SanitizePinnedLayout();

        RebuildAutoHide();
        RefreshTiles();
        RefreshFromSettings();
    }

    /// <summary>Raise the non-activating Dock above Launcher while it is a drag target.</summary>
    public void RaiseAboveLauncherDuringDrag() => _ = WindowStyles.BringToTopWithoutActivation(Handle);

    /// <summary>
    /// Put the Dock and the capture carrier back above the desktop and below normal windows.
    /// </summary>
    public void RestoreDesktopLayer()
    {
        Topmost = false;
        WindowStyles.SendAboveDesktop(Handle);
        var carrier = WindowStyles.FindTopLevelWindow("GlassDesk controlled shared desktop carrier");
        if (carrier != 0) WindowStyles.SendAboveDesktop(carrier);
    }

    /// <summary>Dock 使用栏自身的圆角半径，而不是启动台的。</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius) * DockScale;
    }

    // ---- 磁贴 ----

    /// <summary>
    /// 按布局文档的固定顺序重建磁贴行。
    /// </summary>
    /// <remarks>
    /// 固定顺序来自 <c>dock-layout.json</c> 的稠密有序列表：位置即索引。
    /// 布局里引用了但库里已经没有的 id 直接跳过——那只会发生在数据被
    /// 手工编辑过的时候，正常路径上「从 Dock 移除」会同步更新布局。
    /// </remarks>
    public void RefreshTiles()
    {
        if (_performanceSleepRequested) { _tilesDirty = true; return; }
        var settings = Settings;
        var tracker = _services?.Running;
        var dotBrush = ThemeService.Current?.Palette.ForegroundBrush;

        _models.Clear();

        // 固定项依赖应用服务；未附加宿主时（构造后立刻查询的场合）只少固定项，
        // 内置入口独立于固定项，由 Dock 设置控制可见性。
        if (_services is not null)
        {
            foreach (var id in _services.Layouts.Dock.PinnedItemIds)
            {
                var entry = _services.Library.Find(id);

                if (entry is null || !entry.IsUsable)
                {
                    continue;
                }

                _models.Add(CreateTile(entry, settings, tracker!, dotBrush));
            }
        }

        _tileStrip.SetTiles(_models);
        ApplyStripMetrics();
    }

    private DockTileModel CreateTile(AppEntry entry, DockSettings settings, RunningAppTracker tracker, Brush? dotBrush)
    {
        var tooltip = entry.DisplayName;

        if (entry.IsTargetMissing)
        {
            // 失效条目（R-07）现在先在提示里说清楚；「显示为失效」的视觉
            // 与右键重新定位入口在批次 5 接入。
            tooltip += "（目标已不在原处，可重新定位）";
        }

        return new DockTileModel
        {
            EntryId = entry.Id,
            DisplayName = entry.DisplayName,
            ToolTipText = tooltip,
            Icon = LoadTileIcon(entry),
            DotBrush = dotBrush,
            IconSize = DockSettings.FixedIconSize * DockScale,
            TileMargin = new Thickness(DockSettings.FixedIconSpacing * DockScale / 2, 0, DockSettings.FixedIconSpacing * DockScale / 2, 0),
            IsMissing = entry.IsTargetMissing,
            RunningVisibility = tracker.IsRunning(entry)
                ? Visibility.Visible
                : Visibility.Collapsed,
        };
    }

    /// <summary>
    /// 把尺寸与滚动封顶交给磁贴条。
    /// </summary>
    /// <remarks>
    /// 封顶宽度 = 目标显示器工作区的 DIP 宽度 − 面板内边距、已启用入口与放大余量。
    /// 固定项超出时磁贴条自身横向滚动（修订计划第 6 条），窗口永远不出工作区。
    /// </remarks>
    private void ApplyStripMetrics()
    {
        var settings = Settings;
        var display = CurrentPrimaryDisplay;
        var scale = DockScale;
        DockContentLayer.Margin = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
        if ((LauncherButton.Visibility == Visibility.Visible) != settings.ShowLauncherEntry
            || (ThisPcButton.Visibility == Visibility.Visible) != settings.ShowThisPcEntry
            || (DesktopIconsButton.Visibility == Visibility.Visible) != settings.ShowDesktopIconsEntry)
            ResetEntryHover();
        LauncherButton.Visibility = settings.ShowLauncherEntry ? Visibility.Visible : Visibility.Collapsed;
        ThisPcButton.Visibility = settings.ShowThisPcEntry ? Visibility.Visible : Visibility.Collapsed;
        DesktopIconsButton.Visibility = settings.ShowDesktopIconsEntry ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { LauncherButton, ThisPcButton, DesktopIconsButton })
        {
            button.Width = 48 * scale;
            button.Height = 64 * scale;
            button.Margin = new Thickness(0, 0, 8 * scale, 0);
        }
        LaunchpadTile.Width = LaunchpadTile.Height = ThisPcTile.Width = ThisPcTile.Height
            = DesktopIconsTile.Width = DesktopIconsTile.Height = 48 * scale;
        LaunchpadTile.CornerRadius = new CornerRadius(16 * scale);
        PanelCornerRadius = new CornerRadius(40 * scale);

        var contentLimit = display is null
            ? 0.0
            : Math.Max(1, display.ToDips((int)display.WorkArea.Width) - 32 - (28 + BuiltinEntryWidthDip) * scale);

        if (settings.FrameLengthDip > 0)
            contentLimit = Math.Max(1, ResolveEffectiveFrameLengthDip(settings.FrameLengthDip, display) - (22 + BuiltinEntryWidthDip) * scale);

        _tileStrip.SetMetrics(
            DockSettings.FixedIconSize * scale,
            DockSettings.FixedIconSpacing * scale,
            DockSettings.FixedMagnificationPeak,
            8 * scale,
            contentLimit);
    }

    private double DockScale => double.IsFinite(Settings.SizeScale)
        ? Math.Clamp(Settings.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;

    private double BuiltinEntryWidthDip => 56 * ((Settings.ShowLauncherEntry ? 1 : 0)
        + (Settings.ShowThisPcEntry ? 1 : 0) + (Settings.ShowDesktopIconsEntry ? 1 : 0));

    private double ResolveEffectiveFrameLengthDip(double requested, DisplayInfo? display)
    {
        var entryMinimum = Math.Max(DockSettings.MinimumFrameLengthDip, BuiltinEntryWidthDip + 22) * DockScale;
        if (display is null)
            return Math.Clamp(requested, entryMinimum, DockSettings.MaximumFrameLengthDip);
        var maxWidth = Math.Max(64, display.ToDips((int)display.WorkArea.Width) - 32);
        maxWidth = Math.Min(DockSettings.MaximumFrameLengthDip, maxWidth);
        var minimum = Math.Min(entryMinimum, maxWidth);
        return Math.Clamp(requested, minimum, maxWidth);
    }

    private void ApplyFrameLength()
    {
        var requested = Settings.FrameLengthDip;
        if (requested <= 0)
        {
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
            Width = double.NaN;
            return;
        }

        var display = CurrentPrimaryDisplay;
        SizeToContent = System.Windows.SizeToContent.Height;
        Width = ResolveEffectiveFrameLengthDip(requested, display);
    }

    /// <summary>取一枚磁贴的图标：用户自选的优先，其次缓存里提取的，没有就空着。</summary>
    private BitmapImage? LoadTileIcon(AppEntry entry)
    {
        var custom = entry.CustomIconPath;

        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return LoadBitmap(custom);
        }

        if (_services is null || string.IsNullOrEmpty(entry.IconSource))
        {
            return null;
        }

        var cached = _services.Icons.PathFor(entry.IconSource);
        if (!File.Exists(cached))
        {
            cached = _services.Icons.EnsureFor(entry.IconSource)?.Path ?? cached;
        }

        return File.Exists(cached) ? LoadBitmap(cached) : null;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            // OnLoad：把像素读进内存就释放文件句柄，否则用户在资源管理器里
            // 删不掉正在被 Dock 显示的那张图标。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    /// <summary>磁贴点击（D-08）：内置入口分派；文件夹条目弹面板（D-11）；其余先激活后启动。</summary>
    private void OnTileClicked(object? sender, string entryId)
    {
        if (entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            RunEntryAction(entryId["entry:".Length..]);
            return;
        }

        if (_services is null)
        {
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is not null && entry.Kind == AppItemKind.Folder && !entry.IsTargetMissing)
        {
            ShowFolderPanel(entry);
            return;
        }

        var outcome = _services.Launch.ActivateOrLaunch(entryId);

        switch (outcome.Outcome)
        {
            case ActivationOutcome.MultipleWindowsFound:
                ShowWindowChooser(outcome.Candidates);
                break;

            case ActivationOutcome.Failed:
                ShowFailureNotice(outcome.Message);
                break;

            default:
                // Activated / Launched 不需要额外反馈：窗口到前台或应用启动本身就是反馈。
                break;
        }
    }

    /// <summary>内置入口的动作分派。</summary>
    private void RunEntryAction(string name)
    {
        switch (name)
        {
            case "launcher":
                // D-12：入口磁贴触发宿主切换回调（Dock 不直接持有启动台）。
                RequestLauncherToggle();
                break;

            case "showdesktop":
                // D-13：实现依据探针 D 的实测结论（逐窗口 SW_MINIMIZE）。
                _ = DesktopCommands.ShowDesktop();
                break;

            case "recyclebin":
                // D-14：回收站是内置入口，直接走外壳；绝不为塞进应用库而
                // 放宽导入的存在性检查（阶段 4 开工说明 4.6 的明确告诫）。
                var opened = ShellExecutor.Open("shell:RecycleBinFolder", null, null);
                if (!opened.Started)
                {
                    ShowFailureNotice("回收站没能打开。");
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 文件夹弹出面板（D-11）：列出文件夹里的条目，点击经外壳打开。
    /// </summary>
    /// <remarks>
    /// 面板打开时取得键盘焦点，支持 Esc；关闭时只在焦点仍属于 Dock 的情况下
    /// 归还原窗口，不抢回用户已切换到的其他应用。面板期间抑制自动隐藏。
    /// 面板里的点击走同一条外壳路径（<see cref="ShellExecutor"/>）。
    /// </remarks>
    private void ShowFolderPanel(AppEntry folderEntry)
    {
        _autoHide?.PushSuppress(DateTime.UtcNow);
        var foregroundBefore = WindowControl.GetForegroundWindow();

        var palette = ThemeService.Current?.Palette;

        var header = new TextBlock
        {
            Text = folderEntry.DisplayName,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(6, 4, 6, 2),
        };

        var list = new StackPanel();

        _ = list.Children.Add(header);
        _ = list.Children.Add(PanelItem("打开这个文件夹", () =>
        {
            var open = ShellExecutor.Open(folderEntry.Target, null, null);
            if (!open.Started)
            {
                ShowFailureNotice("文件夹没能打开。");
            }
        }));

        string[] children;

        try
        {
            children = Directory
                .EnumerateFileSystemEntries(folderEntry.Target)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                .Take(50)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            children = [];
        }

        if (children.Length == 0)
        {
            _ = list.Children.Add(new TextBlock
            {
                Text = "（空文件夹，或读不到内容）",
                Margin = new Thickness(10, 4, 10, 6),
            });
        }

        foreach (var child in children)
        {
            var path = child;
            _ = list.Children.Add(PanelItem(Path.GetFileName(path), () =>
            {
                var open = ShellExecutor.Open(path, null, null);
                if (!open.Started)
                {
                    ShowFailureNotice($"没能打开：{Path.GetFileName(path)}");
                }
            }));
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 220,
            MaxHeight = 420,
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        panel.Focusable = true;
        panel.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape) return;
            popup.IsOpen = false;
            args.Handled = true;
        };
        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, 0);
        };
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = panel.Focus();
    }

    private static TextBlock PanelItem(string text, Action onClick)
    {
        var item = new TextBlock
        {
            Text = text,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        item.MouseLeftButtonUp += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 磁贴右键菜单（D-04）：菜单项与实际行为一致，不放占位项。
    /// </summary>
    /// <remarks>
    /// 主栏带 <c>WS_EX_NOACTIVATE</c>，不激活就点不到菜单外部——菜单自动收起
    /// 需要「点击外部时收起」的语义，所以打开前显式把 Dock 调到前台。
    /// 关闭后延后检查前台，仅在仍属于本弹层/主栏时归还原窗口。
    /// 失效条目（R-07）在这里给出「重新定位」入口。
    /// </remarks>
    private void OnTileContextMenuRequested(object? sender, string entryId)
    {
        if (_services is null)
        {
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is null)
        {
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
        };
        var foregroundBefore = WindowControl.GetForegroundWindow();
        nint menuHandle = 0;
        menu.Opened += (_, _) => menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;

        _ = menu.Items.Add(MenuItem("打开", () => OnTileClicked(this, entryId)));

        if (entry.Kind == AppItemKind.Folder)
        {
            _ = menu.Items.Add(MenuItem("在面板中查看内容", () => ShowFolderPanel(entry)));
        }

        _ = menu.Items.Add(new Separator());

        if (entry.IsTargetMissing)
        {
            // R-07 的界面入口：目标被删之后用户从这里把条目指到新位置。
            _ = menu.Items.Add(MenuItem("重新定位…", () => RelocateFromDialog(entry)));
        }

        _ = menu.Items.Add(MenuItem("从 Dock 移除", () => OnUnpinRequested(this, entryId)));

        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    /// <summary>重新定位对话框：选一个真实存在的新目标，交给维护服务。</summary>
    private void RelocateFromDialog(AppEntry entry)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"重新定位「{entry.DisplayName}」",
            CheckFileExists = false,
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var outcome = _services!.Maintenance.Relocate(entry.Id, dialog.FileName);

        if (outcome.Result != RelocateResult.Relocated)
        {
            ShowFailureNotice(outcome.Message);
        }

        RefreshTiles();
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 多窗口选择列表（D-10）：列出候选窗口的标题，点哪个激活哪个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 交互（修订 4 的焦点规则在这里落地）：
    /// <list type="bullet">
    /// <item>点一行 → 激活那一行的窗口；<b>句柄要从所属列表项取</b>，
    /// 不能只读点击来源的 Tag（来源往往是里面的文字，自己没有 Tag，
    /// 于是点了没反应也不报错——见 <see cref="WindowChooserHit"/>）；</item>
    /// <item>Enter → 激活选中行；Esc → 关闭（等同取消）；</item>
    /// <item>主栏带 <c>WS_EX_NOACTIVATE</c>，弹层要拿键盘焦点必须显式把自己调到前台；
    /// 因此打开时记下当时的前台窗口，<b>取消（Esc / 点外部）时还回去</b>，
    /// 而选中并激活成功时焦点自然留在目标应用上。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ShowWindowChooser(IReadOnlyList<WindowCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);

        // 打开之前的前台窗口：取消时还给它，避免「关掉列表后焦点不知所踪」。
        var foregroundBefore = WindowControl.GetForegroundWindow();
        var activated = false;

        var palette = ThemeService.Current?.Palette;

        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var title = string.IsNullOrWhiteSpace(candidate.Title)
                ? $"窗口 {index + 1}"
                : candidate.Title;

            var item = new ListBoxItem
            {
                Content = title,
                Padding = new Thickness(10, 5, 10, 5),
                Tag = candidate.Handle,
            };

            list.Items.Add(item);
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 160,
        };

        panel.Child = list;

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        // 选中并激活某一行的窗口。返回是否真的激活成功（用于决定要不要还焦点）。
        bool Activate(nint handle)
        {
            popup.IsOpen = false;

            var outcome = LaunchService.ActivateWindow(handle);

            if (outcome.Outcome == ActivationOutcome.Activated)
            {
                activated = true;
                return true;
            }

            // 窗口已经关掉、或系统拒绝前置：不能静默，状态行上要说清楚。
            ShowFailureNotice(outcome.Message ?? "没能把那个窗口切到前台。");
            return false;
        }

        list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var handle = WindowChooserHit.Resolve(list, args.OriginalSource);

            if (handle != 0)
            {
                _ = Activate(handle);
            }
        };

        list.KeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case System.Windows.Input.Key.Enter:
                    if (list.SelectedItem is ListBoxItem { Tag: nint selected } && selected != 0)
                    {
                        _ = Activate(selected);
                    }

                    args.Handled = true;
                    break;

                case System.Windows.Input.Key.Escape:
                    popup.IsOpen = false;
                    args.Handled = true;
                    break;

                default:
                    break;
            }
        };

        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);

            // 取消（没激活任何窗口）时把前台还给打开前的那个窗口；
            // 激活成功时焦点已经属于目标应用，不要再抢回来。
            if (!activated)
            {
                RestorePopupForeground(foregroundBefore, 0);
            }
        };

        // 主栏不激活，弹层要收键盘就得显式取前台；随后把焦点交给列表。
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = list.Focus();
    }

    // Closed can run before the menu action or window activation completes. Defer the
    // decision, and restore only if focus is still ours. Clicking another app must win.
    private void RestorePopupForeground(nint previous, nint popupHandle)
    {
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!WindowControl.IsAlive(previous)) return;
            var current = WindowControl.GetForegroundWindow();
            if (current == Handle || current == 0 || (popupHandle != 0 && current == popupHandle))
                _ = WindowActivation.TryBringToForeground(previous);
        }));
    }

    /// <summary>启动失败不能没有声音地过去：在状态行上亮出原因，几秒后自己恢复。</summary>
    private void ShowFailureNotice(string? message)
    {
        ShowTemporaryNotice(message);
    }

    private void ShowTemporaryNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;

        _failureNoticeTimer ??= NewFailureNoticeTimer();
        _failureNoticeTimer.Stop();
        _failureNoticeTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer NewFailureNoticeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = FailureNoticeDuration,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshDiagnostics();
            RefreshFromSettings();
        };

        return timer;
    }

    // ---- 运行状态点（D-07） ----

    private void OnRunningChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 调和在线程池上触发；圆点更新回到界面线程。只改可见性，不重建磁贴。
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_services is null || _performanceSleepRequested)
            {
                return;
            }

            foreach (var tile in _models)
            {
                var entry = _services.Library.Find(tile.EntryId);

                tile.RunningVisibility = entry is not null && _services.Running.IsRunning(entry)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        });
    }

    // ---- 通知与刷新 ----

    /// <summary>用当前的主题与材质状态更新说明文字。</summary>
    public void RefreshDiagnostics()
    {
        var theme = ThemeService.Current;

        var surface = SurfaceMode == GlassSurfaceMode.Acrylic
            ? "亚克力背景模糊"
            : "自绘半透明面板（无背景模糊）";

        StatusText.Text = $"{surface}｜{(theme?.IsDark == true ? "深色" : "浅色")}";
        StatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 按生效中的程序坞设置刷新说明文字（D-16 的可见落点之一）。
    /// </summary>
    public void RefreshFromSettings()
    {
        StatusText.Visibility = Visibility.Collapsed;
        ApplyFrameLength();
        ApplyStripMetrics();
    }

    private void OnDockSettingsChanged(object? sender, EventArgs e)
    {
        RebuildAutoHide();
        RefreshFromSettings();
        RefreshTiles();

        // 位置与偏移也属于即时生效（D-16）：设置一变，已打开的 Dock 当场搬家。
        _slideOffsetPixels = 0;
        PositionOnDisplay();
    }

    /// <summary>请求宿主切换启动台；按钮点击附带按钮当前的物理屏幕矩形。</summary>
    private void RequestLauncherToggle(PixelRect? sourceBounds = null) => _toggleLauncher?.Invoke(sourceBounds);

    private void OnLauncherButtonClick(object sender, RoutedEventArgs e) => RequestLauncherToggle(GetLauncherButtonPixelBounds());

    /// <summary>Read the launcher button's current physical screen rectangle after Dock layout.</summary>
    public PixelRect? GetLauncherButtonPixelBounds()
    {
        if (!Settings.ShowLauncherEntry || LauncherButton.Visibility != Visibility.Visible
            || !IsVisible || !IsOpticalSurfaceVisible
            || LauncherButton.ActualWidth < 1 || LauncherButton.ActualHeight < 1) return null;

        try
        {
            var topLeft = LaunchpadTile.PointToScreen(new Point());
            var bottomRight = LaunchpadTile.PointToScreen(new Point(LaunchpadTile.ActualWidth, LaunchpadTile.ActualHeight));
            return new PixelRect((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
                Math.Max(1, (int)Math.Ceiling(bottomRight.X - topLeft.X)),
                Math.Max(1, (int)Math.Ceiling(bottomRight.Y - topLeft.Y)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnThisPcButtonClick(object sender, RoutedEventArgs e)
    {
        var opened = ShellExecutor.Open("shell:MyComputerFolder", null, null);
        if (!opened.Started)
        {
            ShowFailureNotice("此电脑没能打开。");
        }
    }

    private void OnDesktopIconsButtonClick(object sender, RoutedEventArgs e) => ToggleDesktopIcons?.Invoke();

    // ---- 摆放 ----

    /// <summary>
    /// 按生效中的设置把 Dock 摆到启动时绑定的主显示器（D-15/D-23）。
    /// </summary>
    /// <remarks>
    /// 摆放走<strong>启动时 Primary Monitor 的物理像素和 DPI</strong>；旧配置中的
    /// <c>TargetDisplayId</c> 只保留数据兼容，不参与生产窗口选择。矩形交给
    /// <see cref="DisplayService.MoveWindowToPixelRect"/>。
    /// 几何在 <see cref="DockPlacementCalculator"/>（纯函数，可离线单测）。
    /// </remarks>
    public bool SuppressPassiveTriggersWhenFullscreen { get; set; } = true;
    public void RefreshDisplayPlacement()
    {
        ApplyFrameLength();
        ApplyStripMetrics();
        PositionOnDisplay();
    }

    public void StopForPrimaryDisplayChange()
    {
        _frameTimer?.Stop();
        Hide();
    }

    public void ResumeAfterPrimaryDisplayChange(bool show)
    {
        RebuildAutoHide();
        RefreshDisplayPlacement();
        if (show && !_performanceSleepRequested) Show();
    }

    public void PositionOnDisplay()
    {
        var handle = Handle;

        if (handle == 0)
        {
            return;
        }

        var display = CurrentPrimaryDisplay;
        if (display is null)
        {
            return;
        }

        // 用了 SizeToContent，最终尺寸要等布局跑完才知道。
        var widthDips = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var heightDips = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        if (widthDips <= 0 || heightDips <= 0)
        {
            return;
        }

        // 极端缩放下取整可能得 0；窗口至少要有 1 物理像素。
        var widthPixels = Math.Max(1, display.ToPixels(widthDips));
        var heightPixels = Math.Max(1, display.ToPixels(heightDips));

        _restingRect = DockPlacementCalculator.Calculate(
            display.WorkArea,
            Settings.Placement,
            Settings.OffsetX,
            Settings.OffsetY,
            widthPixels,
            heightPixels,
            display.Dpi);

        if (PrimaryDisplaySession is { IsAvailable: true } session)
            _restingRect = session.ClampWindowToWorkArea(_restingRect);

        _fullyHiddenOffsetPixels = SlideOutDistancePixels(_restingRect, display.Bounds, Settings.Placement);

        ApplySlide(_restingRect, _slideOffsetPixels);
    }

    /// <summary>保持 HWND 在主屏工作区内，只在窗口剪裁范围内滑动内容与共享面板几何。</summary>
    private void ApplySlide(PixelRect restingRect, double slideOffsetPixels)
    {
        var slidePixels = (int)Math.Round(slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var concealed = restingRect.Height > 0 && Math.Abs(slidePixels) >= restingRect.Height;
        // HWND equality describes layout, not the pixels painted while hidden.
        // Retire old glass only once the entire panel is clipped, so every reveal
        // prepares current content without disturbing the visible hide animation.
        if (concealed && !_panelWasConcealed) GlassSurface.RetireConcealedMaterial();
        _panelWasConcealed = concealed;
        var backdropActive = IsOpticalSurfaceVisible;
        if (backdropActive && !_backdropWasActive) GlassSurface.RequestBackdropRefresh(revealing: true);
        _backdropWasActive = backdropActive;
        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable) return;
            var fixedWindow = session.ClampWindowToWorkArea(restingRect);
            if (DisplayService.GetWindowPixelBounds(Handle) != fixedWindow)
                _ = DisplayService.MoveWindowToPixelRect(Handle, fixedWindow);
            var scale = Math.Max(.01, session.Display.ScaleFactor);
            var visualOffsetDip = slidePixels / scale;
            DockSlideTransform.Y = visualOffsetDip;
            _slidePanelBoundsDip = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                // Match the launcher: a fixed desktop texture with a moving GPU lens.
                // Only Content translates. New capture frames keep the same coordinates
                // throughout the slide and therefore remain consumable during motion.
                var hiddenOffsetDip = _fullyHiddenOffsetPixels * SlideOutDirection() / scale;
                GlassSurface.SetPanelShape(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
                GlassSurface.SetContainerTransitionBounds(
                    new Rect(0, Math.Min(0, hiddenOffsetDip), ActualWidth,
                        ActualHeight + Math.Abs(hiddenOffsetDip)));
                SetSharedPanelClip(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
            }
            var shadowStrength = Math.Clamp(1 - Math.Abs(visualOffsetDip) / Math.Max(1, ActualHeight), 0, 1);
            SetTransitionShadowShape(Math.Abs(visualOffsetDip) < .01
                    ? null
                    : _slidePanelBoundsDip,
                PanelCornerRadius.TopLeft, shadowStrength);
            return;
        }

        // Unbound design/test windows keep their legacy placement behavior. AppHost binds
        // every production GlassWindow to one immutable PrimaryDisplaySession.
        DockSlideTransform.Y = 0;
        SetTransitionShadowShape(null, 0);
        var rect = restingRect with { Y = restingRect.Y + slidePixels };
        _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
    }

    /// <summary>内容滑出方向：底部 Dock 向下 +1，顶部 Dock 向上 −1。</summary>
    private int SlideOutDirection()
        => Settings.Placement == DockPlacement.TopCenter ? -1 : 1;

    /// <summary>内容完全隐藏所需的物理像素偏移，窗口 HWND 本身始终留在主屏内。</summary>
    private static int SlideOutDistancePixels(PixelRect restingRect, PixelRect displayBounds, DockPlacement placement)
    {
        var distanceToDisplayEdge = placement == DockPlacement.TopCenter
            ? restingRect.Bottom - displayBounds.Y
            : displayBounds.Bottom - restingRect.Y;

        // Dock 靠工作区边缘停靠，工作区与显示器边缘之间可能有任务栏；再多移两像素，
        // 避免物理像素取整或窗口表面边界留下细线。
        return Math.Max(0, distanceToDisplayEdge) + 2;
    }

    // ---- 自动隐藏（D-19） ----

    /// <summary>按当前设置重建状态机。设置可随时改，状态机不可变所以整体换。</summary>
    private void RebuildAutoHide()
    {
        var settings = Settings;

        _autoHide = new AutoHideController(settings.AutoHideLeaveDelayMs);

        _autoHide.ForceShown(DateTime.UtcNow);
        _slideOffsetPixels = 0;

        if (_frameTimer is null)
        {
            // Capture completion/icon work can keep Normal queued while the panel
            // is concealed. Poll/slide admission must still advance to its first
            // visible pixel; settled idle remains 100 ms and game sleep stops it.
            _frameTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            {
                Interval = FrameInterval,
            };
            _frameTimer.Tick += (_, _) => OnAutoHideFrame();
        }

        _frameTimer.IsEnabled = !_performanceSleepRequested;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// 公共帧：探测指针 → 状态机 → 滑动插值。
    /// </summary>
    /// <remarks>
    /// 全部判定用<strong>缓存的静止矩形</strong>与显示枚举，不碰 WPF 可视树；
    /// 16 ms 一次的成本只有一次光标查询与少量算术。
    /// </remarks>
    private void OnAutoHideFrame()
    {
        if (_performanceSleepRequested) { AdvancePerformanceSleep(); return; }
        if (_autoHide is null || _services is null || _restingRect.Width == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var suppressed = fullscreen || covered;
        var pointerOverDock = cursor is { } currentPoint
            && Inflate(GetVisibleDockRect(), 2).Contains(currentPoint.X, currentPoint.Y);

        var pointerOverDockPosition = cursor is { } position
            && Inflate(_restingRect, 2).Contains(position.X, position.Y);
        UpdateAutoHideState(now, fullscreen, covered, pointerOverDockPosition, pointerOverDock);

        // 滑动插值：向目标滑出量靠近。
        var target = _autoHide.State == AutoHideState.Hidden
            ? (double)_fullyHiddenOffsetPixels
            : 0.0;

        // Admit the reveal intent before preparation starts the native owner.
        // Waiting for optical visibility lets a foreground-policy tick put that
        // new owner back to sleep while the panel is still crossing the edge.
        NotifyPerformanceSurfaceVisibility();

        // Prepare a complete material before the first pixels slide back into view.
        if (target == 0 && _backgroundCaptureSleeping) SetBackgroundCaptureSleeping(false);
        else if (target == 0) PrepareConcealedReveal();

        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect,target);
            NotifyPerformanceSurfaceVisibility();
            return;
        }
        if (Math.Abs(_slideOffsetPixels - target) > 0.5)
        {
            _slideOffsetPixels += (target - _slideOffsetPixels) * 0.25;

            if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
            {
                _slideOffsetPixels = target;
            }

            ApplySlide(_restingRect, _slideOffsetPixels);
        }
        else if (_slideOffsetPixels != target)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }

        if (_frameTimer is not null)
            _frameTimer.Interval = !_backgroundCaptureSleeping && (suppressed || _autoHide.Suppressed)
                || Math.Abs(_slideOffsetPixels - target) > 0.5
                ? FrameInterval
                : TimeSpan.FromMilliseconds(100);
        NotifyPerformanceSurfaceVisibility();
    }

    private void UpdateAutoHideState(DateTime now, bool fullscreen, bool covered,
        bool pointerOverDockPosition, bool pointerOverVisibleDock)
    {
        if (_autoHide is null) return;
        if (_launcherContainerActive)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (fullscreen || covered)
        {
            if (_revealLocked)
            {
                _edgeRevealedDuringSuppression = false;
                _autoHide.ForceHide(now);
            }
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverDockPosition, _revealLocked)
                    && _autoHide.TryReveal(false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverVisibleDock) _autoHide.PointerEnter(now);
                else _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
            else
                _autoHide.ForceHide(now);
        }
        else
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }

        if (_autoHide.State == AutoHideState.Hidden)
            _edgeRevealedDuringSuppression = false;
    }

    private PixelRect GetVisibleDockRect()
    {
        var slidePixels = (int)Math.Round(_slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var translated = _restingRect with { Y = _restingRect.Y + slidePixels };
        if (PrimaryDisplaySession is null) return translated;
        var left = Math.Max(_restingRect.X, translated.X);
        var top = Math.Max(_restingRect.Y, translated.Y);
        var right = Math.Min(_restingRect.Right, translated.Right);
        var bottom = Math.Min(_restingRect.Bottom, translated.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(_restingRect.X, _restingRect.Y, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>切换本次运行期间的遮挡唤出锁；不影响未被覆盖的 Dock。</summary>
    public void ToggleRevealLock()
    {
        _revealLocked = !_revealLocked;
        if (_performanceSleepRequested) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            _frameTimer.IsEnabled = true;
        }
        OnAutoHideFrame();
    }

    private static PixelRect Inflate(PixelRect rect, int by)
        => new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnDockWindowMessage);
        }

        var restoreTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        restoreTimer.Tick += (_, _) =>
        {
            restoreTimer.Stop();
            RestoreDesktopLayer();
        };
        restoreTimer.Start();

        RefreshDiagnostics();

        // 必须在句柄建立之后改扩展样式：点击本窗口时不得把前台让给别人。
        // 弹出物要取焦点时显式调 WindowActivation.TryBringToForeground。
        WindowStyles.MakeNoActivate(Handle);
    }

    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int WmSize = 0x0005;
    private const int SizeMinimized = 1;

    private nint OnDockWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        var minimizeRequested = message == WmSysCommand
            && (int)((long)wParam & 0xFFF0) == ScMinimize;
        var minimized = message == WmSize && (int)wParam == SizeMinimized;

        if (minimizeRequested || minimized)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(RestoreDockAfterShowDesktop),
                System.Windows.Threading.DispatcherPriority.Send);
        }

        return 0;
    }

    private void RestoreDockAfterShowDesktop()
    {
        if (_performanceSleeping || _performanceSleepRequested)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (WindowControl.IsMinimized(Handle))
        {
            _ = WindowControl.Restore(Handle);
        }

        if (!IsVisible)
        {
            Show();
        }

        WindowStyles.MakeNoActivate(Handle);
        RestoreDesktopLayer();
    }

    protected override void OnClosed(EventArgs e)
    {
        _frameTimer?.Stop();
        _failureNoticeTimer?.Stop();
        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        base.OnClosed(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // 窗口尺寸（内容）变化后按设置重新落位；SizeToContent 的首次布局也会走到这里。
        PositionOnDisplay();
    }
}
