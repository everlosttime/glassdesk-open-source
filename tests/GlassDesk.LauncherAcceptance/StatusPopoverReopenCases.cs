using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyStatusPopoverReopen(StatusBarWindow bar, GlassDeskDataStore data, string report, bool software, string material)
    {
        var appearance = data.Settings.Current.Appearance;
        appearance.ControlCenterGlassMaterial = Enum.Parse<GlassMaterialMode>(material);
        ThemeService.Current!.Update(appearance);
        Pump(TimeSpan.FromSeconds(2));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", flags)!.GetValue(bar)!;
        var calendar = (DateNotificationWindow)typeof(StatusBarWindow).GetField("_dateNotifications", flags)!.GetValue(bar)!;
        var anchor = (PixelRect)typeof(StatusBarWindow).GetField("_restingRect", flags)!.GetValue(bar)!;
        var display = DisplayService.Primary()!;
        var rounds = new List<object>();
        foreach (var popup in new GlassWindow[] { center, calendar })
        {
            var handle = new WindowInteropHelper(popup).EnsureHandle();
            if (software && HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            if (popup is ControlCenterWindow first) first.ShowNear(anchor); else calendar.ShowNear(anchor);
            Pump(TimeSpan.FromMilliseconds(500));
            var bounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(popup).Handle)!.Value;
            var crop = new PixelBounds(bounds.X - display.Bounds.X + display.ToPixels(40),
                bounds.Y - display.Bounds.Y + display.ToPixels(45),
                bounds.X - display.Bounds.X + display.ToPixels(40) + 2,
                bounds.Y - display.Bounds.Y + display.ToPixels(180));
            using var sampler = new MonitorCaptureReference.MotionSampler(new WindowInteropHelper(popup).Handle, crop);
            Pump(TimeSpan.FromMilliseconds(300));
            popup.Hide(); Pump(TimeSpan.FromMilliseconds(500));
            // Only a narrow strip through this fixture's own popup is read; no desktop image is saved.
            for (var round = 0; round < 5; round++)
            {
                var before = sampler.Snapshot().Length;
                var showTicks = Stopwatch.GetTimestamp();
                if (popup is ControlCenterWindow c) c.ShowNear(anchor); else calendar.ShowNear(anchor);
                var samples = new List<object>();
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 900)
                {
                    samples.Add(new { milliseconds = watch.Elapsed.TotalMilliseconds, popup.IsVisible,
                        popup.IsActive, popup.Opacity, popup.PresentationOpacity, rootOpacity = ((UIElement)popup.Content).Opacity });
                    Pump(TimeSpan.FromMilliseconds(8));
                }
                var pixels = sampler.Snapshot().Skip(Math.Max(0, before - 2)).ToArray();
                var baseline = pixels.LastOrDefault(p => p.SourceQpc < showTicks)?.CenterBgra;
                var final = pixels.LastOrDefault()?.CenterBgra;
                var peak = 0d;
                var flash = false;
                var measurable = false;
                if (baseline is { } background && final is { } settled)
                {
                    var delta = Channels(settled).Zip(Channels(background), (a, b) => a - b).ToArray();
                    var norm = delta.Sum(v => v * v);
                    measurable = norm >= 100;
                    foreach (var pixel in pixels.Where(p => p.SourceQpc >= showTicks))
                    {
                        if (norm < 100) break;
                        var distance = Channels(pixel.CenterBgra).Zip(Channels(background), (a, b) => a - b)
                            .Zip(delta, (a, b) => a * b).Sum() / norm;
                        if (peak > .8 && distance < .3) flash = true;
                        peak = Math.Max(peak, distance);
                    }
                }
                Check($"{popup.GetType().Name}重复打开{round + 1}无旧帧闪现", "status/popover-reopen",
                    measurable && !flash && sampler.Failure is null && pixels.Length > 5,
                    "no settled frame followed by concealed frame", $"flash={flash},frames={pixels.Length},capture={sampler.Failure}");
                rounds.Add(new { kind = popup.GetType().Name, round, showTicks, flash, samples, pixels, sampler.Failure });
                popup.Hide(); Pump(TimeSpan.FromMilliseconds(350));
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, material, software, rounds, cases = Cases }, new JsonSerializerOptions { WriteIndented = true }));
        return _failed == 0 ? 0 : 1;
    }

    private static double[] Channels(uint bgra) => [(bgra & 255), ((bgra >> 8) & 255), ((bgra >> 16) & 255)];
}
