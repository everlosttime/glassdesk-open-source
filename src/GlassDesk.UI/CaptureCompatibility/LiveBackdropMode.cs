using GlassDesk.UI.Theming;

namespace GlassDesk.UI.CaptureCompatibility;

/// <summary>
/// 「背景实时更新」开关的唯一读取点。
/// </summary>
/// <remarks>
/// 设置界面只负责把 <see cref="GlassDesk.Core.Models.Settings.AppearanceSettings.LiveBackdrop"/>
/// 推进 <see cref="ThemeService"/>；捕获链路（包括它自己的后台 STA 线程）只从这里读，
/// 于是两侧不需要互相知道对方，也不会出现两份状态各说各话。
///
/// 取不到主题服务时按开启处理：那只可能发生在窗口与捕获线程都还没建立的时候，
/// 此时这条分支根本不会被执行，默认值只决定「万一」时的行为。
/// </remarks>
internal static class LiveBackdropMode
{
    internal static bool Enabled => ThemeService.Current?.Appearance.LiveBackdrop ?? true;
}
