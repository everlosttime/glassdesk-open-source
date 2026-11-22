using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Models;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private void AddAutomaticClassificationMenuOnOpen(ContextMenu menu, string itemId)
    {
        MenuItem? submenu = null;
        menu.Opened += (_, _) =>
        {
            if (submenu is not null) menu.Items.Remove(submenu);
            submenu = null;
            if (!_settings.GroupModeEnabled || _workspace is null) return;
            submenu = new MenuItem { Header = "调整智能分类" };
            var current = _workspace.Layout.AutoClassifications.FirstOrDefault(item => item.ItemId == itemId);
            foreach (var category in LauncherCategoryCatalog.All)
            {
                var categoryId = category.Id;
                var choice = new MenuItem
                {
                    Header = category.Name,
                    IsCheckable = true,
                    IsChecked = current?.CategoryId == categoryId,
                };
                choice.Click += (_, _) => CommitFilterEdit(layout => LauncherAutoGroupingRules.SetManualCategory(layout, itemId, categoryId));
                submenu.Items.Add(choice);
            }
            if (_workspace.Layout.FilterGroups.Count > 0)
            {
                var existing = new MenuItem { Header = "移至已有分组" };
                foreach (var group in _workspace.Layout.FilterGroups)
                {
                    var groupId = group.Id;
                    AddMenu(existing, group.Name.Replace("_", "__", StringComparison.Ordinal),
                        () => CommitFilterEdit(layout => LauncherAutoGroupingRules.SetManualGroup(layout, itemId, groupId)));
                }
                submenu.Items.Add(new Separator());
                submenu.Items.Add(existing);
            }
            submenu.Items.Add(new Separator());
            AddMenu(submenu, "仅保留手动分组", () => CommitFilterEdit(layout => LauncherAutoGroupingRules.LockManual(layout, itemId)));
            menu.Items.Add(submenu);
        };
    }

    private void AddRestoreAutomaticCategoriesButton()
    {
        if (_workspace?.Layout.DisabledAutomaticCategoryIds.Count is not > 0) return;
        var restore = new Button
        {
            Content = "恢复自动分类",
            ToolTip = "重新允许已删除的自动分类。已有应用可在设置中预览整理。",
            Style = (Style)FindResource("FolderActionButton"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 5, 10, 5),
        };
        restore.Click += (_, _) =>
        {
            CommitFilterEdit(LauncherAutoGroupingRules.RestoreAutomaticGroups);
            if (_workspace?.Layout.DisabledAutomaticCategoryIds.Count == 0)
                ShowNotice("已恢复后续应用的自动分类；整理已有应用请在设置中选择“智能整理已有应用”。", autoDismiss: true);
        };
        FilterGroupButtons.Children.Add(restore);
    }
}
