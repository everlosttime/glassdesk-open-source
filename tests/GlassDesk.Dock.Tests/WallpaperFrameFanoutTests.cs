using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

namespace GlassDesk.Dock.Tests;

public sealed class WallpaperFrameFanoutTests
{
    [Fact]
    public void RecoveringProviderReopensTheSameSourceAfterSharedScopeFailure()
    {
        var created=0;var fallbacks=0;var sources=new List<RecoverySource>();
        using var capture=new RecoveringDesktopSceneCapture(new(0,0,2,2),()=>
        {
            var source=new RecoverySource(++created==1);sources.Add(source);return source;
        },()=>{fallbacks++;return new RecoverySource(false);},Stopwatch.GetTimestamp);
        Assert.Null(capture.TryRead());
        Assert.Equal(0,fallbacks);Assert.Equal(2,created);Assert.True(sources[0].Disposed);
        Assert.NotNull(capture.TryRead());
    }

    [Fact]
    public void NormalWorkersReceiveTheSharedFrameScopeWithoutUserConfiguration()
    {
        var oldSource=Environment.GetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE");
        var oldFrames=Environment.GetEnvironmentVariable(SharedWallpaperBuffer.FrameFanoutEnvironmentVariable);
        Environment.SetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE",null);
        Environment.SetEnvironmentVariable(SharedWallpaperBuffer.FrameFanoutEnvironmentVariable,null);
        try
        {
            var start=new ProcessStartInfo(CompatibilityProbePath.File("GlassCaptureProbe.exe"));
            start.ArgumentList.Add("--capture-protocol-fixture");start.ArgumentList.Add("wallpaper-settings");
            using var capture=new IsolatedDesktopSceneCapture(new(0,0,16,16),start);
            var pixels=new byte[16*16*4];
            Assert.NotNull(capture.TryReadSampled(new(0,0,16,16),16,16,pixels));
            Assert.Equal(new byte[]{1,1,0,255},pixels[..4]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE",oldSource);
            Environment.SetEnvironmentVariable(SharedWallpaperBuffer.FrameFanoutEnvironmentVariable,oldFrames);
        }
    }

    [Fact]
    public void FailedPeersRotateTheirSharedScopeOnceBeforeRecovery()
    {
        var prior = Environment.GetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE");
        Environment.SetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE", "1");
        try
        {
            var scope = CaptureProcessFamily.WallpaperSourceScope;
            IsolatedDesktopSceneCapture Peer()
            {
                var start = new ProcessStartInfo(CompatibilityProbePath.File("GlassCaptureProbe.exe"));
                start.ArgumentList.Add("--capture-protocol-fixture"); start.ArgumentList.Add("wallpaper-source-abandoned");
                return new(new(0, 0, 16, 16), start);
            }
            using var a = Peer(); using var b = Peer();
            var pixels = new byte[16 * 16 * 4];
            Assert.Throws<SharedWallpaperBufferException>(() => a.TryReadSampled(new(0, 0, 16, 16), 16, 16, pixels));
            var nextScope = CaptureProcessFamily.WallpaperSourceScope;
            Assert.NotEqual(scope, nextScope);
            Assert.Throws<SharedWallpaperBufferException>(() => b.TryReadSampled(new(0, 0, 16, 16), 16, 16, pixels));
            Assert.Equal(nextScope, CaptureProcessFamily.WallpaperSourceScope);
        }
        finally { Environment.SetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE", prior); }
    }

    [Fact]
    public void IndependentConsumersReadOneCurrentSourceAndEachConsumerStillAdvances()
    {
        var scope = Guid.NewGuid().ToString("N"); var reads = 0;
        bool Read(nint _, nint dc) { Fill(dc, reads++ == 0 ? 0x000000ffu : 0x00ff0000u); return true; }
        using var a = Create(scope, Read); using var b = Create(scope, Read);
        var first = new byte[16 * 16 * 4]; var second = new byte[first.Length];
        Assert.True(Draw(a, first)); Assert.True(Draw(b, second));
        Assert.Equal(1, reads); Assert.Equal(first, second);
        Assert.Equal(a.LastSourceQpcTicks, b.LastSourceQpcTicks);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, first[..4]);
        Assert.True(Draw(a, first)); Assert.Equal(2, reads);
        Assert.True(Draw(b, second)); Assert.Equal(2, reads); Assert.Equal(first, second);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, first[..4]);
    }

    [Fact]
    public void ANewSourceTimeBarrierForcesAFreshReadAndFutureBarriersAreRejected()
    {
        var scope = Guid.NewGuid().ToString("N"); var reads = 0;
        bool Read(nint _, nint dc) { Fill(dc, reads++ == 0 ? 0x000000ffu : 0x00ff0000u); return true; }
        using var a = Create(scope, Read); using var b = Create(scope, Read);
        var pixels = new byte[16 * 16 * 4]; Assert.True(Draw(a, pixels));
        var barrier = Stopwatch.GetTimestamp();
        Assert.True(b.TryDraw(77, 0, 0, 16, 16, 0, 0, 16, 16, pixels, barrier));
        Assert.Equal(2, reads); Assert.True(b.LastSourceQpcTicks >= barrier);
        Assert.False(b.TryDraw(77, 0, 0, 16, 16, 0, 0, 16, 16, pixels, Stopwatch.GetTimestamp() + Stopwatch.Frequency));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void FailedFreshWriteCannotLeaveAnOldPublishedIdentityOnChangedPixels()
    {
        var scope = Guid.NewGuid().ToString("N"); var fail = false; var reads = 0;
        bool Read(nint _, nint dc) { reads++; Fill(dc, fail ? 0x00ff0000u : 0x000000ffu); return !fail; }
        using var a = Create(scope, Read); using var b = Create(scope, Read);
        var pixels = new byte[16 * 16 * 4];
        Assert.True(Draw(a, pixels)); Assert.True(Draw(b, pixels)); Assert.Equal(1, reads);
        fail = true;
        Assert.False(Draw(a, pixels));
        using var newcomer = Create(scope, Read);
        Assert.False(Draw(newcomer, pixels));
    }

    [Fact]
    public void AConsumerCannotReuseAnExpiredPublishedFrame()
    {
        var scope = Guid.NewGuid().ToString("N"); var reads = 0;
        bool Read(nint _, nint dc) { Fill(dc, reads++ == 0 ? 0x000000ffu : 0x00ff0000u); return true; }
        using var a = Create(scope, Read); using var b = Create(scope, Read);
        var pixels = new byte[16 * 16 * 4]; Assert.True(Draw(a, pixels));
        Thread.Sleep(150);
        Assert.True(Draw(b, pixels)); Assert.Equal(2, reads);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
    }

    [Fact]
    public void ABlackRegionOutsideTheFirstCropStillReacquiresForTheSecondConsumer()
    {
        var scope=Guid.NewGuid().ToString("N");var reads=0;
        bool Read(nint _,nint dc)
        {
            reads++;
            Paint(dc,0,0,32,16,0x00ff0000);
            if(reads==1)Paint(dc,16,0,32,16,0);
            return true;
        }
        using var a=new LiveWallpaperSurface(32,16,16,16,Read,null,scope,true);
        using var b=new LiveWallpaperSurface(32,16,16,16,Read,null,scope,true);
        var pixels=new byte[16*16*4];
        Assert.True(a.TryDraw(77,0,0,16,16,0,0,32,16,pixels));Assert.Equal(2,reads);
        Assert.True(b.TryDraw(77,16,0,16,16,0,0,32,16,pixels));Assert.Equal(2,reads);
        Assert.Equal(new byte[]{255,0,0,255},pixels[..4]);
    }

    private static LiveWallpaperSurface Create(string scope, Func<nint, nint, bool> read) => new(16, 16, 16, 16, read, null, scope, true);
    private sealed class RecoverySource(bool fail):IDesktopSceneCapture
    {
        internal bool Disposed {get;private set;}
        public long SceneRevision=>1;
        public nint CarrierHandle=>0;
        public bool UpdateExclusions()=>false;
        public void ReleaseReadbackBuffers() { }
        public CapturePixels? TryRead(TimeSpan wait=default,CancellationToken cancellation=default,long notBefore=0,bool reuse=false)
        {
            if(fail)throw new SharedWallpaperBufferException();
            return new(2,2,new byte[16]){SourceQpcTicks=Stopwatch.GetTimestamp()};
        }
        public void Dispose()=>Disposed=true;
    }
    private static bool Draw(LiveWallpaperSurface surface, byte[] pixels) => surface.TryDraw(77, 0, 0, 16, 16, 0, 0, 16, 16, pixels);
    private static void Fill(nint dc, uint color)
        =>Paint(dc,0,0,16,16,color);
    private static void Paint(nint dc,int left,int top,int right,int bottom,uint color)
    {
        var brush = CreateSolidBrush(color); var rect = new Rect { Left=left,Top=top,Right=right,Bottom=bottom };
        try { Assert.NotEqual(0, FillRect(dc, ref rect, brush)); }
        finally { DeleteObject(brush); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
}
