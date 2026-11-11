using System.Runtime.InteropServices;
using System.Windows.Media;

namespace GlassDesk.Rendering;

/// <summary>WPF performance-bias kernel in target pixels. OutputX/Y carry the fractional
/// translation removed from effect input rendering. Tint uses scRGB; its alpha is
/// ignored like WPF. Opacity/mask ordering belongs to the scene compiler.</summary>
public sealed record DirectGroupShadow(uint Radius,float OffsetX,float OffsetY,Color Color,float Opacity,float OutputX=0,float OutputY=0);
public sealed record DirectGroupEffectStatistics(ulong TextureBytes,ulong Allocations,ulong Passes);
public sealed partial class NativeDirectPresenter
{
    public void ApplyGroupShadow(DirectGroupShadow shadow)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(shadow);
        if(shadow.Radius>100 || !float.IsFinite(shadow.OffsetX) || !float.IsFinite(shadow.OffsetY) || Math.Abs(shadow.OffsetX)>256 || Math.Abs(shadow.OffsetY)>256
            || !float.IsFinite(shadow.Opacity) || shadow.Opacity<0 || shadow.Opacity>1)throw new ArgumentOutOfRangeException(nameof(shadow));
        if(!float.IsFinite(shadow.OutputX) || !float.IsFinite(shadow.OutputY) || Math.Abs(shadow.OutputX)>1 || Math.Abs(shadow.OutputY)>1)throw new ArgumentOutOfRangeException(nameof(shadow));
        if(!ShadowChannel(shadow.Color.ScR) || !ShadowChannel(shadow.Color.ScG) || !ShadowChannel(shadow.Color.ScB))throw new ArgumentOutOfRangeException(nameof(shadow));
        var info=new NativeMethods.GroupShadow{Size=48,Version=1,Radius=shadow.Radius,OffsetX=shadow.OffsetX,OffsetY=shadow.OffsetY,
            Red=shadow.Color.ScR,Green=shadow.Color.ScG,Blue=shadow.Color.ScB,Opacity=shadow.Opacity,OutputX=shadow.OutputX,OutputY=shadow.OutputY};
        Marshal.ThrowExceptionForHR(NativeMethods.PresenterApplyGroupShadow(_handle,in info));
    }
    private static bool ShadowChannel(float value)=>float.IsFinite(value) && value>=0 && value<=1;
    public DirectGroupEffectStatistics GroupEffectStatistics
    {
        get{EnsureUsable();var s=new NativeMethods.GroupEffectStats{Size=32,Version=1};Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetGroupEffectStatistics(_handle,ref s));return new(s.TextureBytes,s.Allocations,s.Passes);}
    }
}
internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct GroupShadow{internal uint Size,Version,Radius,Reserved;internal float OffsetX,OffsetY,Red,Green,Blue,Opacity,OutputX,OutputY;}
    [StructLayout(LayoutKind.Sequential)] internal struct GroupEffectStats{internal uint Size,Version;internal ulong TextureBytes,Allocations,Passes;}
    [DllImport(Dll,EntryPoint="GD_PresenterApplyGroupShadow",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterApplyGroupShadow(PresenterHandle p,in GroupShadow info);
    [DllImport(Dll,EntryPoint="GD_PresenterGetGroupEffectStatistics",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterGetGroupEffectStatistics(PresenterHandle p,ref GroupEffectStats info);
    [DllImport(Dll,EntryPoint="GD_PresenterPushEffectGroup",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterPushEffectGroup(PresenterHandle p,in GroupInfo info);
}
