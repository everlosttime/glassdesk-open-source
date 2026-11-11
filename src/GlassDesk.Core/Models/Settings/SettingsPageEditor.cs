using System.Text.Json;
using GlassDesk.Core.Input;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Models.Settings;

public static class SettingsPageEditor
{
    public static GlassDeskSettings Clone(GlassDeskSettings settings) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(settings, GlassDeskJsonContext.Default.GlassDeskSettings),
        GlassDeskJsonContext.Default.GlassDeskSettings)!;

    public static void Apply(GlassDeskSettings target, GlassDeskSettings source)
    {
        target.UpdateNoticeSnoozedUntilUtc = source.UpdateNoticeSnoozedUntilUtc;
        target.Appearance = source.Appearance;
        target.NaiwaOptions = source.NaiwaOptions;
        target.Dock = source.Dock;
        target.Launcher = source.Launcher;
        target.Shortcuts = source.Shortcuts;
        target.Display = source.Display;
        target.Performance = source.Performance;
        target.Nearby = source.Nearby;
        target.DesktopTaskbar = source.DesktopTaskbar;
    }

    public static GlassDeskSettings ResetPage(GlassDeskSettings settings, string pageName)
    {
        var copy = Clone(settings);
        switch (pageName)
        {
            case "外观":
                copy.Appearance = new()
                {
                    DockCornerRadius = settings.Appearance.DockCornerRadius,
                    NaiwaEnabled = settings.Appearance.NaiwaEnabled,
                };
                break;
            case "Dock":
                copy.Dock = new() { TargetDisplayId = settings.Dock.TargetDisplayId };
                copy.Appearance.DockCornerRadius = 40;
                break;
            case "回收站":
                copy.Appearance.NaiwaEnabled = false;
                copy.NaiwaOptions = new();
                break;
            case "启动台": copy.Launcher = new(); break;
            case "快捷操作": copy.Shortcuts = new(); break;
            case "显示器": copy.Display = new(); copy.Dock.TargetDisplayId = null; break;
            case "性能": copy.Performance = new(); break;
            case "附近传输": copy.Nearby = new(); break;
            case "桌面与任务栏": copy.DesktopTaskbar = new(); break;
            default: throw new ArgumentException("此页面没有可重置的配置。", nameof(pageName));
        }
        return copy;
    }

    public static string? Validate(GlassDeskSettings settings)
    {
        var dock = settings.Dock;
        static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if ((dock.FrameLengthDip != 0 && !Range(dock.FrameLengthDip, 64, DockSettings.MaximumFrameLengthDip))
            || !Range(dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale)
            || !Range(dock.StatusBarSizeScale, DockSettings.MinimumStatusBarSizeScale, DockSettings.MaximumStatusBarSizeScale)
            || !Range(dock.IconSize, 32, 96) || !Range(dock.IconSpacing, 0, 24) || !Range(dock.MagnificationPeak, 1, 2)
            || !Range(dock.OffsetX, -200, 200) || !Range(dock.OffsetY, -200, 200)
            || !Range(dock.StatusBarOffsetX, -10000, 10000) || !Range(dock.StatusBarOffsetY, -10000, 10000)
            || !Range(dock.AutoHideLeaveDelayMs, 0, 3000) || !Range(dock.EdgeHotzoneSize, 1, 64)
            || !Enum.IsDefined(dock.Placement) || !Enum.IsDefined(dock.StatusBarEdge)) return "Dock 数值超出允许范围。";
        if (!Range(settings.Launcher.Rows, 2, 10) || !Range(settings.Launcher.Columns, 2, 12)
            || !Range(settings.Launcher.WindowWorkAreaRatio, 0.3, 1)) return "启动台网格或窗口比例超出允许范围。";
        if (!Range(settings.Appearance.DockCornerRadius, 0, 40) || !Range(settings.Appearance.LauncherCornerRadius, 0, 80)
            || !Range(settings.Appearance.GlassTintOpacity, 0, 1)
            || !Range(settings.Appearance.GlassBackgroundClarity, 0, 1)
            || !Range(settings.Appearance.DockGlassTransparency, 0, 1)
            || !Range(settings.Appearance.LauncherGlassTransparency, 0, 1)
            || !Range(settings.Appearance.StatusBarGlassTransparency ?? settings.Appearance.DockGlassTransparency, 0, 1)
            || !Range(settings.Appearance.GetGlassTransparency(GlassMaterialTarget.ControlCenter), 0, 1)
            || !Range(settings.Appearance.DockBuiltinIconBrightness, 0, 1)
            || !Range(settings.Appearance.LauncherTextBrightness, 0, 1)
            || !Range(settings.Appearance.StatusBarTextBrightness, 0, 1)
            || !Enum.IsDefined(settings.Appearance.Theme)
            || !Enum.IsDefined(settings.Appearance.GlassMaterial)
            || !Enum.IsDefined(settings.Appearance.LauncherGlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
            || !Enum.IsDefined(settings.Appearance.DockGlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
            || !Enum.IsDefined(settings.Appearance.StatusBarGlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
            || !Enum.IsDefined(settings.Appearance.ControlCenterGlassMaterial)) return "外观数值超出允许范围。";
        var shortcuts = settings.Shortcuts;
        if (!Range(shortcuts.HotCornerDwellMs, 100, 3000) || !Enum.IsDefined(shortcuts.HotCornerTarget)
            || !Enum.IsDefined(shortcuts.MouseBackAction) || !Enum.IsDefined(shortcuts.MouseForwardAction)) return "热角停留时间应为 100–3000 毫秒，且触发动作必须有效。";
        if (shortcuts.HotCornersEnabled && shortcuts.HotCornerTarget == HotCorner.None) return "开启热角后需要选择一个角落。";
        var seen = new HashSet<HotkeyChord>();
        foreach (var (_, definition) in HotkeyText.Bindings(shortcuts))
        {
            if (!definition.Enabled) continue;
            if (!HotkeyText.TryGetChord(definition, out var chord, out var error)) return error;
            if (!seen.Add(chord)) return "多个操作使用了同一个快捷键。";
        }
        return null;
    }
}
