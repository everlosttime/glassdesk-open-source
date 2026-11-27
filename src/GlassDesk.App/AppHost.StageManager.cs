using System.IO;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private StageManager? _stageManager;
    private StageParking? _stageParking;

    private void InitializeStageManager()
    {
        if (_data is null) return;
        _stageParking = new StageParking(Path.Combine(_data.Paths.RootDirectory, "stage-window-recovery.json"));
        _stageParking.Recover();
        if (_data.Settings.Current.StageManagerEnabled) StartStageManager();
    }

    private string? SetStageManagerEnabled(bool enabled)
    {
        if (_data is null || _shuttingDown) return "台前调度暂不可用。";
        var settings = _data.Settings.Current;
        var previous = settings.StageManagerEnabled;
        if (enabled == previous) return null;
        try
        {
            settings.StageManagerEnabled = enabled;
            _data.Settings.Save();
        }
        catch (Exception error)
        {
            settings.StageManagerEnabled = previous;
            return "无法保存台前调度设置：" + error.Message;
        }
        if (enabled) StartStageManager();
        else StopStageManager();
        return null;
    }

    private void StartStageManager()
    {
        if (_stageParking is null || _stageManager is not null) return;
        _stageManager = new StageManager(_application.Dispatcher, _stageParking, LeftDockBounds,
            () => ThemeService.Current?.AnimationsEnabled != false && SystemParameters.ClientAreaAnimation);
        _stageManager.SetPaused(_performance?.IsSuppressionRequested == true || _windowTopmostSessionSuspended);
    }

    private void ToggleStageWindowSelection() => ToggleWindowSelection(stage: true);

    private string? DescribeStageTarget(WindowTopmostTarget target) =>
        _stageManager?.IsSelected(target) == true ? "点击移出台前调度"
            : target.IsTopmost ? null : "点击加入台前调度";

    private void OnStageWindowSelected(WindowTopmostTarget target)
    {
        if (!CanSelectWindowTopmost || _data is null) return;
        var enabled = _data.Settings.Current.StageManagerEnabled;
        StartStageManager();
        var result = _stageManager?.ToggleSelection(target);
        var error = result is null ? "这个窗口暂时无法加入台前调度。"
            : enabled ? null : SetStageManagerEnabled(true);
        if (error is not null)
        {
            if (!enabled) StopStageManager();
            ReportInteractionError(error);
            _windowTopmostPicker?.ShowFeedback(error, isError: true);
            return;
        }
        _settings?.LoadStageManagerControl();
        _windowTopmostPicker?.ShowFeedback(result == true ? "窗口已加入台前调度" : "窗口已移出台前调度");
    }

    private PixelRect? LeftDockBounds() =>
        _dock is { IsVisible: true } dock && _data?.Settings.Current.Dock.Placement == DockPlacement.LeftCenter
            ? DisplayService.GetWindowPixelBounds(new WindowInteropHelper(dock).Handle) : null;

    private void StopStageManager()
    {
        if (_pickingStageWindow && _windowTopmostPicker?.IsSelecting == true)
            _windowTopmostPicker.Cancel();
        _stageManager?.Dispose();
        _stageManager = null;
    }
}
