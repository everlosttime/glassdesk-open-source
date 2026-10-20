using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class PhoneBorderTests
{
    [Fact]
    public void FramedControlsUseTheSharedContourAndKeepCornersTransparent()
    {
        StaTest.Run(() =>
        {
            var border = new PhoneBorder
            {
                Width = 100,
                Height = 60,
                CornerRadius = new CornerRadius(20),
                Background = Brushes.White,
                BorderBrush = Brushes.Red,
                BorderThickness = new Thickness(1),
            };
            border.Measure(new Size(100, 60));
            border.Arrange(new Rect(0, 0, 100, 60));

            var image = new RenderTargetBitmap(100, 60, 96, 96, PixelFormats.Pbgra32);
            image.Render(border);
            var pixels = new byte[100 * 60 * 4];
            image.CopyPixels(pixels, 100 * 4, 0);

            Assert.Equal(0, pixels[3]);
            Assert.Equal(255, pixels[(30 * 100 + 50) * 4 + 3]);
            Assert.True(border.Clip?.FillContains(new Point(3, 3)) == false,
                $"Outside corner clip: {border.Clip?.FillContains(new Point(3, 3))}");
            Assert.True(border.Clip?.FillContains(new Point(50, 30)) == true,
                $"Inside body clip: {border.Clip?.FillContains(new Point(50, 30))}");
        });
    }
}
