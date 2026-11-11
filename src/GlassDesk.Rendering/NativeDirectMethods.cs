using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.Rendering;

internal static partial class NativeMethods
{
    [DllImport(Dll,EntryPoint="GD_PresenterCreate",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterCreate(ref CreateInfo info,out PresenterHandle handle);
    [DllImport(Dll,EntryPoint="GD_PresenterDestroy",CallingConvention=CallingConvention.Cdecl)] internal static extern void PresenterDestroy(nint handle);
    [DllImport(Dll,EntryPoint="GD_PresenterResize",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterResize(PresenterHandle handle,uint width,uint height);
    [DllImport(Dll,EntryPoint="GD_PresenterClear",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterClear(PresenterHandle handle,float r,float g,float b,float a);
    [DllImport(Dll,EntryPoint="GD_PresenterPresent",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterPresent(PresenterHandle handle);
    [DllImport(Dll,EntryPoint="GD_PresenterGetWaitHandle",CallingConvention=CallingConvention.Cdecl)] internal static extern nint PresenterGetWaitHandle(PresenterHandle handle);
    [DllImport(Dll,EntryPoint="GD_PresenterGetStatistics",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterGetStatistics(PresenterHandle handle,ref PresenterStats stats);
    [DllImport(Dll,EntryPoint="GD_PresenterSetImage",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterSetImage(PresenterHandle handle,uint key,ref Bitmap bitmap);
    [DllImport(Dll,EntryPoint="GD_PresenterRemoveImage",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterRemoveImage(PresenterHandle handle,uint key);
    [DllImport(Dll,EntryPoint="GD_PresenterDrawImages",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterDrawImages(PresenterHandle handle,ref ImageBatch batch);
    [DllImport(Dll,EntryPoint="GD_PresenterDrawMappedImages",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterDrawMappedImages(PresenterHandle handle,ref ImageBatch batch);
    [DllImport(Dll,EntryPoint="GD_PresenterGetImageStatistics",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterGetImageStatistics(PresenterHandle handle,ref ImageStats stats);
    [DllImport(Dll,EntryPoint="GD_PresenterUploadBitmap",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterUploadBitmap(PresenterHandle handle,ref Bitmap bitmap);
    [DllImport(Dll,EntryPoint="GD_PresenterRenderGlass",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterRenderGlass(PresenterHandle handle,ref Material material,ref GlassScene scene);

    [StructLayout(LayoutKind.Sequential)] internal struct ImageDraw
    { internal uint Key,Reserved;internal float X,Y,Width,Height,Opacity;internal uint Reserved2; }
    [StructLayout(LayoutKind.Sequential)] internal struct ImageBatch
    { internal uint Size,Version,Count,Reserved;internal float HostWidth,HostHeight;internal nint Items; }
    [StructLayout(LayoutKind.Sequential)] internal struct MappedImageDraw
    { internal ImageDraw Geometry;internal float MapX,MapY,MapWidth,MapHeight; }
    [StructLayout(LayoutKind.Sequential)] internal struct ImageStats
    { internal uint Size,Version,ImageCount,Reserved;internal ulong SourceBytes,CacheBytes,PrefilterRuns; }
    [StructLayout(LayoutKind.Sequential)] internal struct PresenterStats
    {
        internal uint Size,Version;internal ulong Submitted,WouldBlock;
        internal uint Width,Height,BufferCount,D3D9ResourceCount;internal ulong BufferBytes;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct GlassScene
    {
        internal uint Size,Version;
        internal float HostWidth,HostHeight,PanelX,PanelY,PanelWidth,PanelHeight,BackdropX,BackdropY,BackdropWidth,BackdropHeight;
        internal float Radius,Strength,Dispersion,InnerGain,OuterGain;
        internal uint Launchpad,Outline,Reserved;
    }
}

internal sealed class PresenterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PresenterHandle():base(true){}
    protected override bool ReleaseHandle(){NativeMethods.PresenterDestroy(handle);return true;}
}
