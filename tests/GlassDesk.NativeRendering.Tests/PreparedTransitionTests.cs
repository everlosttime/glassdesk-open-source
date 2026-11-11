using System.Runtime.InteropServices;
using System.Windows;

namespace GlassDesk.NativeRendering.Tests;

public sealed class PreparedTransitionTests
{
    [Theory]
    [InlineData(0u, 8f)]
    [InlineData(0u, 28f)]
    [InlineData(1u, 28f)]
    public void PreparedBackdropIsNotFilteredAgainAndSharesOutputWithShadow(uint frosted, float radius) => NativeEngineTests.OnSta(() =>
    {
        var library = NativeLibrary.Load(Api.LibraryPath);
        try { Assert.True(NativeLibrary.TryGetExport(library, "GD_PresenterUploadPreparedBackdrop", out _), "No prepared-material transport; raw material filtering would change accepted pixels."); }
        finally { NativeLibrary.Free(library); }
        const int w = 264, h = 180;
        var host = DirectShadowTests.Host(w, h); host.Show();
        var info = Info.Create(w, h, new System.Windows.Interop.WindowInteropHelper(host).Handle); info.Flags = 1;
        Assert.Equal(0, DirectPresenterTests.Create(ref info, out var presenter));
        var source = new byte[64 * 48 * 4];
        for (var i = 0; i < source.Length; i += 4) { source[i] = 70; source[i + 1] = 90; source[i + 2] = 120; source[i + 3] = 255; }
        var pin = GCHandle.Alloc(source, GCHandleType.Pinned);
        var layers = DirectShadowTests.DefaultLayers(); var layerPin = GCHandle.Alloc(layers, GCHandleType.Pinned);
        try
        {
            var bitmap = new StaticMaterialTests.Upload { Size = 32, Version = 1, Width = 64, Height = 48, Stride = 256, Pixels = pin.AddrOfPinnedObject() };
            Assert.Equal(0, Upload(presenter, 0, ref bitmap));
            var panel = new Rect(42, 42, 180, 84);
            var shadow = DirectShadowTests.Scene.Create(w, h, panel, radius, true, true, layerPin.AddrOfPinnedObject(), (uint)layers.Length);
            Assert.Equal(0, DirectShadowTests.Render(presenter, ref shadow));
            var group = new GroupInfo { Size = 32, Version = 1, Opacity = 1 };
            Assert.Equal(0, Push(presenter, ref group));
            var scene = new DirectGlassTests.GlassScene { Size = 80, Version = 2, HostWidth = w, HostHeight = h,
                PanelX = 42, PanelY = 42, PanelWidth = 180, PanelHeight = 84,
                BackdropWidth = w, BackdropHeight = h, Radius = radius, Strength = 1, Dispersion = 1, InnerGain = 1, OuterGain = 1, Launchpad = 1, Outline = 1 };
            Assert.Equal(0, Render(presenter, 0, ref scene, frosted));
            Assert.Equal(0, Pop(presenter));
            var pixels = new byte[w * h * 4]; Assert.Equal(0, DirectGlassTests.ReadPixels(presenter, pixels, (ulong)pixels.Length, w * 4));
            var center = (84 * w + 132) * 4;
            for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(source[c] - pixels[center + c]), 0, 1);
            Assert.True(pixels[(84 * w + 39) * 4 + 3] > 0, "Glass draw erased the shadow.");
            Assert.Equal(0, pixels[3]);
            Assert.Equal(0, DirectShadowTests.Render(presenter, ref shadow));
            Assert.Equal(0, Composite(presenter, 0, ref scene, frosted));
            var direct = new byte[pixels.Length];
            Assert.Equal(0, DirectGlassTests.ReadPixels(presenter, direct, (ulong)direct.Length, w * 4));
            for (var i = 0; i < pixels.Length; i++) Assert.InRange(Math.Abs(pixels[i] - direct[i]), 0, 1);
        }
        finally { layerPin.Free(); pin.Free(); DirectPresenterTests.Destroy(presenter); host.Close(); }
    });
    [StructLayout(LayoutKind.Sequential)] private struct GroupInfo { internal uint Size, Version; internal float Opacity, Left, Top, Right, Bottom; internal uint Bounded; }
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint = "GD_PresenterUploadPreparedBackdrop", CallingConvention = CallingConvention.Cdecl)] private static extern int Upload(nint presenter, uint slot, ref StaticMaterialTests.Upload bitmap);
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint = "GD_PresenterRenderPreparedGlass", CallingConvention = CallingConvention.Cdecl)] private static extern int Render(nint presenter, uint slot, ref DirectGlassTests.GlassScene scene, uint frosted);
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint = "GD_PresenterCompositePreparedGlass", CallingConvention = CallingConvention.Cdecl)] private static extern int Composite(nint presenter, uint slot, ref DirectGlassTests.GlassScene scene, uint frosted);
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint = "GD_PresenterPushGroup", CallingConvention = CallingConvention.Cdecl)] private static extern int Push(nint presenter, ref GroupInfo group);
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint = "GD_PresenterPopGroup", CallingConvention = CallingConvention.Cdecl)] private static extern int Pop(nint presenter);
}
