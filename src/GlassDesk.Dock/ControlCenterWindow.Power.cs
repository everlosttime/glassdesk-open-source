using System.ComponentModel;
using System.Windows;
using GlassDesk.Platform;

namespace GlassDesk.Dock;

public partial class ControlCenterWindow
{
    private readonly SystemPowerController _powerActions;
    private bool _powerActionPending;

    private void OnPowerClick(object sender, RoutedEventArgs e)
    {
        if (_powerActionPending) return;
        _devicePicker.Hide();
        PowerMenu.PlacementTarget = PowerButton;
        PowerMenu.IsOpen = !PowerMenu.IsOpen;
    }

    private async void OnShutdownClick(object sender, RoutedEventArgs e)
        => await RequestPowerActionAsync(SystemPowerAction.Shutdown);

    private async void OnRestartClick(object sender, RoutedEventArgs e)
        => await RequestPowerActionAsync(SystemPowerAction.Restart);

    private async Task RequestPowerActionAsync(SystemPowerAction action)
    {
        if (_closed || _powerActionPending) return;
        _powerActionPending = true;
        PowerButton.IsEnabled = false;
        PowerMenu.IsOpen = false;
        _devicePicker.Hide();
        try
        {
            if (await _powerActions.RequestAsync(action)) Hide();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or System.IO.IOException)
        {
            if (!_closed)
                MessageBox.Show(this, error.Message, "无法" + PowerActionLabel(action),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _powerActionPending = false;
            PowerButton.IsEnabled = true;
        }
    }

    private bool ConfirmPowerAction(SystemPowerAction action)
    {
        var label = PowerActionLabel(action);
        return MessageBox.Show(this, $"确定要{label}电脑吗？\n请先保存正在编辑的文件。",
            "确认" + label, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private static string PowerActionLabel(SystemPowerAction action)
        => action == SystemPowerAction.Restart ? "重启" : "关机";
}
