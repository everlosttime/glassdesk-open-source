using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Models;
using GlassDesk.Dock;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(true,true)]
    public void ActualDockForegroundCompilesBuiltinStrokesGlyphAndSlideClip(bool sliding,bool retained)=>NativeEngineTests.OnSta(()=>
    {
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass});
        var dock=new DockWindow();
        try
        {
            var glass=(LiquidGlassSurface)dock.FindName("GlassSurface");glass.IsBackdropActive=()=>false;glass.Visibility=Visibility.Collapsed;
            var strip=(DockTileStrip)typeof(DockWindow).GetField("_tileStrip",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!;
            strip.SetTiles([new DockTileModel{EntryId="compiler-probe",DisplayName="probe",Glyph="\uE8A5"}]);dock.Show();dock.UpdateLayout();
            global::Program.Pump(TimeSpan.FromMilliseconds(80));if(retained)dock.CaptureRetainedForegroundSnapshot();
            var root=(Grid)dock.FindName("DockVisualRoot");if(sliding){root.RenderTransform=new TranslateTransform(0,13.35);root.Opacity=.67;}
            // Mirror the actual unchanged HWND's foreground Visual data. Direct
            // desktop/Dock HWND WGC remains a separate, unverified acceptance.
            var size=new Rect(0,0,dock.ActualWidth,dock.ActualHeight);
            var brush=new VisualBrush((Visual)dock.Content){Stretch=Stretch.None,ViewboxUnits=BrushMappingMode.Absolute,Viewbox=size,
                ViewportUnits=BrushMappingMode.Absolute,Viewport=size,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top};
            var reference=new DockReference(brush,dock.Clip,dock.Opacity,size);var mirror=Host(reference,size.Width,size.Height);mirror.Show();mirror.UpdateLayout();
            try
            {
                var capture=retained?(Func<VisualDrawingSnapshot>)dock.CaptureRetainedForegroundSnapshot:dock.CaptureForegroundSnapshot;
                CompareNative((retained?"retained-dock-":"dock-")+sliding,mirror,capture);
                if(retained)
                {
                    var cached=dock.CaptureRetainedForegroundSnapshot();var full=dock.CaptureForegroundSnapshot();
                    Assert.Equal(full.VisualCount,cached.VisualCount);Assert.Equal(full.DrawingCount,cached.DrawingCount);
                    Assert.Equal(full.GlyphCount,cached.GlyphCount);Assert.Equal(full.BitmapSourceBytes,cached.BitmapSourceBytes);
                    Assert.True(dock.RetainedForegroundStatistics!.DrawingReuses>0);
                }
            }
            finally{mirror.Close();}
        }
        finally{dock.Close();}
    });
    private sealed class DockReference(Brush paint,Geometry clip,double opacity,Rect bounds):FrameworkElement
    {protected override void OnRender(DrawingContext dc){dc.PushClip(clip);dc.PushOpacity(opacity);dc.DrawRectangle(paint,null,bounds);dc.Pop();dc.Pop();}}
}
