using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherAutoOrganizationSettingsTests
{
    [Fact]
    public void DesktopSettingsDescribeAllTopLevelItemsAndManualGroupingWhenAutomaticGroupingIsOff()
    {
        StaTest.Run(() =>
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-desktop-items-ui-" + Guid.NewGuid().ToString("N"))));
            data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                Assert.Equal("同步桌面项目到启动台", Assert.IsType<CheckBox>(window.FindName("LauncherAutoAddDesktopCheck")).Content);
                var description = Assert.IsType<TextBlock>(window.FindName("LauncherDesktopItemsDescription")).Text;
                foreach (var scope in new[] { "文件夹", "文件", "图片", "快捷方式", "桌面顶层", "不展开", "不复制" }) Assert.Contains(scope, description);
                var sync = Assert.IsType<TextBlock>(window.FindName("LauncherDesktopSyncDescription")).Text;
                foreach (var behavior in new[] { "新增", "删除", "自动管理入口", "改名" }) Assert.Contains(behavior, sync);
                var safety = Assert.IsType<TextBlock>(window.FindName("LauncherDesktopSyncSafetyDescription")).Text;
                foreach (var boundary in new[] { "不删除实际文件", "单独手动添加", "不可访问", "保留入口" }) Assert.Contains(boundary, safety);
                Assert.Equal("扫描桌面项目…", Assert.IsType<Button>(window.FindName("LauncherScanDesktopButton")).Content);
                var smart = Assert.IsType<CheckBox>(window.FindName("LauncherSmartGroupingCheck"));
                Assert.Equal("新项目按文件类型自动分组", smart.Content);
                smart.IsChecked = false;
                Assert.False(data.Settings.Current.Launcher.SmartGroupingEnabled);
                Assert.True(Assert.IsType<Button>(window.FindName("LauncherOrganizeExistingButton")).IsEnabled);
                Assert.Contains("手动创建、命名和整理分组", Assert.IsType<TextBlock>(window.FindName("LauncherTypeGroupingDescription")).Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DesktopSyncPreviewExplainsRemovalOfOnlyAutomaticallyManagedEntries()
    {
        StaTest.Run(() =>
        {
            var preview = new LauncherOrganizationPreviewWindow("同步桌面项目", "选择已有项目，或跳过存量并开启同步。", [], true);
            try
            {
                var notice = Assert.IsType<TextBlock>(preview.FindName("OrganizationPreviewNotice")).Text;
                Assert.Contains("删除", notice);
                Assert.Contains("自动管理入口", notice);
                Assert.Contains("单独手动添加", notice);
            }
            finally { preview.Close(); }
        });
    }

    [Fact]
    public void PreviewLoadsAppIconWithoutKeepingItsCacheFileOpen()
    {
        StaTest.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "GlassDesk-preview-icon-" + Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 40, 180, 255 }, 4)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var item = new LauncherOrganizationPreviewItem("app", "桌面项目", "其他", "测试");
            var iconProperty = typeof(LauncherOrganizationPreviewItem).GetProperty("IconPath");
            Assert.NotNull(iconProperty);
            iconProperty.SetValue(item, path);
            var dialog = new LauncherOrganizationPreviewWindow("预览", "测试", [item], false);
            try
            {
                var list = Assert.IsType<ListBox>(dialog.FindName("OrganizationPreviewList"));
                var label = Assert.IsType<StackPanel>(Assert.IsType<CheckBox>(list.Items[0]).Content);
                var row = Assert.IsType<DockPanel>(label.Children[0]);
                Assert.NotNull(Assert.Single(row.Children.OfType<Image>()).Source);
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            finally { dialog.Close(); if (File.Exists(path)) File.Delete(path); }
        });
    }

    [Fact]
    public void DesktopScanCanRepeatWithoutEnablingAutomaticAddAndShowsPartialFailure()
    {
        StaTest.Run(() =>
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-auto-scan-" + Guid.NewGuid().ToString("N"))));
            data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                var scans = 0;
                window.ScanDesktopAppsAsync = () => { scans++; return Task.FromResult("已添加 2 项，失败 1 项，请重试。"); };
                var button = Assert.IsType<Button>(window.FindName("LauncherScanDesktopButton"));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(2, scans);
                Assert.False(data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled);
                Assert.Contains("失败 1 项", Assert.IsType<TextBlock>(window.FindName("LauncherSettingsStatus")).Text);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(740, 560)]
    [InlineData(420, 340)]
    public void LongPreviewScrollsAndKeepsConfirmAndSkipActionsVisible(double width, double height)
    {
        StaTest.Run(() =>
        {
            var dialog = new LauncherOrganizationPreviewWindow("同步已有桌面项目", "选择已有桌面项目，或跳过存量并开启同步。",
                Enumerable.Range(0, 50).Select(index => new LauncherOrganizationPreviewItem(index.ToString(),
                    index == 0 ? "中文文件与较长的项目名称 · Presentation Final Version.pptx" : "桌面项目 " + (index + 1),
                    "其他", "按文件类型整理")).ToArray(), true);
            try
            {
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
                dialog.Width = width; dialog.Height = height;
                _ = new WindowInteropHelper(dialog).EnsureHandle();
                var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                foreach (var name in new[] { "ApplySelectedButton", "SkipExistingButton", "CancelPreviewButton" })
                {
                    var button = Assert.IsType<Button>(dialog.FindName(name));
                    var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    Assert.InRange(bounds.Left, 0, width); Assert.InRange(bounds.Right, 0, width);
                    Assert.InRange(bounds.Top, 0, height); Assert.InRange(bounds.Bottom, 0, height);
                }
                Assert.InRange(Assert.IsType<ListBox>(dialog.FindName("OrganizationPreviewList")).ActualHeight, 20, height - 80);
                var previewRoot = Environment.GetEnvironmentVariable("GLASSDESK_AUTO_ORGANIZATION_PREVIEW");
                if (!string.IsNullOrWhiteSpace(previewRoot))
                {
                    Directory.CreateDirectory(previewRoot);
                    var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(previewRoot, $"launcher-organization-{width:0}.png")); encoder.Save(file);
                }
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void CancelledDesktopPreviewKeepsOptInOffAndSuccessfulPreviewPersistsThenNotifies()
    {
        StaTest.Run(() =>
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-auto-confirm-" + Guid.NewGuid().ToString("N"))));
            data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                var changed = 0;
                window.LauncherSettingsChanged += (_, _) => changed++;
                var enable = typeof(SettingsWindow).GetProperty("EnableDesktopAutoAddAsync");
                Assert.NotNull(enable);
                var toggle = Assert.IsType<CheckBox>(window.FindName("LauncherAutoAddDesktopCheck"));
                window.EnableDesktopAutoAddAsync = () => Task.FromResult(new DesktopAutoAddEnableResult(false));
                toggle.IsChecked = true;
                Assert.False(toggle.IsChecked);
                Assert.False(data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled);
                Assert.Equal(0, changed);
                window.EnableDesktopAutoAddAsync = () => Task.FromResult(new DesktopAutoAddEnableResult(true));
                toggle.IsChecked = true;
                Assert.True(data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled);
                Assert.Equal(1, changed);
                Assert.Contains("桌面同步已开启", Assert.IsType<TextBlock>(window.FindName("LauncherSettingsStatus")).Text);
                var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
                Assert.True(reload.Current.Launcher.AutoAddDesktopAppsEnabled);
                toggle.IsChecked = false;
                Assert.False(data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled);
                Assert.Equal(2, changed);
                Assert.Contains("桌面同步已关闭", Assert.IsType<TextBlock>(window.FindName("LauncherSettingsStatus")).Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PreviewSelectionLeavesManualRowsUntouchedAndCanSkipAllExistingDesktopApps()
    {
        StaTest.Run(() =>
        {
            var previewType = typeof(SettingsWindow).Assembly.GetType("GlassDesk.Settings.LauncherOrganizationPreviewWindow");
            Assert.NotNull(previewType);
            var rowType = typeof(SettingsWindow).Assembly.GetType("GlassDesk.Settings.LauncherOrganizationPreviewItem");
            Assert.NotNull(rowType);
            var rows = Array.CreateInstance(rowType, 2);
            rows.SetValue(Activator.CreateInstance(rowType, ["one", "照片.jpg", "图片", "根据文件扩展名分类", false]), 0);
            rows.SetValue(Activator.CreateInstance(rowType, ["two", "自定义文件夹", "我的分组", "保留手动分类", true]), 1);
            var dialog = Assert.IsAssignableFrom<Window>(Activator.CreateInstance(previewType, ["添加已有桌面项目", "选择要导入的项目", rows, true]));
            try
            {
                var list = Assert.IsType<ListBox>(dialog.FindName("OrganizationPreviewList"));
                Assert.True(Assert.IsType<CheckBox>(list.Items[0]).IsChecked);
                Assert.False(Assert.IsType<CheckBox>(list.Items[1]).IsEnabled);
                var selected = Assert.IsAssignableFrom<IReadOnlyList<string>>(previewType.GetProperty("SelectedIds")!.GetValue(dialog));
                Assert.Equal(["one"], selected);
                var categoriesProperty = previewType.GetProperty("SelectedCategories");
                Assert.NotNull(categoriesProperty);
                var rowPanel = Assert.IsType<StackPanel>(Assert.IsType<CheckBox>(list.Items[0]).Content);
                var category = Assert.Single(rowPanel.Children.OfType<ComboBox>());
                Assert.All(category.Items.Cast<object>(), value => Assert.IsType<string>(Assert.IsType<ComboBoxItem>(value).Content));
                Assert.Equal(LauncherCategoryCatalog.All.Select(item => item.Id), category.Items.OfType<ComboBoxItem>().Select(item => Assert.IsType<string>(item.Tag)));
                Assert.DoesNotContain(category.Items.OfType<ComboBoxItem>(), item => item.Content is "办公学习" or "社交通讯" or "游戏");
                category.SelectedValue = "word";
                var categories = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(categoriesProperty.GetValue(dialog));
                Assert.Equal("word", categories["one"]);
                var editMode = previewType.GetMethod("SetCategoryEditingEnabled");
                Assert.NotNull(editMode);
                editMode.Invoke(dialog, [false]);
                Assert.False(category.IsEnabled);
                Assert.Contains("文件夹模式", Assert.IsType<TextBlock>(dialog.FindName("CategoryModeNotice")).Text);
                var manualPanel = Assert.IsType<StackPanel>(Assert.IsType<CheckBox>(list.Items[1]).Content);
                Assert.False(Assert.Single(manualPanel.Children.OfType<ComboBox>()).IsEnabled);
                Assert.IsType<Button>(dialog.FindName("SkipExistingButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("SkipExisting", previewType.GetProperty("Choice")!.GetValue(dialog)!.ToString());
                Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(previewType.GetProperty("SelectedIds")!.GetValue(dialog)));
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void UnrelatedLauncherEditsPreserveAutomaticChoicesAndFolderModeDisablesGroupingActions()
    {
        StaTest.Run(() =>
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-auto-ui-" + Guid.NewGuid().ToString("N"))));
            data.LoadAll();
            var auto = typeof(LauncherSettings).GetProperty("AutoAddDesktopAppsEnabled");
            var smart = typeof(LauncherSettings).GetProperty("SmartGroupingEnabled");
            Assert.NotNull(auto); Assert.NotNull(smart);
            auto.SetValue(data.Settings.Current.Launcher, true);
            smart.SetValue(data.Settings.Current.Launcher, false);
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                Assert.True(Assert.IsType<CheckBox>(window.FindName("LauncherAutoAddDesktopCheck")).IsChecked);
                var smartCheck = Assert.IsType<CheckBox>(window.FindName("LauncherSmartGroupingCheck"));
                Assert.False(smartCheck.IsChecked);
                Assert.IsType<Slider>(window.FindName("LauncherRowsSlider")).Value = 6;
                var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
                Assert.Equal(true, auto.GetValue(reload.Current.Launcher));
                Assert.Equal(false, smart.GetValue(reload.Current.Launcher));
                Assert.IsType<ComboBox>(window.FindName("LauncherModeCombo")).SelectedIndex = 0;
                Assert.False(smartCheck.IsEnabled);
                Assert.False(Assert.IsType<Button>(window.FindName("LauncherOrganizeExistingButton")).IsEnabled);
                Assert.True(Assert.IsType<Button>(window.FindName("LauncherScanDesktopButton")).IsEnabled);
            }
            finally { window.Close(); }
        });
    }
}
