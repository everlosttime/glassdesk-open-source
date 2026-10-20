namespace GlassDesk.App;

internal enum PerformanceState { Active, WarmHidden, Sleeping, Waking, TemporaryActive }

/// <summary>Dispatcher-owned policy. Time is supplied by the host so transitions need no polling.</summary>
internal sealed class PerformanceCoordinator
{
    private static readonly TimeSpan EnterFullscreenDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LeaveFullscreenDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan KeepWarmDelay = TimeSpan.FromSeconds(5);
    private bool _observedFullscreen, _stableFullscreen;
    private bool _dockVisible = true, _launcherVisible, _temporaryActivation;
    private bool _sessionSuspended;
    private DateTimeOffset? _fullscreenDeadline, _hiddenSince;

    public PerformanceCoordinator(DateTimeOffset now) { }

    public PerformanceState State { get; private set; } = PerformanceState.Active;
    public long Generation { get; private set; }
    public bool AutoSuspend { get; private set; } = true;
    public bool ManualSleep { get; private set; }
    public bool IsSuppressionRequested => ManualSleep || _sessionSuspended || AutoSuspend && _stableFullscreen;
    public bool IsTemporaryActivation => _temporaryActivation;
    public DateTimeOffset? NextTransitionAt
    {
        get
        {
            var hiddenDeadline = State == PerformanceState.WarmHidden && _hiddenSince.HasValue
                ? _hiddenSince.Value + KeepWarmDelay : (DateTimeOffset?)null;
            if (!_fullscreenDeadline.HasValue) return hiddenDeadline;
            if (!hiddenDeadline.HasValue) return _fullscreenDeadline;
            return _fullscreenDeadline.Value < hiddenDeadline.Value ? _fullscreenDeadline : hiddenDeadline;
        }
    }

    public void ObserveForeground(bool fullscreen, bool belongsToCurrentProcess, DateTimeOffset now)
    {
        // Tray menus, the message window and the explicitly opened launcher do not end a game.
        if (belongsToCurrentProcess) return;
        if (_observedFullscreen != fullscreen)
        {
            _observedFullscreen = fullscreen;
            _fullscreenDeadline = _stableFullscreen == fullscreen ? null
                : now + (fullscreen ? EnterFullscreenDelay : LeaveFullscreenDelay);
        }
        Advance(now);
    }

    public void SetAutoSuspend(bool enabled, DateTimeOffset now)
    {
        AutoSuspend = enabled;
        Advance(now);
    }

    public void SetManualSleep(bool enabled, DateTimeOffset now)
    {
        ManualSleep = enabled;
        if (enabled) _temporaryActivation = false;
        Advance(now);
    }

    public void SetSessionSuspended(bool suspended, DateTimeOffset now)
    {
        _sessionSuspended = suspended;
        if (suspended) _temporaryActivation = false;
        Advance(now);
    }

    public void SetVisibleSurfaces(bool dockVisible, bool launcherVisible, DateTimeOffset now)
    {
        if (_launcherVisible && !launcherVisible) _temporaryActivation = false;
        _dockVisible = dockVisible;
        _launcherVisible = launcherVisible;
        if (dockVisible || launcherVisible) _hiddenSince = null;
        else _hiddenSince ??= now;
        Advance(now);
    }

    public void RequestTemporaryActivation(DateTimeOffset now)
    {
        if (_sessionSuspended) return;
        _temporaryActivation = true;
        _launcherVisible = true;
        _hiddenSince = null;
        if (State == PerformanceState.Sleeping) Transition(PerformanceState.Waking);
        else Advance(now);
    }

    public void CompleteWake(DateTimeOffset now)
    {
        if (State != PerformanceState.Waking) return;
        Transition(_temporaryActivation && IsSuppressionRequested
            ? PerformanceState.TemporaryActive : PerformanceState.Active);
        Advance(now);
    }

    public void Advance(DateTimeOffset now)
    {
        if (_fullscreenDeadline is { } deadline && now >= deadline)
        {
            _stableFullscreen = _observedFullscreen;
            _fullscreenDeadline = null;
        }
        if (IsSuppressionRequested && !_temporaryActivation || _sessionSuspended)
        {
            Transition(PerformanceState.Sleeping);
            return;
        }
        if (_dockVisible || _launcherVisible || _temporaryActivation)
        {
            _hiddenSince = null;
            if (State == PerformanceState.Sleeping) Transition(PerformanceState.Waking);
            else if (State != PerformanceState.Waking)
                Transition(_temporaryActivation && IsSuppressionRequested
                    ? PerformanceState.TemporaryActive : PerformanceState.Active);
            return;
        }
        _hiddenSince ??= now;
        Transition(now >= _hiddenSince.Value + KeepWarmDelay ? PerformanceState.Sleeping : PerformanceState.WarmHidden);
    }

    private void Transition(PerformanceState state)
    {
        if (State == state) return;
        State = state;
        Generation++;
    }
}
