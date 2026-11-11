using System.Windows.Controls;
using GlassDesk.Launcher;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherKeyboardFocusVisualTests
{
    [Theory]
    [InlineData("PageScroll")]
    [InlineData("GroupScroll")]
    [InlineData("OutgoingPageScroll")]
    public void PageContainersCannotDrawTheKeyboardOnlyDottedOutline(string name)
    {
        StaTest.Run(() =>
        {
            var launcher = new LauncherWindow();
            try
            {
                var scroll = Assert.IsType<ScrollViewer>(launcher.FindName(name));
                Assert.Null(scroll.FocusVisualStyle);
                // Removing the decoration must not disable scrolling or text input.
                Assert.True(scroll.Focusable);
                Assert.True(((TextBox)launcher.FindName("SearchBox")).Focusable);
                Assert.True(((TextBox)launcher.FindName("SearchBox")).IsTabStop);
            }
            finally { launcher.Close(); }
        });
    }
}
