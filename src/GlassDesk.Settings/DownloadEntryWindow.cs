using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Controls;

namespace GlassDesk.Settings;

/// <summary>A single, offline website QR code opened by the manual download entry.</summary>
public sealed class DownloadEntryWindow : GlassWindow
{
    protected override bool UseOpaquePanel => true;

    public DownloadEntryWindow()
    {
        Title = "下载入口";
        Width = 320;
        Height = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        using var stream = typeof(DownloadEntryWindow).Assembly.GetManifestResourceStream("GlassDesk.Settings.Downloads.WebsiteQr.png")
            ?? throw new InvalidDataException("未找到官网二维码。");
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        AutomationProperties.SetName(image, "GlassDesk 官网二维码");
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(18), Padding = new Thickness(12), Child = image };
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 18;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CenterOnPrimaryWorkArea();
    }
}
