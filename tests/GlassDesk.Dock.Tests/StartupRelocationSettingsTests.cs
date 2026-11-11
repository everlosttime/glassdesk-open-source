using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.Platform.Startup;
using GlassDesk.Settings;
using Microsoft.Win32;

namespace GlassDesk.Dock.Tests;

public sealed class StartupRelocationSettingsTests
{
    [Theory]
    [InlineData("GlassDesk.exe", true)]
    [InlineData("Other.exe", false)]
    public void UserCanExplicitlyAdoptOnlyARecognizableOldInstallation(string oldName, bool mayAdopt)
    {
        StaTest.Run(() =>
        {
            var id = Guid.NewGuid().ToString("N");
            var keyPath = @"Software\GlassDesk.Tests\StartupUI\" + id;
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-startup-ui-" + id);
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "GlassDesk.exe"); File.WriteAllText(path, "placeholder");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            const string name = "Startup";
            var oldCommand = $"\"C:\\Missing\\{oldName}\" --autostart";
            key.SetValue(name, oldCommand);
            var registration = (StartupRegistration)Activator.CreateInstance(typeof(StartupRegistration),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [key, name], null)!;
            var window = new SettingsWindow();
            try
            {
                window.AttachStartupRegistration(registration, path);
                var check = (CheckBox)window.FindName("AutostartCheck");
                Assert.Contains("开机自启动", check.Content.ToString());
                Assert.False(check.IsChecked);
                Assert.Equal(mayAdopt, check.IsEnabled);
                Assert.Equal(oldCommand, key.GetValue(name)); // Merely opening settings never changes registration.
                if (!mayAdopt) return;
                check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                Assert.True(check.IsChecked);
                Assert.Equal($"\"{path}\" --autostart", key.GetValue(name));
                check.IsChecked = false; check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                Assert.Null(key.GetValue(name));
            }
            finally { window.Close(); Registry.CurrentUser.DeleteSubKeyTree(keyPath); Directory.Delete(root, true); }
        });
    }
}
