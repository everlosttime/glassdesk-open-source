using GlassDesk.Rendering;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ManagedSharedDeviceTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void FinalizerReleasesSiblingsWhileSurvivorContinuesDrawing(bool overlapping,bool diagnostics) => NativeEngineTests.OnSta(() =>
    {
        var hosts=Enumerable.Range(0,17).Select(_=>DirectShadowTests.Host(80,80)).ToArray();
        if(!overlapping)for(var i=1;i<hosts.Length;i++){hosts[i].Left=120+(i-1)%8*85;hosts[i].Top=(i-1)/8*90;}
        try
        {
            foreach(var host in hosts){host.Show();host.UpdateLayout();}
            // Initialize the 17 layered WPF HWNDs before attaching native
            // trees. Otherwise the first WPF redirection update is deferred
            // until the capture's message pump, after the lifetime exercise.
            global::Program.Pump(TimeSpan.FromMilliseconds(80));
            using var survivor=diagnostics?DiagnosticPresenter(new WindowInteropHelper(hosts[0]).Handle):new NativeDirectPresenter(new WindowInteropHelper(hosts[0]).Handle,100,100);
            survivor.SetVector(1,new RectangleGeometry(new(0,0,12,5),2.5,2.5),new(80,80),Colors.White);
            survivor.SetGlyphRun(1,ManagedTextTests.MakeRun(out _,out _));
            AwaitFrame(survivor);survivor.Clear(Colors.Blue);Assert.True(survivor.Present());
            var initialVisible=WgcWindowCapture.Capture(new WindowInteropHelper(hosts[0]).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Survivor was not visible before finalizer exercise.");
            Assert.Equal(new byte[]{255,0,0,255},initialVisible.Center);
            var baseline=survivor.DeviceStatistics;
            var weak=new List<WeakReference>();
            for(var i=1;i<hosts.Length;i++)weak.Add(AbandonSibling(survivor,new WindowInteropHelper(hosts[i]).Handle));
            using var start=new ManualResetEventSlim();
            var finalizing=Task.Run(()=>
            {
                start.Wait();
                // Diagnostic lifetime test only: never used in product or memory acceptance.
                for(var i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();}
            });
            start.Set();
            var iterations=0;
            do
            {
                AwaitFrame(survivor);
                survivor.Clear(Colors.Transparent);
                survivor.RenderShadow(new(new(80,80),new(8,12,64,56),26),[new(8,2,.15f,Colors.Black)]);
                survivor.PushGroup(.7f);
                survivor.DrawVectors(new(80,80),[new(1,new(20,20,80,80))]);
                survivor.DrawGlyphRuns(new(80,80),[new(1,new(12,68),Colors.White)]);
                survivor.ApplyGroupShadow(new(5,2,2,Colors.Black,.4f));
                survivor.BeginGroupMask();survivor.Clear(Colors.White);survivor.PopGroup();
                Assert.True(survivor.Present());
                Assert.Equal(baseline.Identity,survivor.DeviceStatistics.Identity);
                iterations++;
            }while(!finalizing.IsCompleted && iterations<1000);
            Assert.True(finalizing.Wait(TimeSpan.FromSeconds(15)),"Finalizer teardown stalled shared rendering.");
            Assert.All(weak,w=>Assert.False(w.IsAlive));
            Assert.Equal(1u,survivor.DeviceStatistics.Presenters);
            Assert.Equal(baseline.LiveDevices,survivor.DeviceStatistics.LiveDevices);
            AwaitFrame(survivor);survivor.Clear(Colors.Blue);
            uint nativePixel=0;int? read=null;
            if(diagnostics){var safe=(SafeHandle)typeof(NativeDirectPresenter).GetField("_handle",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(survivor)!;read=DirectPresenterTests.ReadPixel(safe.DangerousGetHandle(),50,50,out nativePixel);}
            Assert.True(survivor.Present());
            var handle=new WindowInteropHelper(hosts[0]).Handle;
            var covered=WgcWindowCapture.Capture(handle,TimeSpan.FromSeconds(3));
            // Keep both visible and overlapped exercises. A queued candidate
            // under 16 layered HWNDs is not proof of a displayed frame.
            foreach(var host in hosts.Skip(1))host.Hide();
            global::Program.Pump(TimeSpan.FromMilliseconds(80));
            var pending=WgcWindowCapture.Capture(handle,TimeSpan.FromSeconds(3));
            AwaitFrame(survivor);survivor.Clear(Colors.Blue);Assert.True(survivor.Present());
            var capture=WgcWindowCapture.Capture(handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Surviving presenter lost output.");
            var output=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"group-parity");System.IO.Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(System.IO.Path.Combine(output,$"finalizer-survivor-{overlapping}-{diagnostics}.json"),System.Text.Json.JsonSerializer.Serialize(new{
                iterations,overlapping,diagnostics,read,nativePixel,before=initialVisible.Center.Select(b=>(int)b).ToArray(),covered=covered?.Center.Select(b=>(int)b).ToArray(),
                pendingAfterUncover=pending?.Center.Select(b=>(int)b).ToArray(),visible=capture.Center.Select(b=>(int)b).ToArray(),
                redrawnForVisibleAcceptance=true}));
            if(diagnostics){Assert.Equal(0,read);Assert.Equal(0xff0000ffu,nativePixel);}
            Assert.Equal(new byte[]{255,0,0,255},covered?.Center);
            Assert.Equal(new byte[]{255,0,0,255},pending?.Center);
            Assert.Equal(new byte[]{255,0,0,255},capture.Center);
        }
        finally{foreach(var host in hosts)host.Close();}
    });

    private static void AwaitFrame(NativeDirectPresenter p)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();uint result;
        while((result=WaitForSingleObject(p.FrameLatencyHandle,0))==258 && watch.Elapsed<TimeSpan.FromSeconds(3))
            global::Program.Pump(TimeSpan.FromMilliseconds(2));
        Assert.Equal(0u,result);
    }
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle,uint milliseconds);

    // Explicit flags=1 diagnostic ownership only. Production's public factory
    // remains flags=0 and cannot enable readback. Reflection stays in the test.
    private static NativeDirectPresenter DiagnosticPresenter(nint hwnd)
    {
        var info=Info.Create(100,100,hwnd);info.Flags=1;Assert.Equal(0,DirectPresenterTests.Create(ref info,out var raw));
        var handleType=typeof(NativeDirectPresenter).Assembly.GetType("GlassDesk.Rendering.PresenterHandle",true)!;
        var safe=(SafeHandle)Activator.CreateInstance(handleType,true)!;
        typeof(SafeHandle).GetMethod("SetHandle",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(safe,[raw]);
        return (NativeDirectPresenter)Activator.CreateInstance(typeof(NativeDirectPresenter),BindingFlags.Instance|BindingFlags.NonPublic,null,[safe],null)!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonSibling(NativeDirectPresenter parent,nint hwnd)
    {
        var child=parent.CreateSibling(hwnd,100,100);
        child.RenderShadow(new(new(80,80),new(8,12,64,56),26),[new(8,2,.15f,Colors.Black)]);
        child.SetStrokeVector(1,Geometry.Parse(DirectVectorTests.PcPath),new(48,48),Colors.White,3.5f);
        child.SetGradientVector(2,new RectangleGeometry(new(0,0,48,64)),new(48,64),DirectGradientTests.ProductionBrush(2));
        child.SetVectorBrushOpacity(2,.46f);
        child.PushGroup(.7f);
        child.DrawVectors(new(80,80),[new(2,new(12,6,48,64)),new(1,new(12,10,48,48),.4f)]);
        child.SetGlyphRun(1,ManagedTextTests.MakeRun(out _,out _));child.DrawGlyphRuns(new(80,80),[new(1,new(12,68),Colors.White)]);
        child.ApplyGroupShadow(new(5,2,2,Colors.Black,.4f));
        child.BeginGroupMask();child.Clear(Colors.White);child.PopGroup();
        child.Present();
        // Finalizer also releases an abandoned, unpresentable active layer.
        child.PushGroup();child.Clear(Colors.Red);child.BeginGroupMask();
        return new WeakReference(child);
    }

    [Fact]
    public void SiblingNormalOutputSurvivesParentDisposeAndRejectsInvalidAccess() => NativeEngineTests.OnSta(() =>
    {
        var first=DirectShadowTests.Host(80,80);var second=DirectShadowTests.Host(80,80);
        first.Show();second.Show();
        try
        {
            var hwnd=new WindowInteropHelper(second).Handle;
            using var parent=new NativeDirectPresenter(new WindowInteropHelper(first).Handle,100,100);
            using var child=parent.CreateSibling(hwnd,100,100);
            var initial=parent.DeviceStatistics;
            var shared=child.DeviceStatistics;
            Assert.Equal(initial.Identity,shared.Identity);
            Assert.Equal(2u,shared.Presenters);
            parent.Clear(Colors.Red);child.Clear(Color.FromArgb(128,204,102,51));Assert.True(child.Present());
            parent.Dispose();
            shared=child.DeviceStatistics;Assert.Equal(1u,shared.Presenters);
            child.Clear(Color.FromArgb(128,204,102,51));Assert.True(child.Present());
            var capture=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Shared normal output missing.");
            var expected=new[]{26,51,102,128};for(var c=0;c<4;c++)Assert.InRange((int)capture.Center[c],expected[c]-1,expected[c]+1);
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>{_=child.DeviceStatistics;})).GetAwaiter().GetResult());
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>child.CreateSibling(hwnd,100,100))).GetAwaiter().GetResult());
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>child.PushGroup())).GetAwaiter().GetResult());
            child.Dispose();
            Assert.Throws<ObjectDisposedException>(()=>{_=child.DeviceStatistics;});
            Assert.Throws<ObjectDisposedException>(()=>parent.CreateSibling(hwnd,100,100));
        }
        finally{first.Close();second.Close();}
    });
}
