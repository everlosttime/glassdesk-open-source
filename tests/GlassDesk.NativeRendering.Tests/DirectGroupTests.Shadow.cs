using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class DirectGroupTests
{
    [Fact]
    public void ShadowAbiStateAndScratchLifetimeAreBounded()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,100,100);
            var h=Handle(p);var info=new ShadowInfo{Size=48,Version=1,Radius=5,Opacity=.4f};
            Assert.Equal(unchecked((int)0x80070057),ApplyShadowNull(h,0));
            info.Size=40;Assert.Equal(unchecked((int)0x80070057),ApplyShadow(h,ref info));info.Size=48;
            info.Version=2;Assert.Equal(unchecked((int)0x80004002),ApplyShadow(h,ref info));info.Version=1;
            info.Radius=101;Assert.Equal(unchecked((int)0x80070057),ApplyShadow(h,ref info));info.Radius=5;
            info.OutputX=float.NaN;Assert.Equal(unchecked((int)0x80070057),ApplyShadow(h,ref info));info.OutputX=0;
            info.Red=-1;Assert.Equal(unchecked((int)0x80070057),ApplyShadow(h,ref info));info.Red=0;
            Assert.Equal(unchecked((int)0x8007139f),ApplyShadow(h,ref info));
            p.PushGroup();p.Clear(Colors.Red);
            var before=p.GroupStatistics;
            var wrong=Task.Run(()=>{var copy=info;return ApplyShadow(h,ref copy);}).GetAwaiter().GetResult();
            Assert.Equal(unchecked((int)0x8001010e),wrong);Assert.Equal(before,p.GroupStatistics);
            p.ApplyGroupShadow(new(5,2,2,Colors.Black,0));Assert.Equal(0ul,p.GroupEffectStatistics.TextureBytes);
            p.ApplyGroupShadow(new(5,2,2,Colors.Black,.4f));
            var first=p.GroupEffectStatistics;Assert.Equal(80000ul,first.TextureBytes);Assert.Equal(2ul,first.Allocations);Assert.Equal(3ul,first.Passes);
            p.ApplyGroupShadow(new(19,-2,3,Colors.Cyan,.65f));
            Assert.Equal(first.Allocations,p.GroupEffectStatistics.Allocations);Assert.Equal(first.Passes+7,p.GroupEffectStatistics.Passes);
            p.BeginGroupMask();Assert.Equal(unchecked((int)0x8007139f),ApplyShadow(h,ref info));
            Assert.Throws<COMException>(()=>p.TrimGroupCache());p.DiscardGroups();p.TrimGroupCache();
            Assert.Equal(0ul,p.GroupEffectStatistics.TextureBytes);Assert.Equal(0ul,p.GroupStatistics.ColorBytes+p.GroupStatistics.MaskBytes);
            p.PushGroup();p.Clear(Colors.Blue);p.ApplyGroupShadow(new(0,0,0,Colors.Black,.5f,.25f,.6f));p.PopGroup();
            Assert.Equal(first.Allocations+2,p.GroupEffectStatistics.Allocations);
            Assert.Equal(unchecked((int)0x80070005),DirectPresenterTests.ReadPixel(h,10,10,out _));
            p.Resize(101,102);Assert.Equal(0ul,p.GroupEffectStatistics.TextureBytes);
        }
        finally{host.Close();}
    });

    [Fact]
    public void ShadowScratchSharesGroupBudgetAndFailureKeepsLayersReclaimable()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,1280,1280);
            for(var i=0;i<9;i++){p.PushGroup();p.BeginGroupMask();}
            p.PushGroup();var before=p.GroupStatistics;
            Assert.Equal(124518400ul,before.ColorBytes+before.MaskBytes);
            Assert.Throws<OutOfMemoryException>(()=>p.ApplyGroupShadow(new(14,5,5,Colors.Black,.28f)));
            Assert.Equal(before,p.GroupStatistics);Assert.Equal(0ul,p.GroupEffectStatistics.TextureBytes);
            Assert.Equal(0ul,p.GroupEffectStatistics.Allocations);Assert.Equal(0ul,p.GroupEffectStatistics.Passes);
            p.DiscardGroups();p.TrimGroupCache();p.PushGroup();p.Clear(Colors.Red);
            p.ApplyGroupShadow(new(14,5,5,Colors.Black,.28f));p.PopGroup();
            Assert.Equal(13107200ul,p.GroupEffectStatistics.TextureBytes);
            p.TrimGroupCache();Assert.Equal(0ul,p.GroupEffectStatistics.TextureBytes);
        }
        finally{host.Close();}
    });

    [StructLayout(LayoutKind.Sequential)]private struct ShadowInfo
    {internal uint Size,Version,Radius,Reserved;internal float OffsetX,OffsetY,Red,Green,Blue,Opacity,OutputX,OutputY;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterApplyGroupShadow",CallingConvention=CallingConvention.Cdecl)]private static extern int ApplyShadow(nint p,ref ShadowInfo info);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterApplyGroupShadow",CallingConvention=CallingConvention.Cdecl)]private static extern int ApplyShadowNull(nint p,nint info);
}
