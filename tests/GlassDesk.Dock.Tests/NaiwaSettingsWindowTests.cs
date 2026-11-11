using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public class NaiwaSettingsWindowTests
{
    [Fact]
    public void RecycleBinHasItsOwnSettingsSection()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow { ShowActivated = false };
            try
            {
                Assert.Contains("回收站", window.PlannedPages);
                Assert.Null(window.FindName("NaiwaManualButton"));
                Assert.Equal("回收站", Assert.IsType<TextBlock>(window.FindName("NaiwaSectionHeading")).Text);
                Assert.Equal("启用黄色回收站", Assert.IsType<CheckBox>(window.FindName("NaiwaEnabledCheck")).Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RecycleBinShowsInlineInteractionGuide()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow { ShowActivated = false };
            try
            {
                var guide = Assert.IsType<StackPanel>(window.FindName("NaiwaInteractionGuide"));
                var rows = guide.Children.OfType<TextBlock>()
                    .Select(row => new TextRange(row.ContentStart, row.ContentEnd).Text).ToArray();
                Assert.Equal("互动动作", rows[0]);
                Assert.Equal(5, rows.Length);
                Assert.Contains("大笑", rows[1]);
                Assert.Contains("重力下落", rows[2]);
                Assert.Contains("哭泣", rows[3]);
                Assert.Contains("6 只小奶蛙", rows[4]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PreferencesPersistAndMuteRestoresThePreviousVolume()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-naiwa-settings-" + Guid.NewGuid().ToString("N"));
            var window = new SettingsWindow { ShowActivated = false };
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                data.Settings.Current.Appearance.NaiwaEnabled = true;
                window.AttachDataStore(data, null);
                Assert.True(Assert.IsType<CheckBox>(window.FindName("NaiwaEnabledCheck")).IsChecked);

                Assert.IsType<Slider>(window.FindName("NaiwaScaleSlider")).Value = 120;
                Assert.IsType<Slider>(window.FindName("NaiwaVolumeSlider")).Value = 42;
                foreach (var name in new[] { "NaiwaSoundCheck", "NaiwaRecycleCheck", "NaiwaAutoHideCheck" })
                    Assert.IsType<CheckBox>(window.FindName(name)).IsChecked = false;
                var mute = Assert.IsType<Button>(window.FindName("NaiwaMuteButton"));
                Click(mute);
                Assert.Equal(0, data.Settings.Current.NaiwaOptions?.Volume);
                Assert.Equal("取消静音", mute.Content);
                Click(mute);
                Assert.Equal("静音", mute.Content);

                var actions = new List<string>();
                window.RunNaiwaAction = action =>
                {
                    actions.Add(action);
                    return null;
                };
                foreach (var name in new[] { "NaiwaHungryButton", "NaiwaStopButton" })
                    Click(Assert.IsType<Button>(window.FindName(name)));
                Assert.Equal(new[] { "hungry", "stop" }, actions);

                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.True(reloaded.Current.Appearance.NaiwaEnabled);
                var options = Assert.IsType<NaiwaOptions>(reloaded.Current.NaiwaOptions);
                Assert.Equal(120, options.Scale);
                Assert.Equal(42, options.Volume);
                Assert.False(options.Sound);
                Assert.False(options.Recycle);
                Assert.False(options.AutoHide);

                window.Show();
                window.UpdateLayout();
                var pages = Assert.IsType<ListBox>(window.FindName("SettingsPageList"));
                pages.SelectedItem = "回收站";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                var reset = Assert.IsType<Button>(window.FindName("ResetPageButton"));
                Assert.True(reset.IsEnabled);
                Click(reset);
                Assert.False(Assert.IsType<CheckBox>(window.FindName("NaiwaEnabledCheck")).IsChecked);
                Assert.Equal(50, Assert.IsType<Slider>(window.FindName("NaiwaScaleSlider")).Value);
                Assert.Equal(100, Assert.IsType<Slider>(window.FindName("NaiwaVolumeSlider")).Value);
            }
            finally
            {
                window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
}
