using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopSurfaceConcealTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConcealStopsChildPresentationAndRestoresItWithoutRecreatingDesktopWindows(bool pinned)
    {
        StaTest.Run(()=>
        {
            using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings{PinBarsToDesktop=pinned,ReduceMotion=true});
            var dock=new DockWindow();
            try
            {
                dock.Show();var handle=new WindowInteropHelper(dock).Handle;
                var conceal=typeof(GlassWindow).GetMethod("HideSurface",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                var show=typeof(GlassWindow).GetMethod("ShowSurface",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                Assert.NotNull(conceal);Assert.NotNull(show);
                conceal.Invoke(dock,null);
                Assert.False(((UIElement)dock.Content).IsVisible);
                Assert.Equal(pinned,dock.IsVisible);
                if(pinned)Assert.Equal(0,dock.Opacity);
                show.Invoke(dock,null);
                Assert.True(dock.IsVisible);Assert.True(((UIElement)dock.Content).IsVisible);
                Assert.Equal(1,dock.Opacity);Assert.Equal(handle,new WindowInteropHelper(dock).Handle);
            }
            finally{dock.Close();}
        });
    }
}
