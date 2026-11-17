using System.Windows.Interop;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public class LayeredWindowPresentationTests
{
    [Fact]
    public void HiddenWindowClearPreservesGeometryAndAllowsReopen()
    {
        StaTest.Run(() =>
        {
            var popup = new ControlCenterWindow();
            popup.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                popup.ShowNear(new PixelRect(100, 100, 400, 60));
                popup.Hide();
                var handle = new WindowInteropHelper(popup).Handle;
                var before = DisplayService.GetWindowPixelBounds(handle);
                Assert.True(LayeredWindowPresentation.ClearHiddenPixels(handle));
                Assert.Equal(before, DisplayService.GetWindowPixelBounds(handle));
                Assert.False(popup.IsVisible);
                popup.ShowNear(new PixelRect(100, 100, 400, 60));
                Assert.True(popup.IsVisible);
                Assert.Equal(before, DisplayService.GetWindowPixelBounds(handle));
            }
            finally { popup.Close(); }
        });
    }

    [Fact]
    public void VisibleWindowCannotBeCleared()
    {
        StaTest.Run(() =>
        {
            var popup = new ControlCenterWindow();
            try
            {
                popup.Show();
                Assert.False(LayeredWindowPresentation.ClearHiddenPixels(new WindowInteropHelper(popup).Handle));
                Assert.True(popup.IsVisible);
            }
            finally { popup.Close(); }
        });
    }

    [Fact]
    public void MissingWindowCannotBeCleared() => Assert.False(LayeredWindowPresentation.ClearHiddenPixels(0));
}
