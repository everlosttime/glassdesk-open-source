using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class ShellCaptureProbe
{
    internal static int Run(string output)
    {
        var candidates = new List<(nint Handle, string Class, bool DesktopView)>();
        EnumWindows((handle, _) =>
        {
            var name = ClassName(handle);
            if (name is not ("Progman" or "WorkerW") || !IsWindowVisible(handle)) return true;
            var hasDesktop = false;
            EnumChildWindows(handle, (child, _) =>
            {
                var childClass = ClassName(child);
                hasDesktop |= childClass == "SHELLDLL_DefView";
                if (childClass is "SHELLDLL_DefView" or "SysListView32") candidates.Add((child, childClass, true));
                return true;
            }, 0);
            candidates.Add((handle, name, hasDesktop));
            return true;
        }, 0);
        candidates.Add((GetDesktopWindow(), "DesktopWindow", false));
        var checks = new List<object>();
        var desktopSources = 0; var usableDesktopSources = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.DesktopView) desktopSources++;
            try
            {
                var captured = WgcWindowCapture.Capture(candidate.Handle, TimeSpan.FromSeconds(4), true);
                var opaquePixels = 0; var nonemptyPixels = 0;
                if (captured is not null)
                    for (var i = 3; i < captured.Pixels.Length; i += 4)
                    { if (captured.Pixels[i] == 255) opaquePixels++; if (captured.Pixels[i] != 0) nonemptyPixels++; }
                var usable = captured is not null && nonemptyPixels > 0;
                if (candidate.DesktopView && usable) usableDesktopSources++;
                checks.Add(new { windowClass = candidate.Class, containsDesktopView = candidate.DesktopView,
                    usable, captured?.Width, captured?.Height, opaquePixels, nonemptyPixels });
            }
            catch (Exception error)
            {
                // Characterize the bounded classic-GDI alternative in a disposable process.
                // PrintWindow is synchronous; never let an Explorer paint stall hang the caller indefinitely.
                string? legacy = null;
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--legacy-shell " + candidate.Handle.ToInt64())
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (child is not null)
                {
                    if (child.WaitForExit(4000)) legacy = child.StandardOutput.ReadToEnd().Trim();
                    else { child.Kill(); child.WaitForExit(); legacy = "Timed out; synchronous legacy paint is unsuitable."; }
                }
                checks.Add(new { windowClass = candidate.Class, containsDesktopView = candidate.DesktopView,
                    usable = false, validWindow = IsWindow(candidate.Handle),
                    visibleWindow = IsWindowVisible(candidate.Handle), hresult = $"0x{error.HResult:X8}",
                    legacyCharacterization = legacy, error = error.ToString() });
            }
        }
        var passed = desktopSources > 0 && usableDesktopSources == desktopSources;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed, desktopSources, usableDesktopSources,
            note = "Shell capture support only. No user pixels/images/titles are saved. Nonempty pixels do not prove wallpaper/icon correctness or scene equivalence.",
            checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(output));
        return passed ? 0 : 1;
    }

    private static string ClassName(nint handle) { var text = new StringBuilder(256); GetClassName(handle, text, text.Capacity); return text.ToString(); }
    private delegate bool EnumProc(nint handle, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint handle, EnumProc callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int count);
}
