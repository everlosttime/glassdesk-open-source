using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlassDesk.UI.Controls;
using GlassDesk.Core.Models;
using System.IO;
using System.Windows.Media.Imaging;

namespace GlassDesk.Settings;

public sealed record LauncherOrganizationPreviewItem(string Id, string DisplayName, string GroupName, string Reason, bool IsManual = false)
{
    public string? CategoryId { get; init; }
    public string? IconPath { get; init; }
}
public enum LauncherOrganizationPreviewChoice { Cancel, ApplySelected, SkipExisting }

/// <summary>只收集用户选择；关闭或取消不会修改应用库和启动台布局。</summary>
public sealed class LauncherOrganizationPreviewWindow : GlassWindow
{
    private readonly List<(LauncherOrganizationPreviewItem Item, CheckBox Choice, ComboBox Category)> _rows = [];
    private readonly Button _apply;
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _categoryModeNotice = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75,
        Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
    public LauncherOrganizationPreviewChoice Choice { get; private set; }
    public IReadOnlyList<string> SelectedIds => Choice == LauncherOrganizationPreviewChoice.SkipExisting
        ? [] : _rows.Where(row => row.Choice.IsChecked == true && !row.Item.IsManual).Select(row => row.Item.Id).ToArray();
    public IReadOnlyDictionary<string, string> SelectedCategories => _rows.Where(row => SelectedIds.Contains(row.Item.Id))
        .ToDictionary(row => row.Item.Id, row => row.Category.SelectedValue as string ?? LauncherCategoryCatalog.Uncategorized, StringComparer.Ordinal);

    public LauncherOrganizationPreviewWindow(string title, string description,
        IReadOnlyList<LauncherOrganizationPreviewItem> items, bool desktopEnable)
        : this(title, description, items, desktopEnable, false) { }

    public LauncherOrganizationPreviewWindow(string title, string description,
        IReadOnlyList<LauncherOrganizationPreviewItem> items, bool desktopEnable, bool desktopImportOnly)
    {
        ArgumentNullException.ThrowIfNull(items);
        Title = title; Width = 740; Height = 560; MinWidth = 420; MinHeight = 340;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        AutomationProperties.SetName(this, title);
        NameScope.SetNameScope(this, new NameScope());
        var panel = new DockPanel { Margin = new Thickness(22) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = description, Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap, Opacity = .8 });
        RegisterName("CategoryModeNotice", _categoryModeNotice); heading.Children.Add(_categoryModeNotice);
        var selection = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_count, Dock.Right); selection.Children.Add(_count);
        var selectAll = new Button { Content = "全选", MinWidth = 60, Margin = new Thickness(0, 0, 8, 0) };
        var clearAll = new Button { Content = "取消全选", MinWidth = 80 };
        RegisterName("SelectAllButton", selectAll); RegisterName("ClearSelectionButton", clearAll);
        selectAll.Click += (_, _) => SelectAll(true); clearAll.Click += (_, _) => SelectAll(false);
        var selectionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        selectionButtons.Children.Add(selectAll); selectionButtons.Children.Add(clearAll);
        selection.Children.Add(selectionButtons); heading.Children.Add(selection);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);

        var footer = new StackPanel();
        var notice = new TextBlock { Text = desktopImportOnly
                ? "只导入所选项目，桌面同步开关保持原状。同步删除只移除自动管理入口，单独手动添加的内容保留。"
                : desktopEnable
                ? "跳过未选存量。同步新增、改名和删除，仅移除自动管理入口；单独手动添加的内容保留。"
                : "只添加所选项目的文件类型分类，保留已有顺序、文件夹和手动分组。",
            TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 12, 0, 0) };
        RegisterName("OrganizationPreviewNotice", notice); footer.Children.Add(notice);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 66, Margin = new Thickness(0, 0, 8, 6), IsCancel = true };
        cancel.Click += (_, _) => Complete(LauncherOrganizationPreviewChoice.Cancel);
        RegisterName("CancelPreviewButton", cancel); actions.Children.Add(cancel);
        if (desktopEnable)
        {
            var skip = new Button { Content = "跳过存量并开启", MinWidth = 125, Margin = new Thickness(0, 0, 8, 6) };
            RegisterName("SkipExistingButton", skip); actions.Children.Add(skip);
            skip.Click += (_, _) => Complete(LauncherOrganizationPreviewChoice.SkipExisting);
        }
        _apply = new Button { Content = desktopImportOnly ? "导入所选" : desktopEnable ? "导入所选并开启" : "应用所选分类", MinWidth = 125,
            Margin = new Thickness(0, 0, 0, 6), IsDefault = true };
        RegisterName("ApplySelectedButton", _apply); actions.Children.Add(_apply);
        _apply.Click += (_, _) => { if (SelectedIds.Count > 0) Complete(LauncherOrganizationPreviewChoice.ApplySelected); };
        footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);

        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "项目分类预览"); RegisterName("OrganizationPreviewList", list);
        foreach (var item in items)
        {
            var label = new StackPanel();
            var appName = new DockPanel();
            var defaultIcon = CreatePreviewIcon(item.IconPath);
            DockPanel.SetDock(defaultIcon, Dock.Left); appName.Children.Add(defaultIcon);
            appName.Children.Add(new TextBlock { Text = item.DisplayName + (item.IsManual ? " · " + item.GroupName : string.Empty),
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(appName);
            label.Children.Add(new TextBlock { Text = item.IsManual ? "已手动整理，保留现有分类" : item.Reason,
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .7, Margin = new Thickness(0, 3, 0, 0) });
            var category = new ComboBox { ItemsSource = LauncherCategoryCatalog.All.Select(value => new ComboBoxItem { Content = value.Name, Tag = value.Id }).ToArray(),
                SelectedValuePath = nameof(ComboBoxItem.Tag),
                SelectedValue = item.CategoryId ?? LauncherCategoryCatalog.All.FirstOrDefault(value => value.Name == item.GroupName)?.Id
                    ?? LauncherCategoryCatalog.Uncategorized,
                IsEnabled = !item.IsManual, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(category, item.DisplayName + "的分类");
            label.Children.Add(category);
            var check = new CheckBox { Content = label, IsChecked = !item.IsManual, IsEnabled = !item.IsManual,
                Padding = new Thickness(4, 6, 4, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(check, item.DisplayName + "，" + item.GroupName);
            check.Checked += (_, _) => UpdateCount(); check.Unchecked += (_, _) => UpdateCount();
            _rows.Add((item, check, category)); list.Items.Add(check);
        }
        if (items.Count == 0) panel.Children.Add(new TextBlock { Text = "没有需要处理的项目。", Opacity = .7,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        else panel.Children.Add(list);
        var surface = new Grid(); surface.Children.Add(new LiquidGlassSurface { UseLauncherTransparency = true });
        surface.Children.Add(panel); Content = surface; UpdateCount();
    }

    private static FrameworkElement CreatePreviewIcon(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 32; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return new Image { Source = bitmap, Width = 28, Height = 28, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException) { }
        }
        return new TextBlock { Text = "▦", FontSize = 24, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    private void SelectAll(bool selected)
    {
        foreach (var row in _rows) if (!row.Item.IsManual) row.Choice.IsChecked = selected;
        UpdateCount();
    }

    public void SetCategoryEditingEnabled(bool enabled)
    {
        foreach (var row in _rows) row.Category.IsEnabled = enabled && !row.Item.IsManual;
        _categoryModeNotice.Text = enabled ? string.Empty : "当前为文件夹模式，本次只添加项目。切换到分组模式后可按文件类型整理分类。";
        _categoryModeNotice.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCount()
    {
        var selected = SelectedIds.Count;
        _count.Text = $"已选 {selected} / {_rows.Count(row => !row.Item.IsManual)} 项";
        _apply.IsEnabled = selected > 0;
    }

    private void Complete(LauncherOrganizationPreviewChoice choice)
    {
        Choice = choice;
        if (IsVisible) DialogResult = choice != LauncherOrganizationPreviewChoice.Cancel;
    }
}
