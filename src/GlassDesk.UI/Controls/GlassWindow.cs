using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Core.Geometry;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

// WPF 自带 System.Windows.ThemeMode，与设置模型里的同名枚举冲突，用别名固定到我们自己那个。
using ThemeMode = GlassDesk.Core.Models.ThemeMode;

namespace GlassDesk.UI.Controls;

/// <summary>窗口最终采用的玻璃做法。</summary>
public enum GlassSurfaceMode
{
    /// <summary>拿到了真实的背景模糊。观感接近 macOS 的毛玻璃。</summary>
    Acrylic = 0,

    /// <summary>
    /// 只有自绘的半透明填充，背后不模糊。
    /// </summary>
    /// <remarks>
    /// 这是降级路径，不是失败：模糊所用的接口未公开，未来构建可能移除它，
    /// 所以必须有一条不依赖它的外观。
    /// </remarks>
    SelfDrawn = 1,

    /// <summary>Clear refractive material with an explicitly self-excluded desktop source.</summary>
    OpticalGlass = 2,
}

/// <summary>
/// 本产品显示的每个窗口的基类。
/// </summary>
/// <remarks>
/// Main windows share clear optical surfaces and continuous-curvature clipping. A verified
/// capture-exclusion session keeps their own pixels out of the live desktop source. This
/// intentionally also excludes GlassDesk from supported screenshots and recording tools.
/// A separate input-transparent GlassShadow provides ambient elevation without changing
/// the main window's alpha-zero outside corners. Opaque accessibility mode remains available.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF window resources are acquired in OnSourceInitialized and released in OnClosed.")]
public abstract class GlassWindow : Window
{
    private CaptureExclusionSession? _captureExclusion;
    private GlassShadow? _shadow;
    private PrimaryDisplaySession? _primaryDisplaySession;
    /// <summary>
    /// 面板之外留出的空白，DIP。**默认为 0**。
    /// </summary>
    /// <remarks>
    /// Keep the main window exactly the size of its panel. Nonzero shadow pixels in this
    /// window would intercept other applications' clicks. GlassShadow instead owns a separate
    /// WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE window around this zero-inset panel.
    /// </remarks>
    private const double DefaultChromeInset = 0;

    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette),
        typeof(GlassPalette),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PanelCornerRadiusProperty = DependencyProperty.Register(
        nameof(PanelCornerRadius),
        typeof(CornerRadius),
        typeof(GlassWindow),
        new PropertyMetadata(new CornerRadius(36)));

    public static readonly DependencyProperty ChromeInsetProperty = DependencyProperty.Register(
        nameof(ChromeInset),
        typeof(Thickness),
        typeof(GlassWindow),
        new PropertyMetadata(new Thickness(DefaultChromeInset)));

    public static readonly DependencyProperty PanelBackgroundBrushProperty = DependencyProperty.Register(
        nameof(PanelBackgroundBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PanelBorderBrushProperty = DependencyProperty.Register(
        nameof(PanelBorderBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ForegroundBrushProperty = DependencyProperty.Register(
        nameof(ForegroundBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty DockBuiltinIconBrushProperty = DependencyProperty.Register(
        nameof(DockBuiltinIconBrush), typeof(Brush), typeof(GlassWindow), new PropertyMetadata(null));

    public static readonly DependencyProperty LaunchpadIconFilledProperty = DependencyProperty.Register(
        nameof(LaunchpadIconFilled), typeof(bool), typeof(GlassWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty SecondaryForegroundBrushProperty = DependencyProperty.Register(
        nameof(SecondaryForegroundBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(GlassWindow),
        new PropertyMetadata(null));

    protected GlassWindow()
    {
        WindowStyle = WindowStyle.None;

        // 分层窗口，理由见类注释第 2 条。
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        DpiChanged += (_, e) =>
        {
            // Child Image DPI notifications bubble through Window when icons attach
            // during hover, dragging and paging, even at the identical DPI. Only an
            // actual scale change of this window invalidates desktop capture.
            if (!ReferenceEquals(e.OriginalSource, this) || e.OldDpi.Equals(e.NewDpi)) return;
            GlassDesk.UI.BackdropRecovery.RetryBackground("WindowDpiChanged");
        };

        // 窗口默认不可调整大小；需要调整大小的窗口自行开启。
        ResizeMode = ResizeMode.NoResize;
    }

    /// <summary>当前主题的配色。主题变化时整体替换。</summary>
    public GlassPalette? Palette
    {
        get => (GlassPalette?)GetValue(PaletteProperty);
        private set => SetValue(PaletteProperty, value);
    }

    /// <summary>本窗口面板的圆角半径，由外观设置驱动。</summary>
    public CornerRadius PanelCornerRadius
    {
        get => (CornerRadius)GetValue(PanelCornerRadiusProperty);
        protected set => SetValue(PanelCornerRadiusProperty, value);
    }

    /// <summary>Builds the panel outline used by clipping, glass rendering, and its shadow.</summary>
    protected virtual Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    /// <summary>Tests a point against the panel outline used by native hit testing.</summary>
    protected virtual bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(bounds, radius, point);

    /// <summary>Gets the inward edge data used by the optical refraction map.</summary>
    protected virtual bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(bounds, radius, point, out depth, out inwardNormal);

    /// <summary>Whether the active panel outline uses the shared phone-like superellipse.</summary>
    protected virtual bool UsesSquirclePanelGeometryCore => true;

    /// <summary>Allows readable utility windows to opt out of desktop glass independently.</summary>
    protected virtual bool UseOpaquePanel => false;

    /// <summary>Only the Launcher opts into the independent text brightness setting.</summary>
    protected virtual bool UseLauncherTextBrightness => false;

    internal Geometry CreateGlassPanelGeometry(Rect bounds, double radius) =>
        CreatePanelGeometryCore(bounds, radius);

    internal bool TryGetGlassPanelEdge(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        TryGetPanelGeometryEdgeCore(bounds, radius, point, out depth, out inwardNormal);

    internal bool UsesSquirclePanelGeometry => UsesSquirclePanelGeometryCore;

    /// <summary>Tracks the animated panel shape in the independent, input-transparent shadow.</summary>
    protected void SetTransitionShadowShape(Rect? panelBounds, double radius, double strength = 1) =>
        _shadow?.SetTransitionShape(panelBounds, radius, strength);

    private Geometry? _sharedPanelClip;
    protected void SetSharedPanelClip(Rect bounds, double radius)
    {
        SetSharedPanelGeometry(CreateGlassPanelGeometry(bounds, radius));
    }

    protected void SetSharedPanelGeometry(Geometry geometry)
    {
        _sharedPanelClip = geometry;
        Clip = _sharedPanelClip;
    }

    /// <summary>
    /// 面板之外留出的外边距，DIP。面板的 XAML 根元素绑定它作为 Margin。
    /// </summary>
    /// <remarks>
    /// <strong>当前恒为 0，也就是窗口矩形恰好等于可见面板。</strong>
    /// 属性保留下来是为了让面板与窗口边界的换算只有一处（命中测试与裁剪都要用），
    /// 而不是因为还有留白。要不要恢复留白、以及为什么恢复之后必须同时换一种画投影的方式，
    /// 见 <see cref="DefaultChromeInset"/> 的说明。
    /// </remarks>
    public Thickness ChromeInset
    {
        get => (Thickness)GetValue(ChromeInsetProperty);
        protected set => SetValue(ChromeInsetProperty, value);
    }

    /// <summary>
    /// 面板填充。具体用哪一层由本窗口决定——见 <see cref="ApplyAppearance"/>。
    /// </summary>
    public Brush? PanelBackgroundBrush
    {
        get => (Brush?)GetValue(PanelBackgroundBrushProperty);
        private set => SetValue(PanelBackgroundBrushProperty, value);
    }

    /// <summary>面板边缘的细亮边。</summary>
    public Brush? PanelBorderBrush
    {
        get => (Brush?)GetValue(PanelBorderBrushProperty);
        private set => SetValue(PanelBorderBrushProperty, value);
    }

    public Brush? ForegroundBrush
    {
        get => (Brush?)GetValue(ForegroundBrushProperty);
        private set => SetValue(ForegroundBrushProperty, value);
    }

    public Brush? DockBuiltinIconBrush
    {
        get => (Brush?)GetValue(DockBuiltinIconBrushProperty);
        private set => SetValue(DockBuiltinIconBrushProperty, value);
    }

    public bool LaunchpadIconFilled
    {
        get => (bool)GetValue(LaunchpadIconFilledProperty);
        private set => SetValue(LaunchpadIconFilledProperty, value);
    }

    public Brush? SecondaryForegroundBrush
    {
        get => (Brush?)GetValue(SecondaryForegroundBrushProperty);
        private set => SetValue(SecondaryForegroundBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        private set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>本窗口最终采用的玻璃做法。诊断与样例以此为准。</summary>
    public GlassSurfaceMode SurfaceMode { get; private set; } = GlassSurfaceMode.SelfDrawn;

    /// <summary>The process's fixed Primary Monitor session, inherited by owned glass dialogs.</summary>
    public PrimaryDisplaySession? PrimaryDisplaySession =>
        _primaryDisplaySession ?? (Owner as GlassWindow)?.PrimaryDisplaySession;

    /// <summary>Bind this production window to the Primary Monitor captured at process startup.</summary>
    public void BindPrimaryDisplay(PrimaryDisplaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsLoaded || PresentationSource.FromVisual(this) is not null)
            throw new InvalidOperationException("Bind the Primary Monitor before creating the window handle.");
        if (_primaryDisplaySession is not null && !ReferenceEquals(_primaryDisplaySession, session))
            throw new InvalidOperationException("A GlassDesk window cannot be rebound to another display session.");
        _primaryDisplaySession = session;
    }

    /// <summary>Clamp a physical window rectangle to the process Primary Monitor's work area.</summary>
    protected PixelRect PlaceWindowWithinPrimaryWorkArea(PixelRect requested)
    {
        var session = PrimaryDisplaySession;
        if (session is null || !session.IsAvailable) return new PixelRect(0, 0, 0, 0);
        var display = session.Display;
        var rect = session.ClampWindowToWorkArea(requested);
        var dipScale = 96.0 / Math.Max(1, display.Dpi);
        Width = rect.Width * dipScale;
        Height = rect.Height * dipScale;
        if (Handle != 0) _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
        return rect;
    }

    /// <summary>Center a sized, not-yet-shown window inside the fixed Primary Monitor work area.</summary>
    protected PixelRect CenterOnPrimaryWorkArea()
    {
        var session = PrimaryDisplaySession;
        if (session is null || !session.IsAvailable) return new PixelRect(0, 0, 0, 0);
        var display = session.Display;
        var widthDip = double.IsFinite(Width) ? Width : ActualWidth;
        var heightDip = double.IsFinite(Height) ? Height : ActualHeight;
        if (widthDip <= 0 || heightDip <= 0) return new PixelRect(0, 0, 0, 0);

        var width = Math.Max(1, display.ToPixels(widthDip));
        var height = Math.Max(1, display.ToPixels(heightDip));
        var work = display.WorkArea;
        var requested = new PixelRect(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height);
        var placed = session.ClampWindowToWorkArea(requested);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = display.ToDips(placed.Width);
        Height = display.ToDips(placed.Height);
        var handle = Handle != 0 ? Handle : new WindowInteropHelper(this).EnsureHandle();
        _ = DisplayService.MoveWindowToPixelRect(handle, placed);
        return placed;
    }

    /// <summary>Keep an owned GlassDesk dialog inside the current work area of its fixed Primary.</summary>
    public void ConstrainCurrentBoundsToPrimary()
    {
        var session = PrimaryDisplaySession;
        if (session is null || !session.IsAvailable || Handle == 0
            || DisplayService.GetWindowPixelBounds(Handle) is not { Width: > 0, Height: > 0 } current)
            return;
        var placed = session.ClampWindowToWorkArea(current);
        if (placed == current) return;
        var dipScale = 96.0 / Math.Max(1, session.Display.Dpi);
        Width = placed.Width * dipScale;
        Height = placed.Height * dipScale;
        _ = DisplayService.MoveWindowToPixelRect(Handle, placed);
    }

    /// <summary>
    /// 窗口句柄；在源初始化之前为 <see cref="IntPtr.Zero"/>。
    /// </summary>
    protected nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 这个表面采用配置中的哪个半径。计划书给程序坞和启动台定了不同的半径，
    /// 所以这个选择不能放在基类里。
    /// </summary>
    protected virtual double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable)
            {
                Hide();
                return;
            }

            if (DisplayService.GetWindowPixelBounds(Handle) is { Width: > 0, Height: > 0 } initialBounds)
            {
                var primaryBounds = session.ClampWindowToWorkArea(initialBounds);
                if (primaryBounds != initialBounds)
                    _ = DisplayService.MoveWindowToPixelRect(Handle, primaryBounds);
            }
        }

        // Independent candidate only: the compositor source excludes our process.
        ApplyAppearance();

        _shadow = new GlassShadow(this);

        InstallHitTestHook();

        if (ThemeService.Current is { } theme)
        {
            theme.Changed += OnThemeChanged;
        }
    }

    /// <summary>
    /// 让窗口圆角之外的点，在**本进程自己的窗口之间**放行。
    /// </summary>
    /// <remarks>
    /// <b>适用范围比直觉窄得多，这一点是实测纠正过来的。</b>
    ///
    /// <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c> 时，按微软文档其转发语义
    /// **仅限同一线程的窗口**——消息沿 z 序在同线程窗口之间传递，直到有一个返回
    /// 非 HTTRANSPARENT，**不会**传给其它进程的窗口。
    ///
    /// 所以本处理器的作用仅限于本产品自己的窗口互相重叠的场合（启动台盖住程序坞、
    /// 设置中心盖住启动台等，它们都在界面线程上）。跨进程的点击穿透只能靠分层窗口
    /// 按像素 alpha 做的命中测试，而那条路只在**真正 alpha 为 0** 的地方生效。
    ///
    /// <para>
    /// <b>让它不再承担这件事的是 <see cref="UpdateGlassClip"/>。</b> 圆角缺口现在被
    /// 从可绘制区域里挖掉了，那里是真的 alpha 0，因此跨进程也穿透——实测复测通过
    /// （圆角缺口处下层另一进程的窗口能收到点击；面板内部仍然正常捕获）。
    /// 这一处理器作为一道保险留着：若将来为了投影重新引入非零留白，
    /// 它至少能保证本产品自己的窗口之间不互相吞点击。
    /// </para>
    /// </remarks>
    private void InstallHitTestHook()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        source.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmNcHitTest = 0x0084;
        const int HtTransparent = -1;

        if (message != WmNcHitTest)
        {
            return 0;
        }

        // lParam 里是**屏幕物理像素**坐标，低字为 x、高字为 y（各自带符号）。
        var screenX = unchecked((short)(long)lParam);
        var screenY = unchecked((short)((long)lParam >> 16));

        if (!IsInsidePanel(hwnd, screenX, screenY))
        {
            handled = true;
            return HtTransparent;
        }

        return 0;
    }

    /// <summary>把屏幕坐标换算成窗口内 DIP，再交给圆角判定。</summary>
    private bool IsInsidePanel(nint hwnd, int screenX, int screenY)
    {
        if (PrimaryDisplaySession is { IsAvailable: false }) return false;
        var bounds = DisplayService.GetWindowPixelBounds(hwnd);
        if (bounds is not { } rect)
        {
            // 拿不到窗口矩形时不要拦：宁可像普通窗口一样响应，也不要把窗口变得点不动。
            return true;
        }

        var dpi = PrimaryDisplaySession is { IsAvailable: true } primary
            ? primary.Display.Dpi
            : DisplayService.DpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        // 窗口矩形是物理像素，而面板的圆角半径与内边距都是 DIP。
        var panelWidth = (rect.Width / scale) - ChromeInset.Left - ChromeInset.Right;
        var panelHeight = (rect.Height / scale) - ChromeInset.Top - ChromeInset.Bottom;

        var localX = ((screenX - rect.X) / scale) - ChromeInset.Left;
        var localY = ((screenY - rect.Y) / scale) - ChromeInset.Top;

        // 四个角取最大的那个半径：按最宽松的判定，避免把本该可点的区域误判成外部。
        var radius = Math.Max(
            Math.Max(PanelCornerRadius.TopLeft, PanelCornerRadius.TopRight),
            Math.Max(PanelCornerRadius.BottomLeft, PanelCornerRadius.BottomRight));

        return ContainsPanelGeometryCore(new Rect(0, 0, Math.Max(0, panelWidth), Math.Max(0, panelHeight)),
            radius, new Point(localX, localY));
    }

    protected override void OnClosed(EventArgs e)
    {
        _shadow?.Dispose();
        _shadow = null;
        _captureExclusion?.Dispose();
        CaptureExclusionSession.Forget(Handle);
        _captureExclusion = null;
        if (ThemeService.Current is { } theme)
        {
            theme.Changed -= OnThemeChanged;
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// 把当前主题推进窗口，并决定采用哪种玻璃做法。
    /// </summary>
    protected void ApplyAppearance()
    {
        var theme = ThemeService.Current;
        var appearance = theme?.Appearance;

        var palette = theme?.Palette ?? Palette ?? GlassPalette.Create(true, GlassPalette.DefaultAccent, false);
        if (UseOpaquePanel)
            palette = GlassPalette.Create(true, palette.Accent, true);
        Palette = palette;

        if (appearance is not null)
        {
            PanelCornerRadius = new CornerRadius(ResolveCornerRadius(appearance));
        }

        SurfaceMode = UseOpaquePanel ? GlassSurfaceMode.SelfDrawn : ResolveSurface(theme, appearance);
        ApplySurface(palette);
        ApplyPaletteBrushes(palette);
        UpdateGlassClip();
        _shadow?.Update();
    }

    /// <summary>
    /// 把四个圆角缺口从窗口的可绘制区域里挖掉，让它们成为**真正的 alpha 0**。
    /// </summary>
    /// <remarks>
    /// 这是圆角处吞点击问题的**正解**，取代了先前「用 <c>WM_NCHITTEST</c> 放行」的做法。
    ///
    /// 起因：圆角缺口里本来会渲染出投影的半透明像素，alpha 不为 0，于是那些点被算作
    /// 窗口的一部分而吞掉点击。而 <c>HTTRANSPARENT</c> 的转发只限同线程，跨进程无效——
    /// 实测证实过（同进程的对照点能穿透，跨进程的圆角处不能）。
    ///
    /// 分层窗口的命中测试是**按像素 alpha** 做的：只要那个点真是 alpha 0，系统就会
    /// 把点击放行给下层的任意窗口，不论它在哪个进程。所以正确的做法不是「告诉系统放行」，
    /// 而是「让它本来就透明」。
    ///
    /// 几何构成：`(整窗 − 面板外框) ∪ 圆角面板`。当前 <see cref="ChromeInset"/> 为 0，
    /// 所以「面板外框」就是整窗，几何退化成「圆角面板」——面板之外一律 alpha 0。
    /// 若将来恢复了留白，这一式子会自动把边框区一并保留，不必改这里。
    /// </remarks>
    private void UpdateGlassClip()
    {
        if (_sharedPanelClip is not null) { Clip = _sharedPanelClip; return; }
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            // 布局尚未完成，等 OnRenderSizeChanged 再来。
            Clip = null;
            return;
        }

        var panelWidth = Math.Max(0, width - ChromeInset.Left - ChromeInset.Right);
        var panelHeight = Math.Max(0, height - ChromeInset.Top - ChromeInset.Bottom);

        if (panelWidth <= 0 || panelHeight <= 0)
        {
            Clip = null;
            return;
        }

        var panelBox = new Rect(ChromeInset.Left, ChromeInset.Top, panelWidth, panelHeight);

        // 四个角取最大的半径，与命中判定保持一致。
        var radius = Math.Max(
            Math.Max(PanelCornerRadius.TopLeft, PanelCornerRadius.TopRight),
            Math.Max(PanelCornerRadius.BottomLeft, PanelCornerRadius.BottomRight));

        radius = Math.Min(radius, Math.Min(panelWidth, panelHeight) / 2);

        var frame = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)),
            new RectangleGeometry(panelBox));

        var panel = CreatePanelGeometryCore(panelBox, radius);

        var clip = new CombinedGeometry(GeometryCombineMode.Union, frame, panel);
        clip.Freeze();

        Clip = clip;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // 尺寸变了，挖洞的几何也得跟着变，否则缺口位置会错位。
        UpdateGlassClip();
    }

    /// <summary>
    /// 决定本次采用实时光学玻璃还是清透降级。
    /// </summary>
    /// <remarks>
    /// 用户要求降低透明度、或系统关闭了透明效果时，直接走自绘：那种情况下不该再模糊，
    /// 半透明本身就是用户想去掉的东西。
    /// </remarks>
    private static GlassSurfaceMode ResolveSurface(ThemeService? theme, AppearanceSettings? appearance)
    {
        if (appearance?.UseSystemBackdrop == false || theme?.UseOpaquePanels == true)
        {
            return GlassSurfaceMode.SelfDrawn;
        }

        return GlassSurfaceMode.OpticalGlass;
    }

    /// <summary>把选定的做法施加到窗口上。</summary>
    private void ApplySurface(GlassPalette palette)
    {
        var handle = Handle;
        if (handle == 0)
        {
            return;
        }

        if (SurfaceMode == GlassSurfaceMode.Acrylic)
        {
            var applied = WindowAcrylic.TryApply(
                handle,
                ResolveAcrylicTintAlpha(palette),
                palette.AcrylicTint.R,
                palette.AcrylicTint.G,
                palette.AcrylicTint.B);

            // 接口未公开，调用失败属于预期之内：安静降级，不留半吊子状态。
            if (!applied)
            {
                SurfaceMode = GlassSurfaceMode.SelfDrawn;
            }
        }
        else
        {
            WindowAcrylic.TryRemove(handle);
        }
    }

    /// <summary>Allows a window-specific material to control its native transmission density.</summary>
    protected virtual byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.AcrylicTintAlpha;

    /// <summary>
    /// 把调色板翻译成本窗口实际要画的那几个画刷。
    /// </summary>
    /// <remarks>
    /// 填充的选择放在这里，因为这是唯一同时知道「调色板」与「最终采用哪种做法」的地方。
    /// 用了亚克力时填充要几乎透明——色调已经由亚克力那层提供了，再叠一层就把模糊盖住了。
    /// </remarks>
    private void ApplyPaletteBrushes(GlassPalette palette)
    {
        if (UseOpaquePanel)
        {
            var color = palette.FallbackPanelBackground;
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            PanelBackgroundBrush = brush;
        }
        else
            PanelBackgroundBrush = palette.ResolvePanelBrush(SurfaceMode == GlassSurfaceMode.Acrylic);
        PanelBorderBrush = palette.PanelBorderBrush;
        var appearance = ThemeService.Current?.Appearance;
        DockBuiltinIconBrush = MonochromeBrush(appearance?.DockBuiltinIconBrightness ?? 1);
        LaunchpadIconFilled = appearance?.LaunchpadIconFilled ?? true;
        var foreground = UseLauncherTextBrightness
            ? MonochromeBrush(appearance?.LauncherTextBrightness ?? 1) : palette.ForegroundBrush;
        ForegroundBrush = foreground;
        SecondaryForegroundBrush = UseLauncherTextBrightness ? foreground : palette.SecondaryForegroundBrush;
        AccentBrush = palette.AccentBrush;
        Foreground = foreground;
    }

    private static SolidColorBrush MonochromeBrush(double brightness)
    {
        var value = (byte)Math.Round(255 * (double.IsFinite(brightness) ? Math.Clamp(brightness, 0, 1) : 1));
        var brush = new SolidColorBrush(Color.FromRgb(value, value, value));
        brush.Freeze(); return brush;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        ApplyAppearance();
    }
}
