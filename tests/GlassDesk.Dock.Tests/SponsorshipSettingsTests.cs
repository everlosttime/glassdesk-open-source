using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public class SponsorshipSettingsTests
{
    [Theory]
    [InlineData(false, "alipay.jpg", "17A5DF70E82F659D288A8981D85B59C04595C9314EC35B73EFF6F4BB195D05E3")]
    [InlineData(true, "wechat.png", "60B353A958A6D85D28B50AD38A64F98078A7C9B16F8AF525AE55D47B7522CB00")]
    public void PackagedPaymentDialogUsesTheExactOwnerSuppliedImage(bool useWechat, string fileName, string approvedHash)
    {
        StaTest.Run(() =>
        {
            using var resource = typeof(SettingsWindow).Assembly.GetManifestResourceStream("GlassDesk.Settings.Sponsorship." + fileName);
            Assert.NotNull(resource);
            Assert.Equal(approvedHash, Convert.ToHexString(SHA256.HashData(resource)));
            resource.Position = 0;
            var expected = BitmapFrame.Create(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var window = CreateWindow();
            try
            {
                window.Show(); window.UpdateLayout();
                ((RadioButton)window.FindName(useWechat ? "SponsorWechatOption" : "SponsorAlipayOption")).IsChecked = true;
                ClickAndInspectDialog(window, dialog =>
                {
                    var actual = Assert.IsAssignableFrom<BitmapSource>(((Image)dialog.FindName("SponsorCodeImage")).Source);
                    Assert.Equal(expected.PixelWidth, actual.PixelWidth);
                    Assert.Equal(expected.PixelHeight, actual.PixelHeight);
                    Assert.Equal(expected.Format, actual.Format);
                    var stride = (expected.PixelWidth * expected.Format.BitsPerPixel + 7) / 8;
                    var expectedPixels = new byte[stride * expected.PixelHeight];
                    var actualPixels = new byte[expectedPixels.Length];
                    expected.CopyPixels(expectedPixels, stride, 0);
                    actual.CopyPixels(actualPixels, stride, 0);
                    Assert.Equal(expectedPixels, actualPixels);
                    Assert.True(((Image)dialog.FindName("SponsorCodeImage")).IsVisible);
                    ExportPreview(dialog, useWechat ? "GLASSDESK_SPONSORSHIP_WECHAT_PREVIEW" : "GLASSDESK_SPONSORSHIP_ALIPAY_PREVIEW");
                });
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SponsorCategoryShowsAnActionInsteadOfAnInlineCodeAndCannotResetSettings()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-sponsorship-" + Guid.NewGuid().ToString("N"));
            var window = CreateWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                window.AttachDataStore(data, null);
                window.Show(); window.UpdateLayout();
                var pages = (ListBox)window.FindName("SettingsPageList");
                pages.SelectedItem = "Dock";
                Assert.True(((Button)window.FindName("ResetPageButton")).IsEnabled);
                pages.SelectedItem = "赞助支持";
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                window.UpdateLayout();
                var heading = Assert.IsType<TextBlock>(window.FindName("SponsorshipSectionHeading"));
                var scroll = (ScrollViewer)window.FindName("SettingsScroll");
                Assert.InRange(heading.TranslatePoint(new Point(), scroll).Y, 0, 14);
                Assert.False(((Button)window.FindName("ResetPageButton")).IsEnabled);
                Assert.True(Assert.IsType<Button>(window.FindName("SponsorButton")).IsVisible);
                Assert.Null(window.FindName("SponsorCodeImage"));
                Assert.Empty(window.OwnedWindows.Cast<Window>());
                ExportPreview(window, "GLASSDESK_SPONSORSHIP_PREVIEW");
            }
            finally
            {
                window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodeOpensOnlyOnSponsorClickAndUsesTheCurrentlySelectedMethod(bool useWechat)
    {
        StaTest.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                // Plain test images, never shipped as payment codes.
                var alipay = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4);
                var wechat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
                window.SetSponsorshipCodes(alipay, wechat);
                window.Show(); window.UpdateLayout();
                ((RadioButton)window.FindName("SponsorAlipayOption")).IsChecked = true;
                ((RadioButton)window.FindName("SponsorWechatOption")).IsChecked = true;
                ((RadioButton)window.FindName(useWechat ? "SponsorWechatOption" : "SponsorAlipayOption")).IsChecked = true;
                Assert.Empty(window.OwnedWindows.Cast<Window>());
                Assert.Null(window.FindName("SponsorCodeImage"));
                ClickAndInspectDialog(window, dialog =>
                {
                    Assert.Same(window, dialog.Owner);
                    var image = Assert.IsType<Image>(dialog.FindName("SponsorCodeImage"));
                    Assert.Same(useWechat ? wechat : alipay, image.Source);
                    Assert.True(image.IsVisible);
                    Assert.Equal(Visibility.Collapsed, ((TextBlock)dialog.FindName("SponsorCodeUnavailable")).Visibility);
                    Assert.True(((TextBlock)dialog.FindName("SponsorshipFreeNotice")).IsVisible);
                });
                Assert.Empty(window.OwnedWindows.Cast<Window>());
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSelectedCodeNeverDisplaysTheOtherMethodAndDialogCanBeClosed(bool useWechat)
    {
        StaTest.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                var otherCode = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
                window.SetSponsorshipCodes(useWechat ? otherCode : null, useWechat ? null : otherCode);
                window.Show(); window.UpdateLayout();
                ((RadioButton)window.FindName(useWechat ? "SponsorWechatOption" : "SponsorAlipayOption")).IsChecked = true;
                ClickAndInspectDialog(window, dialog =>
                {
                    Assert.Null(((Image)dialog.FindName("SponsorCodeImage")).Source);
                    Assert.True(((TextBlock)dialog.FindName("SponsorCodeUnavailable")).IsVisible);
                    Assert.True(((TextBlock)dialog.FindName("SponsorshipFreeNotice")).IsVisible);
                    ExportPreview(dialog, "GLASSDESK_SPONSORSHIP_DIALOG_PREVIEW");
                    ((Button)dialog.FindName("CloseSponsorButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.False(dialog.IsVisible);
                });
                Assert.True(window.IsVisible);
                Assert.Empty(window.OwnedWindows.Cast<Window>());
            }
            finally { window.Close(); }
        });
    }

    private static SettingsWindow CreateWindow()
    {
        var window = new SettingsWindow { ShowActivated = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        return window;
    }

    private static void ClickAndInspectDialog(SettingsWindow window, Action<Window> inspect)
    {
        var button = Assert.IsType<Button>(window.FindName("SponsorButton"));
        Window? observed = null;
        Exception? failure = null;
        var pending = window.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                observed = Assert.Single(window.OwnedWindows.Cast<Window>());
                inspect(observed);
            }
            catch (Exception error) { failure = error; }
            finally { if (observed?.IsVisible == true) observed.Close(); }
        }, DispatcherPriority.ContextIdle);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        pending.Abort();
        Assert.NotNull(observed);
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void ExportPreview(Window window, string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path)) return;
        var visual = (FrameworkElement)window.Content; visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
