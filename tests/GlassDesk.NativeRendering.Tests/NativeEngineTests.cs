using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GlassDesk.NativeRendering.Tests;

public sealed class NativeEngineTests
{
    [Fact]
    public void VersionMismatchAndMalformedDimensionsCannotCreateResources()
    {
        Assert.True(File.Exists(Api.LibraryPath), "The native rendering core has not been built.");
        var info = Info.Create(64, 32, 0);
        info.Version = 99;
        Assert.Equal(unchecked((int)0x80004002), Api.Create(ref info, out var engine));
        Assert.Equal(0, engine);
        info.Version = 1;
        info.Width = uint.MaxValue;
        Assert.Equal(unchecked((int)0x80070057), Api.Create(ref info, out engine));
        Assert.Equal(0, engine);
    }

    [Fact]
    public void GpuOutputUsesPremultipliedAlphaAndReadbackRequiresExplicitOptIn() => OnSta(() =>
    {
        using var core = new Core(diagnostics: true);
        var frame = core.Frame();
        try
        {
            Assert.NotEqual(0, frame.Surface);
            Assert.Equal(0, Api.ReadPixel(core.Handle, frame.Lease, 5, 8, out var transparent));
            Assert.Equal(0u, transparent);
            Assert.Equal(0, Api.ReadPixel(core.Handle, frame.Lease, 32, 8, out var half));
            Near(half, 26, 51, 102, 128);
            Assert.Equal(0, Api.ReadPixel(core.Handle, frame.Lease, 59, 8, out var opaque));
            Near(opaque, 51, 102, 204, 255);
        }
        finally { Assert.Equal(0, Api.Release(core.Handle, frame.Lease)); }
        Assert.Equal(3ul, core.Stats().CpuReadbacks);
        using var production = new Core();
        var deniedFrame = production.Frame();
        try { Assert.Equal(unchecked((int)0x80070005), Api.ReadPixel(production.Handle, deniedFrame.Lease, 32, 8, out _)); }
        finally { Api.Release(production.Handle, deniedFrame.Lease); }
        Assert.Equal(0ul, production.Stats().CpuReadbacks);
    });

    [Fact]
    public void SlowConsumerCannotAllocateBeyondThreeSlotsOrOverwriteLeasedFrames() => OnSta(() =>
    {
        using var core = new Core();
        var frames = Enumerable.Range(0, 3).Select(_ => core.Frame()).ToArray();
        Assert.Equal(3, frames.Select(f => f.Surface).Distinct().Count());
        try
        {
            var scene = Scene.Create();
            Assert.Equal(1, Api.Render(core.Handle, ref scene));
            var stats = core.Stats();
            Assert.Equal(3u, stats.LeasedSlots);
            Assert.Equal(1ul, stats.DroppedFrames);
            Assert.Equal(64ul * 32 * 4 * 3, stats.OutputTextureBytes);
            Assert.Equal(unchecked((int)0x800700AA), Api.Resize(core.Handle, 128, 64));
        }
        finally { foreach (var frame in frames) Assert.Equal(0, Api.Release(core.Handle, frame.Lease)); }
        Assert.Equal(unchecked((int)0x80070057), Api.Release(core.Handle, frames[0].Lease));
        Assert.Equal(0u, core.Stats().LeasedSlots);
    });

    [Fact]
    public void ResizeAndDeviceRecreationInvalidateOldGenerationWithoutLeakingLeases() => OnSta(() =>
    {
        using var core = new Core();
        var first = core.Frame();
        Api.Release(core.Handle, first.Lease);
        Assert.Equal(0, Api.Resize(core.Handle, 128, 64));
        var resized = core.Frame();
        Assert.Equal(128u, resized.Width);
        Assert.True(resized.Generation > first.Generation);
        Api.Release(core.Handle, resized.Lease);
        Assert.Equal(0, Api.Recreate(core.Handle));
        var recovered = core.Frame();
        Assert.True(recovered.Generation > resized.Generation);
        Assert.Equal(unchecked((int)0x80070057), Api.Release(core.Handle, first.Lease));
        Assert.Equal(1u, core.Stats().LeasedSlots);
        Api.Release(core.Handle, recovered.Lease);
    });

    [Fact]
    public void NormalRenderingDoesNotReadPixelsAndResourceUseStaysBounded() => OnSta(() =>
    {
        using var core = new Core();
        for (var i = 0; i < 120; i++)
        {
            var frame = core.Frame();
            Assert.Equal(0, Api.Release(core.Handle, frame.Lease));
        }
        var stats = core.Stats();
        Assert.Equal(120ul, stats.SubmittedFrames);
        Assert.Equal(0ul, stats.CpuReadbacks);
        Assert.Equal(64ul * 32 * 4 * 3, stats.OutputTextureBytes);
        Assert.Equal(0u, stats.LeasedSlots);
        var scene = Scene.Create();
        var wrongThread = Task.Run(() => Api.Render(core.Handle, ref scene)).GetAwaiter().GetResult();
        Assert.Equal(unchecked((int)0x8001010E), wrongThread);
    });

    private static void Near(uint bgra, params byte[] expected)
    {
        var actual = BitConverter.GetBytes(bgra);
        for (var i = 0; i < 4; i++) Assert.InRange((int)actual[i], expected[i] - 1, expected[i] + 1);
    }

    internal static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native GPU test exceeded its deadline.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class Core : IDisposable
    {
        private readonly Window _window = new();
        internal nint Handle { get; }
        internal Core(bool diagnostics = false)
        {
            var info = Info.Create(64, 32, new WindowInteropHelper(_window).EnsureHandle());
            info.Flags = diagnostics ? 1u : 0u;
            Marshal.ThrowExceptionForHR(Api.Create(ref info, out var engine));
            Handle = engine;
        }
        internal Frame Frame()
        {
            var scene = Scene.Create();
            Assert.Equal(0, Api.Render(Handle, ref scene));
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                var frame = Tests.Frame.Create();
                var hr = Api.Acquire(Handle, ref frame);
                if (hr == 0) return frame;
                Assert.Equal(1, hr);
                Thread.Sleep(1);
            }
            throw new TimeoutException("GPU did not complete a submitted frame.");
        }
        internal Stats Stats() { var stats = Tests.Stats.Create(); Assert.Equal(0, Api.GetStats(Handle, ref stats)); return stats; }
        public void Dispose() { Api.Destroy(Handle); _window.Close(); }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Info
{
    public uint Size, Version, Width, Height;
    public nint Window;
    public uint Flags, Reserved;
    internal static Info Create(uint width, uint height, nint window) => new() { Size = (uint)Marshal.SizeOf<Info>(), Version = 1, Width = width, Height = height, Window = window };
}
[StructLayout(LayoutKind.Sequential)]
internal struct Scene
{
    public uint Size, Version;
    public double Seconds;
    public float Opacity, Radius;
    public uint Pattern, Reserved;
    internal static Scene Create() => new() { Size = (uint)Marshal.SizeOf<Scene>(), Version = 1, Opacity = 1, Radius = 8 };
}
[StructLayout(LayoutKind.Sequential)]
internal struct Frame
{
    public uint Size, Version;
    public nint Surface;
    public ulong Lease, Sequence, Generation;
    public long Qpc;
    public uint Width, Height;
    internal static Frame Create() => new() { Size = (uint)Marshal.SizeOf<Frame>(), Version = 1 };
}
[StructLayout(LayoutKind.Sequential)]
internal struct Stats
{
    public uint Size, Version;
    public ulong Generation, SubmittedFrames, AcquiredFrames, DroppedFrames, CpuReadbacks, OutputTextureBytes;
    public uint Width, Height, LeasedSlots, PendingSlots, AdapterLow;
    public int AdapterHigh;
    internal static Stats Create() => new() { Size = (uint)Marshal.SizeOf<Stats>(), Version = 1 };
}
internal static class Api
{
    internal static string LibraryPath => Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_RENDER_DLL") ??
        Path.Combine(FindRoot(), "artifacts", "native-rendering", "GlassDesk.Rendering.Native.dll");
    static Api() => NativeLibrary.SetDllImportResolver(typeof(Api).Assembly,
        (name, _, _) => name == "GlassDesk.Rendering.Native.dll" ? NativeLibrary.Load(LibraryPath) : 0);
    internal static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("Specify GLASSDESK_NATIVE_RENDER_DLL outside the checkout.");
    }
    private const string Dll = "GlassDesk.Rendering.Native.dll";
    [DllImport(Dll, EntryPoint = "GD_Create", CallingConvention = CallingConvention.Cdecl)] internal static extern int Create(ref Info info, out nint engine);
    [DllImport(Dll, EntryPoint = "GD_Destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void Destroy(nint engine);
    [DllImport(Dll, EntryPoint = "GD_Render", CallingConvention = CallingConvention.Cdecl)] internal static extern int Render(nint engine, ref Scene scene);
    [DllImport(Dll, EntryPoint = "GD_Acquire", CallingConvention = CallingConvention.Cdecl)] internal static extern int Acquire(nint engine, ref Frame frame);
    [DllImport(Dll, EntryPoint = "GD_Release", CallingConvention = CallingConvention.Cdecl)] internal static extern int Release(nint engine, ulong lease);
    [DllImport(Dll, EntryPoint = "GD_Resize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Resize(nint engine, uint width, uint height);
    [DllImport(Dll, EntryPoint = "GD_Recreate", CallingConvention = CallingConvention.Cdecl)] internal static extern int Recreate(nint engine);
    [DllImport(Dll, EntryPoint = "GD_GetStats", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetStats(nint engine, ref Stats stats);
    [DllImport(Dll, EntryPoint = "GD_ReadPixel", CallingConvention = CallingConvention.Cdecl)] internal static extern int ReadPixel(nint engine, ulong lease, uint x, uint y, out uint bgra);
}
