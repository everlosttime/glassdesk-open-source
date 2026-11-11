using System.Runtime.InteropServices;

namespace GlassDesk.Dock.Tests;

public sealed class NativeWallpaperMemoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void LargeCarrierDoesNotAllocateAnotherManagedFullSizeImage()
    {
        StaTest.Run(() =>
        {
            var window = CreateWindowExW(0, "STATIC", "", 0x8000000E, 0, 0, 1, 1, 0, 0, 0, 0);
            Assert.NotEqual(0, window);
            try
            {
                using (var warmup = new NativeWallpaperSurface(window, 1, 1)) { warmup.Update(new byte[4]); }
                var input = new byte[3840 * 2160 * 4];
                var before = GC.GetAllocatedBytesForCurrentThread();
                using var surface = new NativeWallpaperSurface(window, 3840, 2160);
                surface.Update(input); // Include deferred allocation, not just the lightweight constructor.
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                output.WriteLine($"3840x2160 carrier construction and first upload: {allocated} managed bytes.");
                Assert.True(allocated < 128 * 1024, $"Carrier allocated {allocated} managed bytes.");
            }
            finally { DestroyWindow(window); }
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UpdatesPreserveBgrRowsAndPaddingWithoutChangingInput(int width)
    {
        StaTest.Run(() =>
        {
            var window = CreateWindowExW(0, "STATIC", "", 0x8000000E, 0, 0, width, 3, 0, 0, 0, 0);
            Assert.NotEqual(0, window);
            try
            {
                using var surface = new NativeWallpaperSurface(window, width, 3);
                for (var update = 0; update < 2; update++)
                {
                    var input = Enumerable.Range(0, width * 3 * 4).Select(i => (byte)(i + update * 41)).ToArray();
                    var original = input.ToArray();
                    surface.Update(input);
                    var bitmap = SendMessageW(window, 0x0173, 0, 0);
                    Assert.NotEqual(0, bitmap);
                    Assert.NotEqual(0, GetObjectW(bitmap, Marshal.SizeOf<Bitmap>(), out var info));
                    var actual = new byte[info.Stride * 3];
                    Marshal.Copy(info.Bits, actual, 0, actual.Length);
                    for (var row = 0; row < 3; row++)
                    {
                        for (var col = 0; col < width; col++)
                            Assert.Equal(input.AsSpan((row * width + col) * 4, 3).ToArray(),
                                actual.AsSpan(row * info.Stride + col * 3, 3).ToArray());
                        Assert.All(actual.AsSpan(row * info.Stride + width * 3, info.Stride - width * 3).ToArray(), b => Assert.Equal(0, b));
                    }
                    Assert.Equal(original, input);
                }
            }
            finally { DestroyWindow(window); }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type, Width, Height, Stride;
        public ushort Planes, BitsPerPixel;
        public nint Bits;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint bitmap, int size, out Bitmap result);
}

