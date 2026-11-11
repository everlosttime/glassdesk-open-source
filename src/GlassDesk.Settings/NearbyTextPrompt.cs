using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Controls;

namespace GlassDesk.Settings;

internal sealed class NearbyTextPrompt : GlassWindow
{
    protected override bool UseOpaquePanel => true;
    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 24;
    internal static string? Ask(Window owner, string title, string description, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return null;
        var dialog = new NearbyTextPrompt { Owner = owner.IsVisible ? owner : null, Title = title, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var body = new StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        var input = new TextBox { MaxLength = 80, MinHeight = 30 }; body.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
        cancel.Click += (_, _) => dialog.DialogResult = false; accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(accept); body.Children.Add(actions);
        var border = new PhoneBorder { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1), Child = body };
        border.SetResourceReference(Border.BackgroundProperty, "LiquidControlPopup"); border.SetResourceReference(Border.BorderBrushProperty, "LiquidControlEdge");
        dialog.Content = border;
        dialog.Loaded += (_, _) => input.Focus();
        using var registration = cancellationToken.Register(() => dialog.Dispatcher.BeginInvoke(() => dialog.Close()));
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
