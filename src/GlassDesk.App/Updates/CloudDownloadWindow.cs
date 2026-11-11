using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDesk.App.Updates;

/// <summary>Manual cloud-drive downloads, available independently of release discovery.</summary>
internal sealed class CloudDownloadWindow : Window
{
    private readonly TextBlock _status;
    private readonly Action<Uri> _openPage;
    private readonly Action<string> _copyText;

    public CloudDownloadWindow(string configuration, Version? version = null, string? notes = null,
        Action<Uri>? openPage = null, Action<string>? copyText = null, Func<bool>? snoozeNotice = null)
    {
        var downloads = ParseDownloads(configuration);
        _openPage = openPage ?? (url => { using var browser = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); });
        _copyText = copyText ?? Clipboard.SetText;
        Title = "GlassDesk 网盘下载";
        Width = 560; Height = version is null ? 600 : 720; MinWidth = 450; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(23, 26, 33);
        Foreground = Brush(239, 243, 249);
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var grid = new Grid { Margin = new Thickness(26) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var title = Text(version is null ? "选择下载网盘" : $"发现新版本 {version}", 24);
        title.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetName(title, "网盘下载标题");
        header.Children.Add(title);
        header.Children.Add(Text("打开网盘页面后，可自行转存或下载，再手动运行安装包。", 13, muted: true, top: 10));
        Add(header, 0);

        var body = new StackPanel();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            body.Children.Add(Text("更新说明", 16));
            var releaseNotes = Text(notes.Length > 12000 ? notes[..12000] : notes, 13, muted: true, top: 8);
            AutomationProperties.SetName(releaseNotes, "网盘更新说明");
            body.Children.Add(new ScrollViewer
            {
                Content = releaseNotes, MaxHeight = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 20)
            });
        }
        body.Children.Add(Text("请选择网盘，并在页面中核对安装包版本。", 13, muted: true));
        foreach (var download in downloads)
        {
            var card = new StackPanel();
            var name = Text(download.Name, 17);
            name.FontWeight = FontWeights.SemiBold;
            card.Children.Add(name);
            card.Children.Add(Text(download.Code is null ? "可在网盘页面转存或下载" : $"提取码：{download.Code}", 13, muted: true, top: 6));
            var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            var open = Button("打开网盘", "打开" + download.Name);
            open.Background = Brush(55, 104, 190);
            open.Click += (_, _) => RunAction(() => _openPage(download.Url), $"已打开{download.Name}，请在浏览器中继续。", "无法打开网盘：");
            actions.Children.Add(open);
            if (download.Code is not null)
            {
                var copy = Button("复制提取码", "复制" + download.Name + "提取码");
                copy.Click += (_, _) => RunAction(() => _copyText(download.Code), $"已复制{download.Name}提取码。", "无法复制，可使用上方提取码：");
                actions.Children.Add(copy);
            }
            card.Children.Add(actions);
            body.Children.Add(new Border
            {
                Child = card, CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
                Margin = new Thickness(0, 12, 0, 0), Background = Brush(32, 38, 49),
                BorderBrush = Brush(57, 68, 86), BorderThickness = new Thickness(1)
            });
        }
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroll, "网盘下载内容");
        Add(scroll, 1);
        _status = Text("请选择一个网盘。打开页面不会关闭 GlassDesk。", 12, muted: true, top: 18);
        AutomationProperties.SetName(_status, "网盘下载状态");
        var footer = new StackPanel();
        footer.Children.Add(_status);
        if (version is not null && snoozeNotice is not null)
        {
            var snooze = Button("7 天内不再弹出", "7 天内不再弹出更新公告");
            snooze.Margin = new Thickness(0, 12, 0, 0);
            snooze.HorizontalAlignment = HorizontalAlignment.Right;
            snooze.Click += (_, _) =>
            {
                try
                {
                    if (snoozeNotice()) Close();
                    else _status.Text = "未能保存提醒设置，请重试。";
                }
                catch (Exception ex) { _status.Text = "未能保存提醒设置：" + ex.Message; }
            };
            footer.Children.Add(snooze);
        }
        Add(footer, 2);
        Content = grid;

        void Add(UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
    }

    private void RunAction(Action action, string success, string failure)
    {
        try { action(); _status.Text = success; }
        catch (Exception ex) { _status.Text = failure + ex.Message; }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<Download> ParseDownloads(string configuration)
    {
        try
        {
            if (configuration.Length > 8192) throw new InvalidDataException("网盘下载配置过大。");
            using var document = JsonDocument.Parse(configuration);
            var items = document.RootElement.GetProperty("cloudDownloads");
            if (items.GetArrayLength() is < 1 or > 8) throw new InvalidDataException("请配置 1 到 8 个网盘下载入口。");
            var result = new List<Download>();
            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()?.Trim();
                var url = item.GetProperty("url").GetString()?.Trim();
                var code = item.TryGetProperty("extractionCode", out var value) ? value.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(code)) code = null;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 32 || name.Any(char.IsControl)
                    || url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                    || code is { Length: > 64 } || (code?.Any(char.IsControl) ?? false)
                    || result.Any(x => x.Name == name || x.Url == uri))
                    throw new InvalidDataException("网盘名称、HTTPS 分享链接或提取码无效。");
                result.Add(new Download(name, uri, code));
            }
            return result.AsReadOnly();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("网盘下载配置格式无效。", ex); }
    }

    private sealed record Download(string Name, Uri Url, string? Code);
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static TextBlock Text(string text, double size, bool muted = false, double top = 0) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.65,
        Foreground = muted ? Brush(173, 187, 209) : Brush(239, 243, 249), Margin = new Thickness(0, top, 0, 0)
    };
    private static Button Button(string text, string automationName)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(0, 0, 8, 4),
            Background = Brush(42, 52, 67), Foreground = Brushes.White,
            BorderBrush = Brush(70, 83, 104), MinWidth = 100
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }
}
