using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Dock;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class VisualSnapshotTests
{
    [Fact]
    public void ProductionGhostComparesThroughTheSameDirectWindowRenderingPath()=>NativeEngineTests.OnSta(()=>
    {
        var strip=new DockTileStrip();var model=new DockTileModel{Glyph="\uE8A5"};
        var ghost=(Border)typeof(DockTileStrip).GetMethod("CreateGhost",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(strip,[new Border{DataContext=model}])!;
        ghost.Opacity=.6;ghost.RenderTransform=new ScaleTransform(1.12,1.12);ghost.RenderTransformOrigin=new(.5,.5);
        var canvas=new Canvas{Background=Brushes.Transparent};canvas.Children.Add(ghost);Canvas.SetLeft(ghost,20.35);Canvas.SetTop(ghost,15.2);
        var host=Host(canvas,160,120);
        try
        {
            host.Show();host.UpdateLayout();var expected=Capture(host);var snapshot=VisualDrawingSnapshot.Capture(canvas);
            Record("ghost-direct",snapshot);
            Compare("ghost-direct",expected,Render(snapshot,160,120));
        }
        finally{host.Close();}
    });
}
