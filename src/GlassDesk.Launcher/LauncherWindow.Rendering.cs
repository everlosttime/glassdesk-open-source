using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.UI.Controls;

namespace GlassDesk.Launcher;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Window.Closed disposes the icon loader; the window owns its lifecycle.")]
public partial class LauncherWindow
{
    private TilesRenderKey? _lastTilesRenderKey;
    private TilesRenderKey? _retainedTileKey;
    private readonly Dictionary<int, LauncherAppTileButton[]> _retainedTilePages = new();
    private readonly LauncherIconLoader _iconLoader = new();
    private readonly record struct TilesRenderKey(
        long LibraryVersion, long LayoutVersion, string Query, int Page, int Rows, int Columns,
        double PanelWidth, double PanelHeight, string? OpenGroupId, int IconPixels);

    private TilesRenderKey CurrentTilesRenderKey() => new(
        _services!.Library.Version, _services.Layouts.LauncherVersion, _view.Query, _view.Page,
        _view.Rows, _view.Columns, LauncherPanelRoot.Width, LauncherPanelRoot.Height, _view.OpenGroupId, IconPixelSize(AppIconSize));

    private int IconPixelSize(double size)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (int)Math.Ceiling(size * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY) * 1.25);
    }

    private double AppIconSize => Math.Clamp(Math.Floor(((LauncherPanelRoot.Height - 160) / _view.Rows - 44) / 4) * 4, 32, 64);
    private List<LauncherSlot> CurrentSlots()
    {
        if (_services is null || _workspace is null) return [];
        if (_view.IsSearching)
        {
            var members = LauncherLayoutRules.AppIds(_workspace.Layout).ToHashSet(StringComparer.Ordinal);
            return _services.Search.SearchApps(_view.Query, int.MaxValue)
                .Where(hit => members.Contains(hit.Item.Id))
                .Select(hit => new LauncherSlot { ItemId = hit.Item.Id }).ToList();
        }
        return (_dragEdit?.Draft ?? _workspace.Layout).Pages.SelectMany(page => page.Slots).ToList();
    }

    private void RenderTiles(bool reuseUnchanged = false, bool reusePageTiles = false)
    {
        if (_services is null || _workspace is null) return;
        StopPageSlide();
        if (reuseUnchanged && _lastTilesRenderKey == CurrentTilesRenderKey())
        {
            RefreshDiagnostics();
            ApplyContentReveal(_hasTransformPath ? _containerMotion?.CurrentFrame.ContentOpacity ?? 1 : 1);
            return;
        }
        _lastTilesRenderKey = null;
        var slots = CurrentSlots();
        _view.SetItemCount(slots.Count);
        var pageKey = CurrentTilesRenderKey() with { Page = 0 };
        if (!reusePageTiles || _retainedTileKey != pageKey || _dragOrigin is not null)
        {
            _retainedTilePages.Clear();
            _retainedTileKey = pageKey;
        }
        TileGrid.Rows = _view.Rows;
        TileGrid.Columns = _view.Columns;
        TileGrid.MinHeight = _view.Rows * (AppIconSize + 44);
        TileGrid.Children.Clear();
        if (_retainedTilePages.TryGetValue(_view.Page, out var retained))
        {
            foreach (var tile in retained) TileGrid.Children.Add(tile);
        }
        else
            foreach (var slot in slots.Skip(_view.Page * _view.Capacity).Take(_view.Capacity))
                TileGrid.Children.Add(CreateTile(slot));
        _retainedTilePages[_view.Page] = TileGrid.Children.Cast<LauncherAppTileButton>().ToArray();
        foreach (var page in _retainedTilePages.Keys.OrderBy(p => Math.Abs(p - _view.Page)).Skip(2).ToArray())
            _retainedTilePages.Remove(page);
        PageText.Text = $"{_view.Page + 1} / {_view.PageCount}";
        PreviousPage.IsEnabled = _view.Page > 0;
        NextPage.IsEnabled = _view.Page < _view.PageCount - 1;
        if (slots.Count == 0)
            _notice = _view.IsSearching ? "没有匹配的应用。" : "应用库为空。使用“添加”导入文件、文件夹或网址。";
        else if (_notice is "没有匹配的应用。" or "应用库为空。使用“添加”导入文件、文件夹或网址。")
            _notice = null;
        RefreshDiagnostics();
        RenderGroup();
        ApplyContentReveal(_hasTransformPath ? _containerMotion?.CurrentFrame.ContentOpacity ?? 1 : 1);
        // Capture the final page / group after item-count clamping and group validation.
        _lastTilesRenderKey = CurrentTilesRenderKey();
    }

    private LauncherAppTileButton CreateTile(LauncherSlot slot)
    {
        var isGroup = slot.Kind == LauncherSlotKind.Group;
        var id = isGroup ? slot.Group?.Id : slot.ItemId;
        var entry = isGroup ? null : _services?.Library.Find(id);
        var title = isGroup ? slot.Group?.Name ?? "新建分组" : entry?.DisplayName ?? "未知应用";
        var missing = entry?.IsTargetMissing == true;
        var text = new TextBlock
        {
            Text = missing ? "⚠ " + title : title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 128,
            Height = 18,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement iconVisual;
        if (isGroup)
            iconVisual = CreateFolderIcon(slot.Group!);
        else
            iconVisual = new PhoneBorder { Width = AppIconSize, Height = AppIconSize, CornerRadius = new CornerRadius(AppIconSize / 3),
                Background = new LinearGradientBrush(Color.FromRgb(91,144,190),Color.FromRgb(67,92,136),90),
                Child = new TextBlock { Text = title.Length > 0 ? title[..1] : "A", FontSize = 28,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        iconVisual.HorizontalAlignment = HorizontalAlignment.Center;
        var iconHost = new Border { Child = iconVisual };
        if (!isGroup && entry is not null) RequestIcon(entry, iconHost, AppIconSize);
        stack.Children.Add(iconHost);
        stack.Children.Add(text);
        var button = new LauncherAppTileButton(iconHost)
        {
            Content = stack,
            Tag = id,
            ToolTip = missing ? $"{title}：目标已失效。右键可重新定位。" : title,
            Margin = new Thickness(6, 4, 6, 4),
            Padding = new Thickness(4),
            MinWidth = 72,
            MinHeight = AppIconSize + 36,
            RenderTransformOrigin = new Point(.5,.5),
            RenderTransform = new ScaleTransform(1,1),
            Style = (Style)FindResource("LauncherAppTile"),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, missing ? $"{title}，目标已失效" : title);
        button.Click += (_, _) => OnTileActivated(slot);
        if (!_view.IsSearching)
        {
            button.PreviewMouseLeftButtonDown += OnTileMouseDown;
            button.PreviewMouseMove += OnTileMouseMove;
        }
        button.PreviewMouseLeftButtonUp += OnTileMouseUp;
        if (isGroup)
        {
            button.MouseEnter += (_, _) => { button.SetFolderHovered(true); if (iconVisual is Grid g && g.Children[0] is SharedGlassLens lens) AnimateFolderHighlight(lens, 1); };
            button.MouseLeave += (_, _) => { button.SetFolderHovered(false); if (iconVisual is Grid g && g.Children[0] is SharedGlassLens lens) AnimateFolderHighlight(lens, 0); };
        }
        button.ContextMenu = BuildTileMenu(slot);
        return button;
    }

    private void RequestIcon(AppEntry entry, Border host, double size)
    {
        var icons = _services?.Icons;
        if (icons is null) return;
        var source = entry.IconSource ?? string.Empty;
        var custom = entry.CustomIconPath;
        var version = _services!.Library.Version;
        var pixels = IconPixelSize(size);
        var generation = _iconRequestGeneration;
        var key = $"{version}\0{custom}\0{source}\0{pixels}";
        var task = _iconLoader.LoadAsync(key, () => LoadIcon(icons, source, custom, pixels));
        if (task.IsCompletedSuccessfully) Apply(task.Result);
        else _ = CompleteAsync();

        void Apply(BitmapImage? image)
        {
            if (image is null || _services?.Library.Version != version) return;
            if (_backgroundWorkSuspended || generation != _iconRequestGeneration && !IsVisible)
            { _iconsNeedReapply = true; return; }
            // A retained tile can outlive an activation. A late decode may fill that
            // same tile only while awake and at its current DPI; it cannot wake glass.
            if (pixels != IconPixelSize(size)) return;
            host.Child = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center };
        }
        async Task CompleteAsync()
        {
            var image = await task.ConfigureAwait(false);
            if (image is null || host.Dispatcher.HasShutdownStarted) return;
            try { await host.Dispatcher.InvokeAsync(() => Apply(image), System.Windows.Threading.DispatcherPriority.Background); }
            catch (TaskCanceledException) { }
        }
    }

    private static BitmapImage? LoadIcon(GlassDesk.AppServices.IconCache icons, string iconSource, string? customIcon, int pixels)
    {
        var hasCustomIcon = !string.IsNullOrWhiteSpace(customIcon) && File.Exists(customIcon);
        var path = hasCustomIcon ? customIcon : icons.PathFor(iconSource);
        if (!hasCustomIcon && !string.IsNullOrWhiteSpace(iconSource)
            && !string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            path = icons.EnsureFor(iconSource)?.Path ?? path;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return LauncherIconDecoder.DecodeFile(path, pixels);
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or FileFormatException)
        {
            return null;
        }
    }

    private void RenderGroup()
    {
        UpdateFolderPanel();
    }

    private void OnTileActivated(LauncherSlot slot)
    {
        if (_dragEdit is not null || _suppressTileClick) return;
        var id = slot.Kind == LauncherSlotKind.Group ? slot.Group?.Id : slot.ItemId;
        if (slot.Kind == LauncherSlotKind.Group)
        {
            _view.OpenGroupId = id;
            RenderGroup();
        }
        else if (id is not null)
            ActivateEntry(id);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _view.Query = SearchBox.Text;
        _view.OpenGroupId = null;
        RenderTiles();
    }

    private void OnPreviousPage(object sender, RoutedEventArgs e) => NavigatePage(-1);
    private void OnNextPage(object sender, RoutedEventArgs e) => NavigatePage(1);
    private void OnCloseGroup(object sender, RoutedEventArgs e) => CloseFolder();
}
