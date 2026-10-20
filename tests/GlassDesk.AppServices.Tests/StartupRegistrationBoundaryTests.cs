using Microsoft.Win32;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Startup;

namespace GlassDesk.AppServices.Tests;

public sealed class StartupRegistrationBoundaryTests
{
    [Fact]
    public void 恢复和按页恢复默认只改产品数据并保留测试启动注册()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-StartupBoundary-" + Guid.NewGuid().ToString("N"));
        var registryPath = @"Software\GlassDesk.Tests\StartupBoundary\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);

        try
        {
            var executablePath = Path.Combine(root, "GlassDesk 中文 Test.exe");
            File.WriteAllText(executablePath, "test executable placeholder");
            using var runKey = Registry.CurrentUser.CreateSubKey(registryPath, writable: true)!;
            const string valueName = "GlassDesk.Tests.StartupBoundary";
            var registration = new StartupRegistration(runKey, valueName);
            var enabled = registration.SetEnabled(executablePath, enabled: true);
            Assert.True(enabled.Succeeded, enabled.Error);
            var ownedCommand = runKey.GetValue(valueName);

            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "data")));
            data.LoadAll();
            data.Settings.Current.Appearance.AccentColor = "#123456";
            data.Settings.Save();

            var reset = SettingsPageEditor.ResetPage(data.Settings.Current, "外观");
            SettingsPageEditor.Apply(data.Settings.Current, reset);
            data.Settings.Save();

            var archive = Path.Combine(root, "external archive");
            Assert.True(data.Backup.BackupTo(archive).Succeeded);
            Assert.True(data.Backup.RestoreFrom(archive).Succeeded);

            Assert.Equal(ownedCommand, runKey.GetValue(valueName));
            Assert.Equal(StartupRegistrationState.Owned, registration.Query(executablePath).State);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
