using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>一枚磁贴在最后一次绘制帧里的<strong>实际</strong>绘制状态。</summary>
/// <param name="EntryId">条目 id。</param>
/// <param name="Scale">实际施加的缩放。</param>
/// <param name="ZIndex">实际的 <c>Panel.ZIndex</c>——越大越靠上，就是用户看到的覆盖关系。</param>
/// <param name="Left">实际的画布左缘。</param>
public readonly record struct PaintedTileState(string EntryId, double Scale, int ZIndex, double Left);

/// <summary>
/// Dock 的磁贴条：固定槽位布局、放大动画、拖拽排序与溢出滚动都住在这里。
/// </summary>
/// <remarks>
/// <para>
/// <b>几何唯一来源。</b>每一帧由 <see cref="DockMagnification.Compute"/> 算出
/// 所有磁贴的位置与缩放，绘制（Canvas 定位 + 缩放变换）与命中
/// （<see cref="DockMagnification.HitTest"/>）用<strong>同一份</strong>结果；
/// 点击目标不取 WPF 的事件 sender，而是用 HitTest 的判定——
/// 这从结构上保证了「看见 A 就点中 A」，动画中途也一样（D-05）。
/// </para>
///
/// <para>
/// <b>拖拽排序（D-02/R-08）。</b>磁贴拖拽用 WPF 自己的拖放（同窗口内），
/// 手势只翻译成 <see cref="DockReorderModel"/> 的三个动作：
/// 预览只改内存顺序（插入指示线），松手提交时由状态机统一落盘<strong>一次</strong>，
/// Esc 或拖出栏外取消时绝不落盘。落盘次数可断言的性质来自状态机，
/// 不靠界面层自觉。
/// </para>
///
/// <para>
/// <b>溢出（修订计划第 6 条）。</b>固定项的总宽度封顶于可用宽度
/// （目标屏工作区减去放大余量与内边距），超出部分沿 Dock 方向滚动
/// （当前四种停靠位置全部横排，所以是横向滚），滚轮支持，滚动条隐藏，
/// 两端用渐变提示还有内容。磁贴尺寸永不整体缩小。
/// </para>
///
/// <para>
/// 命中与指针坐标一律换算到<strong>内容坐标系</strong>（滚动偏移 + 视口内位置），
/// 滚动之后点击依然正确。
/// </para>
/// </remarks>
public sealed class DockTileStrip : Border
{
    /// <summary>内部拖拽数据格式：本进程磁贴排序拖放。</summary>
    private const string TileDragFormat = "GlassDesk-TileDrag";

    /// <summary>拖出这个距离（DIP，沿轴）就视为「拖出取消固定」（D-03）。</summary>
    private const double UnpinDragDistance = 80;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _canvas;
    private readonly Border _leadingFade;
    private readonly Border _trailingFade;

    private readonly List<DockTileModel> _models = [];
    private readonly List<Border> _tiles = [];
    private readonly List<RadialGradientBrush> _pressGlows = [];

    private readonly DockRenderLoop _animationLoop;
    internal bool IsForegroundAnimationActive=>_animationLoop.IsActive;
    internal void UseForegroundFrames(Action? request)=>_animationLoop.UseExternalFrames(request);
    internal void AdvanceForegroundFrame()=>_animationLoop.AdvanceExternalFrame();
    private bool _pointerActive;
    private double _pointerPosition = double.NaN;
    private int _hoveredTileIndex = -1;
    private int _pressedTileIndex = -1;
    private double[] _pressScale = [];
    private double[] _pressVelocity = [];
    private double[] _pressGlow = [];

    private bool _vertical;
    private double Along(Point point) => _vertical ? point.Y : point.X;

    private double _iconSize = 48;
    private double _spacing = 8;
    private double _peak = 1.15;
    private double _verticalPadding = 8;

    /// <summary>可用宽度上限，DIP。0 表示不限（由外层决定）。</summary>
    private double _maxContentWidth;

    /// <summary>磁贴被点击。参数是应用库条目 id。</summary>
    public event EventHandler<string>? TileClicked;

    /// <summary>磁贴被右键（D-04）。参数是应用库条目 id；内置入口不触发。</summary>
    public event EventHandler<string>? TileContextMenuRequested;

    /// <summary>拖拽提交完成（顺序已落盘）或取消，宿主应重建磁贴行。</summary>
    public event EventHandler? ReorderFinished;

    /// <summary>磁贴被拖出栏外（D-03）：请求取消固定，不删除库条目。</summary>
    public event EventHandler<string>? UnpinRequested;

    /// <summary>排序状态机由宿主注入：落盘委托在宿主手里（经布局存储保存一次）。</summary>
    private DockReorderModel? _reorder;
    private string? _pressedEntryId;
    private Point _pressPosition;
    private bool _dragActive;
    internal bool IsDragging => _dragActive;
    private int _dragStartIndex = -1;

    // ---- 长按拖拽（长按 350ms 后图标跟随鼠标，约束在 Dock 栏内） ----
    private System.Windows.Threading.DispatcherTimer? _pressTimer;
    private Border? _pressTile;
    private Point _pressGrabPoint;
    private bool _longPressDrag;
    private Border? _dragGhost;
    private TranslateTransform? _dragGhostMotion;
    private string? _dragGhostEntryId;
    private readonly Canvas _dragOverlay;

    /// <summary>幽灵实际所在的画布；宿主要求时是窗口级画布，否则退回本条自己的覆盖层。</summary>
    private Canvas? _ghostLayer;

    /// <summary>按下时鼠标在幽灵内部的偏移，跟手时用它抵消，图标才不会跳到指针左上角。</summary>
    private Point _ghostGrab;

    /// <summary>
    /// 长按拖拽时幽灵所在的画布。宿主设成窗口级画布，图标才能沿整条 Dock 跟随鼠标；
    /// 不设则退回本条内部的覆盖层（只在本条范围内移动）。
    /// </summary>
    internal Canvas? DragOverlay { get; set; }

    /// <summary>栏内是否收到过本次拖拽的 Drop（顺序已在那一刻提交）。</summary>
    private bool _droppedInStrip;

    /// <summary>本次拖拽的取消原因；没取消过为 <see cref="DragCancelReason.None"/>。</summary>
    private DragCancelReason _dragCancelReason;

    /// <summary>最近一次拖拽的取消原因。诊断与测试用。</summary>
    public DragCancelReason LastDragCancelReason => _dragCancelReason;

    /// <summary>
    /// 最近一次<strong>真正画到屏幕上</strong>的那一帧几何。
    /// </summary>
    /// <remarks>
    /// 命中的唯一依据（视觉即所得）：绘制用缓动后的指针位置，而点击发生在任意时刻，
    /// 若在点击时用即时指针重算一份几何，动画中途就会与屏幕上的画面分叉——
    /// 实测出现「已绘制几何命中第一枚、点击处理命中第二枚」。
    /// </remarks>
    private TileGeometry[] _paintedGeometry = [];
    private TileGeometry[] _targetGeometry = [];
    private int[] _paintOrder = [];
    private readonly Comparer<int> _paintOrderComparer;

    /// <summary>注入排序状态机并启用内部拖拽。</summary>
    public void SetReorderModel(DockReorderModel? reorder)
    {
        _reorder = reorder;
        AllowDrop = reorder is not null;
    }

    /// <summary>
    /// 参与排序的条目 id（<strong>只有应用库里的真实条目</strong>）。
    /// </summary>
    /// <remarks>
    /// 内置入口（启动台、显示桌面、回收站）固定在独立区域：不参与排序，
    /// 也不能被拖出移除。排序提交时写进布局的就是这个列表。
    /// </remarks>
    public IReadOnlyList<string> ReorderCandidateIds =>
        _models.Take(AppTileCount).Select(model => model.EntryId).ToList();

    /// <summary>应用磁贴的数量（磁贴行里前缀部分；其余是内置入口）。</summary>
    private int AppTileCount { get; set; }

    /// <summary>应用磁贴与内置入口之间的分隔线（视觉上把两组分开）。</summary>
    private readonly System.Windows.Shapes.Rectangle _groupDivider = new()
    {
        Width = 1,
        Fill = Brushes.Gray,
        Opacity = 0.5,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    public DockTileStrip()
    {
        _paintOrderComparer = Comparer<int>.Create((left, right) =>
            DockMagnification.CompareDrawOrder(_paintedGeometry, left, right));
        Background = Brushes.Transparent;
        ClipToBounds = true;

        _canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
        };

        _scroller = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        _scroller.ScrollChanged += (_, _) => UpdateFades();

        _leadingFade = CreateFade(0);
        _trailingFade = CreateFade(1);

        // Esc 取消的记录点：WPF 只对发起拖拽的元素触发这个事件，
        // 而 DoDragDrop 的源就是这个控件。
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp), true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);

        _dragOverlay = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };

        var overlay = new Grid { Background = Brushes.Transparent };
        _ = overlay.Children.Add(_scroller);
        _ = overlay.Children.Add(_leadingFade);
        _ = overlay.Children.Add(_trailingFade);
        _ = overlay.Children.Add(_dragOverlay);

        Child = overlay;

        _animationLoop = new DockRenderLoop(OnFrame);
        Unloaded += (_, _) => ResetHover();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetHover(); };
    }

    /// <summary>Versions content/layout rebuilds for the opt-in retained source.
    /// Pose updates stay in the existing actual visual tree.</summary>
    public ulong ForegroundDrawingVersion {get;private set;}

    /// <summary>
    /// 整行重建。磁贴集合变化时调用；尺寸设置走 <see cref="SetMetrics"/>。
    /// </summary>
    /// <remarks>
    /// 约定：<strong>前面的都是应用磁贴，最后的若干枚内置入口</strong>
    /// （id 带 <see cref="DockLayoutPolicy.EntryTilePrefix"/>）。分组在这里算一次，
    /// 之后排序、拖拽与分隔线都以它为准。
    /// </remarks>
    public void SetTiles(IReadOnlyList<DockTileModel> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);ForegroundDrawingVersion++;

        _models.Clear();
        _models.AddRange(tiles);

        // 内置入口一定在末尾（DockWindow 就是这么排的），但这里不假定——
        // 按 id 前缀数出前缀部分的应用数，遇到第一个入口就收尾。
        AppTileCount = 0;

        foreach (var model in _models)
        {
            if (DockLayoutPolicy.IsEntryTile(model.EntryId))
            {
                break;
            }

            AppTileCount++;
        }

        _canvas.Children.Clear();
        _tiles.Clear();
        _pressGlows.Clear();

        for (var index = 0; index < _models.Count; index++)
        {
            // 只有应用磁贴挂拖拽处理器：内置入口不参与排序，也不能被拖出移除。
            var draggable = index < AppTileCount;
            var tile = CreateTile(_models[index], draggable);
            _tiles.Add(tile);
            _ = _canvas.Children.Add(tile);
        }

        _ = _canvas.Children.Add(_groupDivider);

        _animationLoop.Stop();
        _pointerActive = false;
        _pointerPosition = double.NaN;
        _hoveredTileIndex = -1;
        _pressedTileIndex = -1;
        _pressScale = Enumerable.Repeat(1.0, _tiles.Count).ToArray();
        _pressVelocity = new double[_tiles.Count];
        _pressGlow = new double[_tiles.Count];
        UpdateTileLayout();
        PaintFrame(null);
    }

    /// <summary>应用尺寸设置并重排。滚动封顶也在这里生效。</summary>
    public void SetMetrics(double iconSize, double spacing, double peak, double verticalPadding, double maxContentWidth, bool vertical = false)
    {
        var oldMetrics=(_iconSize,_spacing,_peak,_verticalPadding,_maxContentWidth);
        _iconSize = Math.Max(1, iconSize);
        _spacing = Math.Max(0, spacing);
        _peak = Math.Max(1, peak);
        _verticalPadding = Math.Max(0, verticalPadding);
        _maxContentWidth = Math.Max(0, maxContentWidth);
        if(oldMetrics!=(_iconSize,_spacing,_peak,_verticalPadding,_maxContentWidth))ForegroundDrawingVersion++;

        // 封顶必须落到**实际布局**上，否则视口会随内容无限变宽：
        // 磁贴只会被窗口裁掉、滚不动也点不到。这里把上限设在控件自身的 MaxWidth 上，
        // 内部的 ScrollViewer 因此拿到有限视口，横向滚动条（隐藏样式）才真正工作。
        if (_vertical != vertical)
        {
            _vertical = vertical;
            _pointerActive = false;
            ForegroundDrawingVersion++;
            ScrollToHorizontalOffset(0);
        }
        var limit = _maxContentWidth > 0 ? _maxContentWidth : double.PositiveInfinity;
        MaxWidth = vertical ? double.PositiveInfinity : limit;
        MaxHeight = vertical ? limit : double.PositiveInfinity;
        HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        _canvas.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _scroller.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        _scroller.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        ConfigureFade(_leadingFade, true);
        ConfigureFade(_trailingFade, false);

        UpdateTileLayout();
        PaintFrame(CurrentPointerOrNull());
    }

    /// <summary>指针离开磁贴条：所有磁贴平滑回到未放大。</summary>
    public void NotifyPointerLeft()
    {
        _pointerActive = false;
        _hoveredTileIndex = -1;
        EnsureAnimating();
    }

    /// <summary>当前内容总宽（DIP，含两端放大越界）。供外层决定窗口尺寸。</summary>
    public double RequiredLength =>
        DockMagnification.RequiredLength(_models.Count, _iconSize, _spacing, _peak);

    /// <summary>垂直于 Dock 轴需要的厚度（DIP，含放大空间）。</summary>
    public double RequiredThickness =>
        DockMagnification.RequiredThickness(_iconSize, _peak, _verticalPadding);

    /// <summary>内容是否超出了封顶宽度。</summary>
    public bool CanScroll => _maxContentWidth > 0 && RequiredLength > _maxContentWidth;

    /// <param name="draggable">
    /// 是否参与排序拖拽。内置入口传 false：它们固定在独立区域，
    /// 既不参与应用排序，也不能被拖出移除。
    /// </param>
    private Border CreateTile(DockTileModel model, bool draggable)
    {
        FrameworkElement body;

        if (model.Icon is { } iconSource)
        {
            var icon = new Image
            {
                Source = iconSource,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = model.IsMissing ? 0.4 : 1,
            };
            // Fractional Dock sizes and hover scales need area filtering. The
            // default linear mode drops partially covered pixels on icon edges.
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            Bind(icon, Image.WidthProperty, model, nameof(DockTileModel.IconSize));
            Bind(icon, Image.HeightProperty, model, nameof(DockTileModel.IconSize));
            body = icon;
        }
        else
        {
            // 内置入口（D-12/D-13/D-14）没有提取来的图标文件，
            // 用系统自带的 Segoe Fluent Icons 字形绘制，零依赖。
            body = new TextBlock
            {
                Text = model.Glyph ?? "?",
                Opacity = model.IsMissing ? 0.4 : 1,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(body, TextBlock.FontSizeProperty, model, nameof(DockTileModel.IconSize));
        }

        // Expose the visible body through UIA; a Border itself has no automation peer.
        System.Windows.Automation.AutomationProperties.SetName(body, model.DisplayName);
        System.Windows.Automation.AutomationProperties.SetAutomationId(body, "dock-tile:" + model.EntryId);

        var dot = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Bind(dot, WidthProperty, model, nameof(DockTileModel.IndicatorWidth));
        Bind(dot, BackgroundProperty, model, nameof(DockTileModel.IndicatorBrush));
        Bind(dot, VisibilityProperty, model, nameof(DockTileModel.RunningVisibility));

        var content = new StackPanel();
        _ = content.Children.Add(body);
        _ = content.Children.Add(dot);

        var pressGlow = new RadialGradientBrush
        {
            Center = new Point(.5, .48),
            GradientOrigin = new Point(.5, .48),
            RadiusX = .72,
            RadiusY = .72,
            Opacity = 0,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(75, 255, 255, 255), 0),
                new(Color.FromArgb(24, 255, 255, 255), .55),
                new(Colors.Transparent, 1),
            },
        };
        _pressGlows.Add(pressGlow);

        var tile = new Border
        {
            Child = content,
            RenderTransform = new ScaleTransform(1, 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.Hand,
            Background = pressGlow,
            ToolTip = model.ToolTipText,
            DataContext = model,
        };

        // 拖拽处理器只挂在应用磁贴上。内置入口没有拖拽，也就没有「拖出移除」这条路。
        if (draggable)
        {
            tile.MouseLeftButtonDown += OnTileMouseDown;
            tile.MouseMove += OnTileMouseMove;
        }

        tile.MouseLeftButtonUp += OnTileMouseUp;
        tile.MouseRightButtonUp += OnTileContextMenu;
        return tile;
    }

    /// <summary>按下磁贴：记录起点与条目，启动长按计时器。</summary>
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: DockTileModel model } tile)
        {
            return;
        }

        _pressedEntryId = model.EntryId;
        _pressPosition = e.GetPosition(this);
        _pressedTileIndex = _models.FindIndex(item => item.EntryId == model.EntryId);
        EnsureAnimating();
        _pressTile = tile;
        _pressGrabPoint = e.GetPosition(tile);

        _pressTimer?.Stop();
        _pressTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Input, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(350) };
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer?.Stop();
            _pressTimer = null;
            if (_pressedEntryId is not null && _pressTile is not null
                && Mouse.LeftButton == MouseButtonState.Pressed)
                StartLongPressDrag(_pressTile);
        };
        _pressTimer.Start();
    }

    /// <summary>
    /// 按住并移动：
    /// - 计时器尚未触发时移动超过 4px → 取消计时器，进入原有 OLE 拖放；
    /// - 已进入长按拖拽模式 → 更新幽灵位置与排序预览。
    /// </summary>
    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_longPressDrag)
        {
            UpdateLongPressDrag();
            return;
        }

        if (_pressTimer is not null && _pressedEntryId is not null)
        {
            var position = e.GetPosition(this);
            if ((position - _pressPosition).Length >= 4)
            {
                _pressTimer.Stop();
                _pressTimer = null;
                _pressTile = null;
            }
        }

        if (_pressedEntryId is null || _dragActive
            || e.LeftButton != MouseButtonState.Pressed
            || _reorder is null)
        {
            return;
        }

        var distance = (e.GetPosition(this) - _pressPosition).Length;
        if (distance < 4)
        {
            return;
        }

        _dragActive = true;
        _pressedTileIndex = -1;
        EnsureAnimating();
        _droppedInStrip = false;
        _dragCancelReason = DragCancelReason.None;

        var draggedEntryId = _pressedEntryId;
        // 排序候选只有应用磁贴，固定本次拖动的起始槽位；插入边界始终
        // 依据拖动前的槽位计算，避免预览多次后目标索引跟着漂移。
        var candidateIds = ReorderCandidateIds.ToArray();
        _dragStartIndex = Array.IndexOf(candidateIds, draggedEntryId);
        _reorder.Begin(candidateIds);

        var data = new DataObject(TileDragFormat, draggedEntryId);
        data.SetData(DataFormats.Serializable, draggedEntryId);

        try
        {
            // 模态循环内部：DragOver/Drop/DragLeave/QueryContinueDrag 都会回到本控件。
            // Esc 取消由 OLE 处理（此时 DoDragDrop 返回 None），取消原因经
            // QueryContinueDrag 的 EscapePressed 记下来——只看位置判定会把
            // 「拖到栏外按 Esc」误当成移出取消固定。
            _ = DragDrop.DoDragDrop(this, data, DragDropEffects.Move);

            // 手势结束了才做一次权威判定：拖拽期间一律不落盘（R-08）。
            var endedOutside = DragEndedOutside();
            var cancelled = ResolveCancelReason();

            var action = DockDragGesture.DecideEnd(_droppedInStrip, endedOutside, cancelled);

            if (action is DragEndAction.CancelOnly or DragEndAction.CancelAndUnpin && _reorder.IsActive)
            {
                _ = _reorder.Cancel();
            }

            if (action == DragEndAction.CancelAndUnpin && !string.IsNullOrEmpty(draggedEntryId))
            {
                UnpinRequested?.Invoke(this, draggedEntryId);
            }

            if (action != DragEndAction.KeepCommitted || !_reorder.IsActive)
            {
                ReorderFinished?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _dragActive = false;
            _droppedInStrip = false;
            _pressedEntryId = null;
            _pressTile = null;
            _dragStartIndex = -1;
        }
    }

    // ---- 长按拖拽（长按 350ms 后图标跟随鼠标，约束在 Dock 栏内） ----

    private void StartLongPressDrag(Border tile)
    {
        if (_reorder is null || _pressedEntryId is null || _dragActive) return;

        _longPressDrag = true;
        _dragActive = true;
        _pressedTileIndex = -1;
        _droppedInStrip = false;
        _dragCancelReason = DragCancelReason.None;

        var draggedEntryId = _pressedEntryId;
        _dragGhostEntryId = draggedEntryId;
        var candidateIds = ReorderCandidateIds.ToArray();
        _dragStartIndex = Array.IndexOf(candidateIds, draggedEntryId);
        _reorder.Begin(candidateIds);

        var layer = DragOverlay ?? _dragOverlay;
        _ghostLayer = layer;
        _dragGhost = CreateGhost(tile);
        layer.Children.Add(_dragGhost);

        // 起点按画布坐标算，并且记下鼠标在幽灵里的偏移：跟手时减掉它，
        // 图标才会「抓着哪儿就从哪儿起」，而不是跳到指针的左上角。
        var ghostOrigin = tile.TranslatePoint(new Point(0, 0), layer);
        _ghostGrab = new Point(_pressGrabPoint.X, _pressGrabPoint.Y);
        _dragGhostMotion = new TranslateTransform(ghostOrigin.X, ghostOrigin.Y);
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(1.12, 1.12));
        transforms.Children.Add(_dragGhostMotion);
        _dragGhost.RenderTransform = transforms;
        _dragGhost.RenderTransformOrigin = new Point(0.5, 0.5);

        tile.Opacity = 0;
        tile.IsHitTestVisible = false;

        _ = Mouse.Capture(this, CaptureMode.SubTree);
        _animationLoop.Start();
    }

    private void UpdateLongPressDrag()
    {
        if (_dragGhost is null || _dragGhostMotion is null) return;

        var layer = _ghostLayer ?? _dragOverlay;
        var ghostW = _dragGhost.Width > 0 ? _dragGhost.Width : _iconSize;
        var ghostH = _dragGhost.Height > 0 ? _dragGhost.Height : _iconSize;

        var cursor = Mouse.GetPosition(layer);
        var desiredX = cursor.X - _ghostGrab.X;
        var desiredY = cursor.Y - _ghostGrab.Y;

        // 约束在画布内：宿主给的是窗口级画布，就等于「整条 Dock 栏」。
        desiredX = Math.Clamp(desiredX, 0, Math.Max(0, layer.ActualWidth - ghostW));
        desiredY = Math.Clamp(desiredY, 0, Math.Max(0, layer.ActualHeight - ghostH));

        _dragGhostMotion.X = desiredX;
        _dragGhostMotion.Y = desiredY;

        // 实时排序预览：幽灵中心换回本条内容坐标，插入边界只认内容坐标。
        if (_reorder?.IsActive == true && _dragGhostEntryId is not null)
        {
            var centerInLayer = new Point(desiredX + ghostW / 2, desiredY + ghostH / 2);
            var centerInStrip = PointFromScreen(layer.PointToScreen(centerInLayer));
            var contentX = ToContentX(Along(centerInStrip));
            var firstCenter = DockMagnification.SlotCenter(0, _iconSize, _spacing, _peak);
            var targetIndex = (int)Math.Round((contentX - firstCenter) / (_iconSize + _spacing));
            _reorder.PreviewMove(_dragGhostEntryId, targetIndex);
            ApplyPreviewOrder();
        }
    }

    /// <summary>
    /// 把排序预览落到画面上：只重排「应用磁贴」这一段，内置入口位置不动。
    /// 顺序没变就直接返回，避免每帧白算一次布局。
    /// </summary>
    private void ApplyPreviewOrder()
    {
        if (_reorder is null || AppTileCount < 2)
        {
            return;
        }

        var order = _reorder.Order;

        if (order.Count != AppTileCount)
        {
            return;
        }

        var changed = false;

        for (var index = 0; index < AppTileCount; index++)
        {
            if (!string.Equals(_models[index].EntryId, order[index], StringComparison.Ordinal))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return;
        }

        var pressedId = _pressedTileIndex >= 0 && _pressedTileIndex < _models.Count
            ? _models[_pressedTileIndex].EntryId : null;
        var byId = new Dictionary<string, (DockTileModel Model, Border Tile, TileGeometry Geometry,
            RadialGradientBrush Brush, double PressScale, double Velocity, double Glow)>(AppTileCount, StringComparer.Ordinal);

        for (var index = 0; index < AppTileCount; index++)
        {
            byId[_models[index].EntryId] = (_models[index], _tiles[index], _paintedGeometry[index],
                _pressGlows[index], _pressScale[index], _pressVelocity[index], _pressGlow[index]);
        }

        for (var index = 0; index < AppTileCount; index++)
        {
            if (!byId.TryGetValue(order[index], out var entry))
            {
                return;
            }

            _models[index] = entry.Model;
            _tiles[index] = entry.Tile;
            _paintedGeometry[index] = entry.Geometry;
            _pressGlows[index] = entry.Brush;
            _pressScale[index] = entry.PressScale;
            _pressVelocity[index] = entry.Velocity;
            _pressGlow[index] = entry.Glow;
        }

        _pressedTileIndex = pressedId is null ? -1 : _models.FindIndex(model => model.EntryId == pressedId);
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            PaintFrame(CurrentPointerOrNull());
        }
        else
        {
            PaintGeometry(_paintedGeometry);
            if (IsVisible) _animationLoop.Start();
        }
    }

    private void EndLongPressDrag(bool commit)
    {
        if (!_longPressDrag) return;

        var entryId = _dragGhostEntryId;
        var hadActiveReorder = _reorder?.IsActive == true;

        if (_pressTile is not null)
        {
            _pressTile.Opacity = 1;
            _pressTile.IsHitTestVisible = true;
        }

        if (_dragGhost is not null)
        {
            (_ghostLayer ?? _dragOverlay).Children.Remove(_dragGhost);
            _dragGhost = null;
        }
        _ghostLayer = null;
        _dragGhostMotion = null;
        _dragGhostEntryId = null;

        _longPressDrag = false;
        _dragActive = false;
        _droppedInStrip = false;
        _pressedEntryId = null;
        _pressTile = null;
        _dragStartIndex = -1;
        _pressedTileIndex = -1;
        if (Mouse.Captured == this) Mouse.Capture(null);

        if (commit && hadActiveReorder && entryId is not null && _reorder is not null)
        {
            // 预览已经是用户看到的那一版，直接提交；落点不用再算一次。
            var outcome = _reorder.Commit();
            if (outcome == ReorderOutcome.SaveFailed)
            {
                System.Windows.MessageBox.Show(
                    "排序没能保存，可能是数据文件暂时写不了。顺序已恢复原样。",
                    "GlassDesk",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            ApplyPreviewOrder();
            ReorderFinished?.Invoke(this, EventArgs.Empty);
        }
        else if (hadActiveReorder && _reorder is not null)
        {
            _ = _reorder.Cancel();
            ApplyPreviewOrder();
            ReorderFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private Border CreateGhost(Border source)
    {
        if (source.DataContext is not DockTileModel model) return new Border();

        FrameworkElement body;
        if (model.Icon is { } iconSource)
        {
            body = new Image
            {
                Source = iconSource,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = model.IconSize,
                Height = model.IconSize,
            };
        }
        else
        {
            body = new TextBlock
            {
                Text = model.Glyph ?? "?",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = model.IconSize,
            };
        }

        var ghost = new Border
        {
            Width = source.ActualWidth > 0 ? source.ActualWidth : _iconSize,
            Height = source.ActualHeight > 0 ? source.ActualHeight : _iconSize + 12,
            Background = Brushes.Transparent,
            Child = body,
            IsHitTestVisible = false,
        };

        ghost.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 5,
            Opacity = 0.28,
        };

        return ghost;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_longPressDrag && e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            UpdateLongPressDrag();
            EndLongPressDrag(commit: true);
        }
        _pressTimer?.Stop();
        _pressTimer = null;
        _pressedEntryId = null;
        _pressedTileIndex = -1;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_longPressDrag && e.Key == Key.Escape)
        {
            e.Handled = true;
            EndLongPressDrag(commit: false);
        }
    }

    /// <summary>
    /// 本次拖拽是否被取消，并记下原因。
    /// </summary>
    /// <remarks>
    /// 两路信号，取到哪个算哪个：
    /// <list type="number">
    /// <item><c>QueryContinueDrag</c> 报的 <c>EscapePressed</c>——正路；</item>
    /// <item>手势结束时鼠标主键仍按着——Esc 取消的可观测形态（正常放下必然已松手）。
    /// 这一路是兜底：万一平台上第 1 路没触发，也不会把用户取消掉的条目删掉。</item>
    /// </list>
    /// </remarks>
    private bool ResolveCancelReason()
    {
        if (_dragCancelReason != DragCancelReason.None)
        {
            return true;
        }

        if (InputState.IsPrimaryMouseButtonDown())
        {
            _dragCancelReason = DragCancelReason.PrimaryButtonStillDown;
            return true;
        }

        return false;
    }

    /// <summary>
    /// OLE 拖放循环的状态通知：这里记下 Esc。
    /// </summary>
    /// <remarks>
    /// 必须挂在拖拽源元素上——WPF 只对发起拖拽的那个元素触发这个事件。
    /// </remarks>
    private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            _dragCancelReason = DragCancelReason.Escape;
        }
    }

    /// <summary>
    /// 手势结束时指针是否在栏外（含取消固定阈值）。
    /// </summary>
    /// <remarks>
    /// 用<strong>当刻光标位置</strong>判定，而不是拖拽过程中某次 <c>DragLeave</c> 的位置：
    /// 缓慢移出边界时那一次的位置未必达到阈值，同一个手势的结果会随鼠标速度而变。
    /// 光标是物理像素，经 WPF 的屏幕换算转成控件坐标（含 DPI）。
    /// </remarks>
    private bool DragEndedOutside()
    {
        if (DisplayService.GetCursorPixelPoint() is not { } cursor)
        {
            // 读不到光标（远控会话等）：按「仍在栏内」处理，宁可不动布局。
            return false;
        }

        Point local;

        try
        {
            local = PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }

        if (double.IsNaN(local.X) || double.IsNaN(local.Y))
        {
            return false;
        }

        return local.X < -UnpinDragDistance
            || local.X > ActualWidth + UnpinDragDistance
            || local.Y < -UnpinDragDistance
            || local.Y > ActualHeight + UnpinDragDistance;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);

        if (!IsInternalTileDrag(e.Data, out var entryId) || _reorder is null)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        PreviewReorderAtEventPoint(e, entryId);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        if (!IsInternalTileDrag(e.Data, out var entryId) || _reorder is null)
        {
            return;
        }

        e.Handled = true;
        _droppedInStrip = true;

        // OLE 可能在松手前没有再派发一次 DragOver。Drop 自带最终指针坐标，
        // 用它刷新预览后再提交，避免把上一次 DragOver 的旧位置落盘。
        PreviewReorderAtEventPoint(e, entryId);

        // 松手 = 操作完成：状态机在这里统一落盘一次（R-08）。
        var outcome = _reorder.Commit();

        if (outcome == ReorderOutcome.SaveFailed)
        {
            // 失败已由状态机恢复原顺序；给用户一句能听懂的解释。
            System.Windows.MessageBox.Show(
                "排序没能保存，可能是数据文件暂时写不了。顺序已恢复原样。",
                "GlassDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ReorderFinished?.Invoke(this, EventArgs.Empty);
    }

    private void PreviewReorderAtEventPoint(DragEventArgs e, string entryId)
    {
        if (_dragStartIndex < 0 || _reorder?.IsActive != true)
        {
            return;
        }

        // OLE DragOver/Drop 参数携带的是本次事件的目标坐标；滚动偏移只加一次。
        var viewportX = Along(e.GetPosition(_scroller));
        if (double.IsNaN(viewportX))
        {
            return;
        }

        var insertionIndex = IndexForInsert(ToContentX(viewportX));
        var targetIndex = insertionIndex > _dragStartIndex ? insertionIndex - 1 : insertionIndex;
        _reorder.PreviewMove(entryId, targetIndex);
        ApplyPreviewOrder();
    }

    private bool IsInternalTileDrag(IDataObject data, out string entryId)
    {
        if (_dragActive && data.GetDataPresent(TileDragFormat))
        {
            var value = data.GetData(TileDragFormat) as string;

            if (!string.IsNullOrEmpty(value))
            {
                entryId = value;
                return true;
            }
        }

        entryId = string.Empty;
        return false;
    }

    /// <summary>
    /// 内容坐标落在哪个插入边界：每枚磁贴中心的左侧插到前面，右侧插到后面。
    /// </summary>
    /// <remarks>
    /// 只在<strong>应用磁贴</strong>范围内取值：内置入口那一侧不是排序目标，
    /// 拖到那儿也只是「插到最后一个应用之后」。
    /// </remarks>
    private int IndexForInsert(double contentPosition)
    {
        if (AppTileCount == 0)
        {
            return 0;
        }

        var geometry = DockMagnification.Compute(_models.Count, _iconSize, _spacing, _peak, null);

        for (var index = 0; index < AppTileCount; index++)
        {
            if (contentPosition < geometry[index].Center)
            {
                return index;
            }
        }

        return AppTileCount;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_longPressDrag || _dragActive || e.Handled) return;
        _pressedTileIndex = -1;
        EnsureAnimating();
        // 命中不以 sender 为准，也不在这里重算几何：用**最后一次绘制帧**做判定
        // （视觉即所得）。重算会在动画中途与屏幕上的画面分叉。
        // 换算走「视口坐标 → 内容坐标」这一条路（只加一次偏移）。
        var viewportX = PointerViewportPosition();

        if (viewportX is not { } position)
        {
            return;
        }

        var hit = ResolveHitAtViewportX(position);

        if (hit >= 0 && hit < _models.Count)
        {
            TileClicked?.Invoke(this, _models[hit].EntryId);
        }
    }

    private void OnTileContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: DockTileModel { EntryId: var entryId } }
            && !entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            TileContextMenuRequested?.Invoke(this, entryId);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_longPressDrag)
        {
            UpdateLongPressDrag();
            return;
        }

        _pointerActive = true;

        // 目标位置先记录，插值交给帧循环——鼠标事件风暴不再每条都触发重排。
        // WPF 偶尔给出 NaN 坐标（正在布局），那就保持上一个目标。
        if (PointerContentPosition() is { } contentX)
        {
            _pointerPosition = contentX;
        }

        EnsureAnimating();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        NotifyPointerLeft();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_longPressDrag && !IsMouseCaptureWithin) EndLongPressDrag(commit: false);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (CanScroll)
        {
            ScrollToHorizontalOffset(HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 指针在<strong>视口</strong>内的位置换算到内容坐标；读不到时为 null。
    /// </summary>
    /// <remarks>
    /// <strong>换算只有这一处，也只加一次偏移。</strong>
    /// <c>Mouse.GetPosition(_canvas)</c> 拿到的已经是内容坐标——画布是滚动内容的子元素，
    /// 它的坐标空间本身已经包含了滚动位移；再走一次 <see cref="ToContentX"/>
    /// 会把偏移加两遍。实测：滚动 120 DIP、视口位置 50 时算成 290（正确是 170），
    /// 表现为滚动后的点击、放大与拖拽插入位置整体偏移。
    /// </remarks>
    private double? PointerViewportPosition()
    {
        // 相对本控件（= 视口）取坐标，不相对画布。
        var position = Mouse.GetPosition(this);

        return double.IsNaN(Along(position)) ? null : Along(position);
    }

    /// <summary>指针在滚动画布内容坐标系内的位置，已包含实际滚动变换。</summary>
    private double? PointerContentPosition()
    {
        // 画布自身的坐标变换已经反映 ScrollViewer 偏移；这里不能再额外加偏移。
        var position = Mouse.GetPosition(_canvas);

        return double.IsNaN(Along(position)) ? null : Along(position);
    }

    /// <summary>
    /// 把<strong>视口内</strong>的 x 换算成内容坐标（加上当前滚动偏移）。
    /// </summary>
    /// <remarks>
    /// 输入必须是视口坐标。相对画布读到的位置已经是内容坐标，不要再经过这里——
    /// 那会把偏移加两遍（见 <see cref="PointerViewportPosition"/>）。
    /// </remarks>
    public double ToContentX(double viewportX) => viewportX + HorizontalOffset;

    /// <summary>
    /// 用<strong>视口内</strong>的坐标解析命中（滚动后仍正确）。
    /// </summary>
    /// <remarks>
    /// 点击、悬停与拖拽插入都走这一条路，因此「只加一次偏移」这条性质
    /// 在测试里覆盖的就是运行时真正执行的代码，而不是另写一份等价逻辑。
    /// </remarks>
    public int ResolveHitAtViewportX(double viewportX) => ResolveHit(ToContentX(viewportX));

    /// <summary>滚动到指定横向偏移（DIP）。诊断与测试用。</summary>
    public void ScrollToHorizontalOffset(double offset)
    {
        if (_vertical) _scroller.ScrollToVerticalOffset(offset);
        else _scroller.ScrollToHorizontalOffset(offset);
    }

    /// <summary>当前横向滚动偏移（DIP）。诊断与测试用。</summary>
    public double HorizontalOffset => _vertical ? _scroller.VerticalOffset : _scroller.HorizontalOffset;

    /// <summary>当前视口宽度（DIP）。诊断与测试用。</summary>
    public double ViewportWidth => _vertical ? _scroller.ViewportHeight : _scroller.ViewportWidth;

    /// <summary>滚到最右端。诊断与测试用。</summary>
    public void ScrollToEnd() => ScrollToHorizontalOffset(double.PositiveInfinity);

    private void EnsureAnimating()
    {
        if (IsVisible) _animationLoop.Start();
        else PaintFrame(CurrentPointerOrNull());
    }

    /// <summary>动画帧：所有磁贴同时追随当前指针对应的目标缩放，全部到位后停帧。</summary>
    private bool OnFrame(double deltaSeconds)
    {
        if (_longPressDrag)
        {
            if (Keyboard.IsKeyDown(Key.Escape))
            {
                _dragCancelReason = DragCancelReason.Escape;
                EndLongPressDrag(commit: false);
            }
            else if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                EndLongPressDrag(commit: true);
            }
            else UpdateLongPressDrag();
        }
        var targetPointer = CurrentPointerOrNull();
        // Resolve the latest pointer once per frame, rather than for every queued mouse event.
        _hoveredTileIndex = targetPointer is { } position ? FindHoveredTile(position) : -1;
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            SnapPressFeedback();
            PaintFrame(targetPointer);
            return !_longPressDrag;
        }
        return AnimateFrame(targetPointer, deltaSeconds) && !_longPressDrag;
    }

    private void ResetHover()
    {
        _animationLoop.Stop();
        _pointerActive = false;
        _pointerPosition = double.NaN;
        _hoveredTileIndex = -1;
        PaintFrame(null);
        if (_longPressDrag)
        {
            EndLongPressDrag(commit: false);
        }
        _pressTimer?.Stop();
        _pressTimer = null;
        _pressTile = null;
        _pressedEntryId = null;
    }

    /// <summary>
    /// 按给定指针位置绘制一帧，并把这一帧的几何缓存给命中使用。
    /// </summary>
    /// <remarks>
    /// 缓动由帧循环负责；这个方法只做「给定指针 → 几何 → 屏幕」这一步，
    /// 因此可以在测试里确定性地重放某一帧，验证点击与画面一致。
    /// </remarks>
    public void PaintFrame(double? pointerPosition)
    {
        DockMagnification.ComputeInto(_paintedGeometry, _iconSize, _spacing, _peak, pointerPosition);
        ApplyHoveredPeak(_paintedGeometry, pointerPosition);
        PaintGeometry(_paintedGeometry);
    }

    private bool AnimateFrame(double? pointerPosition, double deltaSeconds)
    {
        var targets = _targetGeometry;
        DockMagnification.ComputeInto(targets, _iconSize, _spacing, _peak, pointerPosition);
        ApplyHoveredPeak(targets, pointerPosition);
        var geometry = _paintedGeometry;
        var allSettled = AdvancePressFeedback(deltaSeconds);

        for (var index = 0; index < targets.Length; index++)
        {
            var currentScale = geometry[index].Scale;
            var targetScale = targets[index].Scale;
            var nextScale = DockHoverScale.Advance(currentScale, targetScale, deltaSeconds);
            var nextCenter = DockHoverScale.Advance(geometry[index].Center, targets[index].Center, deltaSeconds);

            if (nextScale != targetScale || nextCenter != targets[index].Center)
            {
                allSettled = false;
            }

            geometry[index] = new TileGeometry(nextCenter, nextScale);
        }

        PaintGeometry(geometry);
        return allSettled;
    }

    /// <summary>给按下的图标一个短促的凝胶式压缩；松开时欠阻尼回弹。</summary>
    private bool AdvancePressFeedback(double deltaSeconds)
    {
        if (_pressScale.Length != _tiles.Count) return true;
        var seconds = Math.Clamp(deltaSeconds, 0, .05);
        var allSettled = true;

        for (var index = 0; index < _pressScale.Length; index++)
        {
            var target = index == _pressedTileIndex ? .94 : 1.0;
            var displacement = _pressScale[index] - target;
            var velocity = _pressVelocity[index];
            const double frequency = 22;
            const double damping = .68;
            var dampedFrequency = frequency * Math.Sqrt(1 - damping * damping);
            var decay = Math.Exp(-damping * frequency * seconds);
            var sine = Math.Sin(dampedFrequency * seconds);
            var cosine = Math.Cos(dampedFrequency * seconds);
            var coefficient = (velocity + damping * frequency * displacement) / dampedFrequency;
            var wave = displacement * cosine + coefficient * sine;
            var next = target + decay * wave;
            var nextVelocity = decay * (dampedFrequency * (-displacement * sine + coefficient * cosine)
                - damping * frequency * wave);

            if (Math.Abs(next - target) < .0005 && Math.Abs(nextVelocity) < .01)
            {
                next = target;
                nextVelocity = 0;
            }
            else
            {
                allSettled = false;
            }

            _pressScale[index] = Math.Clamp(next, .9, 1.04);
            _pressVelocity[index] = nextVelocity;
            var glowTarget = index == _pressedTileIndex ? 1.0 : 0.0;
            _pressGlow[index] += (glowTarget - _pressGlow[index]) * (1 - Math.Exp(-18 * seconds));
            if (Math.Abs(_pressGlow[index] - glowTarget) < .005) _pressGlow[index] = glowTarget;
            else allSettled = false;
            if (index < _pressGlows.Count) _pressGlows[index].Opacity = .46 * _pressGlow[index];
        }

        return allSettled;
    }

    private void SnapPressFeedback()
    {
        for (var index = 0; index < _pressScale.Length; index++)
        {
            _pressScale[index] = index == _pressedTileIndex ? .94 : 1;
            _pressVelocity[index] = 0;
            _pressGlow[index] = 0;
            if (index < _pressGlows.Count) _pressGlows[index].Opacity = 0;
        }
    }

    private void ApplyHoveredPeak(TileGeometry[] geometry, double? pointerPosition)
    {
        if (pointerPosition is null || _hoveredTileIndex < 0 || _hoveredTileIndex >= geometry.Length)
        {
            return;
        }

        geometry[_hoveredTileIndex] = new TileGeometry(geometry[_hoveredTileIndex].Center, _peak);
    }

    private int FindHoveredTile(double contentPosition)
    {
        var nearestIndex = -1;
        var nearestDistance = double.PositiveInfinity;
        var halfExpandedWidth = _iconSize * _peak / 2;

        for (var index = 0; index < _models.Count; index++)
        {
            var center = DockMagnification.SlotCenter(index, _iconSize, _spacing, _peak);
            var distance = Math.Abs(contentPosition - center);
            if (distance <= halfExpandedWidth && distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearestIndex;
    }

    private void UpdateTileLayout()
    {
        var count = _models.Count;
        if (_paintedGeometry.Length != count)
        {
            _paintedGeometry = new TileGeometry[count];
            _targetGeometry = new TileGeometry[count];
            _paintOrder = Enumerable.Range(0, count).ToArray();
        }
        DockMagnification.ComputeInto(_paintedGeometry, _iconSize, _spacing, _peak, null);
        _canvas.Width = _vertical ? RequiredThickness : RequiredLength;
        _canvas.Height = _vertical ? RequiredLength : RequiredThickness;
        var halfOverhangThickness = (RequiredThickness - _iconSize) / 2;
        for (var index = 0; index < count; index++)
        {
            var start = _paintedGeometry[index].Center - _iconSize / 2;
            _tiles[index].Width = _vertical ? _iconSize : double.NaN;
            Canvas.SetLeft(_tiles[index], _vertical ? halfOverhangThickness : start);
            Canvas.SetTop(_tiles[index], _vertical ? start : halfOverhangThickness);
        }
        UpdateGroupDivider(_paintedGeometry);
        UpdateFades();
    }

    private void PaintGeometry(TileGeometry[] geometry)
    {
        // z 序取绘制与命中共同的那一份：缩放大的在上，重叠处可见者即被点中者。
        var order = _paintOrder;
        // 绝大多数动画帧的相对缩放次序没有变化（例如鼠标停在同一枚图标上时）。
        // 这时保留上一帧已排好的顺序，避免每个合成帧都做 O(n log n) 排序；缩放
        // 曲线让两枚图标跨越时，下方检查会立即发现逆序并恢复完整排序。
        if (!IsPaintOrderCurrent(order))
        {
            Array.Sort(order, _paintOrderComparer);
        }

        for (var rank = 0; rank < order.Length; rank++)
        {
            var index = order[rank];
            var tile = _tiles[index];
            var scale = geometry[index].Scale * (index < _pressScale.Length ? _pressScale[index] : 1);
            var left = geometry[index].Center - _iconSize / 2;
            if (_vertical)
            {
                if (Canvas.GetTop(tile) != left) Canvas.SetTop(tile, left);
            }
            else if (Canvas.GetLeft(tile) != left) Canvas.SetLeft(tile, left);

            // order 的第 0 项是「最上层」，而 ZIndex 越大越靠上——所以是**取反**赋值。
            // 早先写成 Panel.SetZIndex(tile, rank) 时，递增的 rank 把最小的磁贴放到了最上层，
            // 绘制层级与命中顺序恰好相反（重叠区「看见 A 点中 B」）。
            var zIndex = order.Length - rank;
            if (Panel.GetZIndex(tile) != zIndex) Panel.SetZIndex(tile, zIndex);

            if (tile.RenderTransform is ScaleTransform transform)
            {
                if (transform.ScaleX != scale) transform.ScaleX = scale;
                if (transform.ScaleY != scale) transform.ScaleY = scale;
            }
        }

    }

    /// <summary>
    /// 当前绘制顺序是否仍符合最新几何的「大缩放在上」规则。
    /// </summary>
    /// <remarks>
    /// 只检查相邻项即可验证整个已排序数组。该检查是 O(n)，而完整排序是
    /// O(n log n)；它让静止悬停及缓动收尾阶段不必反复排序，同时保留原有
    /// <see cref="DockMagnification.CompareDrawOrder"/> 作为唯一排序规则。
    /// </remarks>
    private bool IsPaintOrderCurrent(int[] order)
    {
        for (var index = 1; index < order.Length; index++)
        {
            if (_paintOrderComparer.Compare(order[index - 1], order[index]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 把分隔线放在「应用磁贴」与「内置入口」两组的中间。
    /// </summary>
    /// <remarks>
    /// 只有两组都在时才显示。位置取两组相邻两枚磁贴槽位中心的中点——
    /// 与磁贴一样落在画布坐标里，因此滚动时跟随内容。
    /// </remarks>
    private void UpdateGroupDivider(TileGeometry[] geometry)
    {
        if (AppTileCount == 0 || AppTileCount >= geometry.Length)
        {
            _groupDivider.Visibility = Visibility.Collapsed;
            return;
        }

        var lastAppCenter = geometry[AppTileCount - 1].Center;
        var firstEntryCenter = geometry[AppTileCount].Center;

        var center = (lastAppCenter + firstEntryCenter) / 2;
        Canvas.SetLeft(_groupDivider, _vertical ? _iconSize * 0.15 : center);
        Canvas.SetTop(_groupDivider, _vertical ? center : _iconSize * 0.15);
        _groupDivider.Width = _vertical ? _iconSize * 0.7 : 1;
        _groupDivider.Height = _vertical ? 1 : _iconSize * 0.7;
        _groupDivider.Visibility = Visibility.Visible;

        // 分隔线不参与命中的重叠归属（它不可点击），层级压在磁贴之下即可。
        Panel.SetZIndex(_groupDivider, -1);
    }

    /// <summary>
    /// 按<strong>最后一次绘制帧</strong>解析指针命中的磁贴；没有命中返回 -1。
    /// </summary>
    /// <param name="pointerPosition">指针沿 Dock 轴的位置（画布坐标，DIP）。</param>
    public int ResolveHit(double pointerPosition)
        => _paintedGeometry.Length == 0
            ? -1
            : DockMagnification.HitTest(_paintedGeometry, _iconSize, pointerPosition);

    /// <summary>最后一次绘制帧的几何快照（诊断与测试用）。</summary>
    public IReadOnlyList<TileGeometry> PaintedGeometry => _paintedGeometry.ToArray();

    /// <summary>
    /// 最后一次绘制帧里每枚磁贴的<strong>实际</strong>绘制状态（读的是可视树上的真值）。
    /// </summary>
    /// <remarks>
    /// 刻意读 <c>Panel.GetZIndex</c> 与 <c>Canvas.GetLeft</c>，而不是把算出来的数组再报一遍：
    /// 「绘制层级与命中顺序相反」这个缺陷之所以能溜过测试，正是因为当时的断言核对的是
    /// 几何顺序数组（派生量），没有核对真正生效的层级。诊断与测试要读生效值。
    /// </remarks>
    public IReadOnlyList<PaintedTileState> PaintedTiles
    {
        get
        {
            var states = new List<PaintedTileState>(_tiles.Count);

            for (var index = 0; index < _tiles.Count; index++)
            {
                var tile = _tiles[index];
                var scale = tile.RenderTransform is ScaleTransform transform ? transform.ScaleX : 1.0;
                var entryId = index < _models.Count ? _models[index].EntryId : string.Empty;

                states.Add(new PaintedTileState(entryId, scale, Panel.GetZIndex(tile), Canvas.GetLeft(tile)));
            }

            return states;
        }
    }


    private double? CurrentPointerOrNull()
        => _pointerActive && !double.IsNaN(_pointerPosition) ? _pointerPosition : null;

    /// <summary>两端渐隐提示：可滚动且还没滚到头的一侧显示。</summary>
    private void UpdateFades()
    {
        var scrollable = CanScroll;

        _leadingFade.Visibility = scrollable && HorizontalOffset > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        var maxOffset = Math.Max(0, (_vertical ? _scroller.ExtentHeight : _scroller.ExtentWidth) - ViewportWidth);
        _trailingFade.Visibility = scrollable && HorizontalOffset < maxOffset - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ConfigureFade(Border fade, bool leading)
    {
        fade.Width = _vertical ? double.NaN : 28;
        fade.Height = _vertical ? 28 : double.NaN;
        fade.HorizontalAlignment = _vertical ? HorizontalAlignment.Stretch
            : leading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        fade.VerticalAlignment = !_vertical ? VerticalAlignment.Stretch
            : leading ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        var gradient = (LinearGradientBrush)fade.Background;
        var edge = leading ? 0 : 1;
        gradient.StartPoint = _vertical ? new Point(.5, edge) : new Point(edge, .5);
        gradient.EndPoint = _vertical ? new Point(.5, 1 - edge) : new Point(1 - edge, .5);
    }

    private static Border CreateFade(double direction)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(direction, 0.5),
            EndPoint = new Point(1 - direction, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x88, 0x00, 0x00, 0x00), 0),
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1),
            },
        };

        return new Border
        {
            Background = gradient,
            Width = 28,
            HorizontalAlignment = direction < 0.5 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
    }

    private static void Bind(FrameworkElement element, DependencyProperty property, object source, string path)
    {
        _ = element.SetBinding(property, new System.Windows.Data.Binding
        {
            Source = source,
            Path = new PropertyPath(path),
        });
    }
}
