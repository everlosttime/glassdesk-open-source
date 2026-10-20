using System.Reflection;
using GlassDesk.Platform.Displays;
namespace GlassDesk.Dock.Tests;
public sealed class Phase6DisplayTests
{
    [Fact] public void CenteredWindowKeepsNegativeMonitorCoordinates()
    {
        var type=typeof(PixelRect).Assembly.GetType("GlassDesk.Platform.Displays.WindowPlacementPolicy",true)!;
        var method=type.GetMethod("Centered")!;
        var area=new PixelRect(-1920,40,1920,1040);
        Assert.Equal(new PixelRect(-1594,217,1267,686),method.Invoke(null,[area,0.66,false]));
        Assert.Equal(area,method.Invoke(null,[area,0.66,true]));
    }
    [Theory]
    [InlineData(false,true,false,false,true,false,false)]
    [InlineData(true,true,true,true,true,false,false)]
    [InlineData(true,true,false,true,true,true,false)]
    [InlineData(true,true,false,false,true,true,false)]
    [InlineData(true,true,true,false,true,false,true)]
    [InlineData(true,false,false,false,false,false,false)]
    public void PassiveRevealHonorsIndependentGates(bool hotzone,bool enabled,bool fullscreen,bool suppressFullscreen,bool covered,bool blockCovered,bool expected)
    {
        var type=typeof(DockWindow).Assembly.GetType("GlassDesk.Dock.PassiveRevealPolicy",true)!;
        Assert.Equal(expected,type.GetMethod("CanReveal")!.Invoke(null,[hotzone,enabled,fullscreen,suppressFullscreen,covered,blockCovered]));
    }
}
