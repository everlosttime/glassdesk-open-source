using System.Reflection;
using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

namespace GlassDesk.Dock.Tests;

public sealed class LiveWallpaperReadbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientPartialBlackReadbackReacquiresTheCompleteCurrentFrame(bool shared)
    {
        using var source = new PrintSource(0x000000FF) { PartialFrame = read => read == 0 };
        using var capture = Create(source,shared);
        var pixels = new byte[16 * 16 * 4];
        Assert.True(capture.TryDraw(1, 0, 0, 16, 16, 0, 0, 16, 16, pixels));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan(15 * 4, 4).ToArray());
        Assert.Equal(2, source.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentMixedBlackContentIsDeliveredWithItsActualColors(bool shared)
    {
        using var source = new PrintSource(0x000000FF) { PartialFrame = read => read > 0 };
        using var capture = Create(source,shared);
        var pixels = new byte[16 * 16 * 4];
        Assert.True(capture.TryDraw(1, 0, 0, 16, 16, 0, 0, 16, 16, pixels));
        Assert.True(capture.TryDraw(1, 0, 0, 16, 16, 0, 0, 16, 16, pixels));
        Assert.Equal(new byte[] { 51, 51, 51, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels.AsSpan(15 * 4, 4).ToArray());
        Assert.InRange(source.Reads, 2, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientBlackReadbackReacquiresFreshPixelsInsteadOfPublishingTheEmptyRead(bool shared)
    {
        using var source = new PrintSource(0, 0x000000FF);
        using var capture = Create(source,shared);
        var pixels = new byte[16 * 16 * 4];
        Assert.True(capture.TryDraw(1, 0, 0, 16, 16, 0, 0, 16, 16, pixels));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
        Assert.Equal(2, source.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistentBlackWallpaperReplacesEarlierColorWithoutUnboundedRetries(bool shared)
    {
        using var source = new PrintSource(0x000000FF, 0);
        using var capture = Create(source,shared);
        var pixels = new byte[16 * 16 * 4];
        Assert.True(capture.TryDraw(1, 0, 0, 16, 16, 0, 0, 16, 16, pixels));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
        Assert.Equal(1, source.Reads);
        Assert.True(capture.TryDraw(1, 0, 0, 16, 16, 0, 0, 16, 16, pixels));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels[..4]);
        Assert.InRange(source.Reads, 2, 4);
    }

    private static LiveWallpaperSurface Create(PrintSource source,bool shared)
    {
        // Inject only the nondeterministic external PrintWindow boundary; the DIBs,
        // crop, StretchBlt, flush and alpha normalization remain real native operations.
        var ctor = typeof(LiveWallpaperSurface).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(int),typeof(int),typeof(int),typeof(int),typeof(Func<nint,nint,bool>),typeof(Action<CaptureTrace>),typeof(string)], null);
        Assert.NotNull(ctor);
        return (LiveWallpaperSurface)ctor.Invoke([16,16,16,16,(Func<nint,nint,bool>)source.Read,null,shared?Guid.NewGuid().ToString("N"):null]);
    }
    private sealed class PrintSource(params uint[] colors) : IDisposable
    {
        internal int Reads { get; private set; }
        internal Func<int, bool>? PartialFrame { get; init; }
        internal bool Read(nint window, nint dc)
        {
            var read = Reads++;
            var partial = PartialFrame?.Invoke(read) == true;
            var color = partial ? 0u : colors[Math.Min(read, colors.Length - 1)];
            var brush = CreateSolidBrush(color); var rect = new Rect { Right=16,Bottom=16 };
            try
            {
                if (FillRect(dc,ref rect,brush) == 0) return false;
                if (partial)
                {
                    var gray = CreateSolidBrush(0x00333333); rect.Right = 8;
                    try { return FillRect(dc, ref rect, gray) != 0; }
                    finally { DeleteObject(gray); }
                }
                return true;
            }
            finally { DeleteObject(brush); }
        }
        public void Dispose() { }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
