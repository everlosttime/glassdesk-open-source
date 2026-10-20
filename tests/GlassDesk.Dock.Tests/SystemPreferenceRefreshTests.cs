using System.Reflection;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;
using Microsoft.Win32;

namespace GlassDesk.Dock.Tests;

public sealed class SystemPreferenceRefreshTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void SystemNotificationPublishesOnlyAChangedResolvedAppearance(bool changeTheme, int expectedNotifications)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { Theme = ThemeMode.Dark });
            var previous = theme.Palette;
            var notifications = 0;
            theme.Changed += (_, _) => notifications++;
            if (changeTheme) theme.Appearance.Theme = ThemeMode.Light;
            typeof(ThemeService).GetMethod("OnUserPreferenceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(theme, [null, new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)]);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(expectedNotifications, notifications);
            Assert.Equal(!changeTheme, theme.Palette.IsDark);
            if (!changeTheme) Assert.Same(previous, theme.Palette);
        });
    }
}
