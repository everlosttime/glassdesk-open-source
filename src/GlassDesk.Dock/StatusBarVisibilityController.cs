using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock;

/// <summary>Uses Dock's leave-delay state machine with independent status-bar pointer and coverage inputs.</summary>
public sealed class StatusBarVisibilityController
{
    private AutoHideController? _autoHide;
    private int _delay;
    private bool _revealedDuringSuppression;
    private bool _pointerWasOver;

    public AutoHideState Update(DockSettings settings, DateTime now, bool pointerOver,
        bool covered, bool fullscreen, bool launcherActive, bool revealLocked, bool enabled, bool sleeping,
        bool fixedDesktopLayer = false)
    {
        var pointerEntered = pointerOver && !_pointerWasOver;
        _pointerWasOver = pointerOver;
        if (_autoHide is null || _delay != settings.AutoHideLeaveDelayMs)
        {
            _delay = settings.AutoHideLeaveDelayMs;
            _autoHide = new AutoHideController(Math.Clamp(_delay, 0, 3000));
        }
        if (!enabled || sleeping)
        {
            _revealedDuringSuppression = false;
            _autoHide.ForceHide(now);
        }
        else if (launcherActive)
        {
            _revealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (!fixedDesktopLayer && (covered || fullscreen))
        {
            if (revealLocked) _autoHide.ForceHide(now);
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                // A stationary pointer carried in by a covering window is not a fresh reveal.
                // Otherwise the next policy tick reverses the hide animation halfway through.
                if (pointerEntered && _autoHide.TryReveal(false, now)) _revealedDuringSuppression = true;
            }
            else if (_revealedDuringSuppression) PollPointer(now, pointerOver);
            else _autoHide.ForceHide(now);
        }
        else
        {
            // Keep a deliberate hover reveal across a brief uncovered observation while the
            // pointer remains inside; losing/reacquiring coverage must not bounce the panel.
            if (!pointerOver) _revealedDuringSuppression = false;
            if (settings.AutoHide) PollPointer(now, pointerOver);
            else _autoHide.ForceShown(now);
        }
        if (_autoHide.State == AutoHideState.Hidden) _revealedDuringSuppression = false;
        return _autoHide.State;
    }

    private void PollPointer(DateTime now, bool pointerOver)
    {
        if (pointerOver) _autoHide!.PointerEnter(now);
        else _autoHide!.PointerLeave(now);
        _autoHide.Poll(now);
    }
}
