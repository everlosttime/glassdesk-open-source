using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--source", var sourceKind]) return Source(sourceKind);
        if (args is ["--identity", var identityReport]) return CaptureCapability.WriteReport(identityReport, false);
        if (args is ["--request-borderless", var permissionReport]) return CaptureCapability.WriteReport(permissionReport, true);
        if (args is ["--shell-probe", var shellReport]) return ShellCaptureProbe.Run(shellReport);
        if (args is ["--legacy-shell", var legacyHandle]) return LegacyShellCapture.Run(nint.Parse(legacyHandle, CultureInfo.InvariantCulture));
        if (args is ["--legacy-shell-frame", var frameHandle]) return LegacyShellCapture.WriteFrame(nint.Parse(frameHandle, CultureInfo.InvariantCulture));
        if (args is ["--desktop-icons-probe", var iconReport]) return DesktopIconsProbe.Run(iconReport);
        if (args is ["--desktop-source-performance", var performanceReport]) return DesktopSourcePerformance.Run(performanceReport);
        if (args is ["--async-backdrop-benchmark", var asyncReport]) return AsyncBackdropBenchmark.Run(asyncReport);
        if (args is ["--scene-reference-probe", var sceneReport]) return SceneReferenceProbe.Run(sceneReport);
        if (args is ["--scene-popup-probe", var popupReport]) return SceneReferenceProbe.Run(popupReport,true);
        if (args is ["--scene-secondary-probe", var secondaryReport]) return SceneReferenceProbe.Run(secondaryReport,true,true);
        if (args is ["--scene-popup-printwindow-probe", var printedPopupReport]) return SceneReferenceProbe.Run(printedPopupReport,true,false,true);
        var reportIndex = Array.IndexOf(args, "--report");
        var output = reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : "spike-report.json";
        var borderless = args.Contains("--borderless");
        var checks = new List<object>();
        bool passed = true;
        foreach (var kind in new[] { "opaque", "alpha", "system" })
        {
            try
            {
                using var source = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--source " + kind)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                })!;
                Window? foreground = null;
                try
                {
                    var ready = source.StandardOutput.ReadLine();
                    if (ready is null || !ready.StartsWith("READY|", StringComparison.Ordinal))
                        throw new InvalidOperationException("Controlled source did not start.");
                    var sourceHandles = ready.Split('|');
                    var handle = nint.Parse(sourceHandles[1], CultureInfo.InvariantCulture);
                    var backingHandle = nint.Parse(sourceHandles[2], CultureInfo.InvariantCulture);
                    // This visible magenta foreground MUST NOT enter a window-only capture.
                    foreground = new Window { Title = "GlassDesk backdrop spike foreground", Width = 160,
                        Height = 120, Left = 110, Top = 110, WindowStyle = WindowStyle.None,
                        AllowsTransparency = true, Background = Brushes.Magenta, Topmost = true,
                        ShowActivated = false, ShowInTaskbar = false };
                    foreground.Show();
                    Pump(TimeSpan.FromMilliseconds(300));
                    var frame = WgcWindowCapture.Capture(handle, TimeSpan.FromSeconds(5), borderless);
                    var pixel = frame?.Center;
                    // Explicit literal oracle: solid green or premultiplied 50% green.
                    var expected = kind == "alpha" ? new byte[] { 0, 128, 0, 128 } : new byte[] { 0, 255, 0, 255 };
                    var ok = pixel is not null && pixel.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 2);
                    checks.Add(new { scenario = kind, passed = ok, expectedBgra = expected.Select(value => (int)value).ToArray(),
                        actualBgra = pixel?.Select(value => (int)value).ToArray(), frame?.Width, frame?.Height,
                        reason = ok ? "Window-only source preserves the controlled pixels." : "Source pixels/alpha differ; do not integrate." });
                    passed &= ok;
                    if (frame is not null)
                    {
                        var backingFrame = WgcWindowCapture.Capture(backingHandle, TimeSpan.FromSeconds(5), borderless)
                            ?? throw new InvalidOperationException("Controlled backing capture is missing.");
                        var bounds = WgcWindowCapture.ReadScreenBounds(handle);
                        var backingBounds = WgcWindowCapture.ReadScreenBounds(backingHandle);
                        // This point is below the magenta foreground, so the real desktop is an independent oracle.
                        var probeX = bounds.X + bounds.Width / 2; var probeY = bounds.Y + bounds.Height * 2 / 3;
                        var region = new ScreenRect(probeX - 16, probeY - 16, 32, 32);
                        var composited = GpuBackdropCompositor.Compose([
                            new CapturedLayer(backingFrame, backingBounds), new CapturedLayer(frame, bounds)], region, 32, 32);
                        var compositePixel = composited?.Center;
                        var realDesktop = WgcWindowCapture.ReadScreenRgb(probeX, probeY);
                        var expectedComposite = kind == "alpha" ? new byte[] { 127, 255, 127, 255 } : new byte[] { 0, 255, 0, 255 };
                        var sceneOk = compositePixel is not null
                            && compositePixel.Zip(expectedComposite).All(pair => Math.Abs(pair.First - pair.Second) <= 2)
                            && Math.Abs(compositePixel[2] - realDesktop[0]) <= 2
                            && Math.Abs(compositePixel[1] - realDesktop[1]) <= 2
                            && Math.Abs(compositePixel[0] - realDesktop[2]) <= 2;
                        checks.Add(new { scenario = kind + "-gpu-scene", passed = sceneOk,
                            expectedBgra = expectedComposite.Select(value => (int)value).ToArray(),
                            actualBgra = compositePixel?.Select(value => (int)value).ToArray(), desktopReferenceRgb = realDesktop,
                            note = "Static controlled scene. Capture uploads/readbacks are not the proposed production cadence." });
                        passed &= sceneOk;
                    }
                    if (kind == "system" && frame is not null)
                    {
                        var observation = WgcWindowCapture.InspectSystemShadow(handle, frame);
                        checks.Add(new { scenario = "system-shadow-source-completeness", passed = !observation.MissingShadowPixels,
                            observation, reason = observation.MissingShadowPixels
                                ? "DWM shadow pixels exist outside the captured source extent. Window textures alone do not reconstruct the desktop. Do not integrate."
                                : "No missing shadow pixels detected at the controlled probe points; not proof of complete desktop reconstruction." });
                        passed &= !observation.MissingShadowPixels;
                    }
                    source.StandardInput.WriteLine("blue-and-resize"); source.StandardInput.Flush();
                    if (source.StandardOutput.ReadLine() != "BLUE-RESIZED")
                        throw new InvalidOperationException("Controlled source update did not complete.");
                    Program.Pump(TimeSpan.FromMilliseconds(200));
                    var changed = WgcWindowCapture.Capture(handle, TimeSpan.FromSeconds(5), borderless);
                    var changedPixel = changed?.Center;
                    var changedExpected = kind == "alpha" ? new byte[] { 128, 0, 0, 128 } : new byte[] { 255, 0, 0, 255 };
                    var updated = changedPixel is not null && changedPixel.Zip(changedExpected).All(pair => Math.Abs(pair.First - pair.Second) <= 2)
                        && changed!.Width > frame!.Width && changed.Height < frame.Height;
                    checks.Add(new { scenario = kind + "-color-and-size-change", passed = updated,
                        expectedBgra = changedExpected.Select(value => (int)value).ToArray(),
                        actualBgra = changedPixel?.Select(value => (int)value).ToArray(), changed?.Width, changed?.Height });
                    passed &= updated;
                }
                finally
                {
                    foreground?.Close();
                    if (!source.HasExited) { source.StandardInput.WriteLine("exit"); source.StandardInput.Flush(); }
                    if (!source.WaitForExit(3000)) source.Kill();
                }
            }
            catch (Exception error)
            {
                passed = false;
                checks.Add(new { scenario = kind, passed = false, error = error.ToString() });
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed, borderlessRequested = borderless, os = Environment.OSVersion.Version.ToString(),
            note = "Controlled synthetic windows only. This does not validate desktop reconstruction, borderless capture, external recording or presented FPS.",
            checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(output));
        return passed ? 0 : 1;
    }

    private static int Source(string kind)
    {
        DesktopReferenceCapture.EnsurePhysicalCoordinates();
        var backing = new Window { Title = "GlassDesk controlled white backing", Width = 560, Height = 400,
            Left = 50, Top = 50, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.White, Topmost = true, ShowActivated = false, ShowInTaskbar = false };
        backing.Show();
        var source = new Window { Title = "GlassDesk controlled WGC source " + kind,
            Width = 320, Height = 240, Left = 100, Top = 100,
            WindowStyle = kind == "system" ? WindowStyle.SingleBorderWindow : WindowStyle.None,
            AllowsTransparency = kind != "system", Topmost = true,
            Background = new SolidColorBrush(Color.FromArgb(kind == "alpha" ? (byte)128 : (byte)255, 0, 255, 0)),
            ShowActivated = kind=="system", ShowInTaskbar = false };
        source.Show();
        if(kind=="system") source.Activate();
        Popup? popup=null;
        Pump(TimeSpan.FromMilliseconds(200));
        Console.WriteLine("READY|" + new WindowInteropHelper(source).Handle.ToInt64().ToString(CultureInfo.InvariantCulture)
            + "|" + new WindowInteropHelper(backing).Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        _ = Task.Run(() =>
        {
            while (Console.ReadLine() is { } command)
            {
                if (command == "blue-and-resize")
                {
                    source.Dispatcher.Invoke(() =>
                    {
                        source.Background = new SolidColorBrush(Color.FromArgb(kind == "alpha" ? (byte)128 : (byte)255, 0, 0, 255));
                        source.Width = 400; source.Height = 200;
                    });
                    Console.WriteLine("BLUE-RESIZED"); Console.Out.Flush();
                    continue;
                }
                if(command=="move")
                {
                    source.Dispatcher.Invoke(()=>{source.Left=125;source.Top=115;});
                    Console.WriteLine("MOVED");Console.Out.Flush();continue;
                }
                if(command is "popup" or "popup-outside")
                {
                    long popupHandle=0;
                    source.Dispatcher.Invoke(()=>
                    {
                        if(popup is not null) popup.IsOpen=false;
                        popup=new Popup {AllowsTransparency=true,PlacementTarget=source,Placement=PlacementMode.Relative,
                            HorizontalOffset=command=="popup-outside" ? source.ActualWidth-35 : 180,VerticalOffset=80,StaysOpen=true,PopupAnimation=PopupAnimation.None,
                            Child=new Border {Width=130,Height=70,CornerRadius=new CornerRadius(12),Background=Brushes.Yellow}};
                        popup.IsOpen=true;
                        if(PresentationSource.FromVisual(popup.Child) is HwndSource popupSource) popupHandle=popupSource.Handle.ToInt64();
                    });
                    Console.WriteLine("POPUP|"+popupHandle);Console.Out.Flush();continue;
                }
                if(command.StartsWith("restore-foreground|",StringComparison.Ordinal))
                {
                    var target=nint.Parse(command.Split('|')[1],CultureInfo.InvariantCulture);
                    source.Dispatcher.Invoke(()=>SetForegroundWindow(target));
                    Console.WriteLine("RESTORED");Console.Out.Flush();continue;
                }
                break;
            }
            source.Dispatcher.BeginInvoke(new Action(() => { if(popup is not null) popup.IsOpen=false; source.Close(); backing.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }));
        });
        Dispatcher.Run();
        return 0;
    }

    internal static void Pump(TimeSpan duration)
    {
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
