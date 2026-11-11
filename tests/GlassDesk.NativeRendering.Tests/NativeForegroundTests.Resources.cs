using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void RetiredResourceKeysAreReusableInsteadOfExhaustingTheIdentityCounter()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Background=Brushes.Red};var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            var expected=Capture(source);var snapshot=VisualDrawingSnapshot.Capture(root);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            p.Prepare(snapshot,new(80,80)).Dispose();
            typeof(NativeForegroundPresenter).GetField("_nextKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(p,uint.MaxValue);
            using var next=p.Prepare(snapshot,new(80,80));Await(p);p.Clear(Colors.Transparent);p.Draw(next);Assert.True(p.Present());
            ManagedVectorTests.Compare("foreground-recycled-keys",expected,Capture(target));
        }
        finally{source.Close();target.Close();}
    });
    [Fact]
    public void NativeBudgetUploadFailureRollsBackNewResourcesAndKeepsOldSceneDrawable()=>NativeEngineTests.OnSta(()=>
    {
        var element=new ManyVectors();var source=Host(element,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            var expected=Capture(source);var snapshot=VisualDrawingSnapshot.Capture(element);
            using var p=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);using var old=p.Prepare(snapshot,new(80,80));
            var before=p.VectorStatistics.NativeVectorCount;Assert.Equal(130u,before);
            // Identical scenes now intentionally share sources. Change the
            // candidate while keeping one cached hit before the upload failure.
            element.Ink=Brushes.Blue;element.InvalidateVisual();Capture(source);
            var changed=VisualDrawingSnapshot.Capture(element);var references=p.ResourceStatistics.References;var compiled=p.CompileStatistics;
            Assert.Throws<OutOfMemoryException>(()=>p.Prepare(changed,new(80,80)));Assert.Equal(before,p.VectorStatistics.NativeVectorCount);
            Assert.Equal(references,p.ResourceStatistics.References);
            Assert.Equal(compiled.Contexts,p.CompileStatistics.Contexts);Assert.Equal(compiled.Payloads,p.CompileStatistics.Payloads);Assert.Equal(compiled.PayloadBytes,p.CompileStatistics.PayloadBytes);
            Await(p);p.Clear(Colors.Transparent);p.Draw(old);Assert.True(p.Present());ManagedVectorTests.Compare("foreground-budget-rollback",expected,Capture(target));
            old.Dispose();Assert.Equal(0u,p.VectorStatistics.NativeVectorCount);Assert.Equal(0,p.ResourceStatistics.Resources);Assert.Equal(0,p.ResourceStatistics.DescriptorBytes);
            Assert.Equal(0,p.CompileStatistics.PayloadBytes);Assert.Equal(0,p.CompileStatistics.Contexts);
        }
        finally{source.Close();target.Close();}
    });
    private sealed class ManyVectors:FrameworkElement
    {internal Brush Ink=Brushes.Red;protected override void OnRender(DrawingContext dc){for(var i=0;i<130;i++)dc.DrawRectangle(i==0?Brushes.White:Ink,null,new Rect(2+(i%10)*4,2+(i/10)*4,3,3));}}
    [Fact]
    public void UnsupportedCandidateIsRejectedBeforeAnyNativeResourceUpload()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Canvas();root.Children.Add(new Border{Width=20,Height=20,Background=Brushes.Red});
        var bad=new Border{Width=20,Height=20,Background=new LinearGradientBrush(Colors.Red,Colors.Blue,0){SpreadMethod=GradientSpreadMethod.Reflect}};
        Canvas.SetLeft(bad,25);root.Children.Add(bad);var host=Host(root,80,80);host.Show();host.UpdateLayout();Capture(host);
        dynamic renderer=CreateRenderer(new WindowInteropHelper(host).Handle,100,100);using var lifetime=(IDisposable)renderer;
        try
        {
            var before=renderer.VectorStatistics;
            Assert.Throws<NotSupportedException>(()=>renderer.Prepare(VisualDrawingSnapshot.Capture(root),new Size(80,80)));
            Assert.Equal((ulong)before.GeometryBuilds,(ulong)renderer.VectorStatistics.GeometryBuilds);
            Assert.Equal((uint)before.NativeVectorCount,(uint)renderer.VectorStatistics.NativeVectorCount);
        }
        finally{host.Close();}
    });
    [Fact]
    public void RepeatedBitmapReferencesUseOneNativeSourceAndDisposeReclaimsIt()=>NativeEngineTests.OnSta(()=>
    {
        var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Pbgra32,null,Enumerable.Repeat((byte)255,1024).ToArray(),64);bitmap.Freeze();
        var root=new Canvas();for(var i=0;i<3;i++){var image=new Image{Source=bitmap,Width=16,Height=16};Canvas.SetLeft(image,i*20);root.Children.Add(image);}
        var host=Host(root,80,80);host.Show();host.UpdateLayout();Capture(host);
        dynamic renderer=CreateRenderer(new WindowInteropHelper(host).Handle,100,100);using var lifetime=(IDisposable)renderer;
        try
        {
            dynamic scene=renderer.Prepare(VisualDrawingSnapshot.Capture(root),new Size(80,80));
            try{Assert.Equal(1u,(uint)renderer.ImageStatistics.ImageCount);Assert.Equal(1024ul,(ulong)renderer.ImageStatistics.SourceBytes);}
            finally{((IDisposable)scene).Dispose();}
            Assert.Equal(0u,(uint)renderer.ImageStatistics.ImageCount);Assert.Equal(0ul,(ulong)renderer.ImageStatistics.SourceBytes);
        }
        finally{host.Close();}
    });
}
