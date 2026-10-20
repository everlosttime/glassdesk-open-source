using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private ImageSource? _alipaySponsorCode;
    private ImageSource? _wechatSponsorCode;

    private void LoadSponsorshipCodes() => SetSponsorshipCodes(
        LoadSponsorshipCode("alipay.jpg") ?? LoadSponsorshipCode("alipay.png"), LoadSponsorshipCode("wechat.png"));

    private static BitmapFrame? LoadSponsorshipCode(string fileName)
    {
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream(
                "GlassDesk.Settings.Sponsorship." + fileName);
            if (stream is null) return null;
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            // Missing or unreadable payment artwork must not prevent free use of Settings.
            return null;
        }
    }

    /// <summary>设置作者提供的原始收款码；未提供的渠道不会显示其它渠道的二维码。</summary>
    public void SetSponsorshipCodes(ImageSource? alipay, ImageSource? wechat)
    {
        _alipaySponsorCode = alipay;
        _wechatSponsorCode = wechat;
    }

    private void OnSponsorClick(object sender, RoutedEventArgs e)
    {
        var useWechat = SponsorWechatOption.IsChecked == true;
        var dialog = new SponsorshipWindow(useWechat, useWechat ? _wechatSponsorCode : _alipaySponsorCode)
        { Owner = this };
        dialog.ShowDialog();
    }
}
