using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Rendering;

public sealed partial class NativeDirectPresenter
{
    /// <summary>Uploads the accepted WPF material pixels without applying a second prefilter.</summary>
    public void UploadPreparedBackdrop(BitmapSource source, uint slot = 0)
    {
        EnsureUsable(); ArgumentNullException.ThrowIfNull(source);
        if (source.Format != PixelFormats.Pbgra32) source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var width = source.PixelWidth; var height = source.PixelHeight;
        var length = checked(width * height * 4); var pixels = Marshal.AllocHGlobal(length);
        try
        {
            source.CopyPixels(Int32Rect.Empty, pixels, length, width * 4);
            var bitmap = new NativeMethods.Bitmap { Size = 32, Version = 1, Width = (uint)width, Height = (uint)height, Stride = (uint)width * 4, Pixels = pixels };
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterUploadPreparedBackdrop(_handle, slot, ref bitmap));
        }
        finally { Marshal.FreeHGlobal(pixels); }
    }

    public void RenderPreparedGlass(DirectGlassScene scene, bool frosted = false, uint slot = 0, bool composite = false)
    {
        EnsureUsable(); ArgumentNullException.ThrowIfNull(scene);
        var p = scene.PanelBounds; var b = scene.BackdropBounds;
        var s = new NativeMethods.GlassScene { Size = 80, Version = 2, HostWidth = (float)scene.HostSize.Width, HostHeight = (float)scene.HostSize.Height,
            PanelX = (float)p.X, PanelY = (float)p.Y, PanelWidth = (float)p.Width, PanelHeight = (float)p.Height,
            BackdropX = (float)b.X, BackdropY = (float)b.Y, BackdropWidth = (float)b.Width, BackdropHeight = (float)b.Height,
            Radius = scene.Radius, Strength = scene.Strength, Dispersion = scene.Dispersion, InnerGain = scene.InnerGain, OuterGain = scene.OuterGain,
            Launchpad = scene.Launchpad ? 1u : 0u, Outline = scene.CompactOutline ? 1u : 0u };
        Marshal.ThrowExceptionForHR(composite
            ? NativeMethods.PresenterCompositePreparedGlass(_handle, slot, ref s, frosted ? 1u : 0u)
            : NativeMethods.PresenterRenderPreparedGlass(_handle, slot, ref s, frosted ? 1u : 0u));
    }
}
internal static partial class NativeMethods
{
    [DllImport(Dll, EntryPoint = "GD_PresenterUploadPreparedBackdrop", CallingConvention = CallingConvention.Cdecl)] internal static extern int PresenterUploadPreparedBackdrop(PresenterHandle p, uint slot, ref Bitmap bitmap);
    [DllImport(Dll, EntryPoint = "GD_PresenterRenderPreparedGlass", CallingConvention = CallingConvention.Cdecl)] internal static extern int PresenterRenderPreparedGlass(PresenterHandle p, uint slot, ref GlassScene scene, uint frosted);
    [DllImport(Dll, EntryPoint = "GD_PresenterCompositePreparedGlass", CallingConvention = CallingConvention.Cdecl)] internal static extern int PresenterCompositePreparedGlass(PresenterHandle p, uint slot, ref GlassScene scene, uint frosted);
}
