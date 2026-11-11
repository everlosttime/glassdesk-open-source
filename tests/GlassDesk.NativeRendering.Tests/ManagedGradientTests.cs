using GlassDesk.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ManagedGradientTests
{
    [Theory]
    [InlineData(2,.46f)]
    [InlineData(1,1f)]
    [InlineData(3,.4f)]
    public void BrushSnapshotsAndOpacityOnlyAnimationPreserveProductionOutputAndCaches(int kind,float opacity)=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var hwnd=new WindowInteropHelper(host).Handle;var dpi=PresentationSource.FromVisual(host)!.CompositionTarget!.TransformToDevice;
            using var presenter=new NativeDirectPresenter(hwnd,(int)Math.Round(80*dpi.M11),(int)Math.Round(80*dpi.M22));
            var brush=DirectGradientTests.ProductionBrush(kind);brush.Opacity=opacity;
            // Inset source geometry deliberately differs from sourceSize. Brush
            // mapping must follow its actual bounds, as WPF does.
            var geometry=new RectangleGeometry(new(3,4,48,64));
            presenter.SetGradientVector(1,geometry,new(54,72),brush);
            brush.GradientStops[0].Color=Colors.Red;geometry.Rect=new(500,500,2,2);
            DirectVectorDraw[] draws=[new(1,new(9.35,2.2,54,72))];
            foreach(var alpha in new[]{opacity,0f,.18f})
            {
                presenter.SetVectorBrushOpacity(1,alpha);presenter.Clear(Colors.Transparent);presenter.DrawVectors(new(80,80),draws);Assert.True(presenter.Present());
                var expectedBrush=DirectGradientTests.ProductionBrush(kind);expectedBrush.Opacity=alpha;
                var root=new Canvas();var panel=new Border{Width=48,Height=64,Background=expectedBrush};root.Children.Add(panel);Canvas.SetLeft(panel,12.35);Canvas.SetTop(panel,6.2);
                var reference=ManagedVectorTests.Capture(root);
                ManagedVectorTests.Compare($"managed-gradient-{kind}-{alpha}",reference,WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Managed gradient output missing."));
                var stats=presenter.VectorStatistics;
                Assert.Equal(1ul,stats.GeometryBuilds);Assert.Equal(1ul,stats.CoverageBuilds);Assert.True(stats.CacheBytes>0);
            }
            var unsupported=DirectGradientTests.ProductionBrush(kind);unsupported.SpreadMethod=GradientSpreadMethod.Repeat;
            Assert.Throws<NotSupportedException>(()=>presenter.SetGradientVector(1,new RectangleGeometry(new(0,0,48,64)),new(48,64),unsupported));
            Assert.Throws<ArgumentOutOfRangeException>(()=>presenter.SetVectorBrushOpacity(1,float.NaN));
            presenter.DrawVectors(new(80,80),draws);Assert.Equal(1ul,presenter.VectorStatistics.GeometryBuilds);
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>presenter.SetVectorBrushOpacity(1,.2f))).GetAwaiter().GetResult());
            presenter.Resize(120,120);Assert.Equal(0ul,presenter.VectorStatistics.CacheBytes);presenter.DrawVectors(new(80,80),draws);Assert.True(presenter.VectorStatistics.CoverageBuilds>1);
            presenter.Dispose();Assert.Throws<ObjectDisposedException>(()=>presenter.SetVectorBrushOpacity(1,.2f));
        }
        finally{host.Close();}
    });

    [Fact]
    public void UnsupportedBrushSemanticsAreRejectedBeforeReplacingTheAsset()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,100,100);var geometry=new RectangleGeometry(new(0,0,48,64));
            p.SetGradientVector(1,geometry,new(48,64),DirectGradientTests.ProductionBrush(2));p.DrawVectors(new(80,80),[new(1,new(0,0,48,64))]);var stats=p.VectorStatistics;
            var brush=(RadialGradientBrush)DirectGradientTests.ProductionBrush(2);brush.GradientOrigin=new(.3,.4);
            Assert.Throws<NotSupportedException>(()=>p.SetGradientVector(1,geometry,new(48,64),brush));
            brush=(RadialGradientBrush)DirectGradientTests.ProductionBrush(2);brush.MappingMode=BrushMappingMode.Absolute;
            Assert.Throws<NotSupportedException>(()=>p.SetGradientVector(1,geometry,new(48,64),brush));
            brush=(RadialGradientBrush)DirectGradientTests.ProductionBrush(2);brush.ColorInterpolationMode=ColorInterpolationMode.ScRgbLinearInterpolation;
            Assert.Throws<NotSupportedException>(()=>p.SetGradientVector(1,geometry,new(48,64),brush));
            brush=(RadialGradientBrush)DirectGradientTests.ProductionBrush(2);brush.RelativeTransform=new RotateTransform(10);
            Assert.Throws<NotSupportedException>(()=>p.SetGradientVector(1,geometry,new(48,64),brush));
            brush=(RadialGradientBrush)DirectGradientTests.ProductionBrush(2);brush.GradientStops[1].Offset=0;
            Assert.Throws<NotSupportedException>(()=>p.SetGradientVector(1,geometry,new(48,64),brush));
            Assert.Equal(stats,p.VectorStatistics);
        }
        finally{host.Close();}
    });
}
