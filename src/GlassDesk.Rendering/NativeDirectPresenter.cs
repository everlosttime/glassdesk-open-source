using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassDesk.Rendering;

public sealed record DirectPresentationStatistics(ulong Submitted,ulong WouldBlock,uint Width,uint Height,
    uint BufferCount,uint D3D9ResourceCount,ulong BufferBytes);
public sealed record DirectImageStatistics(uint ImageCount,ulong SourceBytes,ulong CacheBytes,ulong PrefilterRuns);
public readonly record struct DirectImageDraw(uint Key,Rect Bounds,float Opacity=1,Rect? TextureBounds=null);
public sealed record DirectGlassScene(Size HostSize,Rect PanelBounds,Rect BackdropBounds,float Radius,float Strength,
    float Dispersion=1,float InnerGain=1,float OuterGain=1,bool Launchpad=false,bool CompactOutline=true);

/// <summary>
/// Opt-in direct composition on an existing owned HWND. Its caller retains all
/// input, layout and fallback decisions. GPU command failures must discard the
/// candidate frame rather than presenting it. No CPU readback API is exposed.
/// </summary>
public sealed partial class NativeDirectPresenter : DispatcherObject,IDisposable
{
    private readonly PresenterHandle _handle;
    private bool _disposed;

    public NativeDirectPresenter(nint window,int width,int height)
    {
        VerifyAccess();
        if(!Environment.Is64BitProcess || !OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))
            throw new PlatformNotSupportedException("Direct rendering requires Windows 10 2004+ x64.");
        var info=new NativeMethods.CreateInfo{Size=32,Version=1,Window=window,Width=checked((uint)width),Height=checked((uint)height)};
        var result=NativeMethods.PresenterCreate(ref info,out _handle);
        if(result<0){_handle.Dispose();Marshal.ThrowExceptionForHR(result);}
    }

    private void EnsureUsable(){VerifyAccess();ObjectDisposedException.ThrowIf(_disposed,this);}

    /// <summary>Borrowed until Dispose. Never CloseHandle this value.</summary>
    public nint FrameLatencyHandle { get {EnsureUsable();return NativeMethods.PresenterGetWaitHandle(_handle);} }
    public DirectPresentationStatistics Statistics
    {
        get
        {
            EnsureUsable();var s=new NativeMethods.PresenterStats{Size=48,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetStatistics(_handle,ref s));
            return new(s.Submitted,s.WouldBlock,s.Width,s.Height,s.BufferCount,s.D3D9ResourceCount,s.BufferBytes);
        }
    }
    public DirectImageStatistics ImageStatistics
    {
        get
        {
            EnsureUsable();var s=new NativeMethods.ImageStats{Size=40,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetImageStatistics(_handle,ref s));
            return new(s.ImageCount,s.SourceBytes,s.CacheBytes,s.PrefilterRuns);
        }
    }
    public void Clear(Color color)
    {
        EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterClear(_handle,color.R/255f,color.G/255f,color.B/255f,color.A/255f));
    }
    public void Resize(int width,int height)
    {
        EnsureUsable();Marshal.ThrowExceptionForHR(NativeMethods.PresenterResize(_handle,checked((uint)width),checked((uint)height)));
    }
    /// <summary>True means submission succeeded, not that a frame was displayed.</summary>
    public bool Present()
    {
        EnsureUsable();var result=NativeMethods.PresenterPresent(_handle);Marshal.ThrowExceptionForHR(result);return result==0;
    }
    public bool RemoveImage(uint key)
    {
        EnsureUsable();var result=NativeMethods.PresenterRemoveImage(_handle,key);Marshal.ThrowExceptionForHR(result);return result==0;
    }
    public void SetImage(uint key,BitmapSource bitmap)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(bitmap);
        if(key==0)throw new ArgumentOutOfRangeException(nameof(key));
        var width=bitmap.PixelWidth;var height=bitmap.PixelHeight;
        if(width<1 || height<1 || width>8192 || height>8192 || (long)width*height*4>32*1024*1024)
            throw new ArgumentException("Image exceeds the bounded native image resource budget.",nameof(bitmap));
        var source=bitmap.Format==PixelFormats.Pbgra32?bitmap:new FormatConvertedBitmap(bitmap,PixelFormats.Pbgra32,null,0);
        // A one-shot native staging buffer avoids leaving each large icon on
        // the managed LOH until a later collection. D3D copies it synchronously.
        var length=checked(width*height*4);var pixels=Marshal.AllocHGlobal(length);
        try
        {
            source.CopyPixels(Int32Rect.Empty,pixels,length,width*4);
            var b=new NativeMethods.Bitmap{Size=32,Version=1,Width=(uint)width,Height=(uint)height,Stride=(uint)width*4,Pixels=pixels};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterSetImage(_handle,key,ref b));
        }
        finally{Marshal.FreeHGlobal(pixels);}
    }
    public void UploadWallpaper(byte[] bgra,int width,int height)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(bgra);
        if(width<1 || height<1 || bgra.LongLength!=(long)width*height*4)throw new ArgumentException("A complete BGRA frame is required.",nameof(bgra));
        Upload(bgra,width,height,b=>NativeMethods.PresenterUploadBitmap(_handle,ref b));
    }
    private static void Upload(byte[] pixels,int width,int height,Func<NativeMethods.Bitmap,int> send)
    {
        var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
        try
        {
            var b=new NativeMethods.Bitmap{Size=32,Version=1,Width=checked((uint)width),Height=checked((uint)height),Stride=checked((uint)width*4),Pixels=pin.AddrOfPinnedObject()};
            Marshal.ThrowExceptionForHR(send(b));
        }
        finally{pin.Free();}
    }
    public void RenderGlass(StaticGlassMaterial material,DirectGlassScene scene)
    {
        if(!TryRenderGlass(material,scene))throw new NotSupportedException("This material or optical scene requires the legacy renderer.");
    }
    /// <summary>False leaves the frame untouched and requires the legacy path. Other GPU failures throw.</summary>
    public bool TryRenderGlass(StaticGlassMaterial material,DirectGlassScene scene)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(material);ArgumentNullException.ThrowIfNull(scene);
        var m=new NativeMethods.Material{Size=48,Version=1,DipWidth=material.DipWidth,DipHeight=material.DipHeight,Strength=material.Strength,
            Clarity=material.Clarity,TintOpacity=material.TintOpacity,Kind=material.Frosted?1u:0u,Dark=material.Dark?1u:0u};
        var panel=scene.PanelBounds;var back=scene.BackdropBounds;
        var s=new NativeMethods.GlassScene{Size=80,Version=2,HostWidth=(float)scene.HostSize.Width,HostHeight=(float)scene.HostSize.Height,
            PanelX=(float)panel.X,PanelY=(float)panel.Y,PanelWidth=(float)panel.Width,PanelHeight=(float)panel.Height,
            BackdropX=(float)back.X,BackdropY=(float)back.Y,BackdropWidth=(float)back.Width,BackdropHeight=(float)back.Height,
            Radius=scene.Radius,Strength=scene.Strength,Dispersion=scene.Dispersion,InnerGain=scene.InnerGain,OuterGain=scene.OuterGain,
            Launchpad=scene.Launchpad?1u:0u,Outline=scene.CompactOutline?1u:0u};
        var result=NativeMethods.PresenterRenderGlass(_handle,ref m,ref s);
        if(result==unchecked((int)0x80004001))return false;
        Marshal.ThrowExceptionForHR(result);return true;
    }
    /// <summary>Bounds are final content rectangles; layout owns uniform fitting and Z order.</summary>
    public void DrawImages(Size hostSize,ReadOnlySpan<DirectImageDraw> items)
    {
        EnsureUsable();if(items.Length>256)throw new ArgumentOutOfRangeException(nameof(items));
        foreach(var item in items)if(item.TextureBounds is not null){DrawMappedImages(hostSize,items);return;}
        var scratch=ArrayPool<NativeMethods.ImageDraw>.Shared.Rent(Math.Max(1,items.Length));
        try
        {
            for(var i=0;i<items.Length;i++)
            {
                var d=items[i];scratch[i]=new(){Key=d.Key,X=(float)d.Bounds.X,Y=(float)d.Bounds.Y,Width=(float)d.Bounds.Width,Height=(float)d.Bounds.Height,Opacity=d.Opacity};
            }
            var pin=GCHandle.Alloc(scratch,GCHandleType.Pinned);
            try
            {
                var b=new NativeMethods.ImageBatch{Size=32,Version=1,Count=(uint)items.Length,HostWidth=(float)hostSize.Width,HostHeight=(float)hostSize.Height,Items=pin.AddrOfPinnedObject()};
                Marshal.ThrowExceptionForHR(NativeMethods.PresenterDrawImages(_handle,ref b));
            }
            finally{pin.Free();}
        }
        finally{ArrayPool<NativeMethods.ImageDraw>.Shared.Return(scratch);}
    }
    private void DrawMappedImages(Size hostSize,ReadOnlySpan<DirectImageDraw> items)
    {
        var scratch=ArrayPool<NativeMethods.MappedImageDraw>.Shared.Rent(Math.Max(1,items.Length));
        try
        {
            for(var i=0;i<items.Length;i++)
            {
                var d=items[i];var map=d.TextureBounds??d.Bounds;
                scratch[i]=new(){Geometry=new(){Key=d.Key,X=(float)d.Bounds.X,Y=(float)d.Bounds.Y,Width=(float)d.Bounds.Width,Height=(float)d.Bounds.Height,Opacity=d.Opacity},
                    MapX=(float)map.X,MapY=(float)map.Y,MapWidth=(float)map.Width,MapHeight=(float)map.Height};
            }
            var pin=GCHandle.Alloc(scratch,GCHandleType.Pinned);
            try
            {
                var b=new NativeMethods.ImageBatch{Size=32,Version=1,Count=(uint)items.Length,HostWidth=(float)hostSize.Width,HostHeight=(float)hostSize.Height,Items=pin.AddrOfPinnedObject()};
                Marshal.ThrowExceptionForHR(NativeMethods.PresenterDrawMappedImages(_handle,ref b));
            }
            finally{pin.Free();}
        }
        finally{ArrayPool<NativeMethods.MappedImageDraw>.Shared.Return(scratch);}
    }
    public void Dispose()
    {
        VerifyAccess();if(_disposed)return;_handle.Dispose();_vectorAssets.Clear();_foregroundVectorOpacity?.Clear();Array.Clear(_vectorSlots);_vectorSourceBytes=0;_disposed=true;
    }
}
