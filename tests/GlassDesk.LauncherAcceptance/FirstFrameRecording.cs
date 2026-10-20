using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

// Local evidence only. Desktop frames sample the composed desktop; window frames
// render the WPF visual tree and cannot establish physical display timing.
internal static class FirstFrameRecording
{
    // A bright controlled desktop/fixture is required for this diagnostic: this
    // threshold is never a production capture filter or a general wallpaper test.
    internal sealed class DockContinuityRecording : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private readonly List<object> _samples = [];
        private readonly string _output;
        private bool _disposed;
        internal int DarkSamples { get; private set; }
        internal int WaitingBodySamples { get; private set; }
        internal int SampleCount => _samples.Count;
        internal string? Failure { get; private set; }
        internal int SkippedForPose { get; private set; }
        internal DockContinuityRecording(PixelRect region, Func<string> phase, string output, Func<bool>? measurable = null)
        {
            _output = output; Directory.CreateDirectory(output);
            _thread = new Thread(() =>
            {
                try
                {
                    using var desktop = new DesktopFrames(region);
                    do
                    {
                        if (measurable?.Invoke() == false) { SkippedForPose++; continue; }
                        var ticks = Stopwatch.GetTimestamp();
                        var currentPhase = phase();
                        var mean = desktop.ReadMaterialBand();
                        if (measurable?.Invoke() == false) { SkippedForPose++; continue; }
                        if (mean is null) throw new InvalidOperationException("Desktop band capture failed.");
                        // Calibration on this controlled fixture: the invalidated
                        // waiting body is 50.19, while live blue reflections can be
                        // 52-65. Keep those darker legitimate frames for visual QA.
                        var dark = mean < 65;
                        var waitingBody = mean < 51;
                        if (dark) DarkSamples++;
                        if (waitingBody) WaitingBodySamples++;
                        _samples.Add(new { ticks, phase = currentPhase, meanChannel = mean, dark, waitingBody });
                        if (dark && DarkSamples <= 12 && desktop.Read() is { } image)
                            SavePng(image, Path.Combine(output, "dark-" + DarkSamples + ".png"));
                    } while (!_stop.Wait(TimeSpan.FromMilliseconds(1000d / 60)));
                }
                catch (Exception error) { Failure = error.ToString(); }
            }) { IsBackground = true, Name = "GlassDesk composed Dock continuity recorder" };
            _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set(); _thread.Join(); _stop.Dispose();
            File.WriteAllText(Path.Combine(_output, "composed-dock-continuity.json"), JsonSerializer.Serialize(new
            {
                note = "Independent real-desktop GDI sampling, no affinity override. Bright controlled fixture only; no displayed FPS claim; sub-sample flashes can be missed. When gated, hidden/sliding poses are recorded separately by reveal acceptance.",
                skippedForPose = SkippedForPose,
                samples = SampleCount, darkSamples = DarkSamples, waitingBodySamples = WaitingBodySamples, failure = Failure, observations = _samples
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal static void Record(Window window, Action reveal, string outputDirectory, TimeSpan duration, PixelRect? captureRegionPixels = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(duration));
        Directory.CreateDirectory(outputDirectory);
        var region = captureRegionPixels ?? DisplayService.Primary()?.WorkArea ?? throw new InvalidOperationException("Primary display unavailable.");
        var desktopImages = new List<(BitmapSource Image, long Ticks)>();
        var windowImages = new List<(BitmapSource Image, long Ticks)>();
        var observations = new List<object>();
        var started = Stopwatch.GetTimestamp();
        var dispatcherFrame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stopRecording = new ManualResetEventSlim();
        using var recordingReady = new ManualResetEventSlim();
        var recorder = new Thread(() =>
        {
            try
            {
                using var desktop = new DesktopFrames(region);
                recordingReady.Set();
                do
                {
                    var at = Stopwatch.GetTimestamp();
                    var image = desktop.Read();
                    if (image is not null) desktopImages.Add((image, at));
                } while (!stopRecording.Wait(TimeSpan.FromMilliseconds(1000d / 30))
                    && Stopwatch.GetElapsedTime(started) < duration);
            }
            catch (Exception error) { failures.Enqueue(error.ToString()); recordingReady.Set(); }
        }) { IsBackground = true, Name = "GlassDesk first-frame desktop recorder" };
        recorder.SetApartmentState(ApartmentState.STA);
        void CaptureFrame()
        {
            var at = Stopwatch.GetTimestamp();
            var handle = new WindowInteropHelper(window).Handle;
            uint affinity = 0;
            var affinityKnown = handle != 0 && GetWindowDisplayAffinity(handle, out affinity);
            if (!affinityKnown) affinity = 0;
            var surface = window.FindName("GlassSurface") as LiquidGlassSurface;
            observations.Add(new
            {
                qpcTicks = at, millisecondsSinceReveal = Stopwatch.GetElapsedTime(started, at).TotalMilliseconds,
                windowVisible = window.IsVisible,
                windowOpacity = window.Opacity, affinityKnown, displayAffinity = affinity,
                desktopMayOmitGlassDesk = affinityKnown && affinity != 0,
                material = surface?.InspectPerformance()
            });
        }
        timer.Tick += (_, _) =>
        {
            try { CaptureFrame(); }
            catch (Exception error) { failures.Enqueue(error.ToString()); }
            if (Stopwatch.GetElapsedTime(started) >= duration) dispatcherFrame.Continue = false;
        };
        try
        {
            started = Stopwatch.GetTimestamp();
            recorder.Start();
            if (!recordingReady.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Desktop recorder did not start.");
            CaptureFrame();
            timer.Start(); reveal(); Dispatcher.PushFrame(dispatcherFrame);
        }
        finally { timer.Stop(); stopRecording.Set(); recorder.Join(); }
        // Rendering the full WPF tree can block the UI for hundreds of ms. Take
        // this diagnostic once, after desktop recording and the opening animation.
        if (window.IsVisible && window.ActualWidth > 0 && window.ActualHeight > 0)
        {
            var scale = Math.Min(1, 900 / Math.Max(window.ActualWidth, window.ActualHeight));
            var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale)),
                Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(window); image.Freeze(); windowImages.Add((image, Stopwatch.GetTimestamp()));
        }
        SaveGif(desktopImages, Path.Combine(outputDirectory, "desktop-first-frames.gif"));
        SaveGif(windowImages, Path.Combine(outputDirectory, "window-render-first-frames.gif"));
        if (desktopImages.Count != 0) SavePng(desktopImages[^1].Image, Path.Combine(outputDirectory, "desktop-last-frame.png"));
        if (windowImages.Count != 0) SavePng(windowImages[0].Image, Path.Combine(outputDirectory, "window-final-frame.png"));
        File.WriteAllText(Path.Combine(outputDirectory, "first-frame-recording.json"), JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow, qpcFrequency = Stopwatch.Frequency, captureRegionPixels = region,
            desktopSource = "GDI BitBlt of the real composed primary desktop, downsampled to at most 900 pixels; no affinity override.",
            windowSource = "One WPF RenderTargetBitmap after recording finishes, not an opening frame or displayed GPU FPS.",
            limitation = "30 Hz sampled recording can miss shorter display events. Recording itself adds capture and encoding work; exclude this phase from performance measurements.",
            desktopFrames = desktopImages.Count, windowFrames = windowImages.Count, failures = failures.ToArray(),
            desktopSampleTicks = desktopImages.Select(frame => frame.Ticks).ToArray(), observations
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveGif(List<(BitmapSource Image, long Ticks)> images, string path)
    {
        if (images.Count == 0) return;
        var encoder = new GifBitmapEncoder();
        for (var i = 0; i < images.Count; i++)
        {
            var elapsed = i + 1 < images.Count ? Stopwatch.GetElapsedTime(images[i].Ticks, images[i + 1].Ticks).TotalMilliseconds : 33;
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", (ushort)Math.Clamp(Math.Round(elapsed / 10), 1, ushort.MaxValue));
            metadata.SetQuery("/grctlext/Disposal", (byte)2);
            encoder.Frames.Add(BitmapFrame.Create(images[i].Image, null, metadata, null));
        }
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class DesktopFrames : IDisposable
    {
        private readonly PixelRect _region;
        private readonly int _width, _height;
        private readonly byte[] _pixels;
        private nint _screen, _dc, _bitmap, _oldBitmap, _bits;
        internal DesktopFrames(PixelRect region)
        {
            _region = region;
            var scale = Math.Min(1, 900d / Math.Max(region.Width, region.Height));
            _width = Math.Max(1, (int)Math.Floor(region.Width * scale));
            _height = Math.Max(1, (int)Math.Floor(region.Height * scale));
            _pixels = new byte[checked(_width * _height * 4)];
            _screen = GetDC(0); _dc = CreateCompatibleDC(_screen);
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = _width, Height = -_height, Planes = 1, Bits = 32 };
            _bitmap = CreateDIBSection(_screen, ref info, 0, out _bits, 0, 0);
            if (_screen == 0 || _dc == 0 || _bitmap == 0 || _bits == 0) { Dispose(); throw new InvalidOperationException("Desktop recording allocation failed."); }
            _oldBitmap = SelectObject(_dc, _bitmap); SetStretchBltMode(_dc, 4);
        }
        internal BitmapSource? Read()
        {
            if (!StretchBlt(_dc, 0, 0, _width, _height, _screen, _region.X, _region.Y, _region.Width, _region.Height, 0x40CC0020)) return null;
            GdiFlush(); Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
            for (var i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, _pixels, _width * 4);
            bitmap.Freeze(); return bitmap;
        }
        internal double? ReadMaterialBand()
        {
            if (!StretchBlt(_dc, 0, 0, _width, _height, _screen, _region.X, _region.Y, _region.Width, _region.Height, 0x40CC0020)) return null;
            GdiFlush(); Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
            long total = 0; var count = 0;
            // The upper body avoids every foreground icon and the outer shadow.
            var y = Math.Clamp((int)(_height * .08), 0, _height - 1);
            for (var x = (int)(_width * .55); x < (int)(_width * .88); x++)
            {
                var i = (y * _width + x) * 4;
                total += _pixels[i] + _pixels[i + 1] + _pixels[i + 2]; count += 3;
            }
            return (double)total / Math.Max(1, count);
        }
        public void Dispose()
        {
            if (_dc != 0 && _oldBitmap != 0 && _oldBitmap != -1) SelectObject(_dc, _oldBitmap);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_dc != 0) DeleteDC(_dc);
            if (_screen != 0) ReleaseDC(0, _screen);
            _screen = _dc = _bitmap = _oldBitmap = _bits = 0;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, ImportantColors; }
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint value);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dc, int x, int y, int width, int height,
        nint source, int sx, int sy, int sourceWidth, int sourceHeight, uint mode);
}
