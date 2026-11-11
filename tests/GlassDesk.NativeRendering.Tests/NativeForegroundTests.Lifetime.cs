using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GlassDesk.Rendering;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void UploadedSceneDoesNotKeepSourceBitmapOrWindowAlive()=>NativeEngineTests.OnSta(()=>
    {
        var target=Host(null,80,80);target.Show();
        try
        {
            using var p=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            var (scene,bitmap,window)=PrepareDetached(p);using var lifetime=scene;
            global::Program.Pump(TimeSpan.FromMilliseconds(100));
            // Reference ownership diagnostic only, never a product-memory sample.
            for(var i=0;i<4 && (bitmap.IsAlive || window.IsAlive);i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
            Assert.False(window.IsAlive);Assert.False(bitmap.IsAlive);
            Await(p);p.Clear(Colors.Transparent);p.Draw(scene);Assert.True(p.Present());
            Assert.Contains(Capture(target).Pixels.Where((_,i)=>i%4==3),a=>a>200);
        }
        finally{target.Close();}
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NativeForegroundScene,WeakReference,WeakReference) PrepareDetached(NativeForegroundPresenter p)
    {
        var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Pbgra32,null,Enumerable.Repeat((byte)255,1024).ToArray(),64);bitmap.Freeze();
        var root=new Image{Source=bitmap,Width=16,Height=16};var source=Host(root,80,80);source.Show();Capture(source);
        var scene=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));source.Close();return(scene,new(bitmap),new(source));
    }
    [Fact]
    public void OldAndUpdatedScenesRemainIndependentAndReleaseWithoutCollection()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Background=Brushes.Red};var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            var red=Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            var old=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            root.Background=Brushes.Blue;var blue=Capture(source);var changed=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            Await(p);p.Clear(Colors.Transparent);p.Draw(changed);Assert.True(p.Present());ManagedVectorTests.Compare("foreground-updated",blue,Capture(target));
            Await(p);p.Clear(Colors.Transparent);p.Draw(old);Assert.True(p.Present());ManagedVectorTests.Compare("foreground-retained",red,Capture(target));
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>p.Draw(old))).GetAwaiter().GetResult());
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(old.Dispose)).GetAwaiter().GetResult());
            old.Dispose();Assert.Throws<InvalidOperationException>(()=>p.Draw(old));Assert.Equal(1u,p.VectorStatistics.NativeVectorCount);
            p.Resize(101,101);Assert.Throws<InvalidOperationException>(()=>p.Draw(changed));changed.Dispose();
            Assert.Equal(0u,p.VectorStatistics.NativeVectorCount);Assert.Equal(0u,p.VectorStatistics.AssetCount);p.TrimGroupCache();
            Assert.Equal(0ul,p.GroupStatistics.ColorBytes+p.GroupStatistics.MaskBytes);
        }
        finally{source.Close();target.Close();}
    });
    [Fact]
    public void UnsupportedShadowPreservesTheLiveSceneAndCandidate()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Background=Brushes.Red};var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            using var scene=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            Await(p);p.Clear(Colors.Transparent);p.Draw(scene);Assert.True(p.Present());var before=Capture(target);var stats=p.VectorStatistics;
            root.Effect=new DropShadowEffect{BlurRadius=14,ShadowDepth=5,Opacity=.28};Capture(source);
            Assert.Throws<NotSupportedException>(()=>p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80)));
            Assert.Equal(stats,p.VectorStatistics);ManagedVectorTests.Compare("foreground-rejected-shadow",before,Capture(target));
            Await(p);p.Clear(Colors.Transparent);p.Draw(scene);Assert.True(p.Present());ManagedVectorTests.Compare("foreground-after-rejection",before,Capture(target));
        }
        finally{source.Close();target.Close();}
    });
}
