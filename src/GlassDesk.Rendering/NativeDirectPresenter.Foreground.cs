using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

public sealed partial class NativeDirectPresenter
{
    private Dictionary<uint,float>? _foregroundVectorOpacity;
    internal void SetVisualOffset(float x,float y){EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterSetVisualOffset(_handle,x,y));}
    // The foreground owner exclusively owns this presenter's primitive namespaces.
    // Upload using persistent resource keys, avoiding the general per-batch vector slots.
    internal sealed record PreparedForegroundVector(NativeMethods.VectorSegment[] Commands,Size Size,Color Color,FillRule FillRule,VectorBrush? Brush,byte[] Identity);
    internal void UploadForegroundVector(uint key,ForegroundVector source)
    {
        EnsureUsable();var prepared=source.Prepared;
        UploadVector(key,prepared.Commands,prepared.Size,prepared.Color,prepared.FillRule,prepared.Brush);
        (_foregroundVectorOpacity??=[]).Add(key,prepared.Brush?.Opacity??1);
    }
    internal static PreparedForegroundVector PrepareForegroundVector(Geometry source,Geometry mapping,Size size,Brush paint)
    {
        ValidateVectorSize(size);var geometry=PathGeometry.CreateFromGeometry(source);var commands=VectorCommands(geometry);
        var color=paint is SolidColorBrush solid?solid.Color:Colors.White;
        var brush=paint is GradientBrush gradient?CreateVectorBrush(mapping,size,gradient):null;
        var header=new ForegroundVectorHeader{Width=(float)size.Width,Height=(float)size.Height,FillRule=(uint)geometry.FillRule,
            Color=(uint)color.A<<24|(uint)color.R<<16|(uint)color.G<<8|color.B,Count=(uint)commands.Length,BrushKind=brush?.Kind??0};
        var commandBytes=MemoryMarshal.AsBytes(commands.AsSpan());
        var identity=new byte[24+commandBytes.Length+(brush is null?0:24+brush.Stops.Length*24)];
        MemoryMarshal.Write(identity.AsSpan(),in header);commandBytes.CopyTo(identity.AsSpan(24));
        if(brush is not null)
        {
            var gradientHeader=new ForegroundGradientHeader{X1=brush.X1,Y1=brush.Y1,X2=brush.X2,Y2=brush.Y2,Opacity=brush.Opacity,Count=(uint)brush.Stops.Length};
            var offset=24+commandBytes.Length;MemoryMarshal.Write(identity.AsSpan(offset),in gradientHeader);
            MemoryMarshal.AsBytes(brush.Stops.AsSpan()).CopyTo(identity.AsSpan(offset+24));
        }
        return new(commands,size,color,geometry.FillRule,brush,identity);
    }
    [StructLayout(LayoutKind.Sequential)]private struct ForegroundVectorHeader
    {internal float Width,Height;internal uint FillRule,Color,Count,BrushKind;}
    [StructLayout(LayoutKind.Sequential)]private struct ForegroundGradientHeader
    {internal float X1,Y1,X2,Y2,Opacity;internal uint Count;}

    internal void RemoveForegroundVector(uint key)
    {EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterRemoveVector(_handle,key));_foregroundVectorOpacity?.Remove(key);}
    internal void DrawForegroundVector(uint key,Size host,Rect bounds,float opacity)
    {
        EnsureUsable();
        var item=new NativeMethods.ImageDraw{Key=key,X=(float)bounds.X,Y=(float)bounds.Y,Width=(float)bounds.Width,Height=(float)bounds.Height,Opacity=opacity*_foregroundVectorOpacity![key]};
        // One stack-backed item; no per-command managed pixel/data array.
        unsafe
        {
            var batch=new NativeMethods.ImageBatch{Size=32,Version=1,Count=1,HostWidth=(float)host.Width,HostHeight=(float)host.Height,Items=(nint)(&item)};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterDrawVectors(_handle,ref batch));
        }
    }
}
internal static partial class NativeMethods
{
    [DllImport(Dll,EntryPoint="GD_PresenterSetVisualOffset",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterSetVisualOffset(PresenterHandle presenter,float x,float y);
}
