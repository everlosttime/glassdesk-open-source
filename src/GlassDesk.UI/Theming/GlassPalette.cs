using System.Windows;
using System.Windows.Media;

namespace GlassDesk.UI.Theming;

/// <summary>
/// 一个窗口绘制时用到的颜色，已按浅色或深色算好。
/// </summary>
/// <remarks>
/// 用代码生成而不是资源字典，是为了让强调色与「降低透明度」能直接生效而不必换字典，
/// 也保证程序坞、启动台、设置中心三处必然解析出同一组值。
///
/// <b>两套面板填充是刻意的。</b>阶段 1 只有一套（55% 白色），结果是系统材质与自绘填充
/// 叠加后呈中灰乳白——本机系统主题是深色，DWM 给的是深色 Mica，上面再盖一层半透明白，
/// 两者相互抵消，壁纸几乎透不出来。所以按「背后有没有材质」分开：
/// 有材质时只做极轻的色调压制，没有材质时填充要独自承担玻璃观感。
/// </remarks>
public sealed class GlassPalette
{
    /// <summary>默认强调色：方案指定的蓝色。</summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0x0A, 0x84, 0xFF);

    private GlassPalette(
        bool isDark,
        Color accent,
        Color acrylicOverlay,
        Color fallbackPanel,
        Color acrylicTint,
        byte acrylicTintAlpha,
        Color panelBorder,
        Color foreground,
        Color secondaryForeground)
    {
        IsDark = isDark;
        Accent = accent;
        AcrylicOverlayBackground = acrylicOverlay;
        FallbackPanelBackground = fallbackPanel;
        AcrylicTint = acrylicTint;
        AcrylicTintAlpha = acrylicTintAlpha;
        PanelBorder = panelBorder;
        Foreground = foreground;
        SecondaryForeground = secondaryForeground;
    }

    public bool IsDark { get; }

    public Color Accent { get; }

    /// <summary>
    /// 亚克力生效时叠加的填充。必须很淡——色调已经由亚克力那层提供，
    /// 再叠一层浓的就把模糊盖住了，等于白做。
    /// </summary>
    public Color AcrylicOverlayBackground { get; }

    /// <summary>
    /// 嵌套面板及无背景材质时的轻薄填充，
    /// 并且刻意保留一点通透感，而不是退化成实心色块。
    /// </summary>
    public Color FallbackPanelBackground { get; }

    /// <summary>
    /// 交给亚克力的色调。
    /// </summary>
    /// <remarks>
    /// 这一层是浅色界面的关键。系统材质做不到这件事：Mica 的明暗跟系统主题走，
    /// 深色系统下拿不到浅色面板（实测只有 <c>#383838</c>）。亚克力的色调由我们给，
    /// 所以浅色主题在深色系统上依然是浅色面板。
    /// </remarks>
    public Color AcrylicTint { get; }

    /// <summary>色调层的不透明度。</summary>
    public byte AcrylicTintAlpha { get; }

    /// <summary>给面板描的那道细亮边，玻璃观感主要靠它。</summary>
    public Color PanelBorder { get; }

    public Color Foreground { get; }

    public Color SecondaryForeground { get; }

    public SolidColorBrush AccentBrush => CreateFrozenBrush(Accent);

    public Brush PanelBorderBrush => CreateBorderBrush();

    public SolidColorBrush ForegroundBrush => CreateFrozenBrush(Foreground);

    public SolidColorBrush SecondaryForegroundBrush => CreateFrozenBrush(SecondaryForeground);

    /// <summary>取该用的那层填充。</summary>
    /// <param name="acrylicApplied">是否真的拿到了背景模糊。</param>
    public Brush ResolvePanelBrush(bool acrylicApplied) => CreatePanelBrush(acrylicApplied);

    /// <summary>构造一组调色板。</summary>
    /// <param name="isDark">最终生效的明暗，跟随系统时已解析完毕。</param>
    /// <param name="accent">设置里的强调色。</param>
    /// <param name="opaquePanels">
    /// 用户要求降低透明度，或系统关闭了透明效果。此时不再通透，两套填充都退化为接近实色。
    /// </param>
    public static GlassPalette Create(bool isDark, Color accent, bool opaquePanels)
    {
        if (isDark)
        {
            return new GlassPalette(
                isDark: true,
                accent: accent,
                // 亚克力之上只留一道很淡的白色，给面板一点光泽，不盖住模糊。
                acrylicOverlay: Color.FromArgb(opaquePanels ? (byte)0x00 : (byte)0x0A, 0xFF, 0xFF, 0xFF),
                // 嵌套面板保持轻薄；折射与体积感由共享光学表面承担。
                fallbackPanel: opaquePanels
                    ? Color.FromArgb(0xF4, 0x1C, 0x1D, 0x24)
                    : Color.FromArgb(0x12, 0x1C, 0x1E, 0x26),
                acrylicTint: Color.FromRgb(0x1A, 0x1A, 0x1F),
                acrylicTintAlpha: 0x18,
                panelBorder: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                foreground: Color.FromRgb(0xF2, 0xF2, 0xF7),
                secondaryForeground: Color.FromArgb(0xB3, 0xF2, 0xF2, 0xF7));
        }

        return new GlassPalette(
            isDark: false,
            accent: accent,
            acrylicOverlay: Color.FromArgb(opaquePanels ? (byte)0x00 : (byte)0x14, 0xFF, 0xFF, 0xFF),
            fallbackPanel: opaquePanels
                ? Color.FromArgb(0xF4, 0xF7, 0xF8, 0xFC)
                : Color.FromArgb(0x14, 0xF0, 0xF4, 0xFC),
            acrylicTint: Color.FromRgb(0xF5, 0xF5, 0xF8),
            // 旧系统材质的兼容色调也保持轻薄，避免叠加灰蒙版。
            acrylicTintAlpha: 0x20,
            panelBorder: Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF),
            foreground: Color.FromRgb(0x1C, 0x1C, 0x1E),
            secondaryForeground: Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1E));
    }

    /// <summary>
    /// 解析 <c>#RRGGBB</c> 或 <c>#AARRGGBB</c>，失败时回退到默认强调色，
    /// 免得手工改坏的设置让界面完全没有颜色可用。
    /// </summary>
    public static Color ParseAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAccent;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value.Trim()) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
            // 落到默认值。
        }
        catch (NotSupportedException)
        {
            // 落到默认值。
        }

        return DefaultAccent;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        // 冻结的画刷渲染更省，也可以安全地跨线程共享。
        brush.Freeze();
        return brush;
    }

    private LinearGradientBrush CreatePanelBrush(bool acrylicApplied)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };

        if (acrylicApplied)
        {
            var top = IsDark ? (byte)0x1C : (byte)0x32;
            var middle = IsDark ? (byte)0x08 : (byte)0x16;
            var bottom = IsDark ? (byte)0x13 : (byte)0x22;
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(top, 0xFF, 0xFF, 0xFF), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(middle, 0xFF, 0xFF, 0xFF), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(bottom, Accent.R, Accent.G, Accent.B), 1));
        }
        else
        {
            var top = FallbackPanelBackground;
            var bottom = IsDark
                ? Color.FromArgb(top.A, 0x29, 0x2C, 0x36)
                : Color.FromArgb(top.A, 0xE7, 0xEC, 0xF5);
            brush.GradientStops.Add(new GradientStop(top, 0));
            brush.GradientStops.Add(new GradientStop(bottom, 1));
        }

        brush.Freeze();
        return brush;
    }

    private LinearGradientBrush CreateBorderBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0xA0 : (byte)0xB8, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x34 : (byte)0x58, 0xFF, 0xFF, 0xFF), 0.32));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x20 : (byte)0x42, 0xFF, 0xFF, 0xFF), 0.72));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(IsDark ? (byte)0x74 : (byte)0x9A, Accent.R, Accent.G, Accent.B), 1));
        brush.Freeze();
        return brush;
    }
}
