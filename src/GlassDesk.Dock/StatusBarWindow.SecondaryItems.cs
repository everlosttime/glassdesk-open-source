using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock;

public partial class StatusBarWindow
{
    private bool IsSecondarySelected(string key) => StatusBarSecondaryItemCatalog.Normalize(Settings.StatusBarSecondaryItems).Contains(key);

    private void ApplySecondaryItems()
    {
        var panels = new Dictionary<string, FrameworkElement>
        {
            [StatusBarSecondaryItemCatalog.Cpu] = CpuPanel,
            [StatusBarSecondaryItemCatalog.Memory] = MemoryPanel,
            [StatusBarSecondaryItemCatalog.Fan] = FanPanel,
            [StatusBarSecondaryItemCatalog.MouseBattery] = MouseBatteryPanel,
            [StatusBarSecondaryItemCatalog.HeadsetBattery] = HeadsetBatteryPanel,
            [StatusBarSecondaryItemCatalog.KeyboardBattery] = KeyboardBatteryPanel,
            [StatusBarSecondaryItemCatalog.Music] = MusicSummary,
        };
        foreach (var panel in panels.Values) panel.Visibility = Visibility.Collapsed;
        SecondaryItemsRow.ColumnDefinitions.Clear();
        var items = StatusBarSecondaryItemCatalog.GetRenderItems(Settings.StatusBarSecondaryItems);
        for (var index = 0; index < items.Length; ++index)
        {
            if (index > 0) SecondaryItemsRow.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
            var key = items[index];
            var panel = panels[key];
            Grid.SetColumn(panel, SecondaryItemsRow.ColumnDefinitions.Count);
            SecondaryItemsRow.ColumnDefinitions.Add(new()
            {
                Width = key == StatusBarSecondaryItemCatalog.Music ? new GridLength(1, GridUnitType.Star) : new GridLength(64)
            });
            panel.Visibility = Visibility.Visible;
        }
        // An unused trailing star column keeps ordinary metrics left-aligned when no media is selected.
        if (!items.Contains(StatusBarSecondaryItemCatalog.Music))
            SecondaryItemsRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        CpuPanel.ToolTip = "CPU 使用率（百分比）";
        MemoryPanel.ToolTip = "内存使用率（百分比）";
    }
}
