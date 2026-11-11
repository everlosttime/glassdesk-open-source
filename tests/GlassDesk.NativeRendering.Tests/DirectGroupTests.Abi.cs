using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class DirectGroupTests
{
    [Fact]
    public void InvalidGroupAbiAndWrongThreadLeaveActiveStateIntact()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,100,100);
            var handle=Handle(p);var info=new GroupInfo{Size=32,Version=1,Opacity=.7f};
            Assert.Equal(unchecked((int)0x80070057),PushGroupNull(handle,0));
            info.Size=0;Assert.Equal(unchecked((int)0x80070057),PushGroup(handle,ref info));info.Size=32;
            info.Version=2;Assert.Equal(unchecked((int)0x80004002),PushGroup(handle,ref info));info.Version=1;
            info.Opacity=float.NaN;Assert.Equal(unchecked((int)0x80070057),PushGroup(handle,ref info));info.Opacity=.7f;
            info.Bounded=2;Assert.Equal(unchecked((int)0x80070057),PushGroup(handle,ref info));info.Bounded=1;
            info.Left=10;info.Right=9;Assert.Equal(unchecked((int)0x80070057),PushGroup(handle,ref info));
            info.Left=float.NegativeInfinity;Assert.Equal(unchecked((int)0x80070057),PushGroup(handle,ref info));
            Assert.Equal(0u,p.GroupStatistics.CachedSlots);
            p.PushGroup(.7f,new Rect(2.2,4.3,60,70));p.BeginGroupMask();var active=p.GroupStatistics;
            var wrong=Task.Run(()=>{var copy=new GroupInfo{Size=32,Version=1,Opacity=1};return PushGroup(handle,ref copy);}).GetAwaiter().GetResult();
            Assert.Equal(unchecked((int)0x8001010e),wrong);Assert.Equal(active,p.GroupStatistics);
            var stats=new GroupStats{Size=47,Version=1};Assert.Equal(unchecked((int)0x80070057),GroupStatistics(handle,ref stats));
            stats.Size=48;stats.Version=2;Assert.Equal(unchecked((int)0x80004002),GroupStatistics(handle,ref stats));
            Assert.Equal(active,p.GroupStatistics);p.DiscardGroups();p.TrimGroupCache();
        }
        finally{host.Close();}
    });

    [Fact]
    public void GroupTextureBudgetFailurePreservesExistingLayersAndCanBeReclaimed()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,1280,1280);
            for(var i=0;i<10;i++){p.PushGroup();p.BeginGroupMask();}
            var before=p.GroupStatistics;Assert.Equal(10u,before.Depth);
            Assert.Equal(131072000ul,before.ColorBytes+before.MaskBytes);
            Assert.Throws<OutOfMemoryException>(()=>p.PushGroup());Assert.Equal(before,p.GroupStatistics);
            p.DiscardGroups();p.TrimGroupCache();Assert.Equal(0ul,p.GroupStatistics.ColorBytes+p.GroupStatistics.MaskBytes);
            p.PushGroup();p.PopGroup();Assert.Equal(0u,p.GroupStatistics.Depth);
        }
        finally{host.Close();}
    });

    [StructLayout(LayoutKind.Sequential)] internal struct GroupInfo
    {internal uint Size,Version;internal float Opacity,Left,Top,Right,Bottom;internal uint Bounded;}
    [StructLayout(LayoutKind.Sequential)] internal struct GroupStats
    {internal uint Size,Version,Depth,Slots;internal ulong ColorBytes,MaskBytes,Allocations,Composites;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterPushGroup",CallingConvention=CallingConvention.Cdecl)] internal static extern int PushGroup(nint p,ref GroupInfo info);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterPushGroup",CallingConvention=CallingConvention.Cdecl)] private static extern int PushGroupNull(nint p,nint info);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterBeginGroupMask",CallingConvention=CallingConvention.Cdecl)] internal static extern int BeginMask(nint p);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterPopGroup",CallingConvention=CallingConvention.Cdecl)] internal static extern int PopGroup(nint p);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetGroupStatistics",CallingConvention=CallingConvention.Cdecl)] private static extern int GroupStatistics(nint p,ref GroupStats stats);
}
