using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.UI.Controls;

internal static class LegacyShellCapture
{
    internal static CapturePixels? ReadInWorker(nint handle)
    {
        using var worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--legacy-shell-frame " + handle)
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        var read = Task.Run(() =>
        {
            using var reader = new BinaryReader(worker.StandardOutput.BaseStream);
            if (reader.ReadInt32() != 0x47534346) throw new InvalidDataException("Invalid capture worker header.");
            var width = reader.ReadInt32(); var height = reader.ReadInt32();
            if (width <= 0 || height <= 0 || (long)width * height > 16_000_000) throw new InvalidDataException("Invalid capture worker extent.");
            var size = checked(width * height * 4);
            var pixels = reader.ReadBytes(size);
            if (pixels.Length != size) throw new EndOfStreamException("Incomplete capture worker frame.");
            return new CapturePixels(width, height, pixels);
        });
        var deadline = Stopwatch.StartNew();
        try
        {
            while (!read.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(4)) Program.Pump(TimeSpan.FromMilliseconds(10));
            if (!read.IsCompleted) throw new TimeoutException("Desktop capture worker exceeded four seconds.");
            return read.GetAwaiter().GetResult();
        }
        finally { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(1000); } }
    }

    internal static int WriteFrame(nint handle)
    {
        nint screen = 0, dc = 0, bitmap = 0, previous = 0;
        try
        {
            if (!GetWindowRect(handle, out var rect)) return 1;
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || (long)width * height > 16_000_000) return 1;
            screen = GetDC(0); dc = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (screen == 0 || dc == 0 || bitmap == 0 || bits == 0) return 1;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1 || !PrintWindow(handle, dc, 2)) return 1;
            GdiFlush();
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            using var writer = new BinaryWriter(Console.OpenStandardOutput());
            writer.Write(0x47534346); writer.Write(width); writer.Write(height); writer.Write(pixels);
            return 0;
        }
        finally
        {
            if (previous != 0 && previous != -1 && dc != 0) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    internal static int Run(nint handle)
    {
        nint screen = 0, dc = 0, bitmap = 0, previous = 0;
        try
        {
            if (!GetWindowRect(handle, out var rect)) throw new InvalidOperationException("Shell bounds are unavailable.");
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || (long)width * height > 16_000_000) throw new InvalidOperationException("Shell probe extent exceeds its diagnostic budget.");
            screen = GetDC(0); dc = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (screen == 0 || dc == 0 || bitmap == 0 || bits == 0) throw new InvalidOperationException("GDI diagnostic allocation failed.");
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) throw new InvalidOperationException("GDI diagnostic bitmap could not be selected.");
            var watch = Stopwatch.StartNew();
            var result = PrintWindow(handle, dc, 2);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            GdiFlush();
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var colored = 0; var nonzeroAlpha = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            { if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) colored++; if (pixels[i + 3] != 0) nonzeroAlpha++; }
            static int ColorCount(byte[] sample, int stride)
            {
                var colors = new HashSet<int>();
                for (var i = 0; i + 3 < sample.Length; i += stride * 4)
                    colors.Add(sample[i] | (sample[i + 1] << 8) | (sample[i + 2] << 16));
                return colors.Count;
            }
            var capturedColors = ColorCount(pixels, 67);
            var wallpaper = new WallpaperBackdrop();
            wallpaper.Update(new System.Windows.Rect(rect.Left, rect.Top, width, height), 512, Math.Max(1, (int)Math.Round(512d * height / width)), 0);
            int? referenceColors = wallpaper.Pixels is { } reference ? ColorCount(reference, 17) : null;
            var knownWallpaperMismatch = referenceColors > 1 && capturedColors <= 1;
            Console.WriteLine(JsonSerializer.Serialize(new { printWindowReturned = result, width, height, coloredPixels = colored,
                nonzeroAlphaPixels = nonzeroAlpha, elapsedMs = elapsed, sampledSourceColors = capturedColors,
                sampledWallpaperReferenceColors = referenceColors, knownWallpaperMismatch,
                note = "No user pixels or wallpaper paths saved. A true return or nonzero pixels does not establish desktop fidelity. Uniform paint cannot represent a varied wallpaper reference." }));
            return result && !knownWallpaperMismatch ? 0 : 1;
        }
        catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { error = error.Message })); return 1; }
        finally
        {
            if (previous != 0 && previous != -1 && dc != 0) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, ImportantColors; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint handle, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint source);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint source, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint value);
}
