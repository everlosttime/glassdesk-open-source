using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyAppearanceCustomization(string[] args)
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!File.Exists(Path.Combine(directory, ".full-host-acceptance-root"))
            || File.ReadAllText(Path.Combine(directory, ".full-host-acceptance-root")).Trim() != directory)
            throw new InvalidOperationException("An explicitly marked test package is required.");
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var evidence = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(evidence);
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "GlassDesk.dll"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml") });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var optionsType = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!;
        var options = optionsType.GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()])!;
        var hostType = assembly.GetType("GlassDesk.App.AppHost", true)!;
        var host = (IDisposable)Activator.CreateInstance(hostType, flags, null, [application, options], null)!;
        var shaderFailures = 0;
        EventHandler shaderFailure = (_, _) => shaderFailures++;
        System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += shaderFailure;
        try
        {
            var started = hostType.GetMethod("Start")!.Invoke(host, null)?.ToString();
            if (started != "Started") throw new InvalidOperationException("AppHost did not start: " + started);
            var settings = (SettingsWindow)hostType.GetField("_settings", flags)!.GetValue(host)!;
            var dock = (DockWindow)hostType.GetField("_dock", flags)!.GetValue(host)!;
            var launcher = (LauncherWindow)hostType.GetField("_launcher", flags)!.GetValue(host)!;
            hostType.GetMethod("ShowSettings", flags)!.Invoke(host, null);
            Pump(TimeSpan.FromMilliseconds(500));
            var placement = (ComboBox)settings.FindName("DockPlacementCombo")!;
            ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 1;
            Pump(TimeSpan.FromMilliseconds(200));
            placement.IsDropDownOpen = true;
            Pump(TimeSpan.FromMilliseconds(200));
            var popup = (Popup)placement.Template.FindName("PART_Popup", placement)!;
            var body = (PhoneBorder)popup.Child;
            Check("位置菜单背景不透明且与选择框等宽", "appearance/menu",
                body.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 255
                && body.ActualWidth >= placement.ActualWidth - 1,
                "opaque, width >= selector", $"{body.Background}; menu={body.ActualWidth}, combo={placement.ActualWidth}");
            CaptureElement(body, Path.Combine(evidence, "placement-menu.png"));
            placement.IsDropDownOpen = false;
            var size = (Slider)settings.FindName("DockScaleSlider")!;
            foreach (var scale in new[] { 2d / 3, .75, 1d, 1.5 })
            {
                size.Value = scale;
                Pump(TimeSpan.FromMilliseconds(400));
                var entry = (Button)dock.FindName("LauncherButton")!;
                Check($"Dock 整体大小 {scale:P0} 同步图标与栏高度", "appearance/scale",
                    Math.Abs(entry.ActualWidth - 48 * scale) < 1 && dock.ActualHeight >= 75 * scale,
                    $"icon={48 * scale:0.0}; height >= {75 * scale:0.0}", $"icon={entry.ActualWidth}; height={dock.ActualHeight}");
                settings.Hide();
                hostType.GetMethod("ShowLauncher", flags, null, Type.EmptyTypes, null)!.Invoke(host, null);
                Pump(TimeSpan.FromMilliseconds(600));
                Capture(dock, Path.Combine(evidence, $"dock-{scale:P0}.png"));
                launcher.CloseWithTransition();
                Pump(TimeSpan.FromMilliseconds(600));
                hostType.GetMethod("ShowSettings", flags)!.Invoke(host, null);
            }
            size.Value = 1;
            var material = (ComboBox)settings.FindName("AppearanceMaterialCombo")!;
            foreach (var index in new[] { 1, 0, 1 })
            {
                hostType.GetMethod("ShowSettings", flags)!.Invoke(host, null);
                ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 0;
                material.SelectedIndex = index;
                Pump(TimeSpan.FromMilliseconds(300));
                var expected = index == 1 ? GlassMaterialMode.FrostedGlass : GlassMaterialMode.AndroidLiquidGlass;
                var reloaded = new SettingsStore(Path.Combine(directory, "Data", "settings.json")); reloaded.Load();
                Check("玻璃选择保存并即时广播到主题", "appearance/material",
                    ThemeService.Current!.Appearance.GlassMaterial == expected && reloaded.Current.Appearance.GlassMaterial == expected,
                    expected.ToString(), reloaded.Current.Appearance.GlassMaterial.ToString());
                Capture(settings, Path.Combine(evidence, $"settings-{expected}.png"));
                settings.Hide();
                hostType.GetMethod("ShowLauncher", flags, null, Type.EmptyTypes, null)!.Invoke(host, null);
                var surface = (LiquidGlassSurface)launcher.FindName("GlassSurface")!;
                Check("切换材质后启动台提供完整背景", "appearance/reveal",
                    PumpUntil(() => launcher.IsVisible && SurfaceSnapshot(launcher).GetProperty("lastRenderMaterialReady").GetBoolean(), TimeSpan.FromSeconds(5)),
                    "material ready", SurfaceSnapshot(launcher).ToString());
                Pump(TimeSpan.FromMilliseconds(400));
                Check("材质切换后实时背景恢复", "appearance/live",
                    PumpUntil(() => surface.BackdropSource is GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene,
                        TimeSpan.FromSeconds(5)), "live desktop or compatible scene", surface.BackdropSource.ToString());
                Capture(launcher, Path.Combine(evidence, $"launcher-{expected}.png"));
                if (index == 1)
                {
                    var layers = new[] { "_transitionLayer", "_compatibilityLayer" }
                        .Select(name => (FrameworkElement)typeof(LiquidGlassSurface).GetField(name, flags)!.GetValue(surface)!)
                        .Where(layer => layer.Opacity > 0).ToArray();
                    Check("磨砂开合不使用液态折射着色器", "appearance/no-lens",
                        layers.Length > 0 && layers.All(layer => !(bool)layer.GetType().GetProperty("UsesGpuLens")!.GetValue(layer)!),
                        "no active GPU lens", $"{layers.Length} active material layers");
                }
                launcher.CloseWithTransition();
                Pump(TimeSpan.FromMilliseconds(600));
            }
            Check("无失效玻璃着色器", "appearance/shader", shaderFailures == 0, "0", shaderFailures.ToString());
            material.SelectedIndex = 0;
            hostType.GetMethod("ShowSettings", flags)!.Invoke(host, null);
            ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 1;
            Pump(TimeSpan.FromMilliseconds(200));
            if (args.Contains("--preview"))
            {
                File.WriteAllText(Path.Combine(evidence, "preview-ready"), "ready");
                PumpUntil(() => File.Exists(Path.Combine(evidence, "stop-preview")), TimeSpan.FromMinutes(10));
            }
        }
        catch (Exception error)
        {
            Check("完整宿主外观验收", "appearance/full-host", false, "no exception", error.ToString());
        }
        finally
        {
            System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered -= shaderFailure;
            host.Dispose(); application.Shutdown();
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion,
                driver = "Complete production AppHost, isolated profile copy, actual WPF controls and rendered windows",
                liveProfileUsed = false, shaderFailures, passed = Cases.Count - _failed, failed = _failed, cases = Cases
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;
    }

    private static void CaptureElement(FrameworkElement element, string path)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
