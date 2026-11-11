using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

namespace GlassDesk.Dock.Tests;

public sealed class WallpaperSourceValidationTests
{
    [Fact]
    public void ReusingASpanningSourceValidatesThePeersDisplayBeforeDeliveringItsGoodCrop()
    {
        var reads = 0;
        bool Read(nint _, nint dc)
        {
            reads++; Paint(dc, 0, 64, 0x00ff0000);
            if (reads == 1) Paint(dc, 40, 64, 0);
            return true;
        }
        var scope = Guid.NewGuid().ToString("N");
        using var first = new LiveWallpaperSurface(64, 16, 8, 16, Read, null, scope, true);
        using var peer = new LiveWallpaperSurface(64, 16, 8, 16, Read, null, scope, true);
        // Replace only the nondeterministic platform monitor enumeration; source
        // DIBs, named sharing, time barriers, readback and crop stay real.
        var displays = new ScreenRect[] { new(0, 0, 32, 16), new(32, 0, 32, 16) };
        var field = typeof(LiveWallpaperSurface).GetField("_displays",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(first, displays); field.SetValue(peer, displays);
        var pixels = new byte[8 * 16 * 4];
        Assert.True(first.TryDraw(77, 0, 0, 8, 16, 0, 0, 64, 16, pixels));
        Assert.Equal(1, reads);
        Assert.True(peer.TryDraw(77, 32, 0, 8, 16, 0, 0, 64, 16, pixels));
        Assert.Equal(2, reads);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
    }

    [Fact]
    public void BlackVirtualDesktopGapsOutsideTheConsumersDisplayDoNotCauseRetries()
    {
        var display = GlassDesk.Platform.Displays.DisplayService.Primary()!.Bounds;
        var reads = 0;
        bool Read(nint _, nint dc)
        {
            reads++; Paint(dc, 0, 32, 0); Paint(dc, 16, 32, 0x00ff0000); return true;
        }
        using var capture = new LiveWallpaperSurface(32, 16, 16, 16, Read, null);
        var pixels = new byte[16 * 16 * 4];
        Assert.True(capture.TryDraw(77, display.X, display.Y, 16, 16,
            display.X - 16, display.Y, display.X + 16, display.Y + 16, pixels));
        Assert.Equal(1, reads);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
    }

    [Fact]
    public void AGoodCropCannotPublishAPartiallyBlackSharedSource()
    {
        var reads = 0;
        bool Read(nint _, nint dc)
        {
            reads++;
            Paint(dc, 0, 32, 0x00ff0000);
            if (reads == 1) Paint(dc, 16, 32, 0);
            return true;
        }
        var scope = Guid.NewGuid().ToString("N");
        using var first = new LiveWallpaperSurface(32, 16, 16, 16, Read, null, scope, true);
        using var peer = new LiveWallpaperSurface(32, 16, 16, 16, Read, null, scope, true);
        var left = new byte[16 * 16 * 4]; var right = new byte[left.Length];
        Assert.True(first.TryDraw(77, 0, 0, 16, 16, 0, 0, 32, 16, left));
        // The complete source must be retried before its first publication,
        // even though this consumer's left crop contains no black pixels.
        Assert.Equal(2, reads);
        Assert.True(peer.TryDraw(77, 16, 0, 16, 16, 0, 0, 32, 16, right));
        Assert.Equal(2, reads);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, left[..4]);
        Assert.Equal(left, right);
    }

    private static void Paint(nint dc, int left, int right, uint color)
    {
        var brush = CreateSolidBrush(color); var rect = new NativeRect { Left = left, Right = right, Bottom = 16 };
        try { Assert.NotEqual(0, FillRect(dc, ref rect, brush)); }
        finally { DeleteObject(brush); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
}
