using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class HiddenMaterialRetirementTests
{
    [Fact]
    public void ExpiredHiddenMaterialDiscardsItsPixelsBeforeTheNextReveal()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var host = new Window { Content = surface, Width = 320, Height = 180, ShowInTaskbar = false, ShowActivated = false };
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(LiquidGlassSurface);
            var backdrop = type.GetField("_transitionBackdrop", privateInstance)!;
            try
            {
                host.Show();
                host.Hide();
                backdrop.SetValue(surface, BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32,
                    null, new byte[320 * 180 * 4], 320 * 4));
                type.GetMethod("RetireHiddenPresentation", privateInstance)!.Invoke(surface, null);
                Assert.Null(backdrop.GetValue(surface));
                host.Show();
                Assert.False((bool)type.GetField("_hiddenPresentationRetired", privateInstance)!.GetValue(surface)!);
            }
            finally { host.Close(); }
        });
    }
}
