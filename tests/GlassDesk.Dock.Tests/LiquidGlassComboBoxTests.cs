using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDesk.Dock.Tests;

public sealed class LiquidGlassComboBoxTests
{
    [Fact]
    public void ClosedSelectionDisplaysTheConfiguredLabelForObjectItems()
    {
        StaTest.Run(() =>
        {
            var combo = new ComboBox
            {
                Width = 220,
                DisplayMemberPath = nameof(DeviceOption.Label),
                ItemsSource = new[] { new DeviceOption("auto", "自动选择"), new DeviceOption("fixture", "模拟鼠标") },
                SelectedValuePath = nameof(DeviceOption.Key)
            };
            combo.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")
            });

            combo.SelectedValue = "auto";
            Assert.Equal("自动选择", SelectedText(combo));
            combo.SelectedValue = "fixture";
            Assert.Equal("模拟鼠标", SelectedText(combo));
        });
    }

    private static string SelectedText(ComboBox combo)
    {
        var size = new Size(220, 42);
        combo.Measure(size);
        combo.Arrange(new Rect(size));
        combo.UpdateLayout();
        return Assert.Single(DescendantText(combo)).Text;
    }

    private static IEnumerable<TextBlock> DescendantText(DependencyObject element)
    {
        if (element is TextBlock text) yield return text;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            foreach (var child in DescendantText(VisualTreeHelper.GetChild(element, index)))
                yield return child;
        }
    }

    public sealed record DeviceOption(string Key, string Label);
}
