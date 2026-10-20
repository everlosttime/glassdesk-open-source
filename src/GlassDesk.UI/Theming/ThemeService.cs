using System.Windows;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using Microsoft.Win32;

// WPF 自带 System.Windows.ThemeMode，在同时导入 System.Windows 与设置模型的地方
// 会与我们的类型撞名。
using ThemeMode = GlassDesk.Core.Models.ThemeMode;

namespace GlassDesk.UI.Theming;

/// <summary>
/// 产品外观的唯一事实来源。
/// </summary>
/// <remarks>
/// 刻意做成进程级，而不是按窗口注入。
/// 任何时刻生效的主题都只有一个，Dock、启动台、设置每个窗口
/// 都必须解析出同一套配色；把它变成静态事实，
/// 就不会出现外观变更后两个窗口各说各话的情况。
///
/// 模式为跟随系统时，本服务订阅操作系统的偏好通知并重新解析，
/// 这样在 Windows 里切换浅色与深色，
/// 无需重启即可更新我们的窗口。
/// </remarks>
public sealed class ThemeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ThemeService(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;

        Appearance = new AppearanceSettings();
        Palette = ResolvePalette(Appearance);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>本进程唯一的主题服务。</summary>
    public static ThemeService? Current { get; private set; }

    /// <summary>创建并安装进程级实例。</summary>
    public static ThemeService Initialize(Dispatcher dispatcher)
    {
        Current?.Dispose();
        Current = new ThemeService(dispatcher);
        return Current;
    }

    public AppearanceSettings Appearance { get; private set; }

    /// <summary>跟随系统已解析后的实际主题。</summary>
    public bool IsDark { get; private set; }

    public GlassPalette Palette { get; private set; }

    /// <summary>
    /// 应当避免半透明时为 true：要么用户要求如此，
    /// 要么 Windows 透明效果已关闭，
    /// 半透明面板放在桌面其余部分旁边会显得不对。
    /// </summary>
    public bool UseOpaquePanels { get; private set; }

    /// <summary>任何值变化后在 UI 线程上触发。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 采用新的外观设置并通知每个监听者。
    /// 无论实际有没有变化，调用都是安全的。
    /// </summary>
    public void Update(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        Appearance = appearance;
        Refresh();
    }

    /// <summary>重新解析配色并触发 <see cref="Changed"/>。</summary>
    public void Refresh()
    {
        IsDark = Appearance.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,

            // 跟随操作系统。注册表值缺失或不可读时按浅色处理。
            _ => !SystemThemeReader.IsLightTheme(),
        };

        UseOpaquePanels = Appearance.ReduceTransparency || !SystemThemeReader.IsTransparencyEnabled();
        Palette = GlassPalette.Create(IsDark, GlassPalette.ParseAccent(Appearance.AccentColor), UseOpaquePanels);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 用户要求时抑制动画，
    /// Windows 通过同一设置块报告减少动画的偏好时同样抑制。
    /// </summary>
    public bool AnimationsEnabled => !Appearance.ReduceMotion;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _disposed = true;

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // 在系统线程上触发。只有跟随系统和操作系统驱动的透明效果需要刷新，
        // 而这些属性的每次读取都归 UI 线程所有。
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                // Shell icon visibility also sends General notifications. Broadcast
                // only a changed resolved palette; otherwise every glass surface
                // unnecessarily discards its completed desktop material.
                var next = ResolvePalette(Appearance);
                if (Palette.IsDark == next.IsDark && Palette.Accent == next.Accent
                    && Palette.FallbackPanelBackground == next.FallbackPanelBackground) return;
                Refresh();
            }
        });
    }

    private static GlassPalette ResolvePalette(AppearanceSettings appearance)
    {
        var isDark = appearance.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemThemeReader.IsLightTheme(),
        };

        var opaque = appearance.ReduceTransparency || !SystemThemeReader.IsTransparencyEnabled();
        return GlassPalette.Create(isDark, GlassPalette.ParseAccent(appearance.AccentColor), opaque);
    }

    /// <summary>给只想知道操作系统如何配置的调用方的便捷方法。</summary>
    public static bool SystemPrefersDark() => !SystemThemeReader.IsLightTheme();

    /// <summary>暴露出来，是为了让诊断能用一行报告解析后的状态。</summary>
    public string Describe() =>
        $"主题={Appearance.Theme}（实际 {(IsDark ? "深色" : "浅色")}）" +
        $"，不透明面板={(UseOpaquePanels ? "是" : "否")}" +
        $"，动画={(AnimationsEnabled ? "开" : "关")}" +
        $"，{WindowsCapabilities.Describe()}";
}
