using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Storage;
using System.IO;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;
using System.Windows.Threading;

namespace GlassDesk.Dock.Tests;

public sealed class LunarCalendarTests
{
    [Theory]
    [InlineData(2026, 2, 17, "正月", "农历2026年正月初一")]
    [InlineData(2026, 3, 3, "十五", "农历2026年正月十五")]
    [InlineData(2025, 7, 25, "闰六月", "农历2025年闰六月初一")]
    [InlineData(2025, 8, 23, "七月", "农历2025年七月初一")]
    [InlineData(2026, 11, 10, "初二", "农历2026年十月初二")]
    [InlineData(1900, 1, 1, "", "")]
    [InlineData(2200, 1, 1, "", "")]
    public void LunarLabelsHandleNewYearLeapMonthsAndUnsupportedDates(int year, int month, int day, string label, string full)
    {
        var date = new DateTime(year, month, day);
        Assert.Equal(label, LunarDateTextConverter.Format(date));
        Assert.Equal(full, LunarDateTextConverter.Format(date, fullDate: true));
    }

    [Fact]
    public void SavedModeIsRestoredByANewWindowAndCalendarCellsRenderLunarDates()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings());
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-calendar-test-" + Guid.NewGuid().ToString("N"));
            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Load();
            var window = new DateNotificationWindow();
            try
            {
                window.AttachSettings(store);
                ((RadioButton)window.FindName("LunarCalendarButton")).IsChecked = true;
                var reload = new SettingsStore(store.FilePath);
                reload.Load();
                Assert.True(reload.Current.Dock.CalendarShowLunar);
                window.Close();
                window = new DateNotificationWindow();
                window.AttachSettings(reload);
                Assert.True(((RadioButton)window.FindName("LunarCalendarButton")).IsChecked);
                var calendar = (Calendar)window.FindName("MonthCalendar");
                window.Show();
                calendar.DisplayDate = new DateTime(2025, 7, 25);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                var leapDay = Descendants<CalendarDayButton>(calendar).Single(button => button.DataContext is DateTime date && date == new DateTime(2025, 7, 25));
                Assert.Contains(Descendants<TextBlock>(leapDay), text => text.Text == "闰六月");
                Assert.Contains(Descendants<TextBlock>(leapDay), text => text.Text == "25");
                Assert.InRange(calendar.ActualHeight, 180, 300);
                var footer = Descendants<Button>(content).Single(button => Equals(button.Content, "打开通知设置"));
                var footerBounds = footer.TransformToAncestor(content).TransformBounds(new Rect(footer.RenderSize));
                Assert.InRange(footerBounds.Bottom, 0, content.ActualHeight);
                var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var evidence = Path.Combine(AppContext.BaseDirectory, "calendar-lunar-preview.png");
                using (var output = File.Create(evidence)) encoder.Save(output);
                ((RadioButton)window.FindName("SolarCalendarButton")).IsChecked = true;
                reload.Load();
                Assert.False(reload.Current.Dock.CalendarShowLunar);
            }
            finally
            {
                window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public void AFailedSaveRestoresThePreviousModeWithoutCrashing()
    {
        StaTest.Run(() =>
        {
            var blockingFile = Path.GetTempFileName();
            var store = new SettingsStore(Path.Combine(blockingFile, "settings.json"));
            var window = new DateNotificationWindow();
            try
            {
                window.AttachSettings(store);
                ((RadioButton)window.FindName("LunarCalendarButton")).IsChecked = true;
                Assert.True(((RadioButton)window.FindName("SolarCalendarButton")).IsChecked);
                Assert.False(store.Current.Dock.CalendarShowLunar);
                Assert.NotNull(((TextBlock)window.FindName("LargeDateText")).ToolTip);
            }
            finally { window.Close(); File.Delete(blockingFile); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Fact]
    public void CalendarModesAreExclusiveAndSwitchWithoutChangingTheBrowsedMonth()
    {
        StaTest.Run(() =>
        {
            var window = new DateNotificationWindow();
            try
            {
                var solar = Assert.IsType<RadioButton>(window.FindName("SolarCalendarButton"));
                var lunar = Assert.IsType<RadioButton>(window.FindName("LunarCalendarButton"));
                var calendar = Assert.IsType<Calendar>(window.FindName("MonthCalendar"));
                var date = new DateTime(2025, 7, 25);
                calendar.DisplayDate = date;
                calendar.SelectedDate = date;
                Assert.True(solar.IsChecked);
                lunar.IsChecked = true;
                Assert.False(solar.IsChecked);
                Assert.NotNull(calendar.CalendarDayButtonStyle);
                Assert.Equal(date, calendar.DisplayDate);
                Assert.Equal(date, calendar.SelectedDate);
                solar.IsChecked = true;
                Assert.False(lunar.IsChecked);
                Assert.Null(calendar.CalendarDayButtonStyle);
            }
            finally { window.Close(); }
        });
    }
}
