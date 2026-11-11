using System.Runtime.InteropServices;

namespace GlassDesk.Rendering;

/// <summary>Process-local direct device ownership; excludes bridge/driver/GPU memory.</summary>
public sealed record DirectDeviceStatistics(ulong Identity,uint AdapterLuidLow,int AdapterLuidHigh,
    uint OwnerThread,uint Presenters,uint LiveDevices);

public sealed partial class NativeDirectPresenter
{
    private NativeDirectPresenter(PresenterHandle handle) { _handle=handle; }

    /// <summary>
    /// Shares this presenter's device on the same adapter and Dispatcher.
    /// The caller owns the child's lifetime; disposing the parent is safe.
    /// A different adapter throws; the caller retains independent/legacy fallback.
    /// </summary>
    public NativeDirectPresenter CreateSibling(nint window,int width,int height)
    {
        EnsureUsable();
        var info=new NativeMethods.CreateInfo{Size=32,Version=1,Window=window,Width=checked((uint)width),Height=checked((uint)height)};
        var result=NativeMethods.PresenterCreateSibling(_handle,ref info,out var handle);
        if(result<0){handle.Dispose();Marshal.ThrowExceptionForHR(result);}
        return new NativeDirectPresenter(handle);
    }

    public DirectDeviceStatistics DeviceStatistics
    {
        get
        {
            EnsureUsable();var s=new NativeMethods.DeviceStats{Size=40,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetDeviceStatistics(_handle,ref s));
            return new(s.Identity,s.AdapterLow,s.AdapterHigh,s.OwnerThread,s.Presenters,s.LiveDevices);
        }
    }
}

internal static partial class NativeMethods
{
    [DllImport(Dll,EntryPoint="GD_PresenterCreateSibling",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterCreateSibling(PresenterHandle parent,ref CreateInfo info,out PresenterHandle child);
    [DllImport(Dll,EntryPoint="GD_PresenterGetDeviceStatistics",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterGetDeviceStatistics(PresenterHandle presenter,ref DeviceStats stats);
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceStats
    {
        internal uint Size,Version;internal ulong Identity;
        internal uint AdapterLow;internal int AdapterHigh;
        internal uint OwnerThread,Presenters,LiveDevices,Reserved;
    }
}
