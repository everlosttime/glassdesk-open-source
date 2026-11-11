using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.Platform.Shell;

internal static class DesktopSourcePerformance
{
    internal static int Run(string output)
    {
        object report; var completed = false;
        try
        {
            using var icons = new DesktopIconSession();
            var iconsVisibleBefore = icons.AreIconsVisible();
            var shell = GetShellWindow();
            if (shell == 0) throw new InvalidOperationException("Shell unavailable.");
            var pending = Task.Run(() => DesktopReferenceCapture.MeasureContinuous(shell,TimeSpan.FromSeconds(30),60));
            var deadline = Stopwatch.StartNew();
            while (!pending.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(40)) Program.Pump(TimeSpan.FromMilliseconds(10));
            if (!pending.IsCompleted) throw new TimeoutException("Continuous capture exceeded 40 seconds.");
            report = new { completed = true, iconsVisibleBefore, iconsVisibleAfter = icons.AreIconsVisible(),
                statistics = pending.GetAwaiter().GetResult() }; completed = true;
        }
        catch (Exception error) { report = new { completed = false, error = error.Message }; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine(File.ReadAllText(output));
        return completed ? 0 : 1;
    }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
}
