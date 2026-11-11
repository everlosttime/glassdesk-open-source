using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Dock.Tests;

public sealed class CroppedReadbackTests
{
    [Fact]
    public void CroppedFramesKeepBlackContentSceneTimeBarriersCancellationAndValidation()=>StaTest.Run(()=>
    {
        var window=new Window{Width=256,Height=180,Left=80,Top=80,WindowStyle=WindowStyle.None,
            ShowInTaskbar=false,Background=Brushes.Black};window.Show();
        try
        {
            var traces=new ConcurrentQueue<CaptureTrace>();
            using var capture=new PersistentWindowCapture(new WindowInteropHelper(window).Handle,traces.Enqueue);
            var crop=new PixelBounds(16,18,96,58);
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            Assert.Null(capture.TryReadCropped(crop,cancellation:canceled.Token));
            Assert.Throws<ArgumentOutOfRangeException>(()=>capture.TryReadCropped(new(-1,18,96,58)));
            Assert.DoesNotContain(traces,t=>t.Stage=="ReadbackResource");
            Assert.Equal(new byte[]{0,0,0,255},Read(capture,crop).Center);
            foreach(var brush in new[]{Brushes.Blue,Brushes.Black,Brushes.Lime})
            {
                window.Background=brush;var barrier=Stopwatch.GetTimestamp();
                capture.RetireQueuedFramesAtSceneBoundary(barrier);capture.RefreshSessionAtSceneBoundary();
                var frame=Read(capture,crop,barrier);
                Assert.True(frame.SourceQpcTicks>=barrier);
                Assert.Equal(new[]{brush.Color.B,brush.Color.G,brush.Color.R,(byte)255},frame.Center);
            }
        }
        finally{window.Close();}
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallReadbackMatchesFullPixelsAndGdiScalingWithoutFullSizeStorage(bool transparent)=>StaTest.Run(()=>
    {
        var method=typeof(PersistentWindowCapture).GetMethod("TryReadCropped",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.NotNull(method);
        var pixels=Enumerable.Range(0,640*360).SelectMany(i=>
        {
            var a=transparent?64+i%192:255;
            return new byte[]{(byte)(i%199*a/255),(byte)(i/640%177*a/255),(byte)(i%173*a/255),(byte)a};
        }).ToArray();
        var bitmap=BitmapSource.Create(640,360,96,96,PixelFormats.Pbgra32,null,pixels,2560);bitmap.Freeze();
        var window=new Window{Width=640,Height=360,Left=80,Top=80,WindowStyle=WindowStyle.None,AllowsTransparency=transparent,
            Background=transparent?Brushes.Transparent:Brushes.Black,ShowInTaskbar=false,
            Content=new System.Windows.Controls.Image{Source=bitmap,Stretch=Stretch.Fill}};
        window.Show();window.UpdateLayout();
        try
        {
            CapturePixels full;
            using(var original=new PersistentWindowCapture(new WindowInteropHelper(window).Handle))full=Read(original,null);
            var traces=new ConcurrentQueue<CaptureTrace>();
            using var capture=new PersistentWindowCapture(new WindowInteropHelper(window).Handle,traces.Enqueue);
            var crop=new PixelBounds(31,43,231,143);var actual=Read(capture,crop);
            Assert.Equal(200,actual.Width);Assert.Equal(100,actual.Height);
            for(var y=0;y<actual.Height;y++)
                Assert.Equal(full.Pixels.AsSpan(((y+crop.Top)*full.Width+crop.Left)*4,actual.Width*4).ToArray(),actual.Pixels.AsSpan(y*actual.Width*4,actual.Width*4).ToArray());
            var allocation=Assert.Single(traces,t=>t.Stage=="ReadbackResource" && t.Reason=="Created");
            Assert.Equal(actual.Width,allocation.Width);Assert.Equal(actual.Height,allocation.Height);
            using var resize=new NativeFrameResize();var expectedSmall=new byte[73*41*4];var actualSmall=new byte[expectedSmall.Length];
            resize.ResizeInto(full,73,41,crop,expectedSmall);
            resize.ResizeInto(actual,73,41,new(0,0,actual.Width,actual.Height),actualSmall);
            Assert.Equal(expectedSmall,actualSmall);
            capture.RefreshSessionAtSceneBoundary();var moved=Read(capture,new(301,203,501,303));
            Assert.Same(actual.Pixels,moved.Pixels);
            Assert.Single(traces,t=>t.Stage=="ReadbackResource" && t.Reason=="Created");
            capture.ReleaseReadbackBuffers();capture.RefreshSessionAtSceneBoundary();_=Read(capture,crop);
            Assert.Equal(2,traces.Count(t=>t.Stage=="ReadbackResource" && t.Reason=="Created"));
        }
        finally{window.Close();}
    });

    private static CapturePixels Read(PersistentWindowCapture capture,PixelBounds? crop,long barrier=0)
    {
        var clock=Stopwatch.StartNew();
        while(clock.Elapsed<TimeSpan.FromSeconds(4))
        {
            global::Program.Pump(TimeSpan.FromMilliseconds(5));
            var result=crop is {} c
                ? (CapturePixels?)typeof(PersistentWindowCapture).GetMethod("TryReadCropped",BindingFlags.Instance|BindingFlags.NonPublic)!
                    .Invoke(capture,[c,barrier,TimeSpan.FromMilliseconds(30),CancellationToken.None,true])
                : capture.TryRead(waitForFrame:TimeSpan.FromMilliseconds(30));
            if(result is not null)return result;
        }
        throw new TimeoutException("Controlled crop fixture did not deliver a fresh frame.");
    }
}
