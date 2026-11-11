using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.Rendering;

internal static partial class NativeMethods
{
    private const string Dll = "GlassDesk.Rendering.Native.dll";
    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, (name, _, _) =>
        name == Dll ? NativeLibrary.Load(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_RENDER_DLL")
            ?? Path.Combine(AppContext.BaseDirectory, Dll)) : 0);

    [DllImport(Dll, EntryPoint = "GD_Create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Create(ref CreateInfo info, out EngineHandle engine);
    [DllImport(Dll, EntryPoint = "GD_Destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(nint engine);
    [DllImport(Dll, EntryPoint = "GD_Render", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Render(EngineHandle engine, ref Scene scene);
    [DllImport(Dll, EntryPoint = "GD_UploadBitmap", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int UploadBitmap(EngineHandle engine, ref Bitmap bitmap);
    [DllImport(Dll, EntryPoint = "GD_RenderMaterial", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RenderMaterial(EngineHandle engine, ref Material material);
    [DllImport(Dll, EntryPoint = "GD_Acquire", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Acquire(EngineHandle engine, ref Frame frame);
    [DllImport(Dll, EntryPoint = "GD_Release", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Release(nint engine, ulong lease);
    [DllImport(Dll, EntryPoint = "GD_Resize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Resize(EngineHandle engine, uint width, uint height);
    [DllImport(Dll, EntryPoint = "GD_Recreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Recreate(EngineHandle engine);
    [DllImport(Dll, EntryPoint = "GD_GetStats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetStats(EngineHandle engine, ref Stats stats);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateInfo
    {
        internal uint Size, Version, Width, Height;
        internal nint Window;
        internal uint Flags, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Scene
    {
        internal uint Size, Version;
        internal double Seconds;
        internal float Opacity, Radius;
        internal uint Pattern, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Bitmap
    {
        internal uint Size, Version, Width, Height, Stride, Reserved;
        internal nint Pixels;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Material
    {
        internal uint Size, Version;
        internal float DipWidth, DipHeight, Strength, Clarity, TintOpacity;
        internal uint Kind, Dark, Reserved1, Reserved2, Reserved3;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Frame
    {
        internal uint Size, Version;
        internal nint Surface;
        internal ulong Lease, Sequence, Generation;
        internal long Qpc;
        internal uint Width, Height;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        internal uint Size, Version;
        internal ulong Generation, Submitted, Acquired, Dropped, Readbacks, OutputBytes;
        internal uint Width, Height, Leased, Pending, AdapterLow;
        internal int AdapterHigh;
    }
}

internal sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public EngineHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeMethods.Destroy(handle); return true; }
}

// Holding a lease pins the engine's native lifetime, even after its owner closes.
// The COM surface is AddRef'd by GD_Acquire and released by GD_Release exactly once.
internal sealed class FrameLease : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly EngineHandle _engine;
    internal NativeMethods.Frame Frame { get; }
    internal FrameLease(EngineHandle engine, NativeMethods.Frame frame) : base(true)
    {
        var added = false;
        engine.DangerousAddRef(ref added);
        _engine = engine;
        Frame = frame;
        SetHandle(unchecked((nint)frame.Lease));
    }
    protected override bool ReleaseHandle()
    {
        try { return NativeMethods.Release(_engine.DangerousGetHandle(), unchecked((ulong)handle)) >= 0; }
        finally { _engine.DangerousRelease(); }
    }
}
