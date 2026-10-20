using System.IO;

namespace GlassDesk.Dock.Tests;

internal static class CompatibilityProbePath
{
    internal static string File(string name)
    {
        var built = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../GlassDesk.CompatibilityProbe/bin/Release/net10.0-windows10.0.26100.0", name));
        if (System.IO.File.Exists(built)) return built;
        var adjacent = Path.Combine(AppContext.BaseDirectory, "Probe", name);
        if (System.IO.File.Exists(adjacent)) return adjacent;
        return built;
    }
}
