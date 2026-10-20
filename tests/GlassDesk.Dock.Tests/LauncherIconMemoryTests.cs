using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Imaging;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherIconMemoryTests
{
    private static Type LoaderType => Assembly.Load("GlassDesk.Launcher").GetType("GlassDesk.Launcher.LauncherIconLoader", true)!;
    private static Task<BitmapImage?> Load(object loader, string key, Func<BitmapImage?> resolve) =>
        (Task<BitmapImage?>)LoaderType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loader, [key, resolve])!;

    [Fact]
    public void ByteBudgetEvictsLeastRecentlyUsedDecodedImage()
    {
        StaTest.Run(() =>
        {
            using var loader = (IDisposable)Activator.CreateInstance(LoaderType,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [8192L], null)!;
            var calls = new Dictionary<string, int>();
            BitmapImage? Resolve(string key)
            {
                calls[key] = calls.GetValueOrDefault(key) + 1;
                return Image(32, 32);
            }
            foreach (var key in new[] { "a", "b", "b", "c", "b" })
                Assert.NotNull(Load(loader, key, () => Resolve(key)).GetAwaiter().GetResult());
            Assert.Equal(1, calls["b"]);
            Assert.NotNull(Load(loader, "a", () => Resolve("a")).GetAwaiter().GetResult());
            Assert.Equal(2, calls["a"]);
        });
    }

    [Fact]
    public void SleepingDefersQueuedDecodeAndResumesWithoutLosingRequest()
    {
        StaTest.Run(() =>
        {
            using var loader = (IDisposable)Activator.CreateInstance(LoaderType, nonPublic: true)!;
            var suspend = LoaderType.GetMethod("SetSuspended", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(suspend);
            suspend.Invoke(loader, [true]);
            using var entered = new ManualResetEventSlim();
            var task = Load(loader, "queued", () => { entered.Set(); return Image(32, 32); });
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(150)));
            Assert.False(task.IsCompleted);
            suspend.Invoke(loader, [false]);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.NotNull(task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void DisposingSleepingLoaderCompletesPendingRequest()
    {
        StaTest.Run(() =>
        {
            using var loader = (IDisposable)Activator.CreateInstance(LoaderType, nonPublic: true)!;
            var suspend = LoaderType.GetMethod("SetSuspended", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(suspend);
            suspend.Invoke(loader, [true]);
            var task = Load(loader, "queued", () => throw new InvalidOperationException("decode must remain asleep"));
            loader.Dispose();
            Assert.Null(task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void CustomImageDecodesAtPhysicalDisplaySizeAndPreservesAspectRatio()
    {
        StaTest.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "GlassDesk-icon-decode-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, PngWriter.EncodeBgra(new byte[1024 * 512 * 4], 1024, 512));
            try
            {
                var decoder = Assembly.Load("GlassDesk.Launcher").GetType("GlassDesk.Launcher.LauncherIconDecoder", true)!;
                var image = (BitmapImage)decoder.GetMethod("DecodeFile", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path, 120])!;
                Assert.Equal(120, image.PixelWidth);
                Assert.Equal(60, image.PixelHeight);
                Assert.True(image.IsFrozen);
            }
            finally { File.Delete(path); }
        });
    }

    private static BitmapImage Image(int width, int height)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(PngWriter.EncodeBgra(new byte[width * height * 4], width, height));
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit(); image.Freeze();
        return image;
    }
}
