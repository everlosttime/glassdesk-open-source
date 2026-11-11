using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyStatusPopoverAnimation(StatusBarWindow bar, SettingsWindow settings,
        GlassDeskDataStore data, string report, string evidence)
    {
        Pump(TimeSpan.FromSeconds(2));
        const BindingFlags state = BindingFlags.Instance | BindingFlags.NonPublic;
        var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", state)!.GetValue(bar)!;
        var picker = (DevicePickerWindow)typeof(ControlCenterWindow).GetField("_devicePicker", state)!.GetValue(center)!;
        var calendar = (DateNotificationWindow)typeof(StatusBarWindow).GetField("_dateNotifications", state)!.GetValue(bar)!;
        var anchor = (PixelRect)typeof(StatusBarWindow).GetField("_restingRect", state)!.GetValue(bar)!;
        ThemeService.Current!.Update(new AppearanceSettings { ReduceMotion = false });
        var observations = new List<object>();
        foreach (var name in new[] { "center", "devices", "calendar" })
        {
            Window window = name == "center" ? center : name == "devices" ? picker : calendar;
            if (name == "center") center.ShowNear(anchor);
            else if (name == "devices")
            {
                center.ShowNear(anchor); Pump(TimeSpan.FromMilliseconds(300));
                ((Button)center.FindName("OutputDeviceArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else calendar.ShowNear(anchor);
            var content = (FrameworkElement)window.FindName("PopupContent");
            var shift = (TranslateTransform)content.RenderTransform;
            var initialOpacity = window.Opacity;
            var watch = Stopwatch.StartNew();
            var values = new List<double>();
            while (watch.ElapsedMilliseconds < 350)
            {
                values.Add(window.Opacity);
                observations.Add(new { name, milliseconds = watch.Elapsed.TotalMilliseconds, opacity = window.Opacity, offsetY = shift.Y });
                Pump(TimeSpan.FromMilliseconds(16));
            }
            var reversals = values.Zip(values.Skip(1)).Count(pair => pair.Second < pair.First - .001);
            Check(name + "平滑淡入到完整状态", "status/popover-animation",
                (!SystemParameters.ClientAreaAnimation || initialOpacity < 1) && window.Opacity == 1 && shift.Y == 0 && reversals == 0 && !window.HasAnimatedProperties,
                "initial fade, monotonic to opacity 1 / offset 0, no retained clock", $"initial={initialOpacity:F3},opacity={window.Opacity},Y={shift.Y},reversals={reversals}");
            Capture(window, Path.Combine(evidence, name + "-settled.png"));
            window.Hide(); center.Hide();
        }
        center.ShowNear(anchor);
        ((Button)center.FindName("OutputDeviceArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var check = (CheckBox)settings.FindName("ShowStatusBarCheckBox");
        check.IsChecked = false;
        Check("设置关闭立即收起信息栏和全部弹窗", "status/setting-off", !bar.IsVisible && !center.IsVisible && !picker.IsVisible && !calendar.IsVisible,
            "all hidden", $"{bar.IsVisible}/{center.IsVisible}/{picker.IsVisible}/{calendar.IsVisible}");
        var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
        Check("关闭选择写入磁盘", "status/setting-save-off", !reload.Current.Dock.ShowStatusBar, "false", reload.Current.Dock.ShowStatusBar.ToString());
        check.IsChecked = true; reload.Load();
        Check("重新启用立即生效并保存", "status/setting-on", bar.IsVisible && reload.Current.Dock.ShowStatusBar, "visible and saved true", $"{bar.IsVisible}/{reload.Current.Dock.ShowStatusBar}");
        ThemeService.Current.Update(new AppearanceSettings { ReduceMotion = true });
        center.ShowNear(anchor);
        Check("减少动画时直接显示", "status/reduced-motion", center.Opacity == 1 && !center.HasAnimatedProperties, "complete without animation", center.Opacity.ToString());
        center.Hide();
        settings.Show(); settings.Activate();
        ((ListBox)settings.FindName("SettingsPageList")).SelectedIndex = 1;
        Pump(TimeSpan.FromMilliseconds(500));
        var scroll = (ScrollViewer)settings.FindName("SettingsScroll");
        var position = check.TranslatePoint(new Point(), scroll);
        Check("Dock 设置入口中能直接看到信息栏开关", "status/setting-discoverable", position.Y >= 0 && position.Y + check.ActualHeight <= scroll.ActualHeight,
            "switch inside viewport", $"Y={position.Y:F1},height={scroll.ActualHeight:F1}");
        Capture(settings, Path.Combine(evidence, "settings-information-bar.png"));
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = _failed == 0, cases = Cases, observations,
            windowsAnimationEnabled = SystemParameters.ClientAreaAnimation,
            note = "Production host and isolated data. Opacity/content-offset samples use the real WPF clock; endpoint screenshots are rendered after the animation. No output, network, volume or playback changes." }, new JsonSerializerOptions { WriteIndented = true }));
        return _failed == 0 ? 0 : 1;
    }
}
