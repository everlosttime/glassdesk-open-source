using System.Reflection;
using System.IO;
using System.Windows.Media.Imaging;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherIconLoadingTests
{
    [Fact]
    public void ASlowIconDoesNotRunOnTheUiThreadAndDecodedPixelsAreReused()
    {
        StaTest.Run(() =>
        {
            var assembly = Assembly.Load("GlassDesk.Launcher");
            var type = assembly.GetType("GlassDesk.Launcher.LauncherIconLoader");
            Assert.NotNull(type);
            using var loader = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
            var load = type.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var clear = type.GetMethod("Clear", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ui = Environment.CurrentManagedThreadId;
            var calls = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            BitmapImage? Resolve()
            {
                Assert.NotEqual(ui, Environment.CurrentManagedThreadId);
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Interlocked.Increment(ref calls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(GlassDesk.Core.Imaging.PngWriter.EncodeBgra([0, 255, 0, 255], 1, 1));
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit(); image.Freeze();
                return image;
            }
            var task = (Task<BitmapImage?>)load.Invoke(loader, ["icon-a", (Func<BitmapImage?>)Resolve])!;
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            try
            {
                Assert.False(task.IsCompleted);
                Assert.Same(task, load.Invoke(loader, ["icon-a", (Func<BitmapImage?>)Resolve]));
            }
            finally { release.Set(); }
            var image = task.GetAwaiter().GetResult();
            Assert.NotNull(image); Assert.True(image.IsFrozen);
            Assert.Same(image, ((Task<BitmapImage?>)load.Invoke(loader, ["icon-a", (Func<BitmapImage?>)Resolve])!).Result);
            Assert.Equal(1, calls);
            clear.Invoke(loader, null);
            Assert.NotNull(((Task<BitmapImage?>)load.Invoke(loader, ["icon-a", (Func<BitmapImage?>)Resolve])!).Result);
            Assert.Equal(2, calls);
        });
    }
}
