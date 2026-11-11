using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Imaging;

namespace GlassDesk.Dock.Tests;

public sealed class IconResolutionTests
{
    [Theory]
    [InlineData("Dock", 256)]
    [InlineData("Launcher", 128)]
    public void BothSurfacesChooseTheDetailedIcoFrameInsteadOfTheSmallFirstFrame(string surface, int expectedPixels)
    {
        StaTest.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "GlassDesk-multi-" + Guid.NewGuid().ToString("N") + ".ico");
            var frames = new[] { Pixels(16, 0, 0, 255), Pixels(256, 255, 0, 0) };
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
                var offset = 6 + 16 * frames.Length;
                for (var i = 0; i < frames.Length; i++)
                {
                    writer.Write((byte)(i == 0 ? 16 : 0)); writer.Write((byte)(i == 0 ? 16 : 0));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
                }
                foreach (var frame in frames) writer.Write(frame);
            }
            try
            {
                var method = surface == "Dock"
                    ? typeof(DockWindow).GetMethod("LoadBitmap", BindingFlags.Static | BindingFlags.NonPublic)!
                    : Assembly.Load("GlassDesk.Launcher").GetType("GlassDesk.Launcher.LauncherIconDecoder", true)!
                        .GetMethod("DecodeFile", BindingFlags.Static | BindingFlags.NonPublic)!;
                var result = (BitmapSource)method.Invoke(null, surface == "Dock" ? [path] : [path, 128])!;
                Assert.Equal(expectedPixels, result.PixelWidth);
                Assert.Equal(expectedPixels, result.PixelHeight);
                var pixels = new byte[result.PixelWidth * result.PixelHeight * 4]; result.CopyPixels(pixels, result.PixelWidth * 4, 0);
                var center = (result.PixelHeight / 2 * result.PixelWidth + result.PixelWidth / 2) * 4;
                Assert.True(pixels[center] > 200 && pixels[center + 2] < 30, "The 256-pixel blue frame must be selected, not the red 16-pixel frame.");
                Assert.True(result.IsFrozen);
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally { File.Delete(path); }
        });
    }

    private static byte[] Pixels(int size, byte blue, byte green, byte red)
    {
        var bytes = new byte[size * size * 4];
        for (var i=0; i<bytes.Length; i+=4) { bytes[i]=blue; bytes[i+1]=green; bytes[i+2]=red; bytes[i+3]=255; }
        return PngWriter.EncodeBgra(bytes, size, size);
    }
}
