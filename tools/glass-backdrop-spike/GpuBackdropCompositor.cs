using System.Runtime.InteropServices;
using System.Text;

internal sealed record CapturedLayer(CapturePixels Frame, ScreenRect Bounds);

internal static unsafe class GpuBackdropCompositor
{
    private const string Shader = """
        cbuffer Layout : register(b0) { float4 destination; float2 outputSize; float2 padding; };
        struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        Vertex VSMain(uint id : SV_VertexID) {
            float2 corners[6] = { float2(0,0), float2(1,0), float2(0,1), float2(0,1), float2(1,0), float2(1,1) };
            Vertex result; result.uv = corners[id];
            float2 pixel = destination.xy + result.uv * destination.zw;
            result.position = float4(pixel / outputSize * float2(2,-2) + float2(-1,1), 0, 1);
            return result;
        }
        Texture2D source : register(t0); SamplerState sourceSampler : register(s0);
        float4 PSMain(Vertex input) : SV_TARGET { return source.Sample(sourceSampler, input.uv); }
        """;

    // Characterization only: CPU snapshots are uploaded. Shipping must retain WGC textures on a shared device.
    internal static CapturePixels Compose(IReadOnlyList<CapturedLayer> layers, ScreenRect region, int width, int height)
    {
        if (width <= 0 || height <= 0 || region.Width <= 0 || region.Height <= 0 || (long)width * height > 500_000)
            throw new ArgumentOutOfRangeException(nameof(width));
        nint device = 0, context = 0, target = 0, view = 0, staging = 0, vertex = 0, pixel = 0,
            constants = 0, blend = 0, sampler = 0;
        try
        {
            Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            var desc = new TextureDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1,
                ArraySize = 1, Format = 87, SampleCount = 1, BindFlags = 0x20 };
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Slot(device, 5))(device, &desc, 0, &target));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, 9))(device, target, 0, &view));
            vertex = CreateShader(device, "VSMain", "vs_4_0", 12);
            pixel = CreateShader(device, "PSMain", "ps_4_0", 15);
            var constantDesc = new BufferDesc { ByteWidth = 32, BindFlags = 4 };
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)Slot(device, 3))(device, &constantDesc, 0, &constants));
            var blendDesc = new BlendDesc();
            var firstTarget = (TargetBlend*)blendDesc.Targets;
            *firstTarget = new TargetBlend { Enabled = 1, Source = 2, Destination = 6, Operation = 1,
                SourceAlpha = 2, DestinationAlpha = 6, AlphaOperation = 1, WriteMask = 15 };
            Check(((delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)Slot(device, 20))(device, &blendDesc, &blend));
            var samplerDesc = new SamplerDesc { Filter = 21, AddressU = 3, AddressV = 3, AddressW = 3,
                MaximumAnisotropy = 1, Comparison = 1, MaximumLod = float.MaxValue };
            Check(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)Slot(device, 23))(device, &samplerDesc, &sampler));
            var viewport = new Viewport { Width = width, Height = height, MaximumDepth = 1 };
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(context, 33))(context, 1, &view, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Slot(context, 35))(context, blend, null, uint.MaxValue);
            ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)Slot(context, 44))(context, 1, &viewport);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(context, 24))(context, 4);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(context, 11))(context, vertex, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(context, 9))(context, pixel, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 7))(context, 0, 1, &constants);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 10))(context, 0, 1, &sampler);
            float* clear = stackalloc float[4];
            // Transparent clear: the captured desktop/backing layer must supply every requested pixel.
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Slot(context, 50))(context, view, clear);
            foreach (var layer in layers)
            {
                if (layer.Frame.Width != layer.Bounds.Width || layer.Frame.Height != layer.Bounds.Height)
                    throw new InvalidOperationException("Capture extent and screen bounds disagree; do not stretch a mismatched source.");
                DrawLayer(device, context, constants, layer, region, width, height);
            }
            desc.BindFlags = 0; desc.Usage = 3; desc.CpuAccessFlags = 0x20000;
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Slot(device, 5))(device, &desc, 0, &staging));
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47))(context, staging, target);
            MappedResource mapped;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedResource*, int>)Slot(context, 14))(context, staging, 0, 1, 0, &mapped));
            try
            {
                var output = new byte[checked(width * height * 4)];
                for (var y = 0; y < height; y++) Marshal.Copy(mapped.Data + checked((nint)(y * (long)mapped.RowPitch)), output, y * width * 4, width * 4);
                return new CapturePixels(width, height, output);
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, 15))(context, staging, 0); }
        }
        finally
        {
            if (context != 0) ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 110))(context);
            Release(ref sampler); Release(ref blend); Release(ref constants); Release(ref pixel); Release(ref vertex);
            Release(ref staging); Release(ref view); Release(ref target); Release(ref context); Release(ref device);
        }
    }

    private static void DrawLayer(nint device, nint context, nint constants, CapturedLayer layer, ScreenRect region, int width, int height)
    {
        nint texture = 0, sourceView = 0;
        try
        {
            var desc = new TextureDesc { Width = (uint)layer.Frame.Width, Height = (uint)layer.Frame.Height,
                MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, BindFlags = 8, Usage = 1 };
            fixed (byte* input = layer.Frame.Pixels)
            {
                var initial = new SubresourceData { Data = (nint)input, RowPitch = (uint)layer.Frame.Width * 4 };
                Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, SubresourceData*, nint*, int>)Slot(device, 5))(device, &desc, &initial, &texture));
            }
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, 7))(device, texture, 0, &sourceView));
            var layout = new Layout { X = (float)(layer.Bounds.X - region.X) * width / region.Width,
                Y = (float)(layer.Bounds.Y - region.Y) * height / region.Height,
                Width = (float)layer.Bounds.Width * width / region.Width, Height = (float)layer.Bounds.Height * height / region.Height,
                OutputWidth = width, OutputHeight = height };
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, uint, uint, void>)Slot(context, 48))(context, constants, 0, 0, (nint)(&layout), 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 8))(context, 0, 1, &sourceView);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, 13))(context, 6, 0);
        }
        finally { Release(ref sourceView); Release(ref texture); }
    }

    private static nint CreateShader(nint device, string entry, string target, int slot)
    {
        nint blob = 0, errors = 0, shader = 0;
        var source = Encoding.UTF8.GetBytes(Shader);
        try
        {
            fixed (byte* bytes = source)
            {
                var hr = D3DCompile((nint)bytes, (nuint)source.Length, "GlassDeskBackdropProbe", 0, 0, entry, target, 1 << 15, 0, out blob, out errors);
                if (hr < 0 && errors != 0)
                {
                    var message = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(errors, 3))(errors);
                    throw new InvalidOperationException("Backdrop shader compilation failed: " + Marshal.PtrToStringAnsi(message));
                }
                Check(hr);
            }
            var code = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(blob, 3))(blob);
            var size = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(blob, 4))(blob);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Slot(device, slot))(device, code, size, 0, &shader));
            return shader;
        }
        finally { Release(ref errors); Release(ref blob); }
    }

    private static nint Slot(nint value, int slot) => (*(nint**)value)[slot];
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint value) { if (value != 0) { Marshal.Release(value); value = 0; } }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureStride; }
    [StructLayout(LayoutKind.Sequential)] private struct SubresourceData { public nint Data; public uint RowPitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct Layout { public float X, Y, Width, Height, OutputWidth, OutputHeight, Padding1, Padding2; }
    [StructLayout(LayoutKind.Sequential)] private struct Viewport { public float X, Y, Width, Height, MinimumDepth, MaximumDepth; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendDesc { public int AlphaToCoverage, Independent; public fixed byte Targets[256]; }
    [StructLayout(LayoutKind.Sequential)] private struct TargetBlend { public int Enabled; public uint Source, Destination, Operation, SourceAlpha, DestinationAlpha, AlphaOperation; public byte WriteMask; }
    [StructLayout(LayoutKind.Sequential)] private struct SamplerDesc { public uint Filter, AddressU, AddressV, AddressW; public float LodBias; public uint MaximumAnisotropy, Comparison; public fixed float Border[4]; public float MinimumLod, MaximumLod; }
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags, nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi)] private static extern int D3DCompile(nint source, nuint length, string name, nint defines, nint include, string entry, string target, uint flags, uint effectFlags, out nint blob, out nint errors);
}
