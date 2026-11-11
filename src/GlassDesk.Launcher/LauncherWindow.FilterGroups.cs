using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.UI.Controls;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private string? _activeFilterGroupId;
    private LauncherLayout? _flatProjection;
    private (long Version, int Capacity, string? GroupId) _flatProjectionKey;
    private (long Version, bool Enabled)? _filterButtonsKey;

    private LauncherLayout BrowseLayout
    {
        get
        {
            if (_dragEdit is not null) return _dragEdit.Draft;
            if (_workspace is null || _services is null) return new();
            if (!_settings.GroupModeEnabled) return _workspace.Layout;
            var key = (_services.Layouts.LauncherVersion, _view.Capacity, _activeFilterGroupId);
            if (_flatProjection is null || _flatProjectionKey != key)
            {
                _flatProjection = LauncherFilterRules.Project(_workspace.Layout, _activeFilterGroupId, _view.Capacity);
                _flatProjectionKey = key;
            }
            return _flatProjection;
        }
    }

    private void RenderFilterGroups()
    {
        if (_workspace is null || _services is null) return;
        var groups = _workspace.Layout.FilterGroups;
        if (!_settings.GroupModeEnabled || !groups.Any(group => group.Id == _activeFilterGroupId))
            _activeFilterGroupId = null;
        FilterGroupBar.Visibility = _settings.GroupModeEnabled && groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var key = (_services.Layouts.LauncherVersion, _settings.GroupModeEnabled);
        if (_filterButtonsKey != key)
        {
            CancelFilterGroupDrag();
            FilterGroupButtons.Children.Clear();
            foreach (var group in groups)
            {
                var id = group.Id;
                var chip = new ToggleButton
                {
                    Content = group.Name, Tag = id, ToolTip = group.Name,
                    Style = (Style)FindResource("FilterGroupChip"),
                };
                System.Windows.Automation.AutomationProperties.SetName(chip, "分组：" + group.Name);
                chip.Click += (_, _) => ToggleFilterGroup(id);
                chip.PreviewMouseLeftButtonDown += OnFilterGroupMouseDown;
                var menu = CreateTrackedContextMenu();
                AddMenu(menu, "重命名", () => RenameFilterGroup(id));
                AddMenu(menu, "编辑图标…", () => EditFilterGroup(id));
                AddMenu(menu, "删除分组", () => CommitFilterEdit(layout => LauncherFilterRules.RemoveGroup(layout, id)));
                chip.ContextMenu = menu;
                FilterGroupButtons.Children.Add(chip);
            }
            _filterButtonsKey = key;
        }
        RefreshFilterSelection();
    }

    private void RefreshFilterSelection()
    {
        var selected = _filterTransitionActive ? _filterTransitionTarget : _activeFilterGroupId;
        foreach (var chip in FilterGroupButtons.Children.OfType<ToggleButton>())
            chip.IsChecked = Equals(chip.Tag, selected);
    }

    private void ToggleFilterGroup(string id)
    {
        if (!_settings.GroupModeEnabled || _workspace?.Layout.FilterGroups.Any(group => group.Id == id) != true) return;
        CancelDrag();
        ResetFolderPresentation();
        var selected = _filterTransitionActive ? _filterTransitionTarget : _activeFilterGroupId;
        SwitchFilterGroup(selected == id ? null : id);
    }

    private void AddFilterGroupMenuOnOpen(ContextMenu menu, string itemId)
    {
        var insertAt = menu.Items.Count;
        MenuItem? submenu = null;
        menu.Opened += (_, _) =>
        {
            if (submenu is not null) menu.Items.Remove(submenu);
            submenu = null;
            if (!_settings.GroupModeEnabled || _workspace is null) return;

            foreach (var group in _workspace.Layout.FilterGroups)
            {
                if (group.ItemIds.Contains(itemId)) continue;
                submenu ??= new MenuItem { Header = "添加到已有分组" };
                AddMenu(submenu, group.Name.Replace("_", "__", StringComparison.Ordinal),
                    () => AddIconToFilterGroup(group.Id, itemId));
            }
            if (submenu is not null) menu.Items.Insert(insertAt, submenu);
        };
    }

    private void AddIconToFilterGroup(string groupId, string itemId)
    {
        CommitFilterEdit(layout =>
        {
            var group = layout.FilterGroups.FirstOrDefault(group => group.Id == groupId)
                ?? throw new InvalidOperationException("分组已不存在，请重试。");
            if (!LauncherLayoutRules.AppIds(layout).Contains(itemId))
                throw new InvalidOperationException("图标已不在启动台中，请重试。");
            if (!group.ItemIds.Contains(itemId))
                group.ItemIds.Add(itemId);
            return layout;
        });
    }

    private void CommitFilterEdit(Func<LauncherLayout, LauncherLayout> transform)
    {
        if (_workspace is null) return;
        CancelDrag();
        var edit = _workspace.BeginEdit();
        try
        {
            edit.Draft = transform(edit.Draft);
            if (!_workspace.Commit(edit)) ShowNotice("应用库或布局已变化，请重试分组操作。");
            else _notice = null;
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        { ShowNotice("分组未保存，原有数据仍保留：" + ex.Message); }
        RenderTiles();
    }

    private void RenameFilterGroup(string id)
    {
        var group = _workspace?.Layout.FilterGroups.FirstOrDefault(group => group.Id == id);
        if (group is null) return;
        var values = AskFields("重命名分组", true, ("分组名称（1–60 个字符）", group.Name));
        if (values is not null)
            CommitFilterEdit(layout => LauncherFilterRules.RenameGroup(layout, id, values[0]));
    }

    private void AddFilterGroup()
    {
        if (_workspace is null || _services is null) return;
        var values = AskFields("添加分组", true, ("分组名称（1–60 个字符）", ""));
        if (values is null) return;
        var name = values[0].Trim();
        if (name.Length is < 1 or > 60) { ShowNotice("分组名称应为 1–60 个字符。"); return; }
        var ids = LauncherLayoutRules.AppIds(_workspace.Layout).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) { ShowNotice("请先向启动台添加图标，再创建分组。"); return; }
        var selection = SelectFilterMembers(name, ids);
        if (selection is not null)
            CommitFilterEdit(layout => LauncherFilterRules.AddGroup(layout, "filter-" + Guid.NewGuid().ToString("N"), name, selection));
    }

    private void EditFilterGroup(string id)
    {
        var group = _workspace?.Layout.FilterGroups.FirstOrDefault(group => group.Id == id);
        if (group is null || _services is null) return;
        var version = (_services.Layouts.LauncherVersion, _services.Library.Version);
        var ids = LauncherLayoutRules.AppIds(_workspace!.Layout).Distinct(StringComparer.Ordinal).ToList();
        var selection = SelectFilterMembers(group.Name, ids, group.ItemIds);
        if (selection is null) return;
        if (version != (_services.Layouts.LauncherVersion, _services.Library.Version))
        { ShowNotice("编辑期间应用库或分组已变化，请重新打开后再试。"); return; }
        CommitFilterEdit(layout => LauncherFilterRules.UpdateGroupMembers(layout, id, selection));
    }

    private List<string>? SelectFilterMembers(string name, IReadOnlyList<string> ids, IReadOnlyCollection<string>? currentMembers = null)
    {
        var editing = currentMembers is not null;
        var dialog = new GlassPromptWindow
        {
            UseMenuBackground = true,
            Owner = this, Title = editing ? "编辑分组图标" : "选择分组图标", Width = 680, Height = 550,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        root.Children.Add(title);
        var search = new TextBox { MinHeight = 32, Padding = new Thickness(8, 4, 8, 4), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ToolTip = "筛选已添加到启动台的图标" };
        search.SetBinding(Control.ForegroundProperty, new Binding(nameof(ForegroundBrush)) { Source = dialog });
        search.SetBinding(TextBox.CaretBrushProperty, new Binding(nameof(ForegroundBrush)) { Source = dialog });
        var searchBorder = new PhoneBorder { Child = search, CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 12) };
        searchBorder.SetBinding(Border.BorderBrushProperty, new Binding(nameof(PanelBorderBrush)) { Source = dialog });
        System.Windows.Automation.AutomationProperties.SetName(search, "筛选可选图标");
        Grid.SetRow(searchBorder, 1); root.Children.Add(searchBorder);
        var items = new WrapPanel();
        var scroll = new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var selected = (currentMembers ?? []).Intersect(ids, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var create = new Button { Content = editing ? "保存更改" : "创建分组", MinWidth = 92, IsDefault = true, IsEnabled = editing };
        var actionStyle = new Style(typeof(Button), (Style)FindResource("FolderActionButton"));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .45)); actionStyle.Triggers.Add(disabled);
        cancel.Style = create.Style = actionStyle;
        create.Background = new SolidColorBrush(Color.FromArgb(70, 10, 132, 255));
        actions.Children.Add(cancel); actions.Children.Add(create);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        var count = new TextBlock { Text = "请选择图标 · 已选 0 项", VerticalAlignment = VerticalAlignment.Center, Opacity = .7 };
        footer.Children.Add(count); Grid.SetRow(footer, 3); root.Children.Add(footer);
        var choices = new List<(CheckBox Box, string Search)>();
        foreach (var id in ids)
        {
            if (_services?.Library.Find(id) is not { } entry) continue;
            var icon = new Border { Width = 36, Height = 36, Margin = new Thickness(0, 0, 9, 0),
                Child = new TextBlock { Text = entry.DisplayName.Length > 0 ? entry.DisplayName[..1] : "A", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center } };
            RequestIcon(entry, icon, 36);
            var label = new TextBlock { Text = entry.DisplayName, Width = 106, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(icon); row.Children.Add(label);
            var box = new CheckBox { Content = row, Tag = id, Width = 192, MinHeight = 58, Margin = new Thickness(0, 0, 8, 6), ToolTip = entry.DisplayName, VerticalContentAlignment = VerticalAlignment.Center };
            box.IsChecked = selected.Contains(id);
            box.SetBinding(Control.ForegroundProperty, new Binding(nameof(ForegroundBrush)) { Source = dialog });
            System.Windows.Automation.AutomationProperties.SetName(box, entry.DisplayName);
            box.Checked += (_, _) => { selected.Add(id); UpdateCount(); };
            box.Unchecked += (_, _) => { selected.Remove(id); UpdateCount(); };
            items.Children.Add(box);
            choices.Add((box, entry.DisplayName + " " + string.Join(" ", entry.SearchAliases)));
        }
        UpdateCount();
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            foreach (var choice in choices)
                choice.Box.Visibility = query.Length == 0 || choice.Search.Contains(query, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        create.Click += (_, _) => { if (editing || selected.Count > 0) dialog.DialogResult = true; };
        SetPromptContent(dialog, root);
        dialog.Loaded += (_, _) => search.Focus();
        return ShowModalDialog(dialog.ShowDialog) ? ids.Where(selected.Contains).ToList() : null;

        void UpdateCount()
        {
            count.Text = $"已选 {selected.Count} / {choices.Count} 项";
            create.IsEnabled = editing || selected.Count > 0;
        }
    }
}
