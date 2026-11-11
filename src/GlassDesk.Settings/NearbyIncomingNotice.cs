using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;

namespace GlassDesk.Settings;

public sealed class NearbyIncomingNotice : GlassWindow
{
    protected override bool UseOpaquePanel => true;
    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 24;
    public NearbyIncomingNotice(NearbyIncoming incoming, Action open)
    {
        Title = "GlassDesk · 收到传输请求"; Width = 350; SizeToContent = SizeToContent.Height;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = "收到传输请求", FontSize = 17, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"{incoming.DeviceName}\n{incoming.Files.Count} 个文件 · {NearbyTransferWindow.FormatSize(incoming.Files.Sum(f => f.Size))}\n保存到：{incoming.Directory}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var button = new Button { Content = "查看并确认接收", Padding = new Thickness(12, 6, 12, 6) };
        button.Click += (_, _) => open(); body.Children.Add(button);
        var border = new PhoneBorder { Child = body, CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "LiquidControlPopup"); border.SetResourceReference(Border.BorderBrushProperty, "LiquidControlEdge");
        Content = border;
        Loaded += (_, _) => { Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - ActualWidth - 20); Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - ActualHeight - 20); };
    }
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); WindowStyles.MakeNoActivate(Handle); }
}
