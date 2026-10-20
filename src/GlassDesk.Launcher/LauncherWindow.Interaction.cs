using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private DispatcherTimer? _noticeTimer;

    private void ActivateEntry(string id)
    {
        if (_services is null) return;
        var result = _services.Launch.ActivateOrLaunch(id);
        if (result.Outcome == ActivationOutcome.MultipleWindowsFound)
        {
            var menu = CreateTrackedContextMenu();
            foreach (var candidate in result.Candidates)
            {
                var item = new MenuItem { Header = candidate.Title };
                item.Click += (_, _) =>
                {
                    var activation = LaunchService.ActivateWindow(candidate.Handle);
                    if (activation.Outcome == ActivationOutcome.Failed) ShowNotice(activation.Message ?? "窗口未能激活。");
                    else CloseWithTransition();
                };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = TileGrid;
            menu.IsOpen = true;
        }
        else if (result.Outcome == ActivationOutcome.Failed)
            ShowNotice(result.Message ?? "应用未能打开。");
        else
            CloseWithTransition();
    }

    private void ShowNotice(string notice, bool autoDismiss = false)
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;
        _notice = notice;
        RefreshDiagnostics();
        if (!autoDismiss) return;

        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_noticeTimer, timer)) return;
            _noticeTimer = null;
            // A library refresh may have replaced this success with a persistent status.
            if (_notice != notice) return;
            _notice = null;
            RefreshDiagnostics();
        };
        _noticeTimer = timer;
        timer.Start();
    }

    private void CommitEdit(Func<LauncherLayout, LauncherLayout> transform)
    {
        if (_workspace is null || _view.IsSearching)
        {
            ShowNotice("搜索结果中不能整理应用；请先清空搜索。");
            return;
        }
        var edit = _workspace.BeginEdit();
        try
        {
            edit.Draft = transform(edit.Draft);
            if (!_workspace.Commit(edit)) ShowNotice("应用库或布局已变化，请重试整理操作。");
            else _notice = null;
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        {
            ShowNotice("整理未保存，原布局已恢复：" + ex.Message);
        }
        RenderTiles();
    }

    private ContextMenu BuildTileMenu(LauncherSlot slot)
    {
        var menu = CreateTrackedContextMenu();
        var id = slot.Kind == LauncherSlotKind.Group ? slot.Group?.Id : slot.ItemId;
        if (id is null) return menu;
        if (slot.Kind == LauncherSlotKind.Group)
        {
            AddMenu(menu, "打开文件夹", () => { _view.OpenGroupId = id; RenderGroup(); });
            AddMenu(menu, "重命名文件夹", () => RenameGroup(id));
            AddMenu(menu, "解散文件夹", () => CommitEdit(layout => LauncherLayoutEditor.Dissolve(layout, id, _view.Capacity)));
        }
        else
        {
            AddMenu(menu, "打开", () => ActivateEntry(id));
            AddMenu(menu, "编辑名称、图标和别名", () => EditEntry(id));
            if (_services?.Library.Find(id)?.IsTargetMissing == true)
                AddMenu(menu, "重新定位…", () => RelocateEntry(id));
            var group = _workspace?.Layout.Pages.SelectMany(page => page.Slots)
                .SingleOrDefault(item => item.Group?.ItemIds.Contains(id) == true)?.Group;
            if (group is not null)
            {
                var index = group.ItemIds.IndexOf(id);
                if (index > 0)
                    AddMenu(menu, "在文件夹中前移", () => CommitEdit(layout => LauncherLayoutEditor.MoveGroupMember(layout, group.Id, id, index - 1)));
                if (index < group.ItemIds.Count - 1)
                    AddMenu(menu, "在文件夹中后移", () => CommitEdit(layout => LauncherLayoutEditor.MoveGroupMember(layout, group.Id, id, index + 1)));
                AddMenu(menu, "移出文件夹", () => CommitEdit(layout => LauncherLayoutEditor.RemoveFromGroup(layout, id, group.Id, _view.Page, 0, _view.Capacity)));
            }
        }
        return menu;
    }

    private static void AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

}
