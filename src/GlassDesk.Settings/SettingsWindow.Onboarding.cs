using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    public event EventHandler? OnboardingRequested;

    private void OnReplayOnboardingClick(object sender, RoutedEventArgs e) => OnboardingRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>教学中的入口直接导航到相应设置分类。</summary>
    public void NavigateToPage(string page) => SettingsPageList.SelectedItem = page;

    private GuidedTour? _onboardingGuide;

    /// <summary>Shows explanations beside real settings; it never edits their values.</summary>
    public GuidedTour BeginOnboardingGuide(string page)
    {
        _onboardingGuide?.Dispose();
        NavigateToPage(page);
        GuidedTourStep Step(FrameworkElement target, string title, string description, GuideInteraction interaction, string category)
            => new(target, title, description, interaction, () => NavigateToPage(category));
        var steps = page switch
        {
            "外观" => new[] {
                Step(LauncherMaterialCombo, "先选喜欢的启动台玻璃质感", "这里可以选择液态玻璃、磨砂玻璃或普通透明玻璃。你主动选择后才会保存；喜欢当前效果就直接下一步。", GuideInteraction.Selection, "外观"),
                Step(DockTransparencySlider, "让 Dock 更通透，或更清楚", "拖动滑块观察 Dock 的透明度。文字不容易看清时，可以增加不透明程度。每次调整都会即时保存。", GuideInteraction.Value, "外观"),
                Step(LauncherTransparencySlider, "启动台可以单独调整", "这个滑块只控制启动台透明度，不必与 Dock 一样。不确定时保留当前值即可。", GuideInteraction.Value, "外观"),
                Step(DockScaleSlider, "找到舒服的图标大小", "这里是 Dock 的整体大小。试着小幅拖动，观察真实桌面上的变化；教学不会替你移动或删除图标。", GuideInteraction.Value, "Dock") },
            "Dock" => new[] {
                Step(DockDisplayModeCombo, "让 Dock 常驻，或自动隐藏", "按自己的习惯选择显示方式。自动隐藏后可以靠近所在屏幕边缘唤出。", GuideInteraction.Selection, "Dock"),
                Step(DockScaleSlider, "调整整体大小", "轻轻拖动滑块试试。大小变化不会改变你添加的应用或排列顺序。", GuideInteraction.Value, "Dock"),
                Step(ShowLauncherEntryCheckBox, "认识内置快捷入口", "这里控制九宫格启动台入口是否显示。它与自己添加的应用图标是独立的；保留勾选更方便打开启动台。", GuideInteraction.Explore, "Dock") },
            "信息栏" => new[] {
                Step(ShowStatusBarCheckBox, "桌面信息，一眼就看到", "信息栏展示时间及系统状态。由你决定是否启用；教学不会自动改变开关。", GuideInteraction.Toggle, "信息栏"),
                Step(StatusBarEdgeCombo, "放到顺手的屏幕位置", "启用信息栏后，可以在这里选择位置。选项不可用时先看懂说明，再直接下一步也可以。", GuideInteraction.Selection, "信息栏"),
                Step(StatusBarTextBrightnessSlider, "让文字适合你的壁纸", "调整明暗，让浅色或深色壁纸上的文字更清楚。这里只改变信息栏的文字和图标明暗。", GuideInteraction.Value, "信息栏") },
            "快捷操作" => new[] {
                Step(ToggleLauncherHotkeyBox, "记住打开启动台的快捷键", "这里显示当前绑定。需要修改时再点击录制并按下新组合；不录制也能继续，按 Esc 可取消录制。", GuideInteraction.Explore, "快捷操作"),
                Step(ToggleGameSleepHotkeyBox, "给游戏休眠留一个快捷入口", "游戏休眠会暂停部分后台工作。这里可以查看或录制它的快捷键，不会因为教学自动启用休眠。", GuideInteraction.Explore, "快捷操作"),
                Step(AutoSuspendWhenFullscreenCheck, "全屏时自动减少后台工作", "开启后，全屏应用运行时会自动休眠。正常使用不必反复操作；如果不需要，保留当前选项即可。", GuideInteraction.Toggle, "性能") },
            "性能" => new[] {
                Step(AutoSuspendWhenFullscreenCheck, "全屏时自动休眠", "开启后，全屏应用运行时会自动减少背景处理。是否开启由你选择，不会关闭玻璃效果或移除应用。", GuideInteraction.Toggle, "性能"),
                Step(ToggleGameSleepHotkeyBox, "也可以手动切换", "这里能查看游戏休眠的快捷键。日常还可以从托盘菜单手动开关。", GuideInteraction.Explore, "快捷操作") },
            "备份恢复" => new[] {
                Step(CreateBackupButton, "先认识创建备份", "整理完桌面后，可以主动点击这里，选择一个数据目录之外的文件夹保存备份。教学不会自动打开选择器或创建文件。", GuideInteraction.Explore, "备份恢复"),
                Step(BackupInfoText, "记清备份里有什么", "备份包含设置、应用库、Dock 和启动台布局以及自定义图标。恢复会替换当前配置，需要恢复时再操作。", GuideInteraction.Explore, "备份恢复"),
                Step(ReplayOnboardingButton, "以后随时可以重看", "在“关于”里找到这个按钮即可重新开始教学。现在点“完成实操”返回本次教学，继续下一章节。", GuideInteraction.Explore, "关于") },
            _ => new[] {
                Step(SettingsPageList, "从左边找到你要的设置", "外观、Dock、信息栏、启动台都在这里。点击一个分类，右边会滚动到对应位置；设置窗口也可以直接上下滚动。", GuideInteraction.Selection, page),
                Step(ProductVersionText, "了解正在使用的版本", "“关于”中可以查看版本、检查更新，点击“下载入口”可扫描官网二维码。教学不会主动下载或安装更新。", GuideInteraction.Explore, "关于"),
                Step(ReplayOnboardingButton, "找回这份教学", "以后想再看一遍，就到 设置 → 关于 → 重新查看新手教学。点“完成实操”即可回到刚才的章节。", GuideInteraction.Explore, "关于") }
        };
        var tour = new GuidedTour(this, SettingsGuideRoot, steps);
        _onboardingGuide = tour;
        tour.Closed += (_, _) => { if (ReferenceEquals(_onboardingGuide, tour)) _onboardingGuide = null; };
        return tour;
    }
}
