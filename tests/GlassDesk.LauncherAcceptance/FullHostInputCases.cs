using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using GlassDesk.Launcher;
using GlassDesk.UI;

internal static partial class Program
{
    // Runs the actual AppHost on an explicitly staged, disposable profile copy.
    // No product option, live profile override, or modal/deactivation suppression.
    private static int VerifyFullHostInput(string[] args)
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var marker = Path.Combine(directory, ".full-host-acceptance-root");
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != directory)
            throw new InvalidOperationException("Full-host acceptance requires an explicitly marked test package.");
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var evidence = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(evidence);
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "GlassDesk.dll"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml") });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var optionsType = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!;
        var options = optionsType.GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()])!;
        var hostType = assembly.GetType("GlassDesk.App.AppHost", true)!;
        var host = (IDisposable)Activator.CreateInstance(hostType, instance, null, [application, options], null)!;
        var shaderFailures = 0;
        EventHandler shaderFailure = (_, _) => { shaderFailures++; BackdropRecovery.UseBasicRendering(); };
        System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += shaderFailure;
        try
        {
            var result = hostType.GetMethod("Start")!.Invoke(host, null)!.ToString();
            if (result != "Started") throw new InvalidOperationException("Full AppHost did not start: " + result);
            var window = (LauncherWindow)hostType.GetField("_launcher", instance)!.GetValue(host)!;
            // Discoverable by the supported computer-use client; other behavior stays production.
            window.ShowInTaskbar = true;
            hostType.GetMethod("ShowLauncher", instance, null, Type.EmptyTypes, null)!.Invoke(host, null);
            Pump(TimeSpan.FromSeconds(1));
            var grid = (UniformGrid)window.FindName("TileGrid")!;
            VerifyGlassInputContinuity(window, grid, evidence, Argument(args, "--fixture-exe"), nativePageClicks: true);
            Check("实际宿主未强制保持启动台打开", "glass/full-host", !HasModalKeepOpen(window), "production deactivation", HasModalKeepOpen(window).ToString());
            Check("无失效玻璃着色器", "glass/full-host", shaderFailures == 0, "0", shaderFailures.ToString());
            Capture(window, Path.Combine(evidence, "full-host-launcher.png"));
            BackdropRecovery.ExportDiagnosticReport(Path.Combine(evidence, "backdrop-diagnostics.json"));
        }
        catch (Exception error)
        {
            Check("完整宿主验收", "glass/full-host", false, "no exception", error.ToString());
        }
        finally
        {
            System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered -= shaderFailure;
            host.Dispose(); application.Shutdown();
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion,
                driver = "Complete production AppHost, copied profile, native mouse movement/drag/page clicks; taskbar visibility enabled only for observation.",
                liveProfileUsed = false, modalKeepOpenSuppressed = false, shaderFailures,
                passed = Cases.Count - _failed, failed = _failed, cases = Cases
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;
    }

    private static bool HasModalKeepOpen(LauncherWindow window) =>
        (int)typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! != 0;
}
