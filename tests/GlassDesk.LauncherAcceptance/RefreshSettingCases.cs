using System.IO;
using System.Windows.Controls;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Settings;

internal static partial class Program
{
    private static void VerifyRefreshSetting(LauncherWindow window, GlassDeskDataStore data, string evidence)
    {
        var settings = new SettingsWindow();
        try
        {
            settings.AttachDataStore(data, null);
            settings.LauncherSettingsChanged += (_, value) => window.ApplySettings(value);
            var toggle = (CheckBox)settings.FindName("LauncherHighRefreshCheck");
            Check("旧配置默认关闭高刷新", "refresh/default", toggle.IsChecked == false, "off", toggle.IsChecked.ToString()!);
            toggle.IsChecked = true;
            var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
            Check("设置开关立即保存高刷新选择", "refresh/persistence", reload.Current.Launcher.HighRefreshAnimationsEnabled, "on", reload.Current.Launcher.HighRefreshAnimationsEnabled.ToString());
            window.ApplySettings(reload.Current.Launcher);
            settings.Show(); settings.UpdateLayout();
            ((ListBox)settings.FindName("SettingsPageList")).SelectedIndex = 2;
            toggle.BringIntoView(); Pump(TimeSpan.FromMilliseconds(250));
            Capture(settings, Path.Combine(evidence, "high-refresh-setting.png"));
            settings.Hide();
            var high = Path.Combine(evidence, "high-refresh"); Directory.CreateDirectory(high);
            VerifyHighRefreshAnimation(window, high, true);
            toggle.IsChecked = false;
            reload.Load();
            Check("关闭后保存标准模式", "refresh/persistence", !reload.Current.Launcher.HighRefreshAnimationsEnabled, "off", reload.Current.Launcher.HighRefreshAnimationsEnabled.ToString());
            var standard = Path.Combine(evidence, "standard"); Directory.CreateDirectory(standard);
            VerifyHighRefreshAnimation(window, standard);
        }
        finally { settings.Close(); }
    }
}
