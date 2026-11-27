using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;

namespace GlassDesk.UI;

public static class StagePreviewCapture
{
    private const uint CarrierExtendedStyle = 0x080800A0; // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT
    private const uint CarrierStyle = 0x80000004; // WS_POPUP | SS_BLACKRECT
    private const uint SetThumbnailProperties = 0x1D; // RECTDESTINATION | OPACITY | VISIBLE | SOURCECLIENTAREAONLY

    public static BitmapSource? ReadIcon(nint window)
    {
        _ = SendMessageTimeout(window, 0x007F, 2, 0, 0x0002, 20, out var icon); // WM_GETICON / ICON_SMALL2
        if (icon == 0) icon = Environment.Is64BitProcess ? GetClassLongPtr(window, -34) : GetClassLong(window, -34);
        if (icon == 0) return null;
        try
        {
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon,
                System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or COMException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern int GetClassLong(nint window, int index);

    /// <summary>Reads one DWM thumbnail on a worker without opening a graphics capture session.</summary>
    public static BitmapSource? Capture(StageWindowIdentity identity, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        if (cancellation.IsCancellationRequested || !StageWindows.Matches(identity)
            || !StageWindows.CanCapture(identity.Hwnd)) return null;
        nint carrier = 0, thumbnail = 0;
        try
        {
            carrier = CreateWindowEx(CarrierExtendedStyle, "STATIC", "GlassDesk stage preview", CarrierStyle,
                0, 0, 1, 1, 0, 0, 0, 0);
            if (carrier == 0 || !SetLayeredWindowAttributes(carrier, 0, 0, 2)
                || DwmRegisterThumbnail(carrier, identity.Hwnd, out thumbnail) < 0
                || DwmQueryThumbnailSourceSize(thumbnail, out var source) < 0
                || source.Width <= 0 || source.Height <= 0 || cancellation.IsCancellationRequested) return null;

            var scale = Math.Min(1, Math.Min(1280.0 / source.Width, 800.0 / source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            // DWM needs a visible carrier. Alpha zero, HWND_BOTTOM and SWP_NOACTIVATE keep it off the user's desktop.
            if (!SetWindowPos(carrier, 1, 0, 0, width, height, 0x50)) return null;
            var properties = new ThumbnailProperties
            {
                Flags = SetThumbnailProperties, Destination = new(0, 0, width, height), Opacity = 255, Visible = 1,
            };
            if (DwmUpdateThumbnailProperties(thumbnail, in properties) < 0) return null;

            using var capture = new PrintWindowWindowCapture(carrier, width, height);
            var pixels = capture.TryRead(cancellation: cancellation);
            Trace.TraceInformation("Stage Manager thumbnail {0}: total {1:0} ms, pixels {2}, cancelled {3}",
                identity.Handle, Stopwatch.GetElapsedTime(started).TotalMilliseconds, pixels?.Width, cancellation.IsCancellationRequested);
            if (pixels is null || cancellation.IsCancellationRequested || !StageWindows.Matches(identity)
                || !StageWindows.CanCapture(identity.Hwnd) || !HasPreviewPixels(pixels.Pixels)) return null;

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels.Pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceInformation("Stage Manager preview unavailable: {0}", error.Message);
            return null;
        }
        finally
        {
            if (thumbnail != 0) _ = DwmUnregisterThumbnail(thumbnail);
            if (carrier != 0) _ = DestroyWindow(carrier);
        }
    }

    private static bool HasPreviewPixels(byte[] pixels)
    {
        // A missing or protected thumbnail leaves the black carrier untouched.
        for (var offset = 0; offset < pixels.Length; offset += 4)
            if ((pixels[offset] | pixels[offset + 1] | pixels[offset + 2]) != 0) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct ThumbnailRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    {
        public uint Flags;
        public ThumbnailRect Destination, Source;
        public byte Opacity;
        public int Visible, ClientOnly;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle,
        string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out ThumbnailSize size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, in ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
