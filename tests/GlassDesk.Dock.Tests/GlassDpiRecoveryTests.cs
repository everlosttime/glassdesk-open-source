using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.UI;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassDpiRecoveryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void OnlyARealWindowScaleChangeRetiresBackground(bool windowSource, bool scaleChanged, bool rebuild)
    {
        StaTest.Run(() =>
        {
            var icon = new Image();
            var window = new DpiWindow { Content = icon };
            var before = BackdropRecovery.Generation;
            var args = (DpiChangedEventArgs)Activator.CreateInstance(typeof(DpiChangedEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [new DpiScale(1.5, 1.5), new DpiScale(scaleChanged ? 2 : 1.5, scaleChanged ? 2 : 1.5),
                    Window.DpiChangedEvent, windowSource ? (object)window : icon], null)!;
            if (windowSource) window.RaiseEvent(args); else icon.RaiseEvent(args);
            Assert.Equal(before + (rebuild ? 1 : 0), BackdropRecovery.Generation);
            window.Close();
        });
    }

    private sealed class DpiWindow : GlassWindow;
}
