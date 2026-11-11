using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Rendering;

/// <summary>A retained, dispatcher-owned foreground. Dispose releases its native resources; no source Visual is retained.</summary>
public sealed class NativeForegroundScene:IDisposable
{
    internal NativeForegroundScene(NativeForegroundPresenter owner,Size host,Size nativeHost,uint width,uint height,ForegroundPlan plan,ForegroundResource[] resources)
    {Owner=owner;HostSize=host;NativeHostSize=nativeHost;Width=width;Height=height;Plan=plan;Resources=resources;}
    internal readonly Size NativeHostSize;
    internal readonly NativeForegroundPresenter Owner;
    internal readonly ForegroundPlan Plan;
    internal readonly ForegroundResource[] Resources;
    internal readonly uint Width,Height;
    public Size HostSize {get;}
    public int CommandCount=>Plan.Commands.Count;
    internal bool Disposed;
    public void Dispose()=>Owner.Release(this);
}

internal sealed class ForegroundPlan
{
    internal readonly List<ForegroundCommand> Commands=[];
    internal readonly List<ForegroundAsset> Assets=[];
}
internal abstract record ForegroundAsset(uint Key);
internal sealed record ForegroundVector(uint Key,NativeDirectPresenter.PreparedForegroundVector Prepared,ForegroundVectorCompileKey? CompileKey):ForegroundAsset(Key);
internal sealed record ForegroundImage(uint Key,BitmapSource Bitmap):ForegroundAsset(Key);
internal sealed record ForegroundGlyph(uint Key,GlyphRun Run,TextFormattingMode Mode):ForegroundAsset(Key);
internal enum ForegroundResourceKind{Vector,Image,Glyph}
internal readonly record struct ForegroundResource(ForegroundResourceKind Kind,uint Key);
internal abstract record ForegroundCommand;
internal sealed record ForegroundPush(float Opacity,Rect? DeviceBounds):ForegroundCommand;
internal sealed record ForegroundEffectPush(Rect DeviceBounds):ForegroundCommand;
internal sealed record ForegroundShadow(DirectGroupShadow Shadow):ForegroundCommand;
internal sealed record ForegroundMask:ForegroundCommand;
internal sealed record ForegroundPop:ForegroundCommand;
internal sealed record ForegroundDrawVector(uint Key,float Opacity,Rect Bounds):ForegroundCommand;
internal sealed record ForegroundDrawImage(DirectImageDraw Draw):ForegroundCommand;
internal sealed record ForegroundDrawGlyph(DirectGlyphDraw Draw):ForegroundCommand;
