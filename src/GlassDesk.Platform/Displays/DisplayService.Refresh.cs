using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Displays;

/// <summary>Configured scan-out rate, not observed application/displayed FPS.</summary>
public sealed record DisplayRefreshInfo(string DeviceName,uint Numerator,uint Denominator)
{
    public double Hertz=>Denominator==0?double.NaN:Numerator/(double)Denominator;
}

public static partial class DisplayService
{
    /// <summary>Read the active rational refresh of the monitor containing this
    /// HWND. Unknown/ambiguous topology returns null; never substitute primary.</summary>
    public static DisplayRefreshInfo? RefreshForWindow(nint window)
    {
        if(window==0 || !Display32.GetWindowRect(window,out _))return null;
        var monitor=Display32.MonitorFromWindow(window,Display32.MonitorDefaultToNearest);
        var info=new MonitorInfoEx{Size=Marshal.SizeOf<MonitorInfoEx>(),DeviceName=string.Empty};
        if(monitor==0 || !Display32.GetMonitorInfo(monitor,ref info))return null;
        try
        {
            for(var retry=0;retry<3;retry++)
            {
                if(RefreshNative.GetDisplayConfigBufferSizes(2,out var pathCount,out var modeCount)!=0)return null;
                if(pathCount>128 || modeCount>256)return null;
                var paths=new RefreshNative.Path[pathCount];var modes=new RefreshNative.Mode[modeCount];
                var result=RefreshNative.QueryDisplayConfig(2,ref pathCount,paths,ref modeCount,modes,0);
                if(result==122)continue;
                if(result!=0)return null;
                DisplayRefreshInfo? found=null;
                for(var i=0;i<pathCount;i++)
                {
                    var path=paths[i];
                    if((path.Flags&1)==0 || path.Target.Available==0)continue;
                    var name=new RefreshNative.SourceName{Type=1,Size=84,Adapter=path.Source.Adapter,Id=path.Source.Id,DeviceName=string.Empty};
                    if(RefreshNative.DisplayConfigGetDeviceInfo(ref name)!=0)return null;
                    if(!string.Equals(name.DeviceName,info.DeviceName,StringComparison.OrdinalIgnoreCase))continue;
                    var rate=path.Target.Rate;if(rate.Numerator==0 || rate.Denominator==0)return null;
                    // Clone paths can share a GDI name while scanning out at
                    // different rates. Such a HWND has no single known cadence.
                    if(found is not null && (ulong)found.Numerator*rate.Denominator!=(ulong)rate.Numerator*found.Denominator)return null;
                    found=new(info.DeviceName,rate.Numerator,rate.Denominator);
                }
                return found;
            }
        }
        catch(Exception error)when(error is DllNotFoundException or EntryPointNotFoundException){return null;}
        return null;
    }

    private static class RefreshNative
    {
        [StructLayout(LayoutKind.Sequential)]internal struct Luid{internal uint Low;internal int High;}
        [StructLayout(LayoutKind.Sequential)]internal struct Rational{internal uint Numerator,Denominator;}
        [StructLayout(LayoutKind.Sequential)]internal struct Source{internal Luid Adapter;internal uint Id,Mode,Flags;}
        [StructLayout(LayoutKind.Sequential)]internal struct Target
        {internal Luid Adapter;internal uint Id,Mode,Technology,Rotation,Scaling;internal Rational Rate;internal uint Scanline;internal int Available;internal uint Flags;}
        [StructLayout(LayoutKind.Sequential)]internal struct Path{internal Source Source;internal Target Target;internal uint Flags;}
        [StructLayout(LayoutKind.Explicit,Size=64)]internal struct Mode{[FieldOffset(0)]internal uint Type;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct SourceName
        {internal uint Type,Size;internal Luid Adapter;internal uint Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]internal string DeviceName;}
        [DllImport("user32.dll")]internal static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")]internal static extern int QueryDisplayConfig(uint flags,ref uint paths,[Out]Path[] path,ref uint modes,[Out]Mode[] mode,nint topology);
        [DllImport("user32.dll")]internal static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    }
}
