using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

internal sealed record CapturePixels(int Width, int Height, byte[] Pixels)
{
    internal long SourceQpcTicks { get; init; }
    internal GlassDesk.UI.Controls.GlassBackdropSource Source { get; init; } = GlassDesk.UI.Controls.GlassBackdropSource.LiveDesktop;
    internal bool IsStatic { get; init; }
    public byte[] Center => Pixels.AsSpan(((Height / 2) * Width + Width / 2) * 4, 4).ToArray();
}

internal sealed record ShadowObservation(bool MissingShadowPixels, int CapturedWidth, int CapturedHeight,
    int DwmFrameWidth, int DwmFrameHeight, int[] ShadowSampleRgb, int[] BackingReferenceRgb,
    int SampleX, int SampleY);

internal readonly record struct ScreenRect(int X, int Y, int Width, int Height);

internal static unsafe class WgcWindowCapture
{
    public static CapturePixels? Capture(nint handle, TimeSpan timeout, bool borderless = false, bool includeSecondaryWindows = false)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("WGC is not supported.");
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        IDirect3DDevice? projected = null;
        try
        {
            // BGRA support. Hardware only: this gate must not silently substitute software rendering.
            Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            var dxgiId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Check(Marshal.QueryInterface(device, in dxgiId, out dxgi));
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            projected = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            var item = CreateItem(handle);
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(projected,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            if(includeSecondaryWindows)
            {
                if(!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession","IncludeSecondaryWindows"))
                    throw new NotSupportedException("Secondary-window capture is unavailable on this OS.");
                session.IncludeSecondaryWindows=true;
            }
            if (borderless)
            {
                var permission = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
                var permissionWatch = Stopwatch.StartNew();
                while (!permission.IsCompleted && permissionWatch.Elapsed < TimeSpan.FromSeconds(10))
                    Program.Pump(TimeSpan.FromMilliseconds(20));
                if (!permission.IsCompleted || permission.GetAwaiter().GetResult() != Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                    throw new InvalidOperationException("Borderless permission is absent; do not silently show a capture indicator.");
                session.IsBorderRequired = false;
            }
            session.StartCapture();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                using var frame = pool.TryGetNextFrame();
                if (frame is not null && frame.ContentSize.Width > 0 && frame.ContentSize.Height > 0)
                    return ReadPixels(device, context, frame);
                Program.Pump(TimeSpan.FromMilliseconds(20));
            }
            return null;
        }
        finally
        {
            projected?.Dispose();
            Release(ref inspectable); Release(ref dxgi); Release(ref context); Release(ref device);
        }
    }

    internal static ShadowObservation InspectSystemShadow(nint handle, CapturePixels frame)
    {
        Check(DwmGetWindowAttribute(handle, 9, out var bounds, Marshal.SizeOf<NativeRect>()));
        // Account conservatively for any capture margin before probing OUTSIDE the source extent.
        var x = bounds.Right + Math.Max(0, frame.Width - (bounds.Right - bounds.Left)) + 4;
        var y = (bounds.Top + bounds.Bottom) / 2;
        var dc = GetDC(0);
        if (dc == 0) throw new InvalidOperationException("Controlled desktop comparison is unavailable.");
        try
        {
            var sample = GetPixel(dc, x, y);
            var reference = GetPixel(dc, x + 40, y);
            if (sample == uint.MaxValue || reference == uint.MaxValue) throw new InvalidOperationException("Controlled desktop pixel read failed.");
            static int[] Rgb(uint value) => [(int)(value & 255), (int)((value >> 8) & 255), (int)((value >> 16) & 255)];
            var actual = Rgb(sample); var backing = Rgb(reference);
            var whiteBacking = backing.All(channel => channel >= 253);
            var shadow = actual.All(channel => channel < 250) && actual.Max() - actual.Min() <= 2;
            if (!whiteBacking) throw new InvalidOperationException("Controlled white backing was occluded; shadow comparison is inconclusive.");
            return new ShadowObservation(shadow, frame.Width, frame.Height, bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, actual, backing, x, y);
        }
        finally { ReleaseDC(0, dc); }
    }

    internal static ScreenRect ReadScreenBounds(nint handle)
    {
        Check(DwmGetWindowAttribute(handle, 9, out var bounds, Marshal.SizeOf<NativeRect>()));
        return new ScreenRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    internal static int[] ReadScreenRgb(int x, int y)
    {
        var dc = GetDC(0);
        if (dc == 0) throw new InvalidOperationException("Controlled desktop comparison is unavailable.");
        try
        {
            var value = GetPixel(dc, x, y);
            if (value == uint.MaxValue) throw new InvalidOperationException("Controlled desktop pixel read failed.");
            return [(int)(value & 255), (int)((value >> 8) & 255), (int)((value >> 16) & 255)];
        }
        finally { ReleaseDC(0, dc); }
    }

    internal static GraphicsCaptureItem CreateItem(nint handle)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint text = 0, factory = 0, result = 0;
        try
        {
            Check(WindowsCreateString(name, name.Length, out text));
            var iid = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Check(RoGetActivationFactory(text, ref iid, out factory));
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Check(create(factory, handle, &itemId, &result));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally
        {
            Release(ref result); Release(ref factory);
            if (text != 0) WindowsDeleteString(text);
        }
    }

    internal static CapturePixels ReadPixels(nint device, nint context, Direct3D11CaptureFrame frame)
    {
        using var readback = new PixelReadback();
        return readback.ReadPixels(device, context, frame);
    }

    // A session's worker owns this cache, just like its D3D device/context. Only
    // native staging storage is reused; delivered CPU arrays remain immutable snapshots.
    internal sealed class PixelReadback : IDisposable
    {
        private nint _staging, _device;
        private TextureDesc _description;
        private byte[]? _reusablePixels;

        internal CapturePixels ReadPixels(nint device, nint context, Direct3D11CaptureFrame frame,
            Action<CaptureTrace>? trace = null, bool reusePixels = false)
        {
            nint surface = 0, access = 0, texture = 0;
            try
            {
                surface = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
                var accessId = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                Check(Marshal.QueryInterface(surface, in accessId, out access));
                var textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3])(access, &textureId, &texture));
                TextureDesc desc;
                ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)(*(nint**)texture)[10])(texture, &desc);
                var width = frame.ContentSize.Width; var height = frame.ContentSize.Height;
                if (width <= 0 || height <= 0 || width > desc.Width || height > desc.Height || (long)width * height > 16_000_000)
                    throw new InvalidOperationException("Controlled capture extent is invalid or exceeds the probe budget.");
                desc.Usage = 3; desc.BindFlags = 0; desc.CpuAccessFlags = 0x20000; desc.MiscFlags = 0;
                if (_staging == 0 || _device != device || !SameTexture(_description, desc))
                {
                    Release(ref _staging);
                    nint staging = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)(*(nint**)device)[5])(device, &desc, 0, &staging));
                    _staging = staging; _device = device; _description = desc;
                    trace?.Invoke(new("ReadbackResource", "Created", Stopwatch.GetTimestamp()));
                }
                else trace?.Invoke(new("ReadbackResource", "Reused", Stopwatch.GetTimestamp()));
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)(*(nint**)context)[47])(context, _staging, texture);
                MappedResource mapped;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)(*(nint**)context)[14])(context, _staging, 0, 1, 0, &mapped));
                try
                {
                    var length = checked(width * height * 4);
                    // The live worker consumes raw pixels synchronously before its next
                    // acquisition. Probes/default callers still get independent snapshots.
                    var pixels = reusePixels && _reusablePixels?.Length == length
                        ? _reusablePixels : new byte[length];
                    if (reusePixels) _reusablePixels = pixels;
                    for (var y = 0; y < height; y++)
                        Marshal.Copy(mapped.Data + checked((nint)(y * (long)mapped.RowPitch)), pixels, y * width * 4, width * 4);
                    return new CapturePixels(width, height, pixels)
                    {SourceQpcTicks=(long)Math.Round(frame.SystemRelativeTime.TotalSeconds*System.Diagnostics.Stopwatch.Frequency)};
                }
                finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)(*(nint**)context)[15])(context, _staging, 0); }
            }
            finally
            {
                Release(ref texture); Release(ref access);
                if (surface != 0) WinRT.MarshalInterface<IDirect3DSurface>.DisposeAbi(surface);
            }
        }

        private static bool SameTexture(TextureDesc a, TextureDesc b) =>
            a.Width == b.Width && a.Height == b.Height && a.MipLevels == b.MipLevels && a.ArraySize == b.ArraySize
            && a.Format == b.Format && a.SampleCount == b.SampleCount && a.SampleQuality == b.SampleQuality;

        public void Dispose()
        {
            Release(ref _staging); _device = 0; _description = default; _reusablePixels = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality,
            Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MappedResource { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint value) { if (value != 0) { Marshal.Release(value); value = 0; } }
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint inspectable);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string text, int length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out NativeRect bounds, int size);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}
