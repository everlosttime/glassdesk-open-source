using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class ShortcutRecorderTests
{
    [Theory]
    [InlineData(120, ModifierKeys.None, "WheelUp", "滚轮向上")]
    [InlineData(-120, ModifierKeys.Control, "Ctrl+WheelDown", "Ctrl + 滚轮向下")]
    public void RecordsWheelDirectionAndModifiers(int delta, ModifierKeys modifiers, string stored, string display)
    {
        StaTest.Run(() =>
        {
            using var recorder = new ShortcutRecorder();
            recorder.BeginRecording();
            recorder.CaptureWheel(delta, modifiers);
            Assert.False(recorder.IsRecording);
            Assert.Equal(stored, recorder.Text);
            Assert.Equal(display, recorder.Content);
        });
    }
    [Theory]
    [InlineData(Key.OemComma, "Ctrl+OemComma", "Ctrl + ,")]
    [InlineData(Key.OemPeriod, "Ctrl+OemPeriod", "Ctrl + .")]
    [InlineData(Key.OemPlus, "Ctrl+OemPlus", "Ctrl + =")]
    [InlineData(Key.OemQuestion, "Ctrl+OemQuestion", "Ctrl + /")]
    public void RecordsPunctuationKeys(Key key, string stored, string displayed)
    {
        StaTest.Run(() =>
        {
            using var recorder = new ShortcutRecorder();
            recorder.BeginRecording();
            recorder.CaptureKey(key, ModifierKeys.Control);
            Assert.False(recorder.IsRecording);
            Assert.Equal(stored, recorder.Text);
            Assert.Equal(displayed, recorder.Content);
        });
    }

    [Fact]
    public void RecordsMouseSideButtonWithModifiers()
    {
        StaTest.Run(() =>
        {
            using var recorder = new ShortcutRecorder();
            recorder.BeginRecording();
            recorder.CaptureMouseButton(1, ModifierKeys.None);
            Assert.Equal("XButton1", recorder.Text);
            recorder.BeginRecording();
            recorder.CaptureMouseButton(2, ModifierKeys.Control);
            Assert.Equal("Ctrl+XButton2", recorder.Text);
        });
    }
    [Fact]
    public void RecordingIgnoresModifiersCapturesChordAndEscapeRestoresOriginal()
    {
        StaTest.Run(() =>
        {
            using var recorder = new ShortcutRecorder { Text = "Ctrl+Shift+D" };
            recorder.BeginRecording();
            recorder.CaptureKey(Key.LeftCtrl, ModifierKeys.Control);
            Assert.True(recorder.IsRecording);
            Assert.Equal("Ctrl+Shift+D", recorder.Text);
            recorder.CaptureKey(Key.Escape, ModifierKeys.None);
            Assert.False(recorder.IsRecording);
            Assert.Equal("Ctrl + Shift + D", recorder.Content);
            recorder.BeginRecording();
            recorder.CaptureKey(Key.Space, ModifierKeys.Alt);
            Assert.False(recorder.IsRecording);
            Assert.Equal("Alt+Space", recorder.Text);
            Assert.Equal("Alt + Space", recorder.Content);
        });
    }

    [Theory]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    public void ClearRemovesBinding(Key key)
    {
        StaTest.Run(() =>
        {
            using var recorder = new ShortcutRecorder { Text = "Ctrl+D" };
            recorder.BeginRecording();
            recorder.CaptureKey(key, ModifierKeys.None);
            Assert.False(recorder.IsRecording);
            Assert.Equal("", recorder.Text);
            Assert.Equal("未设置 · 点击录制", recorder.Content);
        });
    }

    [Fact]
    public void SettingsRecordsAndSavesImmediatelyAndRestoresBindingOnFailure()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-recorder-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            SettingsWindow? window = null;
            try
            {
                data.LoadAll();
                data.Settings.Current.Shortcuts.HideLauncher = new() { Enabled = true, Key = "F4" };
                window = new SettingsWindow();
                window.AttachDataStore(data, null);
                Assert.Null(window.FindName("HideLauncherHotkeyBox"));
                window.InteractionSettingsApply = (shortcuts, display) =>
                {
                    data.Settings.Current.Shortcuts = shortcuts;
                    data.Settings.Current.Display = display;
                    data.Settings.Save();
                    return null;
                };
                var recorder = Assert.IsType<ShortcutRecorder>(window.FindName("ToggleLauncherHotkeyBox"));
                recorder.BeginRecording();
                recorder.CaptureKey(Key.D, ModifierKeys.Control | ModifierKeys.Shift);
                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                Assert.Equal("Ctrl+Shift+D", reloaded.Current.Shortcuts.ToggleLauncher.Describe());
                Assert.False(reloaded.Current.Shortcuts.HideLauncher.Enabled);
                window.InteractionSettingsApply = (_, _) => "快捷键已被占用";
                recorder.BeginRecording();
                recorder.CaptureKey(Key.Space, ModifierKeys.Alt);
                Assert.Equal("Ctrl+Shift+D", recorder.Text);
                Assert.Contains("已被占用", Assert.IsType<TextBlock>(window.FindName("InteractionSettingsStatus")).Text);
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
