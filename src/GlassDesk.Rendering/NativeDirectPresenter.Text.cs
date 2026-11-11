using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

public readonly record struct DirectGlyphDraw(uint Key,Point Baseline,Color Color,float Opacity=1,float ScaleX=1,float ScaleY=1);
// Source/mask counters exclude font-file mapping, DirectWrite, pipeline, driver
// and all other product memory. They do not prove process-group residency.
public sealed record DirectGlyphStatistics(uint RunCount,ulong SourceBytes,ulong MaskBytes,ulong Rasterizations,ulong Draws);

public sealed partial class NativeDirectPresenter
{
    /// <summary>
    /// Snapshots the existing WPF layout's real font/glyph data without shaping
    /// again. Supports local font files (including collection face fragments).
    /// Baseline supplied to DrawGlyphRuns is final host-DIP position, with the
    /// source run's BaselineOrigin already included by the layout owner.
    /// Raster uses the monitor's Auto text settings. The layout owner must keep
    /// the existing renderer for explicit rendering/hinting overrides or drawing
    /// group transforms/clips that have not been adapted into the draw list.
    /// </summary>
    public void SetGlyphRun(uint key,GlyphRun run,TextFormattingMode formattingMode=TextFormattingMode.Ideal)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(run);if(key==0)throw new ArgumentOutOfRangeException(nameof(key));
        var typeface=run.GlyphTypeface??throw new ArgumentException("An initialized font is required.",nameof(run));
        var uri=typeface.FontUri;
        if(!uri.IsAbsoluteUri || !uri.IsFile)throw new NotSupportedException("This font source requires the existing renderer.");
        uint face=0;if(!string.IsNullOrEmpty(uri.Fragment) && !uint.TryParse(uri.Fragment.AsSpan(1),NumberStyles.None,CultureInfo.InvariantCulture,out face))
            throw new NotSupportedException("This font collection URI requires the existing renderer.");
        var count=run.GlyphIndices?.Count??0;
        if(count<1 || count>4096 || run.AdvanceWidths?.Count!=count || (run.GlyphOffsets?.Count>0 && run.GlyphOffsets.Count!=count))
            throw new ArgumentException("A bounded complete glyph run is required.",nameof(run));
        var indices=run.GlyphIndices!.ToArray();var advances=run.AdvanceWidths!.Select(v=>(float)v).ToArray();var offsets=new NativeMethods.GlyphOffset[count];
        for(var i=0;run.GlyphOffsets?.Count>0 && i<count;i++)offsets[i]=new(){X=(float)run.GlyphOffsets[i].X,Y=(float)run.GlyphOffsets[i].Y};
        var path=Marshal.StringToHGlobalUni(uri.LocalPath);var indexPin=GCHandle.Alloc(indices,GCHandleType.Pinned);
        var advancePin=GCHandle.Alloc(advances,GCHandleType.Pinned);var offsetPin=GCHandle.Alloc(offsets,GCHandleType.Pinned);
        try
        {
            var info=new NativeMethods.GlyphRunInfo{Size=72,Version=1,Count=(uint)count,FaceIndex=face,Simulations=(uint)typeface.StyleSimulations,
                BidiLevel=checked((uint)run.BidiLevel),Sideways=run.IsSideways?1u:0u,EmSize=(float)run.FontRenderingEmSize,
                MeasuringMode=formattingMode switch{TextFormattingMode.Ideal=>0,TextFormattingMode.Display=>1,_=>throw new ArgumentOutOfRangeException(nameof(formattingMode))},
                FontPath=path,Indices=indexPin.AddrOfPinnedObject(),Advances=advancePin.AddrOfPinnedObject(),Offsets=offsetPin.AddrOfPinnedObject()};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterSetGlyphRun(_handle,key,ref info));
        }
        finally{Marshal.FreeHGlobal(path);indexPin.Free();advancePin.Free();offsetPin.Free();}
    }

    public DirectGlyphStatistics GlyphStatistics
    {
        get
        {
            EnsureUsable();var stats=new NativeMethods.GlyphStats{Size=48,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetGlyphStatistics(_handle,ref stats));
            return new(stats.RunCount,stats.SourceBytes,stats.MaskBytes,stats.Rasterizations,stats.Draws);
        }
    }
    public bool RemoveGlyphRun(uint key)
    {
        EnsureUsable();var result=NativeMethods.PresenterRemoveGlyphRun(_handle,key);Marshal.ThrowExceptionForHR(result);return result==0;
    }
    /// <summary>Call when monitor text-rendering settings change; fonts/layout remain available.</summary>
    public void InvalidateGlyphRasters(){EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterInvalidateGlyphRasters(_handle));}

    public void DrawGlyphRuns(Size hostSize,ReadOnlySpan<DirectGlyphDraw> items)
    {
        EnsureUsable();ValidateVectorSize(hostSize);if(items.Length>256)throw new ArgumentOutOfRangeException(nameof(items));
        var scratch=ArrayPool<NativeMethods.GlyphDraw>.Shared.Rent(Math.Max(1,items.Length));
        try
        {
            for(var i=0;i<items.Length;i++)
            {
                var d=items[i];scratch[i]=new(){Key=d.Key,X=(float)d.Baseline.X,Y=(float)d.Baseline.Y,ScaleX=d.ScaleX,ScaleY=d.ScaleY,Opacity=d.Opacity,
                    R=d.Color.R/255f,G=d.Color.G/255f,B=d.Color.B/255f,A=d.Color.A/255f};
            }
            var pin=GCHandle.Alloc(scratch,GCHandleType.Pinned);
            try
            {
                var batch=new NativeMethods.GlyphBatch{Size=32,Version=1,Count=(uint)items.Length,HostWidth=(float)hostSize.Width,HostHeight=(float)hostSize.Height,Items=pin.AddrOfPinnedObject()};
                Marshal.ThrowExceptionForHR(NativeMethods.PresenterDrawGlyphRuns(_handle,ref batch));
            }
            finally{pin.Free();}
        }
        finally{ArrayPool<NativeMethods.GlyphDraw>.Shared.Return(scratch);}
    }
}
internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphOffset{internal float X,Y;}
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphRunInfo
    {internal uint Size,Version,Count,FaceIndex,Simulations,BidiLevel,Sideways,Reserved;internal float EmSize;internal uint MeasuringMode;internal nint FontPath,Indices,Advances,Offsets;}
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphDraw{internal uint Key,Reserved;internal float X,Y,ScaleX,ScaleY,Opacity,R,G,B,A;internal uint Reserved2;}
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphBatch{internal uint Size,Version,Count,Reserved;internal float HostWidth,HostHeight;internal nint Items;}
    [StructLayout(LayoutKind.Sequential)]internal struct GlyphStats{internal uint Size,Version,RunCount,Reserved;internal ulong SourceBytes,MaskBytes,Rasterizations,Draws;}
    [DllImport(Dll,EntryPoint="GD_PresenterSetGlyphRun",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterSetGlyphRun(PresenterHandle p,uint key,ref GlyphRunInfo info);
    [DllImport(Dll,EntryPoint="GD_PresenterDrawGlyphRuns",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterDrawGlyphRuns(PresenterHandle p,ref GlyphBatch batch);
    [DllImport(Dll,EntryPoint="GD_PresenterGetGlyphStatistics",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterGetGlyphStatistics(PresenterHandle p,ref GlyphStats stats);
    [DllImport(Dll,EntryPoint="GD_PresenterRemoveGlyphRun",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterRemoveGlyphRun(PresenterHandle p,uint key);
    [DllImport(Dll,EntryPoint="GD_PresenterInvalidateGlyphRasters",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterInvalidateGlyphRasters(PresenterHandle p);
}
