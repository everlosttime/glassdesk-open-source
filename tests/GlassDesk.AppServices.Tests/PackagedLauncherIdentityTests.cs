using System.Diagnostics;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class PackagedLauncherIdentityTests
{
    private const string Identity = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Fact]
    public void ManagedShellLauncherMatchesPackagedWindowInsteadOfItsStubExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-launcher-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Launcher.cs");
            var executable = Path.Combine(root, "Launcher.exe");
            File.WriteAllText(source, "class Launcher { static void Main() { System.Diagnostics.Process.Start(\"explorer.exe\", @\"shell:AppsFolder\\" + Identity + "\"); } }");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "/nologo", "/target:winexe", "/out:" + executable, source }) start.ArgumentList.Add(arg);
            using var compiler = Process.Start(start)!;
            var output = compiler.StandardOutput.ReadToEnd();
            compiler.WaitForExit();
            Assert.True(compiler.ExitCode == 0, output);
            var entry = new AppEntry { Id = "stub", Kind = AppItemKind.Program, Target = executable };
            Assert.Equal(Identity, WindowMatcher.ResolveAumid(entry));
            var windows = new[]
            {
                new WindowIdentityInfo(123, "Calculator", 1, true, @"C:\WindowsApps\Calculator.exe", null, Identity),
                new WindowIdentityInfo(456, "Other", 2, false, @"C:\WindowsApps\Calculator.exe", null, "Other.App_family!App"),
            };
            var match = Assert.Single(WindowMatcher.MatchWindows(entry, windows, WindowMatcher.ResolveAumid(entry)));
            Assert.Equal((nint)123, match.Handle);
            Assert.True(match.IsMinimized);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AppsFolderShortcutWithoutExplicitIdentityResolvesItsShellTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), "GlassDesk-shell-identity-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            Assert.True(ShortcutWriter.Write(path, new("shell:AppsFolder\\" + Identity)));
            var entry = new AppEntry { Id = "shell", Kind = AppItemKind.Shortcut, Target = path };
            Assert.Equal(Identity, WindowMatcher.ResolveAumid(entry));
        }
        finally { File.Delete(path); }
    }
}
