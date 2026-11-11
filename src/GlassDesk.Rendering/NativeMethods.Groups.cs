using System.Runtime.InteropServices;

namespace GlassDesk.Rendering;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct GroupInfo
    {internal uint Size,Version;internal float Opacity,Left,Top,Right,Bottom;internal uint Bounded;}
    [StructLayout(LayoutKind.Sequential)] internal struct GroupStats
    {internal uint Size,Version,Depth,CachedSlots;internal ulong ColorBytes,MaskBytes,Allocations,Composites;}
    [DllImport(Dll,EntryPoint="GD_PresenterPushGroup",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterPushGroup(PresenterHandle presenter,in GroupInfo info);
    [DllImport(Dll,EntryPoint="GD_PresenterBeginGroupMask",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterBeginGroupMask(PresenterHandle presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterPopGroup",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterPopGroup(PresenterHandle presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterDiscardGroups",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterDiscardGroups(PresenterHandle presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterTrimGroupCache",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterTrimGroupCache(PresenterHandle presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterGetGroupStatistics",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int PresenterGetGroupStatistics(PresenterHandle presenter,ref GroupStats stats);
}
