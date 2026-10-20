using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.UI.Controls;

namespace GlassDesk.Settings;

/// <summary>仅在用户点击赞助后展示所选支付方式的收款码。</summary>
public partial class SponsorshipWindow : GlassWindow
{
    protected override bool UseOpaquePanel => true;

    public SponsorshipWindow(bool useWechat, ImageSource? code)
    {
        InitializeComponent();
        var method = useWechat ? "微信支付" : "支付宝";
        Title = method + "赞助";
        AutomationProperties.SetName(this, Title);
        SponsorDialogHeading.Text = Title;
        SponsorCodeImage.Source = code;
        SponsorCodeImage.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        SponsorCodeUnavailable.Visibility = code is null ? Visibility.Visible : Visibility.Collapsed;
        SponsorCodeUnavailable.Text = $"暂未提供{method}收款码\n仍可免费使用全部功能";
        SponsorInstructions.Text = code is null
            ? $"{method}赞助暂未开放，感谢您的支持与理解。"
            : $"请使用{method}「扫一扫」，扫描下方收款码。";
        AutomationProperties.SetName(SponsorCodeImage, method + "赞助收款码");
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Mouse.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
