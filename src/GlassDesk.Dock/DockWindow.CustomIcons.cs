using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlassDesk.Platform;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private void RefreshBuiltinIcons()
    {
        foreach (var (key, image, original) in new (string, Image, FrameworkElement)[]
        {
            ("entry:launcher", LauncherCustomIcon, LauncherDefaultGlyph),
            ("entry:this-pc", ThisPcCustomIcon, ThisPcDefaultGlyph),
            ("entry:desktop-icons", DesktopIconsCustomIcon, DesktopIconsDefaultGlyph),
        })
        {
            var path = _services?.DockIcons.GetIconPath(key);
            image.Source = path is not null && System.IO.File.Exists(path) ? LoadBitmap(path) : null;
            image.Visibility = image.Source is not null ? Visibility.Visible : Visibility.Collapsed;
            original.Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AddIconMenuItems(ContextMenu menu, string id)
    {
        menu.Items.Add(MenuItem("自定义图标…", () => ChooseCustomIcon(id)));
        var reset = MenuItem("恢复默认图标", () => ApplyCustomIcon(id, null));
        reset.IsEnabled = !string.IsNullOrWhiteSpace(_services?.DockIcons.GetIconPath(id));
        menu.Items.Add(reset);
    }

    private void OnBuiltinIconContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (_services is null || sender is not Button { Tag: string id } button) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        AddIconMenuItems(menu, id);
        button.ContextMenu = menu;
        e.Handled = true;
        OpenDockContextMenu(menu);
    }

    private void OpenDockContextMenu(ContextMenu menu)
    {
        _autoHide?.PushSuppress(DateTime.UtcNow);
        var foregroundBefore = WindowControl.GetForegroundWindow();
        nint menuHandle = 0;
        menu.Opened += (_, _) => menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;
        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };
        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    private void ChooseCustomIcon(string id)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择自定义图标",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|所有文件|*.*",
            CheckFileExists = true,
        };
        _autoHide?.PushSuppress(DateTime.UtcNow);
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            if (LoadBitmap(dialog.FileName) is null) { ShowFailureNotice("无法读取这张图片，请选择有效的图片文件。"); return; }
            ApplyCustomIcon(id, dialog.FileName);
        }
        finally { _autoHide?.PopSuppress(DateTime.UtcNow); }
    }

    private void ApplyCustomIcon(string id, string? path)
    {
        if (_services?.DockIcons.SetIcon(id, path) != true) ShowFailureNotice("图标保存失败，已保留原图标。");
        RefreshTiles();
    }
}
