using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class DirectGroupTests
{
    private static readonly Geometry Red=Geometry.Parse("M2.3,3.2 H59.3 V53.2 H2.3 Z");
    private static readonly Geometry Blue=Geometry.Parse("M4.35,20.2 H74.35 V69.2 H4.35 Z");
    private static readonly Geometry Clip=Geometry.Parse("M15,6 C8,6 6,15 6,24 L6,54 C6,65 15,72 27,72 L55,72 C68,72 75,62 75,50 L75,23 C75,12 65,6 53,6 Z");
    private static readonly Color RedColor=Color.FromArgb(220,240,30,20),BlueColor=Color.FromArgb(210,20,90,230),Background=Color.FromRgb(11,20,30);
    [Fact]
    public void NativeLibraryExposesGpuGroupOperations()=>NativeEngineTests.OnSta(()=>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try
        {
            foreach(var name in new[]{"GD_PresenterPushGroup","GD_PresenterBeginGroupMask","GD_PresenterPopGroup",
                "GD_PresenterDiscardGroups","GD_PresenterTrimGroupCache","GD_PresenterGetGroupStatistics"})
                Assert.True(NativeLibrary.TryGetExport(library,name,out _),"Missing GPU group operation: "+name);
        }
        finally{NativeLibrary.Free(library);}
    });

    [Theory]
    [InlineData(1f,false,false,false)]
    [InlineData(.46f,true,false,false)]
    [InlineData(1f,false,true,false)]
    [InlineData(.7f,false,true,false)]
    [InlineData(.7f,true,true,true)]
    public void NormalGpuGroupsMatchWpfOverlapClipAndMask(float opacity,bool nested,bool clip,bool mask)=>NativeEngineTests.OnSta(()=>
    {
        var first=DirectShadowTests.Host(80,80);first.Content=new Reference(opacity,nested,clip,mask);first.Show();first.UpdateLayout();
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var expected=Capture(first);var hwnd=new WindowInteropHelper(host).Handle;
            using var presenter=new NativeDirectPresenter(hwnd,expected.Width,expected.Height);
            presenter.SetVector(1,Red,new(80,80),RedColor);presenter.SetVector(2,Blue,new(80,80),BlueColor);
            presenter.SetVector(3,Clip,new(80,80),Colors.White);
            presenter.SetGradientVector(4,new RectangleGeometry(new(0,0,80,80)),new(80,80),Mask());
            presenter.Clear(Background);presenter.PushGroup(opacity);
            if(clip)presenter.PushGroup();
            Draw(presenter,1);
            if(nested)presenter.PushGroup(.6f);
            Draw(presenter,2);
            if(nested)presenter.PopGroup();
            if(clip){presenter.BeginGroupMask();Draw(presenter,3);presenter.PopGroup();}
            if(mask){presenter.BeginGroupMask();Draw(presenter,4);}
            presenter.PopGroup();Assert.True(presenter.Present());
            var actual=Capture(host);var name=$"groups-{opacity}-{nested}-{clip}-{mask}";Save(name,expected,actual);
            ManagedVectorTests.Compare(name,expected,actual);
            Assert.Equal(0u,presenter.GroupStatistics.Depth);Assert.True(presenter.GroupStatistics.Composites>0);
            // Normal groups cannot create a diagnostic readback route.
            Assert.Equal(unchecked((int)0x80070005),DirectPresenterTests.ReadPixel(Handle(presenter),10,10,out _));
        }
        finally{first.Close();host.Close();}
    });

    [Fact]
    public void GroupStateFailureCancellationReuseAndResizeAreBounded()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,100,100);
            Assert.Equal(0u,p.GroupStatistics.CachedSlots);
            Assert.Equal(unchecked((int)0x8007139f),Assert.Throws<COMException>(()=>p.PopGroup()).HResult);
            Assert.Throws<ArgumentOutOfRangeException>(()=>p.PushGroup(float.NaN));
            p.Clear(Background);p.Present();var before=Capture(host);
            // Initialize the new back buffer. Cancellation preserves that
            // candidate; it cannot promise contents for an undrawn flip buffer.
            p.Clear(Background);
            p.PushGroup(.5f);p.Clear(Colors.Red);p.BeginGroupMask();p.Clear(Colors.White);
            var active=p.GroupStatistics;
            Assert.Equal(unchecked((int)0x8007139f),Assert.Throws<COMException>(()=>p.BeginGroupMask()).HResult);
            Assert.Equal(unchecked((int)0x8007139f),Assert.Throws<COMException>(()=>p.TrimGroupCache()).HResult);
            Assert.Equal(unchecked((int)0x80070015),Assert.ThrowsAny<Exception>(()=>p.Present()).HResult);
            Assert.Equal(unchecked((int)0x80070015),Assert.ThrowsAny<Exception>(()=>p.Resize(100,100)).HResult);
            Assert.Equal(active,p.GroupStatistics);
            p.DiscardGroups();Assert.Equal(0u,p.GroupStatistics.Depth);
            p.Present();ManagedVectorTests.Compare("groups-cancel",before,Capture(host));
            for(var i=0;i<4;i++){p.PushGroup(.5f);p.Clear(Colors.Red);p.BeginGroupMask();p.Clear(Colors.White);p.PopGroup();}
            Assert.Equal(active.Allocations,p.GroupStatistics.Allocations);
            Assert.Equal(40000ul,p.GroupStatistics.ColorBytes);Assert.Equal(40000ul,p.GroupStatistics.MaskBytes);
            p.TrimGroupCache();Assert.Equal(0ul,p.GroupStatistics.ColorBytes+p.GroupStatistics.MaskBytes);
            for(var i=0;i<16;i++)p.PushGroup();
            Assert.Throws<OutOfMemoryException>(()=>p.PushGroup());Assert.Equal(16u,p.GroupStatistics.Depth);
            p.DiscardGroups();p.Resize(120,120);Assert.Equal(0u,p.GroupStatistics.CachedSlots);
        }
        finally{host.Close();}
    });

    private static void Draw(NativeDirectPresenter p,uint key)=>p.DrawVectors(new(80,80),[new(key,new(0,0,80,80))]);
    private static LinearGradientBrush Mask()=>new(Colors.White,Color.FromArgb(70,255,255,255),new Point(0,0),new Point(1,0));
    private sealed class Reference(float opacity,bool nested,bool clip,bool mask):FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(Background),null,new(0,0,80,80));dc.PushOpacity(opacity);
            if(mask){var brush=Mask();brush.MappingMode=BrushMappingMode.Absolute;brush.EndPoint=new(80,0);dc.PushOpacityMask(brush);}
            if(clip)dc.PushClip(DirectGroupTests.Clip);
            dc.DrawGeometry(new SolidColorBrush(RedColor),null,Red);
            if(nested)dc.PushOpacity(.6);
            dc.DrawGeometry(new SolidColorBrush(BlueColor),null,Blue);
            if(nested)dc.Pop();if(clip)dc.Pop();if(mask)dc.Pop();dc.Pop();
        }
    }
    private static CapturePixels Capture(Window host)
    {
        host.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));
        _=WgcWindowCapture.Capture(new WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3));
        return WgcWindowCapture.Capture(new WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("No GPU group frame.");
    }
    private static nint Handle(NativeDirectPresenter p)=>((SafeHandle)typeof(NativeDirectPresenter).GetField("_handle",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(p)!).DangerousGetHandle();
    private static void Save(string name,CapturePixels reference,CapturePixels actual)
    {
        var output=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"group-parity");System.IO.Directory.CreateDirectory(output);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,name+"-reference.bgra"),reference.Pixels);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(output,name+"-native.bgra"),actual.Pixels);
    }
}
