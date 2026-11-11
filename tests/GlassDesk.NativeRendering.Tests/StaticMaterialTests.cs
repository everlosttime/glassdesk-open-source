using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlassDesk.NativeRendering.Tests;

public sealed class StaticMaterialTests
{
    [Theory]
    [InlineData(false, 0.5f)]
    [InlineData(false, 1f)]
    [InlineData(true, 0.5f)]
    public void GpuPrefilterMatchesProductionLegacyBytes(bool frosted, float clarity) => NativeEngineTests.OnSta(() =>
    {
        const int w = 16, h = 12;
        var window = new Window();
        var info = Info.Create(w, h, new WindowInteropHelper(window).EnsureHandle()); info.Flags = 1;
        Marshal.ThrowExceptionForHR(Api.Create(ref info, out var engine));
        try
        {
            var bytes = new byte[w*h*4];
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            {
                var i = (y*w+x)*4;
                bytes[i] = (byte)(x*13); bytes[i+1]=(byte)(y*19); bytes[i+2]=(byte)((x+y)%2*255); bytes[i+3]=255;
            }
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var upload = new Upload { Size=32, Version=1, Width=w, Height=h, Stride=w*4, Pixels=pin.AddrOfPinnedObject() };
                Assert.Equal(0, UploadBitmap(engine, ref upload));
            }
            finally { pin.Free(); }
            var material = new Material { Size=48, Version=1, DipWidth=32, DipHeight=24, Strength=1,
                Clarity=clarity, TintOpacity=.16f, Dark=1, Kind=frosted ? 1u : 0u };
            Assert.Equal(0, RenderMaterial(engine, ref material));
            var frame = Wait(engine);
            try
            {
                var expected = new byte[bytes.Length]; var scratch = new byte[bytes.Length];
                if (frosted) GlassDesk.UI.Controls.AndroidLiquidGlassRenderer.RenderFrostedBackdropInto(bytes, expected, scratch, w,h,new Size(32,24),true,1,clarity);
                else GlassDesk.UI.Controls.AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(bytes, expected, scratch,w,h,new Size(32,24),true,1,backgroundClarity:clarity);
                for (uint y=0; y<h; y++) for (uint x=0; x<w; x++)
                {
                    Assert.Equal(0, Api.ReadPixel(engine, frame.Lease, x,y,out var pixel));
                    var actual = BitConverter.GetBytes(pixel);
                    for (var c=0;c<4;c++) Assert.InRange((int)actual[c], Math.Max(0,expected[(y*w+x)*4+c]-2), Math.Min(255,expected[(y*w+x)*4+c]+2));
                }
            }
            finally { Api.Release(engine,frame.Lease); }
        }
        finally { Api.Destroy(engine); window.Close(); }
    });

    [Fact]
    public void StaticMaterialTintsRealUploadedPixelsAndRejectsMalformedUpload() => NativeEngineTests.OnSta(() =>
    {
        var window = new Window();
        var info = Info.Create(64, 32, new WindowInteropHelper(window).EnsureHandle());
        info.Flags = 1;
        Marshal.ThrowExceptionForHR(Api.Create(ref info, out var engine));
        try
        {
            var bytes = new byte[64 * 32 * 4];
            for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = 40; bytes[i+1] = 80; bytes[i+2] = 120; bytes[i+3] = 255; }
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var upload = new Upload { Size = 32, Version = 1, Width = 64, Height = 32, Stride = 256, Pixels = pin.AddrOfPinnedObject() };
                var library = NativeLibrary.Load(Api.LibraryPath);
                try { Assert.True(NativeLibrary.TryGetExport(library, "GD_UploadBitmap", out _), "Real wallpaper upload is missing."); }
                finally { NativeLibrary.Free(library); }
                Assert.Equal(0, UploadBitmap(engine, ref upload));
                upload.Stride = 1;
                Assert.Equal(unchecked((int)0x80070057), UploadBitmap(engine, ref upload));
            }
            finally { pin.Free(); }
            var material = new Material { Size = 48, Version = 1, DipWidth = 64, DipHeight = 32, Strength = 0, Clarity = .5f, TintOpacity = .16f, Dark = 1 };
            Assert.Equal(0, RenderMaterial(engine, ref material));
            var frame = Wait(engine);
            try
            {
                Assert.Equal(0, Api.ReadPixel(engine, frame.Lease, 32, 16, out var pixel));
                Assert.Equal(new byte[] { 40, 80, 120, 255 }, BitConverter.GetBytes(pixel));
            }
            finally { Api.Release(engine, frame.Lease); }
            material.Strength = 1; material.Clarity = 1; material.TintOpacity = .5f;
            Assert.Equal(0, RenderMaterial(engine, ref material));
            frame = Wait(engine);
            try
            {
                Assert.Equal(0, Api.ReadPixel(engine, frame.Lease, 32, 16, out var pixel));
                // Luminance=85.64; saturation 1.5 -> 17,77,137; dark tint 18 at .5 -> 18,48,78.
                var expected = new[] { 18, 48, 78, 255 };
                var actual = BitConverter.GetBytes(pixel);
                for (var c = 0; c < 4; c++) Assert.InRange((int)actual[c], expected[c]-1, expected[c]+1);
            }
            finally { Api.Release(engine, frame.Lease); }
            material.Clarity = float.NaN;
            Assert.Equal(unchecked((int)0x80070057), RenderMaterial(engine, ref material));
        }
        finally { Api.Destroy(engine); window.Close(); }
    });

    internal static Frame Wait(nint engine)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 3)
        {
            var frame = Frame.Create();
            var result = Api.Acquire(engine, ref frame);
            if (result == 0) return frame;
            Assert.Equal(1, result); Thread.Sleep(1);
        }
        throw new TimeoutException();
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Upload
    { internal uint Size, Version, Width, Height, Stride, Reserved; internal nint Pixels; }
    [StructLayout(LayoutKind.Sequential)] internal struct Material
    {
        internal uint Size, Version;
        internal float DipWidth, DipHeight, Strength, Clarity, TintOpacity;
        internal uint Kind, Dark, Reserved1, Reserved2, Reserved3;
    }
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint="GD_UploadBitmap", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int UploadBitmap(nint engine, ref Upload upload);
    [DllImport("GlassDesk.Rendering.Native.dll", EntryPoint="GD_RenderMaterial", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int RenderMaterial(nint engine, ref Material material);
}
