using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void ActiveIndicatorKeepsRoundedEdgeCoverageAtActualDockPose()=>NativeEngineTests.OnSta(()=>
    {
        var dot=new Border{Width=15.2,Height=4.8,CornerRadius=new CornerRadius(2.5),Background=Brushes.Cyan,SnapsToDevicePixels=true,
            RenderTransform=new ScaleTransform(1.28792916588274,1.28792916588274)};
        var canvas=new Canvas();canvas.Children.Add(dot);Canvas.SetLeft(dot,198.49656667293826);Canvas.SetTop(dot,79.24961331577288);
        var host=Host(canvas,368,94.4);host.Show();host.UpdateLayout();
        try{CompareNative("active-indicator-pose",host,canvas,64);}finally{host.Close();}
    });
}
