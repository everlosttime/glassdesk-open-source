using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace GlassDesk.Settings;

/// <summary>A separate, accessible help target; never forwards input to a shortcut recorder.</summary>
public sealed class ShortcutHelpIcon : Button
{
    public static readonly DependencyProperty HelpTitleProperty = DependencyProperty.Register(nameof(HelpTitle), typeof(string), typeof(ShortcutHelpIcon),
        new PropertyMetadata(string.Empty, OnHelpChanged));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ShortcutHelpIcon),
        new PropertyMetadata(string.Empty, OnHelpChanged));
    public string HelpTitle { get => (string)GetValue(HelpTitleProperty); set => SetValue(HelpTitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    private readonly ToolTip _help = new() { Placement = PlacementMode.Bottom, HorizontalOffset = 0, VerticalOffset = 7, MaxWidth = 340 };

    public ShortcutHelpIcon()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        Width = Height = 22; Margin = new Thickness(7, 0, 0, 0); Padding = new Thickness(0);
        Content = "ⓘ"; FontSize = 16; Cursor = Cursors.Help;
        Background = Brushes.Transparent; BorderThickness = new Thickness(0);
        Foreground = new SolidColorBrush(Color.FromRgb(117, 162, 222));
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip = _help; ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0); ToolTipService.SetShowDuration(this, 45000);
        Click += (_, _) => { _help.PlacementTarget = this; _help.IsOpen = true; };
        MouseLeave += (_, _) => _help.IsOpen = false;
        LostKeyboardFocus += (_, _) => _help.IsOpen = false;
        Unloaded += (_, _) => _help.IsOpen = false;
        UpdateHelp();
    }
    private static void OnHelpChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) => ((ShortcutHelpIcon)owner).UpdateHelp();
    private void UpdateHelp()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 300, LineHeight = 21, FontSize = 13 };
        text.Inlines.Add(new Bold(new Run(HelpTitle)));
        text.Inlines.Add(new LineBreak()); text.Inlines.Add(new Run(Description));
        text.Inlines.Add(new LineBreak()); text.Inlines.Add(new LineBreak());
        text.Inlines.Add(new Run("修改方式：点击右侧快捷键，再按新组合。Esc 取消，Backspace / Delete 清除；录制后自动保存。") { Foreground = new SolidColorBrush(Color.FromRgb(165, 183, 208)) });
        _help.Content = text; _help.Background = new SolidColorBrush(Color.FromRgb(30, 40, 58));
        _help.Foreground = Brushes.White; _help.Padding = new Thickness(15); _help.BorderBrush = new SolidColorBrush(Color.FromRgb(90, 118, 153));
        AutomationProperties.SetName(this, HelpTitle + "：功能说明");
        AutomationProperties.SetHelpText(this, Description);
    }
}
