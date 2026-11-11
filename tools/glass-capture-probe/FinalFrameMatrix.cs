using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Dock;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static class FinalFrameMatrix
{
    private static readonly string[] Backgrounds =
    [
        "bright-low-contrast",
        "dark",
        "high-contrast-text-window",
        "straight-lines-grid",
    ];

    private static readonly double[] DockWidthsDip = [300, 760, 1350];
    private static readonly double[] TargetDpis = [96, 144, 192];
    private static readonly FieldInfo FrameField = Field("_frame");
    private static readonly FieldInfo SampledBoundsField = Field("_sampledPanelBounds");
    private static readonly FieldInfo SourcePixelsField = Field("_lastSource");
    private static readonly FieldInfo OpticsField = Field("_optics");
    private static readonly FieldInfo TimerField = Field("_timer");
    private static readonly FieldInfo PanelShapeField = Field("_panelShape");
    private static readonly FieldInfo PanelRadiusField = Field("_panelRadius");
    private static readonly FieldInfo TransitionBlurField = Field("_transitionBlurRadius");
    private static readonly PropertyInfo SquircleProperty = typeof(GlassWindow).GetProperty(
        "UsesSquirclePanelGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GlassWindow squircle property was not found.");

    internal static int Run(string outputDirectory)
    {
        DockWindow? dock = null;
        ThemeService? theme = null;
        Application? application = null;
        try
        {
            var root = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException($"Output directory is not empty: {root}");
            Directory.CreateDirectory(root);

            LiquidGlassDebugState.View = LiquidGlassDebugView.None;
            LiquidGlassDebugState.ShowPerformanceOverlay = false;
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml", UriKind.Absolute)
            });
            theme = ThemeService.Initialize(application.Dispatcher);
            // This exporter records the original optical baseline; the compatible material
            // has its own matrix with explicit material selection and full composition.
            theme.Update(new GlassDesk.Core.Models.Settings.AppearanceSettings
            { GlassMaterial = GlassDesk.Core.Models.GlassMaterialMode.PhysicalGlass });

            dock = new DockWindow
            {
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            dock.Show();
            dock.UpdateLayout();

            var surface = (LiquidGlassSurface)(dock.FindName("GlassSurface")
                ?? throw new InvalidOperationException("DockWindow GlassSurface was not found."));
            var stripHost = (System.Windows.Controls.ContentControl)(dock.FindName("TileStripHost")
                ?? throw new InvalidOperationException("DockWindow TileStripHost was not found."));
            if (stripHost.Content is not FrameworkElement tileStrip)
                throw new InvalidOperationException("DockWindow tile strip is unavailable.");

            var frameChromeWidth = dock.ActualWidth - tileStrip.ActualWidth;
            if (!double.IsFinite(frameChromeWidth) || frameChromeWidth <= 0)
                throw new InvalidOperationException("DockWindow frame width could not be measured.");
            tileStrip.MinWidth = 0;
            tileStrip.MaxWidth = double.PositiveInfinity;
            WaitForLiveDesktop(surface);

            using var timing = new CompositionTimingCollector();
            var samples = new List<CompositionSample>();
            var frames = new List<FrameRecord>();
            var sourceFrames = new List<SourceRecord>();
            var sourceDirectory = Path.Combine(root, "source-inputs");
            Directory.CreateDirectory(sourceDirectory);
            var normalOpticsCosts = new List<double>();

            foreach (var widthDip in DockWidthsDip)
            {
                SetDockWidth(dock, tileStrip, frameChromeWidth, widthDip);
                CenterDock(dock);
                WaitFor(TimeSpan.FromMilliseconds(550));
                Require(surface.BackdropSource == GlassBackdropSource.LiveDesktop,
                    $"{widthDip:0} DIP did not use the production LiveDesktop source before timing.");
                WaitForSurfaceFrame(surface);
                Require(Math.Abs(dock.ActualWidth - widthDip) <= 2,
                    $"Dock width was {dock.ActualWidth:0.00} DIP; expected {widthDip:0} DIP.");
                Require(SourcePixelsField.GetValue(surface) is byte[],
                    "The live production backdrop did not expose its current sampled input.");

                var sample = timing.Measure($"live desktop / {widthDip:0} DIP", TimeSpan.FromSeconds(3));
                samples.Add(sample);
                normalOpticsCosts.Add((double)(typeof(LiquidGlassSurface)
                    .GetField("_lastOpticsMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(surface) ?? 0d));

                foreach (var background in Backgrounds)
                foreach (var targetDpi in TargetDpis)
                {
                    var fileName = $"{background}-w{widthDip:0}-dpi{targetDpi:0}.png";
                    var sourceFile = $"{background}-w{widthDip:0}-dpi{targetDpi:0}-input.png";
                    var result = ExportInjectedOnce(dock, surface, background, targetDpi,
                        Path.Combine(sourceDirectory, sourceFile), Path.Combine(root, fileName));
                    sourceFrames.Add(AnalyzeSource(result.Input, sourceFile, background, widthDip,
                        targetDpi, Path.Combine(sourceDirectory, sourceFile)));
                    frames.Add(new FrameRecord(fileName, background, widthDip, targetDpi,
                        VisualTreeHelper.GetDpi(dock).PixelsPerInchX,
                        result.Bitmap.PixelWidth, result.Bitmap.PixelHeight,
                        dock.ActualWidth, dock.ActualHeight,
                        "LiveDesktop", "DebugPatternInjectedAtProductionLiquidGlassOptics.Render",
                        result.Input.PixelWidth, result.Input.PixelHeight,
                        result.OpticsMilliseconds, result.ExportMilliseconds));
                    Require(surface.BackdropSource == GlassBackdropSource.LiveDesktop,
                        "The live desktop source was not restored after a one-shot Debug export.");
                }
            }

            var allGaps = samples.SelectMany(sample => sample.FrameGapsMs).ToArray();
            var meanFrameTime = allGaps.Length == 0 ? 0 : allGaps.Average();
            var debugExports = frames.Select(frame => frame.OneShotDebugExportMilliseconds).ToArray();
            var manifest = new
            {
                schemaVersion = 2,
                captureMethod = "WPF RenderTargetBitmap over the production DockWindow visual root after production LiquidGlassOptics.Render",
                inputMethod = "one-shot Debug pattern rasterization and injection at the LiquidGlassOptics.Render input boundary; the live sampler is restored immediately after each export",
                liveDpi = (int)Math.Round(VisualTreeHelper.GetDpi(dock).PixelsPerInchX),
                dpiCoverage = "Current physical desktop is 144 DPI; 96/144/192 targets use matching offscreen source and final composition resolutions",
                dockWidthCoverageDip = DockWidthsDip,
                backgrounds = Backgrounds,
                processId = Environment.ProcessId,
                debugViewActiveDuringCompositionTiming = LiquidGlassDebugView.None.ToString(),
                debugViewActiveDuringFinalExports = LiquidGlassDebugState.View.ToString(),
                diagnosticImageGeneratorCallCount = 0,
                diagnosticImageGeneratorPath = "LiquidGlassOptics.Render enters RenderDebugView only when debugView != None; this matrix used None",
                sourceFrames,
                frames,
                compositionSamples = samples,
                compositionSummary = new
                {
                    method = "CompositionTarget.Rendering callback intervals while the production DockWindow sampled LiveDesktop; this is WPF composition timing, not a PresentMon/DWM-present counter",
                    sampleCount = allGaps.Length,
                    sampleDurationSecondsPerWidth = 3,
                    averageFrameTimeMs = meanFrameTime,
                    averageFps = meanFrameTime <= 0 ? 0 : 1000 / meanFrameTime,
                    p95FrameTimeMs = Percentile(allGaps, .95),
                    peakFrameTimeMs = allGaps.Length == 0 ? 0 : allGaps.Max(),
                    conspicuousPeakThresholdMs = 33.34,
                    conspicuousPeakCount = allGaps.Count(gap => gap >= 33.34),
                },
                normalLiveOpticsLastCalculationMs = normalOpticsCosts,
                oneShotDebugExportCostMs = new
                {
                    count = debugExports.Length,
                    average = debugExports.Length == 0 ? 0 : debugExports.Average(),
                    minimum = debugExports.Length == 0 ? 0 : debugExports.Min(),
                    maximum = debugExports.Length == 0 ? 0 : debugExports.Max(),
                    definition = "Input rasterization + production optics + final WPF composite + PNG encoding; this cost occurs only when this Debug exporter is explicitly run",
                },
            };
            File.WriteAllText(Path.Combine(root, "final-frame-matrix.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: exported {frames.Count} one-shot final Background → Glass → Content frames to {root}");
            Console.WriteLine($"Live source DPI {manifest.liveDpi}; composition average {manifest.compositionSummary.averageFps:0.0} FPS, " +
                $"average {manifest.compositionSummary.averageFrameTimeMs:0.00} ms, peak {manifest.compositionSummary.peakFrameTimeMs:0.00} ms.");
            Console.WriteLine($"One-shot Debug export cost {manifest.oneShotDebugExportCostMs.minimum:0.00}–{manifest.oneShotDebugExportCostMs.maximum:0.00} ms; diagnostic image path calls: 0.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (dock is { IsVisible: true }) dock.Close();
            theme?.Dispose();
            if (application is { Dispatcher.HasShutdownStarted: false }) application.Shutdown();
        }
    }

    private static OneShotResult ExportInjectedOnce(DockWindow dock, LiquidGlassSurface surface,
        string background, double dpi, string sourcePath, string finalPath)
    {
        var timer = (DispatcherTimer)(TimerField.GetValue(surface)
            ?? throw new InvalidOperationException("Production surface frame timer was not found."));
        var wasTimerEnabled = timer.IsEnabled;
        var originalFrame = FrameField.GetValue(surface);
        var originalSource = SourcePixelsField.GetValue(surface);
        var originalBounds = SampledBoundsField.GetValue(surface);
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (wasTimerEnabled) timer.Stop();
        try
        {
            Require(surface.BackdropSource == GlassBackdropSource.LiveDesktop,
                "The Debug input can only be injected after a real LiveDesktop sample.");
            if (originalFrame is not BitmapSource liveFrame || liveFrame.PixelWidth <= 0 || liveFrame.PixelHeight <= 0)
                throw new InvalidOperationException("The live production surface frame is unavailable.");
            if (originalBounds is not Rect bounds || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
                throw new InvalidOperationException("The live sampled panel bounds are unavailable.");

            var scale = dpi / 96;
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
            if ((long)width * height > 2_000_000)
                throw new InvalidOperationException($"Debug input exceeds the production optics pixel limit ({width}x{height}).");
            var inputBitmap = RasterizeInput(background, bounds.Size, width, height, dpi);
            var input = new byte[width * height * 4];
            inputBitmap.CopyPixels(input, width * 4, 0);
            SavePng(inputBitmap, sourcePath);

            var owner = Window.GetWindow(surface) as GlassWindow
                ?? throw new InvalidOperationException("The production surface has no GlassWindow owner.");
            var panelShape = PanelShapeField.GetValue(surface);
            var radius = panelShape is null
                ? owner.PanelCornerRadius.TopLeft
                : (double)(PanelRadiusField.GetValue(surface) ?? owner.PanelCornerRadius.TopLeft);
            var squircle = (bool)(SquircleProperty.GetValue(owner) ?? false);
            var strength = .6 + .4 * surface.TransitionProgress;
            var softness = (double)(TransitionBlurField.GetValue(surface) ?? 0d);
            var debugView = LiquidGlassDebugState.View;
            Require(debugView == LiquidGlassDebugView.None,
                "Final frame export must render the production image path without a diagnostic overlay.");

            var optics = (LiquidGlassOptics)(OpticsField.GetValue(surface)
                ?? throw new InvalidOperationException("Production optics renderer was not found."));
            var opticsStarted = System.Diagnostics.Stopwatch.StartNew();
            var refracted = optics.Render(input, width, height, bounds.Size, radius, squircle,
                strength, softness, debugView: debugView);
            var opticsMilliseconds = opticsStarted.Elapsed.TotalMilliseconds;

            var replacement = new WriteableBitmap(width, height, dpi, dpi, PixelFormats.Bgra32, null);
            replacement.WritePixels(new Int32Rect(0, 0, width, height), refracted, width * 4, 0);
            FrameField.SetValue(surface, replacement);
            SampledBoundsField.SetValue(surface, bounds);
            surface.InvalidateVisual();
            dock.UpdateLayout();
            var final = ExportOnce(dock, finalPath, dpi);
            started.Stop();
            return new OneShotResult(inputBitmap, final, opticsMilliseconds, started.Elapsed.TotalMilliseconds);
        }
        finally
        {
            FrameField.SetValue(surface, originalFrame);
            SourcePixelsField.SetValue(surface, originalSource);
            SampledBoundsField.SetValue(surface, originalBounds);
            surface.InvalidateVisual();
            if (wasTimerEnabled) timer.Start();
        }
    }

    private static BitmapSource RasterizeInput(string background, Size sizeDip, int width, int height, double dpi)
    {
        var element = new FinalFrameBackground(background, dpi / 96)
        {
            Width = sizeDip.Width,
            Height = sizeDip.Height,
        };
        element.Measure(sizeDip);
        element.Arrange(new Rect(sizeDip));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static BitmapSource ExportOnce(DockWindow dock, string outputPath, double dpi)
    {
        dock.UpdateLayout();
        var scale = dpi / 96;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(dock.ActualWidth * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(dock.ActualHeight * scale));
        var target = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(dock);
        SavePng(target, outputPath);
        return target;
    }

    private static void SavePng(BitmapSource bitmap, string outputPath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static SourceRecord AnalyzeSource(BitmapSource bitmap, string file, string background,
        double dockWidthDip, double targetDpi, string path)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        long sum = 0;
        long sumSquares = 0;
        var count = bitmap.PixelWidth * bitmap.PixelHeight;
        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        var darkPixels = 0;
        var brightPixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var luma = (byte)Math.Round(.2126 * pixels[index + 2] + .7152 * pixels[index + 1] + .0722 * pixels[index]);
            sum += luma;
            sumSquares += luma * luma;
            minimum = Math.Min(minimum, luma);
            maximum = Math.Max(maximum, luma);
            if (luma < 80) darkPixels++;
            if (luma > 220) brightPixels++;
        }
        var mean = (double)sum / count;
        var variance = Math.Max(0, (double)sumSquares / count - mean * mean);
        return new SourceRecord(file, background, dockWidthDip, targetDpi,
            bitmap.PixelWidth, bitmap.PixelHeight, mean, Math.Sqrt(variance), minimum,
            maximum, (double)darkPixels / count, (double)brightPixels / count, Path.GetFullPath(path));
    }

    private static BitmapSource RasterizeInput(string background, Size sizeDip, int width, int height, double dpi,
        out byte[] pixels)
    {
        var bitmap = RasterizeInput(background, sizeDip, width, height, dpi);
        pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return bitmap;
    }

    private static void SetDockWidth(DockWindow dock, FrameworkElement tileStrip, double frameChromeWidth, double targetDip)
    {
        var stripWidth = Math.Max(0, targetDip - frameChromeWidth);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            tileStrip.Width = stripWidth;
            dock.UpdateLayout();
            var difference = targetDip - dock.ActualWidth;
            if (Math.Abs(difference) <= .25) return;
            stripWidth = Math.Max(0, stripWidth + difference);
            WaitFor(TimeSpan.FromMilliseconds(40));
        }
    }

    private static void CenterDock(DockWindow dock)
    {
        dock.Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - dock.ActualWidth) / 2);
        dock.Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - dock.ActualHeight) / 2);
    }

    private static void WaitForLiveDesktop(LiquidGlassSurface surface)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (surface.BackdropSource == GlassBackdropSource.LiveDesktop
                && FrameField.GetValue(surface) is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 }) return;
            WaitFor(TimeSpan.FromMilliseconds(120));
        }
        throw new TimeoutException($"Production Dock Surface did not reach LiveDesktop; actual source: {surface.BackdropSource}.");
    }

    private static void WaitForSurfaceFrame(LiquidGlassSurface surface)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (FrameField.GetValue(surface) is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 }) return;
            WaitFor(TimeSpan.FromMilliseconds(50));
        }
        throw new TimeoutException("Production Dock Surface has no rendered frame.");
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var ordered = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void WaitFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static FieldInfo Field(string name) => typeof(LiquidGlassSurface).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"LiquidGlassSurface field {name} was not found.");

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed record OneShotResult(BitmapSource Input, BitmapSource Bitmap,
        double OpticsMilliseconds, double ExportMilliseconds);

    private sealed record FrameRecord(
        string File,
        string Background,
        double DockWidthDip,
        double TargetDpi,
        double LiveDpi,
        int PixelWidth,
        int PixelHeight,
        double ActualWidthDip,
        double ActualHeightDip,
        string LiveSourceBeforeInjection,
        string InputMethod,
        int InputPixelWidth,
        int InputPixelHeight,
        double ProductionOpticsMilliseconds,
        double OneShotDebugExportMilliseconds);

    private sealed record SourceRecord(
        string File,
        string Background,
        double DockWidthDip,
        double TargetDpi,
        int PixelWidth,
        int PixelHeight,
        double MeanLuma,
        double LumaStdDev,
        byte MinimumLuma,
        byte MaximumLuma,
        double DarkPixelFraction,
        double BrightPixelFraction,
        string Path);

    private sealed record CompositionSample(string Condition, int FrameCount,
        double AverageFrameTimeMs, double AverageFps, double P95FrameTimeMs,
        double PeakFrameTimeMs, int ConspicuousPeakCount, double[] FrameGapsMs);

    private sealed class CompositionTimingCollector : IDisposable
    {
        private readonly List<double> _gaps = [];
        private long _previous;
        private bool _collecting;

        internal CompositionTimingCollector() => CompositionTarget.Rendering += OnRendering;

        internal CompositionSample Measure(string condition, TimeSpan duration)
        {
            _gaps.Clear();
            _previous = 0;
            _collecting = true;
            WaitFor(duration);
            _collecting = false;
            var gaps = _gaps.ToArray();
            var average = gaps.Length == 0 ? 0 : gaps.Average();
            return new CompositionSample(condition, gaps.Length + (_previous == 0 ? 0 : 1),
                average, average <= 0 ? 0 : 1000 / average,
                Percentile(gaps, .95), gaps.Length == 0 ? 0 : gaps.Max(),
                gaps.Count(gap => gap >= 33.34), gaps);
        }

        public void Dispose() => CompositionTarget.Rendering -= OnRendering;

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_collecting) return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_previous != 0) _gaps.Add(System.Diagnostics.Stopwatch.GetElapsedTime(_previous, now).TotalMilliseconds);
            _previous = now;
        }
    }
}
