using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.Platform.Shell;

internal static class DesktopIconsProbe
{
    internal static int Run(string output)
    {
        using var icons = new DesktopIconSession();
        var original = icons.AreIconsVisible();
        var rounds = new List<object>();
        var recurrence = new Dictionary<(bool Visible, bool Icon, int X, int Y), int>();
        var pass = true; var restored = false; string? failure = null;
        var hiddenCompleted = 0; var shownCompleted = 0;
        var sufficient = true;
        try
        {
            if (original != false) throw new InvalidOperationException("Expected hidden desktop icons; current state is preserved.");
            DesktopReferenceCapture.EnsurePhysicalCoordinates();
            var shell = GetShellWindow();
            if (shell == 0) throw new InvalidOperationException("Shell window is unavailable.");
            foreach (var visible in new[] { false, true })
            {
                if (!icons.TrySetVisible(visible, out var error)) throw new InvalidOperationException(error);
                Program.Pump(TimeSpan.FromMilliseconds(500));
                for (var round = 1; round <= 30; round++)
                {
                    if (icons.AreIconsVisible() != visible) throw new InvalidOperationException("Desktop icon visibility changed during the test.");
                    // A stalled Explorer render never prevents the main STA thread restoring hidden icons.
                    var pending = Task.Run(() =>
                    {
                        DesktopReferenceCapture.EnsurePhysicalCoordinates();
                        var regions = visible ? DesktopReferenceCapture.IconBounds(shell) : new List<PixelBounds>();
                        var frames = DesktopReferenceCapture.Capture(shell);
                        if (visible && !regions.SequenceEqual(DesktopReferenceCapture.IconBounds(shell)))
                            throw new InvalidOperationException("Desktop icon layout moved during sampling.");
                        var points = DesktopReferenceCapture.Candidates(shell, frames, regions, 0);
                        var stats = PixelComparison.Compare(frames.Before, frames.Source, frames.After, points);
                        return (frames, stats, itemCount: regions.Count);
                    });
                    var deadline = Stopwatch.StartNew();
                    while (!pending.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(8))
                        Program.Pump(TimeSpan.FromMilliseconds(10));
                    if (!pending.IsCompleted) throw new TimeoutException("Desktop triplet exceeded eight seconds; run is inconclusive.");
                    var (frames, stats, itemCount) = pending.GetAwaiter().GetResult();
                    if (icons.AreIconsVisible() != visible) throw new InvalidOperationException("Desktop icon state changed across the triplet.");
                    var enough = stats.Wallpaper.Valid >= 50 && (!visible || stats.Icons.Valid >= 50);
                    var ok = enough && stats.Wallpaper.Mismatched == 0 && (!visible || stats.Icons.Mismatched == 0);
                    sufficient &= enough; pass &= ok;
                    foreach (var p in stats.Mismatches)
                    {
                        var key = (visible,p.Icon,p.X,p.Y);
                        recurrence[key] = recurrence.GetValueOrDefault(key) + 1;
                    }
                    rounds.Add(new { iconsVisible = visible, round, passed = ok, sufficientSamples = enough, itemCount,
                        screenBefore = Summary(frames.Before), printWindow = Summary(frames.Source), screenAfter = Summary(frames.After),
                        tripletMs = (frames.After.EndTicks-frames.Before.StartTicks)*1000d/Stopwatch.Frequency,
                        frames.ShadowGuardPixels, wallpaper = stats.Wallpaper, icons = visible ? stats.Icons : null });
                    if (visible) shownCompleted++; else hiddenCompleted++;
                    Console.WriteLine($"{(visible ? "shown" : "hidden")} {round}/30: wallpaper {stats.Wallpaper.Valid} valid/{stats.Wallpaper.Mismatched} mismatch, icons {stats.Icons.Valid} valid/{stats.Icons.Mismatched} mismatch");
                    Program.Pump(TimeSpan.FromMilliseconds(50));
                }
            }
        }
        catch (Exception error) { failure = error.Message; pass = false; }
        finally
        {
            if (original == false)
            {
                if (!icons.TrySetVisible(false, out var error)) failure = (failure ?? "") + " Restore failed: " + error;
                Program.Pump(TimeSpan.FromMilliseconds(300));
                restored = icons.AreIconsVisible() == false;
            }
        }
        pass &= restored && hiddenCompleted == 30 && shownCompleted == 30;
        var repeated = recurrence.Count(p => p.Value >= 3);
        var decision = failure != null || !restored || !sufficient || hiddenCompleted != 30 || shownCompleted != 30
            ? "inconclusive" : pass ? "pixel-fidelity-passed" : repeated > 0 ? "reject-desktop-source" : "inconclusive-nonrepeating-differences";
        var repeatStats = new[] { false, true }.Select(visible => new {
            iconsVisible = visible,
            wallpaperRepeatedMismatchPoints = recurrence.Count(p => p.Key.Visible == visible && !p.Key.Icon && p.Value >= 3),
            iconRepeatedMismatchPoints = recurrence.Count(p => p.Key.Visible == visible && p.Key.Icon && p.Value >= 3),
            maximumRepeatCount = recurrence.Where(p => p.Key.Visible == visible).Select(p => p.Value).DefaultIfEmpty().Max()
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new { passed = pass, decision, originalIconsVisible = original,
            restoredHiddenVisibility = restored, hiddenCompleted, shownCompleted, failure, stopwatchFrequency = Stopwatch.Frequency,
            channelOrder = "B,G,R", stabilityTolerance = 0, comparisonTolerance = 2, minimumSamplesPerCategory = 50,
            repetitionThreshold = 3, repeatStats, rounds,
            note = "Only statistics saved. Triplets captured in this process. Icon category is anonymous item bounds including labels. Visible non-desktop windows and 128px at 96 DPI shadow guard excluded at all capture boundaries; cursor excluded. Screen references are GDI BitBlt with CAPTUREBLT; this does not prove HDR/scRGB, transient between-boundary overlays or screenshot/recording fidelity. No production integration or sustained performance acceptance." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Decision: {decision}; icons hidden: {restored}; report: {Path.GetFullPath(output)}");
        return pass ? 0 : 1;
    }

    private static object Summary(PixelFrame f) => new { physicalScreenBounds = f.Bounds, f.StartedUtc, f.StartTicks, f.EndTicks,
        elapsedMs = (f.EndTicks-f.StartTicks)*1000d/Stopwatch.Frequency };
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
}
