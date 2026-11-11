using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.App.Updates;
using GlassDesk.AppServices.Updates;
using GlassDesk.Settings;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args[0] == "--network") return NetworkAcceptance(args[1], args[2],
            args.Length > 3 ? Version.Parse(args[3]) : new Version(0, 2, 9)).GetAwaiter().GetResult();
        if (args[0] == "--desktop-preview") return DesktopPreview(args[1]);
        if (args[0] == "--control-center-preview") return ControlCenterPreview(args[1]);
        if (args[0] == "--brand-preview") return BrandPreview(args[1]);
        if (args[0] == "--cloud-downloads") return CloudDownloadsAcceptance(args[1]);
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        var checks = new List<object>();
        var passed = true;
        Window? open = null;
        try
        {
            var settings = new SettingsWindow();
            open = settings;
            var clicked = false;
            settings.CheckForUpdatesRequested += (_, _) => clicked = true;
            settings.ShowUpdateStatus("0.2.9", "Checking", checking: true);
            var check = (Button)settings.FindName("CheckUpdateButton");
            Check("Manual check is disabled during a pending check", !check.IsEnabled);
            settings.ShowUpdateStatus("0.2.9", "Failed - retry");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Manual check routes to the host and can retry after failure", check.IsEnabled && clicked);
            Check("Settings display product version rather than assembly identity", ((TextBlock)settings.FindName("ProductVersionText")).Text == "GlassDesk 0.2.9");
            settings.Close();

            var release = new UpdateRelease(new Version(0, 2, 10), "GlassDesk-0.2.10-win-x64-Setup.exe",
                new Uri("https://github.com/everlosttime/glassdesk-updates/releases/download/v0.2.10/GlassDesk-0.2.10-win-x64-Setup.exe"),
                new string('a', 64), 4096, "本次更新\n• 增加启动检查和手动检查更新\n• 后台下载显示百分比，可取消\n• 安装后自动重新打开，保留配置和图标\n\n" + string.Concat(Enumerable.Repeat("详细更新说明：下载完成并校验通过后才执行更新。\n", 22)),
                new Uri("https://github.com/everlosttime/glassdesk-updates/releases/tag/v0.2.10"));
            var calls = 0;
            CancellationToken token = default;
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var prompt = new UpdateWindow(release, true, (progress, cancellation) =>
            {
                calls++; token = cancellation;
                cancellation.Register(() => completion.TrySetCanceled(cancellation));
                progress.Report(62);
                return completion.Task;
            });
            open = prompt;
            prompt.ShowActivated = false;
            prompt.Show();
            Pump(100);
            SaveImage(prompt, Path.Combine(output, "update-prompt.png"));
            var update = Find<Button>(prompt, "立即更新 GlassDesk");
            var later = Find<Button>(prompt, "稍后更新 GlassDesk");
            var bar = Find<ProgressBar>(prompt, "更新下载进度");
            var status = Find<TextBlock>(prompt, "更新下载状态");
            update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(60);
            Check("Download disables duplicate installs and renders 62 percent", calls == 1 && !update.IsEnabled && bar.Value == 62 && status.Text.Contains("62%"));
            SaveImage(prompt, Path.Combine(output, "update-downloading.png"));
            completion.SetException(new IOException("Connection interrupted"));
            Pump(100);
            Check("Failed downloads keep the application available and offer retry", update.IsEnabled && status.Text.Contains("Connection interrupted"));
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(30);
            later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(60);
            Check("Cancel closes the prompt and cancels the actual pending operation", !prompt.IsVisible && token.IsCancellationRequested && calls == 2);

            using var portable = new UpdateWindow(release, false, (_, _) => throw new InvalidOperationException("Portable cannot install"));
            open = portable;
            portable.ShowActivated = false;
            portable.Show();
            Check("Portable copy cannot overwrite an installed program", !Find<Button>(portable, "立即更新 GlassDesk").IsEnabled);
            portable.Close();

            using var installing = new UpdateWindow(release, true, (_, _) => Task.CompletedTask);
            open = installing;
            installing.ShowActivated = false;
            installing.Show();
            installing.MarkInstalling();
            Check("Installation handoff disables cancel and explains restart", !Find<Button>(installing, "稍后更新 GlassDesk").IsEnabled
                && Find<TextBlock>(installing, "更新下载状态").Text.Contains("重新打开"));
            installing.Close();
        }
        catch (Exception ex) { passed = false; checks.Add(new { name = "Unexpected failure", passed = false, error = ex.ToString() }); }
        finally
        {
            open?.Close();
            app.Shutdown();
            File.WriteAllText(Path.Combine(output, "ui-report.json"), JsonSerializer.Serialize(new { passed, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;

        void Check(string name, bool success) { if (!success) passed = false; checks.Add(new { name, passed = success }); Console.WriteLine($"{(success ? "PASS" : "FAIL")}: {name}"); }
    }

    private static int CloudDownloadsAcceptance(string output)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        var checks = new List<object>();
        var passed = true;
        Window? window = null;
        const string configuration = """
            {"cloudDownloads":[
              {"name":"夸克网盘","url":"https://pan.quark.cn/s/037d0cd5080b"},
              {"name":"百度网盘","url":"https://pan.baidu.com/s/1X_TFD0GMp8RJsRLNISjy_w?pwd=0a53","extractionCode":"0a53"}
            ]}
            """;
        try
        {
            var settings = new SettingsWindow();
            window = settings;
            settings.ShowUpdateStatus("1.4.2", "正在检查更新…", checking: true);
            var button = settings.FindName("CloudDownloadButton") as Button;
            Check("Website download entry remains available while GitHub check is pending", button?.IsEnabled == true && Equals(button.Content, "下载入口"));
            var requested = false;
            var requestEvent = typeof(SettingsWindow).GetEvent("CloudDownloadsRequested");
            Check("Settings expose an independent download entry request", requestEvent is not null);
            if (requestEvent is not null && button is not null)
            {
                requestEvent.AddEventHandler(settings, new EventHandler((_, _) => requested = true));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("Download entry routes to the host without checking GitHub", requested);
                settings.ShowUpdateStatus("1.4.2", "无法检查更新");
                Check("Download entry remains available after a failed check", button.IsEnabled);
            }
            settings.ShowActivated = false;
            settings.Show();
            ((ListBox)settings.FindName("SettingsPageList")).SelectedItem = "关于";
            Pump(100);
            SaveImage(settings, Path.Combine(output, "cloud-settings.png"));
            settings.Close();
            var type = typeof(UpdateWindow).Assembly.GetType("GlassDesk.App.Updates.CloudDownloadWindow");
            Check("Cloud download chooser is implemented", type is not null);
            if (type is not null)
            {
                var readConfiguration = typeof(UpdateWindow).Assembly.GetType("GlassDesk.App.AppHost")!
                    .GetMethod("ReadCloudDownloadConfiguration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                var sourceFile = Path.Combine(AppContext.BaseDirectory, "update-source.json");
                var original = File.Exists(sourceFile) ? File.ReadAllText(sourceFile) : null;
                string activeConfiguration;
                try
                {
                    activeConfiguration = (string)readConfiguration.Invoke(null, null)!;
                    File.WriteAllText(sourceFile, "{\"releasesApi\":\"https://unreachable.invalid/releases/latest\"}");
                    var legacy = (string)readConfiguration.Invoke(null, null)!;
                    File.Delete(sourceFile);
                    var missing = (string)readConfiguration.Invoke(null, null)!;
                    using var legacyDocument = JsonDocument.Parse(legacy);
                    using var missingDocument = JsonDocument.Parse(missing);
                    Check("Legacy or missing source configuration uses bundled cloud links without network access",
                        legacyDocument.RootElement.GetProperty("cloudDownloads").GetArrayLength() == 2
                        && missingDocument.RootElement.GetProperty("cloudDownloads").GetArrayLength() == 2);
                }
                finally
                {
                    if (original is null) File.Delete(sourceFile); else File.WriteAllText(sourceFile, original);
                }
                var opened = new List<Uri>();
                var copied = new List<string>();
                window = Create(null, null, opened.Add, copied.Add);
                window.Show();
                Pump(80);
                Check("Opening the chooser does not launch a page or change the clipboard", opened.Count == 0 && copied.Count == 0);
                Check("Unknown remote version is not claimed to be the latest", !Find<TextBlock>(window, "网盘下载标题").Text.Contains("最新"));
                Find<Button>(window, "打开夸克网盘").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<Button>(window, "打开百度网盘").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("Each provider opens its own supplied HTTPS share URL", opened.Select(x => x.AbsoluteUri).SequenceEqual(new[] {
                    "https://pan.quark.cn/s/037d0cd5080b", "https://pan.baidu.com/s/1X_TFD0GMp8RJsRLNISjy_w?pwd=0a53" }));
                Check("Opening a provider does not overwrite the clipboard", copied.Count == 0);
                Find<Button>(window, "复制百度网盘提取码").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("Extraction code is copied only on request", copied.SequenceEqual(new[] { "0a53" }));
                Check("Provider navigation leaves GlassDesk running", window.IsVisible);
                SaveImage(window, Path.Combine(output, "cloud-downloads.png"));
                window.Close();
                var sampleVersion = new Version(1, 4, 5);
                window = Create(sampleVersion, string.Concat(Enumerable.Repeat("新版更新说明：请在网盘中核对版本，下载后手动运行安装包。\n", 30)), _ => throw new IOException("browser unavailable"), _ => throw new IOException("clipboard busy"));
                window.Show(); Pump(80);
                Check("Detected release displays its numeric version", Find<TextBlock>(window, "网盘下载标题").Text.Contains(sampleVersion.ToString()));
                Find<Button>(window, "打开百度网盘").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("Browser launch errors are recoverable", Find<TextBlock>(window, "网盘下载状态").Text.Contains("browser unavailable") && window.IsVisible);
                Find<Button>(window, "复制百度网盘提取码").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("Clipboard errors are recoverable and keep extraction code visible", Find<TextBlock>(window, "网盘下载状态").Text.Contains("clipboard busy"));
                SaveImage(window, Path.Combine(output, "cloud-update.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight; Pump(60);
                var content = Find<ScrollViewer>(window, "网盘下载内容");
                content.ScrollToEnd(); Pump(60);
                var lastButton = Find<Button>(window, "复制百度网盘提取码");
                var position = lastButton.TranslatePoint(new Point(), content);
                Check("Provider actions remain reachable with long notes at minimum size", position.Y >= 0 && position.Y + lastButton.ActualHeight <= content.ActualHeight + 1);
                SaveImage(window, Path.Combine(output, "cloud-downloads-minimum.png"));
                window.Close();
                foreach (var invalidUrl in new[] { "file:///C:/Windows/notepad.exe", "http://pan.quark.cn/s/test", "https://user:password@pan.quark.cn/s/test" })
                {
                    try
                    {
                        var invalid = configuration.Replace("https://pan.quark.cn/s/037d0cd5080b", invalidUrl);
                        var unsafeWindow = (Window)Activator.CreateInstance(type, invalid, null, null, null, null, null)!;
                        unsafeWindow.Close();
                        Check("Unsafe share URL is rejected: " + invalidUrl, false);
                    }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
                    { Check("Unsafe share URL is rejected: " + invalidUrl, true); }
                }

                Window Create(Version? version, string? notes, Action<Uri> openPage, Action<string> copyText) =>
                    (Window)Activator.CreateInstance(type, activeConfiguration, version, notes, openPage, copyText, null)!;
            }
        }
        catch (Exception ex) { passed = false; checks.Add(new { name = "Unexpected failure", passed = false, error = ex.ToString() }); }
        finally
        {
            window?.Close(); app.Shutdown();
            File.WriteAllText(Path.Combine(output, "cloud-ui-report.json"), JsonSerializer.Serialize(new { passed, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
        void Check(string name, bool success) { if (!success) passed = false; checks.Add(new { name, passed = success }); Console.WriteLine($"{(success ? "PASS" : "FAIL")}: {name}"); }
    }

    private static int BrandPreview(string output)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml")
        });
        var artwork = typeof(GlassDesk.App.App).Assembly.GetType("GlassDesk.App.Tray.TrayIconArtwork", throwOnError: true)!;
        var render = artwork.GetMethod("RenderBitmap")!;
        foreach (var size in new[] { 16, 32, 48, 256 })
        {
            var bitmap = (BitmapSource)render.Invoke(null, [size])!;
            if (bitmap.PixelWidth != size || bitmap.PixelHeight != size) throw new InvalidDataException("Unexpected brand dimensions.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"brand-{size}.png"));
            encoder.Save(stream);
        }
        app.Shutdown();
        Console.WriteLine("PASS: packaged shared artwork renders at tray and window sizes");
        return 0;
    }

    private static async Task<int> NetworkAcceptance(string output, string expectedInstaller, Version previousVersion)
    {
        Directory.CreateDirectory(output);
        var results = new List<string>();
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var source = new Uri("https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest");
            var client = new UpdateClient(http, source);
            var expectedVersion = Version.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(expectedInstaller).ProductVersion!.Trim());
            var release = await client.CheckInstallerAsync(previousVersion, CancellationToken.None);
            if (release?.Version != expectedVersion) throw new InvalidDataException($"Public endpoint did not offer {expectedVersion} to {previousVersion}.");
            results.Add($"Anonymous public API offers {expectedVersion} to {previousVersion}, without credentials");
            if (await client.CheckInstallerAsync(expectedVersion, CancellationToken.None) is not null)
                throw new InvalidDataException("The current version was offered an unnecessary reinstall.");
            results.Add("Current version receives no update");
            var downloaded = await client.DownloadAsync(release, Path.Combine(output, "downloads"), null, CancellationToken.None);
            using var expected = File.OpenRead(expectedInstaller);
            using var actual = File.OpenRead(downloaded);
            var expectedHash = Convert.ToHexString(await SHA256.HashDataAsync(expected));
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(actual));
            if (expectedHash != actualHash) throw new InvalidDataException("The anonymously downloaded asset differs from the delivery.");
            results.Add("Anonymous download passes checksum and matches the delivered installer exactly");
            File.WriteAllText(Path.Combine(output, "network-report.json"), JsonSerializer.Serialize(new { passed = true, results, installerSha256 = actualHash.ToLowerInvariant() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: anonymous GitHub release check and full verified installer download");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "network-report.json"), JsonSerializer.Serialize(new { passed = false, results, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Render the retained desktop controls after removing the redundant desktop page.
    private static int DesktopPreview(string output)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        using var theme = GlassDesk.UI.Theming.ThemeService.Initialize(app.Dispatcher);
        theme.Update(new GlassDesk.Core.Models.Settings.AppearanceSettings());
        var settings = new SettingsWindow { ShowActivated = false };
        settings.RefreshDiagnostics();
        settings.Show();
        Pump(100);
        try
        {
            if (settings.PlannedPages.Contains("桌面") || settings.FindName("DesktopSectionHeading") is not null
                || settings.FindName("DesktopIconsToggleButton") is not null)
                throw new InvalidOperationException("The redundant desktop settings page is still present.");
            if (settings.FindName("ToggleDesktopIconsHotkeyBox") is null
                || settings.FindName("ShowDesktopIconsEntryCheckBox") is null)
                throw new InvalidOperationException("The desktop shortcut or Dock entry setting is missing.");
            var list = (ListBox)settings.FindName("SettingsPageList");
            foreach (var page in new[] { "Dock", "快捷操作", "备份恢复", "关于" })
            {
                list.SelectedItem = page;
                Pump(150);
                SaveImage(settings, Path.Combine(output, $"settings-{page}.png"));
            }
        }
        finally
        {
            settings.Close(); app.Shutdown();
        }
        return 0;
    }

    private static int ControlCenterPreview(string output)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        using var theme = GlassDesk.UI.Theming.ThemeService.Initialize(app.Dispatcher);
        theme.Update(new GlassDesk.Core.Models.Settings.AppearanceSettings { Theme = GlassDesk.Core.Models.ThemeMode.Dark });
        var bar = new GlassDesk.Dock.StatusBarWindow();
        bar.ConfigureGameSleep(() => false, _ => { });
        bar.ConfigureNearby(() => { });
        var center = (GlassDesk.Dock.ControlCenterWindow)typeof(GlassDesk.Dock.StatusBarWindow)
            .GetField("_controlCenter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bar)!;
        try
        {
            center.Show();
            Pump(200);
            SaveImage(center, Path.Combine(output, "control-center.png"));
            return 0;
        }
        finally { bar.Close(); app.Shutdown(); }
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && AutomationProperties.GetName(element) == name) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Find<T>(VisualTreeHelper.GetChild(root, i), name); } catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(name);
    }
    private static void SaveImage(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
