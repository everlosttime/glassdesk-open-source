using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRenderingProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1]
            : Path.Combine(AppContext.BaseDirectory, "observations", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        if(args.Contains("--direct-input") || args.Contains("--direct-input-receiver"))return DirectInputProbe.Run(args,output);
        if(args.Contains("--direct-device-mode"))return DirectDeviceProbe.Run(args,output);
        var stateIndex = Array.IndexOf(args, "--state-directory");
        var stateDirectory = stateIndex >= 0 && stateIndex + 1 < args.Length ? args[stateIndex + 1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDesk.NativeRenderingProbe");
        using var recovery = NativeSessionRecovery.TryBegin(stateDirectory);
        if (recovery is null)
        {
            File.WriteAllText(Path.Combine(output, "probe.json"), "{\"passed\":false,\"nativeDisabledAfterUncleanSession\":true}");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        // In-memory probe settings only. Never load or save the installed user's Data.
        theme.Update(new AppearanceSettings { PinBarsToDesktop = true, GlassMaterial = GlassMaterialMode.PlainGlass });
        var durationIndex = Array.IndexOf(args, "--duration-seconds");
        var duration = durationIndex >= 0 && durationIndex + 1 < args.Length && int.TryParse(args[durationIndex + 1], out var parsedDuration)
            ? Math.Clamp(parsedDuration, 1, 600) : 0;
        var probe = new ProbeWindow(output, args.Contains("--self-test"), args.Contains("--observable"), duration, args.Contains("--animate"));
        app.DispatcherUnhandledException += (_, e) => { probe.Fail(e.Exception); e.Handled = true; app.Shutdown(1); };
        app.Run(probe);
        return probe.ExitCode;
    }
}

internal sealed class ProbeWindow : GlassWindow
{
    private readonly string _output;
    private readonly bool _selfTest, _observable;
    private readonly Grid _root = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly TextBlock _label = new() { Foreground = Brushes.White, FontSize = 17, Margin = new Thickness(24), IsHitTestVisible = false };
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Queue<double> _callbackIntervals = new(4096);
    private ulong _totalCallbacks;
    private TimeSpan _lastCallback;
    private NativeD3DImageBridge? _bridge;
    private DispatcherTimer? _timeout;
    private bool _animated, _paused, _reported;
    private int _pointerPresses;
    public int ExitCode { get; private set; }
    protected override bool SupportsDesktopLayer => true;

    internal ProbeWindow(string output, bool selfTest, bool observable, int duration, bool animate)
    {
        _output = output; _selfTest = selfTest; _observable = observable;
        _animated = animate;
        Title = "GlassDesk native GPU bridge probe";
        Width = 640; Height = 400; Left = 120; Top = 150;
        ShowActivated = false; ShowInTaskbar = true;
        _label.Text = "C++ GPU bridge · alpha 0 / 0.5 / 1\nWPF text overlay · Esc closes";
        _root.Children.Add(_image); _root.Children.Add(_label);
        if (!selfTest)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24) };
            void Button(string text, Action action)
            {
                var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4) };
                button.Click += (_, _) => action(); buttons.Children.Add(button);
            }
            Button("Alpha", () => _animated = false);
            Button("Animate", () => _animated = true);
            Button("Pause / Resume", () => { _paused = !_paused; if (_paused) _bridge?.Suspend(); else _bridge?.Resume(); });
            Button("Recover device", () => _bridge?.TryRecover());
            _root.Children.Add(buttons);
        }
        Content = _root;
        PreviewMouseDown += (_, _) => { _pointerPresses++; SaveLiveObservation(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) =>
        {
            if (_observable) SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
            SaveLiveObservation();
            if (duration > 0)
            {
                _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(duration) };
                _timeout.Tick += (_, _) => { _timeout.Stop(); Close(); };
                _timeout.Start();
            }
            if (_selfTest) await SelfTest();
        };
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dpi = VisualTreeHelper.GetDpi(this);
        _bridge = new(new WindowInteropHelper(this).Handle,
            Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX)), Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY)));
        _image.Source = _bridge.Image;
        CompositionTarget.Rendering += Render;
    }
    private void Render(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastCallback) return;
            if (_lastCallback != TimeSpan.Zero)
            {
                if (_callbackIntervals.Count == 4096) _callbackIntervals.Dequeue();
                _callbackIntervals.Enqueue((args.RenderingTime - _lastCallback).TotalMilliseconds);
            }
            _totalCallbacks++;
            _lastCallback = args.RenderingTime;
        }
        _bridge?.TryRender(_watch.Elapsed.TotalSeconds, _animated);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_bridge is null || ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _bridge.Resize(Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
    }
    private async Task SelfTest()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var originalOwner = GetWindow(hwnd, 4);
            var originalStyle = GetWindowLong(hwnd, -16);
            await WaitForUpdates(12);
            // Diagnostic-only WPF screenshot may read pixels internally. It is
            // outside the normal frame path and is not counted as a native read.
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Round(ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(_root);
            using (var stream = File.Create(Path.Combine(_output, "alpha-transport.png")))
            {
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(stream);
            }
            byte[] Pixel(double x)
            {
                var bytes = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * x), bitmap.PixelHeight / 2, 1, 1), bytes, 4, 0);
                return bytes;
            }
            var pixels = new[] { Pixel(.12), Pixel(.5), Pixel(.88) };
            var alpha = Near(pixels[0], [0, 0, 0, 0]) && Near(pixels[1], [26, 51, 102, 128]) && Near(pixels[2], [51, 102, 204, 255]);
            _animated = true;
            await WaitForUpdates(_bridge!.SurfaceUpdates + 20);
            Width = 720; Height = 450;
            await Task.Delay(100);
            await WaitForUpdates(_bridge.SurfaceUpdates + 8);
            var resize = _bridge.Image.PixelWidth == (int)Math.Round(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
            _bridge.Suspend();
            var suspendedAt = _bridge.Statistics.SubmittedFrames;
            await Task.Delay(100);
            var suspend = _bridge.Statistics.SubmittedFrames == suspendedAt && _bridge.Statistics.LeasedSlots == 0;
            _bridge.Resume();
            var generation = _bridge.Statistics.Generation;
            var recovered = _bridge.TryRecover();
            await WaitForUpdates(_bridge.SurfaceUpdates + 8);
            recovered &= _bridge.Statistics.Generation > generation;
            var checks = new
            {
                alphaTransport = alpha, resize, suspend, deviceRecovery = recovered,
                ownerPreserved = originalOwner != 0 && originalOwner == GetWindow(hwnd, 4) && originalOwner == DesktopWindowAttachment.FindDesktopHost(),
                topLevelStylePreserved = originalStyle == GetWindowLong(hwnd, -16) && (originalStyle & 0x40000000) == 0,
                nativeReadbackDisabled = _bridge.Statistics.CpuReadbacks == 0,
                boundedLeases = _bridge.Statistics.LeasedSlots <= 1
            };
            var passed = checks.alphaTransport && checks.resize && checks.suspend && checks.deviceRecovery && checks.ownerPreserved
                && checks.topLevelStylePreserved && checks.nativeReadbackDisabled && checks.boundedLeases;
            WriteReport(new { passed, checks, calibrationBgra = pixels, native = _bridge.Statistics,
                surfaceUpdates = _bridge.SurfaceUpdates, callbackIntervalsMilliseconds = _callbackIntervals, totalCallbacks = _totalCallbacks,
                keyboardWinDVerified = false, presentedFpsMeasured = false, crossProcessClickThroughVerified = false,
                lockScreenRecoveryVerified = false, mixedDpiVerified = false, diagnosticWpfScreenshots = 1,
                os = Environment.OSVersion.VersionString, renderTier = RenderCapability.Tier >> 16,
                limitation = "Synthetic source and diagnostic WPF screenshot. Callbacks/updates are not displayed FPS; no production material or wallpaper was migrated." });
            ExitCode = passed ? 0 : 1;
        }
        catch (Exception error) { Fail(error); }
        finally { Close(); }
    }
    private async Task WaitForUpdates(ulong target)
    {
        var timeout = Stopwatch.StartNew();
        while (_bridge!.SurfaceUpdates < target && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        if (_bridge.SurfaceUpdates < target) throw new TimeoutException($"Only {_bridge.SurfaceUpdates} GPU surfaces reached WPF; target {target}.");
    }
    private static bool Near(byte[] actual, byte[] expected) => actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 2);
    private void SaveLiveObservation() => File.WriteAllText(Path.Combine(_output, "live-observation.json"), JsonSerializer.Serialize(new
    {
        processId = Environment.ProcessId, hwnd = (long)new WindowInteropHelper(this).Handle,
        owner = (long)GetWindow(new WindowInteropHelper(this).Handle, 4), pointerPresses = _pointerPresses,
        native = _bridge?.Statistics, observableCapture = _observable
    }, new JsonSerializerOptions { WriteIndented = true }));
    private void WriteReport(object report)
    {
        File.WriteAllText(Path.Combine(_output, "probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        _reported = true;
    }
    internal void Fail(Exception error)
    {
        ExitCode = 1;
        WriteReport(new { passed = false, error = error.ToString(), os = Environment.OSVersion.VersionString,
            keyboardWinDVerified = false, presentedFpsMeasured = false,
            native = _bridge?.Statistics, updates = _bridge?.SurfaceUpdates,
            available = _bridge?.Image.IsFrontBufferAvailable, fallback = _bridge?.IsFallbackRequired,
            lastNativeError = _bridge?.LastError, callbackIntervals = _callbackIntervals, isVisible = IsVisible, windowState = WindowState.ToString() });
    }
    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= Render;
        _timeout?.Stop();
        if (!_reported) WriteReport(new { passed = false, manualObservationOnly = true, pointerPresses = _pointerPresses,
            native = _bridge?.Statistics, elapsedSeconds = _watch.Elapsed.TotalSeconds, totalCallbacks = _totalCallbacks,
            storedCallbackSamples = _callbackIntervals.Count, keyboardWinDVerified = false, presentedFpsMeasured = false });
        _bridge?.Dispose();
        base.OnClosed(e);
    }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
