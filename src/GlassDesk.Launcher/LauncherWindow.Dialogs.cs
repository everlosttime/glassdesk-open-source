using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.UI.Controls;
using Microsoft.Win32;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var menu = CreateTrackedContextMenu();
        menu.PlacementTarget = sender as Button;
        AddMenu(menu, "从桌面批量添加…", AddFromDesktop);
        AddMenu(menu, "文件、程序或快捷方式…", () =>
        {
            var picker = CreateImportFilePicker();
            if (ShowModalDialog(() => picker.ShowDialog(this))) ImportPaths(picker.FileNames);
        });
        AddMenu(menu, "文件夹…", () =>
        {
            var picker = new OpenFolderDialog();
            if (ShowModalDialog(() => picker.ShowDialog(this))) ImportPaths([picker.FolderName]);
        });
        AddMenu(menu, "网站…", AddWebsite);
        menu.IsOpen = true;
    }

    // 保留 .lnk 来源，避免选择器提前解析目标而丢失名称、参数和自定义图标。
    private static OpenFileDialog CreateImportFilePicker() => new()
    { Multiselect = true, DereferenceLinks = false, Filter = "所有文件|*.*" };

    private void AddFromDesktop()
    {
        if (_services is null) return;
        var dialog = new DesktopImportWindow(GlassDesk.Platform.Shell.DesktopScanner.Scan()) { Owner = this };
        if (ShowModalDialog(dialog.ShowDialog)) ImportPaths(dialog.SelectedPaths);
    }

    private void ImportPaths(IReadOnlyList<string> paths)
    {
        if (_services is null) return;
        try
        {
            var requests = paths.Select(path => new ImportRequest { Source = path }).ToList();
            var before = LauncherLayoutRules.AppIds(_workspace?.Layout ?? new LauncherLayout()).ToHashSet(StringComparer.Ordinal);
            var result = _services.Import.ImportItems(requests);
            var imported = result.Added.Concat(result.Duplicates).Select(item => item.Id).Distinct().ToList();
            var placed = imported.Count(id => !before.Contains(id) && _workspace is not null
                && LauncherLayoutRules.AppIds(_workspace.Layout).Contains(id));
            var summary = $"已添加到启动台 {placed} 项，已有 {imported.Count - placed}，失败 {result.Failures.Count}。";
            if (result.Failures.Count > 0) summary += " " + result.Failures[0].Message;
            ShowNotice(summary, autoDismiss: result.Failures.Count == 0);
            if (result.Failures.Count > 0)
                ShowModalDialog(() =>
                {
                    MessageBox.Show(this, summary + Environment.NewLine + string.Join(Environment.NewLine,
                        result.Failures.Select(failure => failure.Source + "：" + failure.Message)),
                        "导入结果", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                });
            RefreshLibrary();
        }
        catch (StorageException ex)
        {
            ShowNotice("应用库写入失败；请检查数据目录并重试：" + ex.Message);
        }
    }

    private void AddWebsite()
    {
        var values = AskFields("添加网站", ("网站地址", "https://"), ("显示名称", ""));
        if (values is null || _services is null) return;
        try
        {
            var before = LauncherLayoutRules.AppIds(_workspace?.Layout ?? new LauncherLayout()).ToHashSet(StringComparer.Ordinal);
            var result = _services.Import.ImportItems([new ImportRequest
            { Source = values[0], DisplayName = values[1], IsUrl = true }]);
            ShowNotice(result.Failures.Count > 0 ? result.Failures[0].Message
                : result.Added.Concat(result.Duplicates).Any(item => !before.Contains(item.Id))
                    ? "网站已添加。" : "此网站已在启动台中。", autoDismiss: result.Failures.Count == 0);
            RefreshLibrary();
        }
        catch (StorageException ex) { ShowNotice("网站记录未能保存：" + ex.Message); }
    }

    private void RenameGroup(string groupId)
    {
        var group = _workspace?.Layout.Pages.SelectMany(page => page.Slots)
            .SingleOrDefault(slot => slot.Group?.Id == groupId)?.Group;
        if (group is null) return;
        var values = AskFields("重命名文件夹", ("名称", group.Name));
        if (values is not null) CommitEdit(layout => LauncherLayoutEditor.RenameGroup(layout, groupId, values[0]));
    }

    private void EditEntry(string id)
    {
        var entry = _services?.Library.Find(id);
        if (entry is null || _services is null) return;
        var values = AskFields("编辑应用", ("名称", entry.DisplayName),
            ("搜索别名（逗号分隔）", string.Join(", ", entry.SearchAliases)),
            ("自定义图标路径（留空保持现状）", ""));
        if (values is null) return;
        try
        {
            var aliases = values[1].Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!_services.Editor.Edit(id, values[0], aliases, string.IsNullOrWhiteSpace(values[2]) ? null : values[2]))
                ShowNotice("编辑内容无效；名称应为 1–120 字，图标须为可读取的图片文件。");
            else { ShowNotice("应用已更新。", autoDismiss: true); RefreshLibrary(); }
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            ShowNotice("编辑未能保存，原记录已恢复：" + ex.Message);
        }
    }

    private void RelocateEntry(string id)
    {
        var entry = _services?.Library.Find(id);
        if (entry is null || _services is null || entry.Kind == AppItemKind.Url) return;
        string? target = null;
        if (entry.Kind == AppItemKind.Folder)
        {
            var folder = new OpenFolderDialog();
            if (ShowModalDialog(() => folder.ShowDialog(this))) target = folder.FolderName;
        }
        else
        {
            var picker = new OpenFileDialog { Filter = "所有文件|*.*" };
            if (ShowModalDialog(() => picker.ShowDialog(this))) target = picker.FileName;
        }
        if (target is null) return;
        var outcome = _services.Maintenance.Relocate(id, target);
        ShowNotice(outcome.Message, autoDismiss: outcome.Result == RelocateResult.Relocated);
        RefreshLibrary();
    }

    private string[]? AskFields(string title, params (string Label, string Value)[] fields)
    {
        var dialog = new GlassPromptWindow
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 430, SizeToContent = SizeToContent.Height, MinHeight = 150,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 6, 0, 3) });
            var box = new TextBox { Text = field.Value, MinHeight = 28 };
            System.Windows.Automation.AutomationProperties.SetName(box, field.Label);
            if (field.Label.Contains("图标", StringComparison.Ordinal))
            {
                var row = new DockPanel();
                var browse = new Button { Content = "选择…", MinWidth = 72, Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += (_, _) =>
                {
                    var picker = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|所有文件|*.*" };
                    if (ShowModalDialog(() => picker.ShowDialog(dialog))) box.Text = picker.FileName;
                };
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
                row.Children.Add(box);
                panel.Children.Add(row);
            }
            else panel.Children.Add(box);
            inputs.Add(box);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 70, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", MinWidth = 70, IsDefault = true };
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        var surface = new Grid();
        surface.Children.Add(new LiquidGlassSurface { UseLauncherTransparency = true });
        surface.Children.Add(panel);
        dialog.Content = surface;
        dialog.Loaded += (_, _) => inputs[0].Focus();
        return dialog.ShowDialog() == true ? inputs.Select(box => box.Text).ToArray() : null;
    }

    private sealed class GlassPromptWindow : GlassWindow
    {
        protected override double ResolveCornerRadius(AppearanceSettings appearance) =>
            40;
    }
}
