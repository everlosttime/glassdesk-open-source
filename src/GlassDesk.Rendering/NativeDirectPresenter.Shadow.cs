using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

public readonly record struct DirectShadowLayer(float Extent,float Offset,float Opacity,Color Color);
public sealed record DirectShadowScene(Size HostSize,Rect PanelBounds,float Radius,float Opacity=1,
    bool CompactOutline=true,bool ReducedDetail=false);
// TargetBytes is only the BGRA8 accumulation texture; driver/D2D caches and
// swapchain buffers are outside this counter and remain process-level evidence.
public sealed record DirectShadowStatistics(uint ContourCount,ulong GeometryBuilds,ulong Draws,ulong TargetAllocations,ulong TargetBytes);

public sealed partial class NativeDirectPresenter
{
    /// <summary>Dedicated shadow output; the caller retains the independent,
    /// input-transparent shadow HWND and its existing attachment/Z-order policy.</summary>
    public void RenderShadow(DirectShadowScene scene,ReadOnlySpan<DirectShadowLayer> layers)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(scene);
        if(layers.Length>4)throw new ArgumentOutOfRangeException(nameof(layers));
        var items=ArrayPool<NativeMethods.ShadowLayer>.Shared.Rent(4);
        try
        {
            for(var i=0;i<layers.Length;i++)
            {
                var l=layers[i];items[i]=new(){Extent=l.Extent,Offset=l.Offset,Opacity=l.Opacity,
                    R=l.Color.R/255f,G=l.Color.G/255f,B=l.Color.B/255f,A=l.Color.A/255f};
            }
            var pin=GCHandle.Alloc(items,GCHandleType.Pinned);
            try
            {
                var s=new NativeMethods.ShadowScene{Size=64,Version=1,HostWidth=(float)scene.HostSize.Width,HostHeight=(float)scene.HostSize.Height,
                    PanelX=(float)scene.PanelBounds.X,PanelY=(float)scene.PanelBounds.Y,PanelWidth=(float)scene.PanelBounds.Width,PanelHeight=(float)scene.PanelBounds.Height,
                    Radius=scene.Radius,Opacity=scene.Opacity,Outline=scene.CompactOutline?1u:0u,Steps=scene.ReducedDetail?16u:32u,
                    Count=(uint)layers.Length,Layers=pin.AddrOfPinnedObject()};
                Marshal.ThrowExceptionForHR(NativeMethods.PresenterRenderShadow(_handle,ref s));
            }
            finally{pin.Free();}
        }
        finally{ArrayPool<NativeMethods.ShadowLayer>.Shared.Return(items);}
    }
    public DirectShadowStatistics ShadowStatistics
    {
        get
        {
            EnsureUsable();var s=new NativeMethods.ShadowStats{Size=48,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetShadowStatistics(_handle,ref s));
            return new(s.ContourCount,s.GeometryBuilds,s.Draws,s.TargetAllocations,s.TargetBytes);
        }
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct ShadowLayer{internal float Extent,Offset,Opacity,R,G,B,A;internal uint Reserved;}
    [StructLayout(LayoutKind.Sequential)] internal struct ShadowScene
    {
        internal uint Size,Version;internal float HostWidth,HostHeight,PanelX,PanelY,PanelWidth,PanelHeight,Radius,Opacity;
        internal uint Outline,Steps,Count,Reserved;internal nint Layers;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ShadowStats{internal uint Size,Version,ContourCount,Reserved;internal ulong GeometryBuilds,Draws,TargetAllocations,TargetBytes;}
    [DllImport(Dll,EntryPoint="GD_PresenterRenderShadow",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterRenderShadow(PresenterHandle p,ref ShadowScene scene);
    [DllImport(Dll,EntryPoint="GD_PresenterGetShadowStatistics",CallingConvention=CallingConvention.Cdecl)] internal static extern int PresenterGetShadowStatistics(PresenterHandle p,ref ShadowStats stats);
}
