using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Input;
using GlassDesk.Platform.Input;

namespace GlassDesk.Settings;

/// <summary>Captures keyboard chords and delegates registration to the settings host.</summary>
public sealed class ShortcutRecorder : Button, IDisposable
{
    private string _text = "";
    private string _originalText = "";
    private MouseSideButtonHook? _mouseCapture;
    private DispatcherTimer? _mouseReleaseTimer;
    public string Text
    {
        get => _text;
        set { _text = value ?? ""; RefreshDisplay(); }
    }

    public bool IsRecording { get; private set; }
    public event EventHandler? RecordingCompleted;

    public ShortcutRecorder()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        MinHeight = 36;
        Margin = new Thickness(0, 3, 0, 3);
        RefreshDisplay();
        Unloaded += (_, _) => Dispose();
    }

    protected override void OnClick()
    {
        base.OnClick();
        BeginRecording();
    }

    public void BeginRecording()
    {
        if (IsRecording) return;
        Focus();
        _originalText = Text;
        IsRecording = true;
        _mouseReleaseTimer?.Stop();
        if (_mouseCapture is null)
        {
            _mouseCapture = new MouseSideButtonHook(button =>
            {
                if (!IsRecording) return false;
                var modifiers = (ModifierKeys)KeyboardModifierState.Read();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => CaptureMouseButton(button, modifiers)));
                return true;
            }, delta =>
            {
                if (!IsRecording || delta == 0) return false;
                var modifiers = (ModifierKeys)KeyboardModifierState.Read();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => CaptureWheel(delta, modifiers)));
                return true;
            });
            if (!_mouseCapture.TryStart(out _)) { _mouseCapture.Dispose(); _mouseCapture = null; }
        }
        Background = new SolidColorBrush(Color.FromArgb(65, 10, 132, 255));
        BorderBrush = new SolidColorBrush(Color.FromRgb(102, 181, 255));
        RefreshDisplay();
    }

    public void CancelRecording()
    {
        IsRecording = false;
        ClearValue(BackgroundProperty);
        ClearValue(BorderBrushProperty);
        RefreshDisplay();
        if (_mouseCapture is not null)
        {
            _mouseReleaseTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Background,
                (_, _) =>
                {
                    if (KeyboardModifierState.IsDown(5) || KeyboardModifierState.IsDown(6)) return;
                    _mouseReleaseTimer!.Stop();
                    _mouseCapture?.Dispose();
                    _mouseCapture = null;
                }, Dispatcher);
            _mouseReleaseTimer.Start();
        }
    }

    public void RestorePreviousBinding() => Text = _originalText;

    public void Dispose()
    {
        IsRecording = false;
        _mouseReleaseTimer?.Stop();
        _mouseCapture?.Dispose();
        _mouseCapture = null;
        RefreshDisplay();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsKeyboardFocusWithin) CancelRecording();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        if (e.IsRepeat) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        CaptureKey(key, Keyboard.Modifiers);
    }

    /// <summary>Also accepts registered hotkey messages while this recorder has focus.</summary>
    public void CaptureKey(Key key, ModifierKeys modifiers)
    {
        if (!IsRecording) return;
        if (key == Key.Escape) { CancelRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        if (key is Key.Back or Key.Delete) { Complete(""); return; }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var keyName = virtualKey switch
        {
            >= 48 and <= 57 or >= 65 and <= 90 => ((char)virtualKey).ToString(),
            >= 112 and <= 135 => "F" + (virtualKey - 111),
            106 => "Multiply", 107 => "Add", 108 => "Separator", 109 => "Subtract", 110 => "Decimal", 111 => "Divide",
            13 => "Enter", 32 => "Space", 9 => "Tab", 33 => "PageUp", 34 => "PageDown",
            35 => "End", 36 => "Home", 37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down", 45 => "Insert",
            186 => "OemSemicolon", 187 => "OemPlus", 188 => "OemComma", 189 => "OemMinus",
            190 => "OemPeriod", 191 => "OemQuestion", 192 => "OemTilde", 219 => "OemOpenBrackets",
            220 => "OemPipe", 221 => "OemCloseBrackets", 222 => "OemQuotes", 226 => "OemBackslash",
            _ => key.ToString(),
        };
        CaptureNamedKey(keyName, modifiers);
    }

    public void CaptureMouseButton(int button, ModifierKeys modifiers)
    {
        if (IsRecording && button is 1 or 2) CaptureNamedKey("XButton" + button, modifiers);
    }

    public void CaptureWheel(int delta, ModifierKeys modifiers)
    {
        if (IsRecording && delta != 0) CaptureNamedKey(delta > 0 ? "WheelUp" : "WheelDown", modifiers);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsRecording) { base.OnPreviewMouseWheel(e); return; }
        e.Handled = true;
        CaptureWheel(e.Delta, Keyboard.Modifiers);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (IsRecording && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            e.Handled = true;
            CaptureMouseButton(e.ChangedButton == MouseButton.XButton1 ? 1 : 2, Keyboard.Modifiers);
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    private void CaptureNamedKey(string keyName, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyName);
        if (!HotkeyText.TryParse(string.Join('+', parts), out var definition, out var error))
        {
            Content = error;
            return;
        }
        Complete(HotkeyText.Format(definition));
    }

    private void Complete(string text)
    {
        _text = text;
        CancelRecording();
        RecordingCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshDisplay()
    {
        Content = IsRecording ? "请按下新的快捷键…"
            : string.IsNullOrEmpty(Text) ? "未设置 · 点击录制" : HotkeyText.DisplayFormat(Text);
        ToolTip = IsRecording ? "Esc 取消；Backspace / Delete 清除绑定" : "点击录制快捷键";
    }
}
