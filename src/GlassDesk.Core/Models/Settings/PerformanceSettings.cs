namespace GlassDesk.Core.Models.Settings;

/// <summary>Performance policy; session-only manual sleep is deliberately not persisted.</summary>
public sealed class PerformanceSettings
{
    public bool AutoSuspendWhenFullscreen { get; set; } = true;
}
