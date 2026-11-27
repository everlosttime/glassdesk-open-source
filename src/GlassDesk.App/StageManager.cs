using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI;

namespace GlassDesk.App;

internal sealed class StageManager : IDisposable
{
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromSeconds(1);
    private readonly Dispatcher _dispatcher;
    private readonly StageWindows _native = new();
    private readonly StageParking _parking;
    private readonly Func<PixelRect?> _leftDock;
    private readonly Func<bool> _animate;
    private readonly Func<StageWindow, bool> _allowWindow;
    private readonly Func<DisplayInfo, bool> _allowDisplay;
    private readonly Action<StageWindowIdentity> _activateWindow;
    private readonly DispatcherTimer _edge;
    private readonly WindowEventWatcher? _events;
    private readonly Dictionary<string, StageScreen> _screens = [];
    private readonly Dictionary<nint, StageMember> _members = [];
    private readonly Dictionary<nint, StageWindow> _selected = [];
    private readonly SemaphoreSlim _captures = new(3);
    private readonly CancellationTokenSource _lifetime = new();
    private int _refreshQueued;
    private bool _disposed, _paused;
    private long _lastInventory;
    private PixelRect? _dockBounds;
    private Guid? _desktop;

    public StageManager(Dispatcher dispatcher, StageParking parking, Func<PixelRect?> leftDock, Func<bool> animate,
        Func<StageWindow, bool>? allowWindow = null, Func<DisplayInfo, bool>? allowDisplay = null,
        Action<StageWindowIdentity>? activateWindow = null)
    {
        _dispatcher = dispatcher;
        _parking = parking;
        _leftDock = leftDock;
        _animate = animate;
        _allowWindow = allowWindow ?? (_ => true);
        _allowDisplay = allowDisplay ?? (_ => true);
        _activateWindow = activateWindow ?? StageWindows.Activate;
        _edge = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Background, OnEdge, dispatcher);
        _edge.Stop();
        _events = _native.IsAvailable
            ? WindowEventWatcher.TryStart(QueueRefresh, (kind, _, _, _) =>
            {
                if (kind == StageWindows.ForegroundEvent) QueueRefresh();
            }, watchMoveSizeEnd: true)
            : null;
        QueueRefresh();
    }

    public bool IsSelected(WindowTopmostTarget target) =>
        _selected.TryGetValue(target.Handle, out var window) && window.Identity == IdentityOf(target);

    // True joins the session, false releases the window, and null rejects a stale or ineligible selection.
    public bool? ToggleSelection(WindowTopmostTarget target)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _paused) return null;
        var identity = IdentityOf(target);
        if (IsSelected(target))
        {
            Deselect(target.Handle);
            return false;
        }
        if (_native.Inspect(target.Handle) is not { Minimized: false } window
            || window.Identity != identity || !_allowWindow(window)
            || FindDisplay(DisplayService.Enumerate().Where(_allowDisplay), window.Bounds) is null)
            return null;
        _selected[target.Handle] = window;
        _edge.Start();
        QueueRefresh();
        return true;
    }

    private static StageWindowIdentity IdentityOf(WindowTopmostTarget target) =>
        new(target.Handle, target.ProcessId, target.ThreadId, target.ProcessStarted);

    private bool IsSelected(StageWindowIdentity identity) =>
        _selected.TryGetValue(identity.Hwnd, out var selected) && selected.Identity == identity;

    private void Deselect(nint handle)
    {
        if (!_selected.Remove(handle)) return;
        if (_members.TryGetValue(handle, out var member))
        {
            member.Screen.Cancel();
            Remove(member);
            UpdateRail(member.Screen);
        }
        else _parking.Restore(handle);
        QueueRefresh();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused)
        {
            _edge.Stop();
            foreach (var screen in _screens.Values)
            {
                screen.Cancel();
                screen.EdgeSince = 0;
                screen.Rail.SetRevealed(false, animate: false);
            }
        }
        else
        {
            if (_selected.Count > 0) _edge.Start();
            QueueRefresh();
        }
    }

    public void TopologyChanged()
    {
        var displays = DisplayService.Enumerate().Where(_allowDisplay).ToArray();
        if (displays.Length == _screens.Count && displays.All(display =>
                _screens.TryGetValue(display.DeviceName, out var screen) && screen.Display == display)) return;
        foreach (var screen in _screens.Values) screen.Dispose();
        _screens.Clear();
        _members.Clear();
        _parking.RestoreAll();
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0 || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed && !_paused) Refresh();
        }));
    }

    private void Refresh()
    {
        if (!_native.IsAvailable) return;
        foreach (var selected in _selected.Values.ToArray())
        {
            if (!StageWindows.Matches(selected.Identity)) Deselect(selected.Handle);
        }
        if (_selected.Count == 0)
        {
            foreach (var screen in _screens.Values) screen.Rail.SetRevealed(false, animate: false);
            _edge.Stop();
            return;
        }
        _lastInventory = Stopwatch.GetTimestamp();
        var displays = DisplayService.Enumerate().Where(_allowDisplay).ToArray();
        foreach (var display in displays)
        {
            if (_screens.ContainsKey(display.DeviceName)) continue;
            _screens.Add(display.DeviceName, new StageScreen(display, this));
        }

        var foreground = StageWindows.RootOwner(StageWindows.Foreground);
        var snapshot = _native.Enumerate(_selected.ContainsKey).Where(window => IsSelected(window.Identity)).ToArray();
        var currentDesktop = snapshot.FirstOrDefault(window => window.Handle == foreground)?.Desktop
            ?? snapshot.FirstOrDefault(window => !window.Minimized)?.Desktop
            ?? snapshot.FirstOrDefault()?.Desktop;
        if (currentDesktop != _desktop)
        {
            foreach (var screen in _screens.Values) screen.ChangeDesktop(currentDesktop ?? Guid.Empty);
            _desktop = currentDesktop;
        }

        _parking.RemoveClosed();
        _parking.ObserveRestored();
        var seen = new HashSet<nint>();
        // Select the foreground window before the other windows on its monitor.
        var order = snapshot.Select((window, index) => (window.Handle, index)).ToDictionary(window => window.Handle, window => window.index);
        foreach (var window in snapshot.OrderBy(window => window.Handle == foreground ? 0 : 1))
        {
            seen.Add(window.Handle);
            if (_members.TryGetValue(window.Handle, out var previous) && previous.Window.Identity != window.Identity)
            {
                Remove(previous);
                previous = null;
            }
            // Minimized HWNDs may report Windows' off-screen icon rectangle.
            // Keep the last visible placement, including the snapshot from selection.
            var visible = window.Minimized ? previous?.Window ?? _selected[window.Handle] : window;
            var display = FindDisplay(displays, visible.Bounds);
            if (display is null || !_screens.TryGetValue(display.DeviceName, out var screen))
            {
                if (previous is not null && !previous.Screen.Busy) Remove(previous);
                continue;
            }

            if (previous is not null && (previous.Screen != screen || previous.Window.Desktop != window.Desktop))
            {
                if (previous.Screen.Busy || screen.Busy) continue;
                Remove(previous);
                previous = null;
            }

            var workspace = screen.WorkspaceFor(window.Desktop);
            var member = previous ?? new StageMember(visible, screen);
            if (previous is null)
            {
                _members[window.Handle] = member;
                workspace.Add(window.Handle);
            }
            if (!window.Minimized && !screen.Busy)
            {
                member.Window = window;
                member.ZOrder = order[window.Handle];
                _selected[window.Handle] = window;
            }

            var group = workspace.GroupOf(window.Handle);
            if (window.Handle == foreground && !window.Minimized)
            {
                if (!screen.Busy)
                {
                    member.LastFocus = Stopwatch.GetTimestamp();
                    _parking.Forget(window.Handle);
                }
                if (screen.Requested != 0 && !screen.Committing && group != screen.Requested
                    && group != workspace.Active)
                    StartSwitch(screen, group, window.Handle);
                else if (!screen.Busy && group != workspace.Active)
                    StartSwitch(screen, group, window.Handle);
            }
        }

        foreach (var member in _members.Values.ToArray())
        {
            if (seen.Contains(member.Window.Handle) || member.Screen.Busy) continue;
            if (IsOnDesktop(member) || !StageWindows.Matches(member.Window.Identity)) Remove(member);
        }

        foreach (var screen in _screens.Values)
        {
            if (screen.Busy || screen.Collecting || screen.Rail.IsMenuOpen) continue;
            UpdateRail(screen);
            if (StageWindows.IsDesktopForeground) continue;
            var activeParked = GroupMembers(screen, screen.Workspace.Active)
                .Where(member => _parking.Contains(member.Window.Handle) && StageWindows.IsMinimized(member.Window.Handle)).ToArray();
            if (activeParked.Length > 0)
            {
                foreach (var member in activeParked) _parking.Restore(member.Window.Handle);
                continue;
            }
            var foregroundMember = _members.GetValueOrDefault(foreground);
            var toPark = _members.Values.Where(member => member.Screen == screen && IsOnDesktop(member)
                && !StageWindows.IsMinimized(member.Window.Handle) && !_parking.Contains(member.Window.Handle)
                && screen.Workspace.GroupOf(member.Window.Handle) != screen.Workspace.Active).ToArray();
            if (toPark.Length > 0 && (foregroundMember is null || foregroundMember.Screen != screen
                || screen.Workspace.GroupOf(foreground) == screen.Workspace.Active))
                _ = CollectBackgroundAsync(screen, toPark);
            else
            {
                var activeWithoutPreview = GroupMembers(screen, screen.Workspace.Active)
                    .Where(member => !member.PreviewRequested && !StageWindows.IsMinimized(member.Window.Handle)).ToArray();
                if (activeWithoutPreview.Length > 0) _ = WarmActivePreviewsAsync(activeWithoutPreview);
            }
        }
        OnEdge(null, EventArgs.Empty);
    }

    private bool IsOnDesktop(StageMember member) => member.Window.Desktop == _desktop;

    private void Remove(StageMember member)
    {
        var handle = member.Window.Handle;
        member.Screen.WorkspaceFor(member.Window.Desktop).Remove(handle);
        _members.Remove(handle);
        if (!StageWindows.Matches(member.Window.Identity)) _parking.Forget(handle);
        else if (_parking.Contains(handle))
        {
            // Live windows excluded by a changed policy (for example, always-on-top) are released.
            _parking.Restore(handle);
        }
    }

    private async Task CollectBackgroundAsync(StageScreen screen, StageMember[] members)
    {
        screen.Collecting = true;
        try
        {
            var activeWithoutPreview = GroupMembers(screen, screen.Workspace.Active).Where(member => !member.PreviewRequested);
            await WarmAsync(members.Concat(activeWithoutPreview), _lifetime.Token, initial: true);
            if (_disposed || _paused || screen.Closed || screen.Requested != 0) return;
            var foreground = StageWindows.RootOwner(StageWindows.Foreground);
            var eligible = members.Where(member => IsOnDesktop(member)
                && _members.GetValueOrDefault(member.Window.Handle) == member
                && member.Window.Handle != foreground
                && screen.Workspace.GroupOf(member.Window.Handle) != screen.Workspace.Active);
            await _parking.ParkAsync(eligible.Select(member => member.Window),
                identity => !_disposed && !_paused && !screen.Closed && screen.Requested == 0 && IsSelected(identity), _lifetime.Token);
            screen.Motion.Prepare();
            UpdateRail(screen);
        }
        catch (OperationCanceledException) { }
        finally { screen.Collecting = false; }
    }

    private bool IsUserMinimized(StageMember member) =>
        StageWindows.IsMinimized(member.Window.Handle) && !_parking.Contains(member.Window.Handle);

    private async Task WarmActivePreviewsAsync(StageMember[] members)
    {
        try
        {
            await WarmAsync(members, _lifetime.Token, initial: true);
            QueueRefresh();
        }
        catch (OperationCanceledException) { }
    }

    private void StartSwitch(StageScreen screen, nint group, nint external = 0)
    {
        if (_disposed || _paused || group == 0 || screen.Workspace.Members(group).Count == 0
            || screen.Requested == group) return;
        if (screen.Requested == 0 && screen.Workspace.Active == group
            && (external != 0 || !GroupMembers(screen, group).Any(IsUserMinimized))) return;
        _ = SwitchAsync(screen, group, external, merge: false);
    }

    private async Task SwitchAsync(StageScreen screen, nint group, nint external, bool merge)
    {
        var workspace = screen.Workspace;
        var current = workspace.Active;
        var restoreUserMinimized = external == 0 && !merge;
        var restoreActiveCard = restoreUserMinimized && current == group && screen.Requested == 0;
        var foreground = external == 0 ? StageWindows.RootOwner(StageWindows.Foreground) : external;
        var recallInput = restoreUserMinimized ? StageWindows.GetLastInputTick() : null;
        if (!screen.Busy && workspace.GroupOf(foreground) == current
            && _members.TryGetValue(foreground, out var previousFocus))
            previousFocus.LastFocus = Stopwatch.GetTimestamp();
        var request = workspace.BeginSwitch(group);
        var token = screen.Begin(group);
        var started = Stopwatch.GetTimestamp();
        Trace.TraceInformation("Stage Manager switch {0} started on {1}, group {2}, external window {3}",
            request.Generation, screen.Display.DeviceName, group, external);
        try
        {
            var incoming = GroupMembers(screen, group);
            if (restoreActiveCard) incoming = incoming.Where(IsUserMinimized).ToArray();
            else if (!restoreUserMinimized) incoming = incoming.Where(member => !IsUserMinimized(member)).ToArray();
            var manuallyMinimized = restoreUserMinimized ? incoming.Where(IsUserMinimized).ToHashSet() : [];
            var outgoing = merge || current == group ? [] : GroupMembers(screen, current)
                .Where(member => !IsUserMinimized(member)).ToArray();
            var shouldAnimate = !merge && _animate();
            if (shouldAnimate)
            {
                var vacancy = screen.Rail.Slot(group);
                AddMotion(screen, incoming.Where(member => member.Window.Handle != external).ToArray(), vacancy, true);
                UpdateRail(screen, group, current);
                foreach (var other in workspace.Background.Where(key => key != group))
                {
                    AddMotion(screen, GroupMembers(screen, other), screen.Rail.Slot(other), false, movingOnly: true);
                }

                // Let the incoming preview start while capture and the parking journal are still pending.
                screen.Rail.MaskMovingPreviews(screen.Motion.MovingHandles);
                screen.Motion.Start();
                await WarmAsync(outgoing.Where(member => !screen.Motion.HasMotion(member.Window.Handle)), token);
                AddMotion(screen, outgoing, vacancy, false, hold: true);
                screen.Rail.MaskMovingPreviews(screen.Motion.MovingHandles);
                screen.Motion.Start();
                if (outgoing.Any(member => screen.Motion.HasMotion(member.Window.Handle)))
                    await WaitForFrame(token);
            }

            // Move aside only the current group's real windows. Other in-flight groups remain in the rail.
            await _parking.ParkAsync(outgoing.Where(member => member.Window.Handle != external
                && !StageWindows.IsMinimized(member.Window.Handle)).Select(member => member.Window), cancellation: token);
            token.ThrowIfCancellationRequested();

            if (shouldAnimate && screen.Motion.HasMotions)
            {
                await WaitUntil(() => outgoing.All(member => !_parking.Contains(member.Window.Handle)
                    || StageWindows.IsMinimized(member.Window.Handle)), 45, token);
                AddMotion(screen, outgoing, screen.Rail.Slot(current), false);
                // Start the native restore while the last few pixels of the spring are settling.
                await WaitUntil(() => screen.Motion.CanRestoreNativeWindows, 500, token);
            }

            screen.Committing = true;
            var expected = restoreUserMinimized ? incoming : incoming.Where(member => !IsUserMinimized(member)).ToArray();
            var focus = external != 0 ? external : expected.MaxBy(member => member.LastFocus)?.Window.Handle ?? 0;
            var yieldsFocus = false;
            bool MayActivateRecall()
            {
                if (yieldsFocus) return false;
                // Parking the outgoing group can change foreground without new user input.
                var active = StageWindows.RootOwner(StageWindows.Foreground);
                yieldsFocus = active != 0 && active != foreground && active != focus
                    && recallInput is { } initial && StageWindows.GetLastInputTick() is { } latest && initial != latest;
                return !yieldsFocus;
            }
            Func<bool>? mayActivate = restoreUserMinimized ? MayActivateRecall : null;
            await RestoreAsync(expected, focus, token, restoreUserMinimized, mayActivate);
            token.ThrowIfCancellationRequested();

            var activeForeground = StageWindows.RootOwner(StageWindows.Foreground);
            var acceptedFocus = merge || focus != 0 && activeForeground == focus;
            var unready = expected.Where(member => !StageWindows.IsPresented(member.Window.Identity)).Select(member => member.Window.Handle).ToArray();
            // An explicit rail click can complete once the real windows are visible.
            // A delayed foreground grant must not undo a successful restore.
            if (unready.Length > 0 || !acceptedFocus && !restoreUserMinimized)
            {
                Trace.TraceInformation("Stage Manager switch {0} rejected: wanted foreground {1}, actual {2}, unready members {3}",
                    request.Generation, focus, activeForeground, string.Join(",", unready));
                // Preserve an unrelated foreground change, and bring back the previous group's parked windows.
                var visibleIncoming = expected.Where(member => member.Window.Handle != activeForeground
                    && StageWindows.IsPresented(member.Window.Identity)).ToArray();
                foreach (var member in visibleIncoming) _parking.Forget(member.Window.Handle);
                foreach (var member in visibleIncoming.Where(manuallyMinimized.Contains))
                    _ = StageWindows.Minimize(member.Window.Identity);
                if (!merge && !restoreActiveCard)
                    await _parking.ParkAsync(visibleIncoming.Where(member => !manuallyMinimized.Contains(member))
                        .Select(member => member.Window), cancellation: token);
                await RestoreAsync(outgoing, activeForeground == 0 || activeForeground == foreground ? foreground : 0,
                    token, mayActivate: mayActivate);
                return;
            }

            if (merge) workspace.MergeIntoActive(group);
            else workspace.Commit(request);
            var focusMember = expected.FirstOrDefault(member => member.Window.Handle == focus);
            if (!acceptedFocus && focusMember is not null && MayActivateRecall())
                await RetryRecallFocusAsync(focusMember.Window.Identity, foreground, token);
            if (focusMember is not null && StageWindows.RootOwner(StageWindows.Foreground) == focus)
                focusMember.LastFocus = Stopwatch.GetTimestamp();
            foreach (var member in manuallyMinimized)
                if (member.Preview is null) member.PreviewRequested = false;
            if (shouldAnimate && screen.Motion.IsVisible)
            {
                screen.Motion.BeginOpeningHandoff();
                var returning = workspace.Background.Select(key => (Key: key, Members: GroupMembers(screen, key))).ToArray();
                // Keep the moving bitmap in the resting card, even if a newer capture finished during the switch.
                foreach (var member in returning.SelectMany(group => group.Members))
                    member.Preview = screen.Motion.PreviewFor(member.Window.Handle) ?? member.Preview;
                UpdateRail(screen);
                foreach (var item in returning)
                    AddMotion(screen, item.Members, screen.Rail.Slot(item.Key), false, movingOnly: true);
                await WaitUntil(() => screen.Motion.ClosingSettled, 220, token);
                screen.Motion.BeginClosingHandoff(screen.Rail);
                await WaitUntil(() => screen.Motion.HandoffComplete, 180, token);
            }
            Trace.TraceInformation("Stage Manager switch {0} committed after {1:0} ms",
                request.Generation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (screen.End(token))
            {
                UpdateRail(screen);
                QueueRefresh();
            }
        }
    }

    private async Task RetryRecallFocusAsync(StageWindowIdentity target, nint originalForeground, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            var foreground = StageWindows.RootOwner(StageWindows.Foreground);
            if (foreground == target.Hwnd || foreground != 0 && foreground != originalForeground
                || !StageWindows.IsPresented(target)) return;

            _activateWindow(target);
            await WaitUntil(() => StageWindows.RootOwner(StageWindows.Foreground) != foreground, 80, token);
        }
        while (timeout.ElapsedMilliseconds < 900);
    }

    private async Task RestoreAsync(IEnumerable<StageMember> values, nint focus, CancellationToken token,
        bool restoreUserMinimized = false, Func<bool>? mayActivate = null)
    {
        // Restore back-to-front, then anchor the other members behind the actual focus target.
        var members = values.Reverse().Where(member => StageWindows.Matches(member.Window.Identity)).ToArray();
        var toRestore = members.Where(member => _parking.Contains(member.Window.Handle)
            || restoreUserMinimized && StageWindows.IsMinimized(member.Window.Handle)).ToArray();
        foreach (var member in toRestore) Restore(member.Window, placementFallback: false);
        // A foreign GUI thread may still be arranging its restored window after
        // Windows clears its minimized flag. Keep the animation in place until
        // the real window is present, and let Windows choose its restored bounds.
        await WaitUntil(() => toRestore.All(member => StageWindows.IsPresented(member.Window.Identity)), 700, token);
        var waiting = toRestore.Where(member => !StageWindows.IsPresented(member.Window.Identity)).ToArray();
        foreach (var member in waiting) Restore(member.Window, placementFallback: true);
        await WaitUntil(() => waiting.All(member => StageWindows.IsPresented(member.Window.Identity)), 225, token);
        token.ThrowIfCancellationRequested();
        var visible = members.Select(member => member.Window)
            .Where(window => StageWindows.IsPresented(window.Identity)).ToArray();
        foreach (var window in visible) _parking.Forget(window.Handle);
        if (mayActivate?.Invoke() == false) return;

        var target = visible.FirstOrDefault(window => window.Handle == focus);
        if (target is not null)
        {
            StageWindows.Raise(target.Identity);
            var previousForeground = StageWindows.RootOwner(StageWindows.Foreground);
            if (previousForeground != focus)
            {
                _activateWindow(target.Identity);
                await WaitUntil(() => StageWindows.RootOwner(StageWindows.Foreground) == focus, 125, token);
                if (StageWindows.RootOwner(StageWindows.Foreground) == previousForeground && mayActivate?.Invoke() != false)
                {
                    _activateWindow(target.Identity);
                    await WaitUntil(() => StageWindows.RootOwner(StageWindows.Foreground) == focus, 150, token);
                }
            }

            // Do not queue HWND_TOP on another process after selecting the foreground window.
            // The sibling's thread can apply its request later and cover the focused member.
            var anchor = focus;
            foreach (var sibling in visible.Reverse().Where(window => window.Handle != focus))
            {
                if (mayActivate?.Invoke() == false) break;
                var behind = anchor;
                if (!StageWindows.StackBehind(sibling.Identity, behind)) continue;
                await WaitUntil(() => StageWindows.IsDirectlyBehind(sibling.Handle, behind), 35, token);
                if (StageWindows.IsDirectlyBehind(sibling.Handle, behind)) anchor = sibling.Handle;
            }
        }
        else
        {
            foreach (var window in visible) StageWindows.Raise(window.Identity);
        }
        if (toRestore.Length > 0) await WaitForFrame(token);
    }

    private void Restore(StageWindow window, bool placementFallback)
    {
        if (_parking.Contains(window.Handle)) _parking.Restore(window.Handle, placementFallback);
        else if (placementFallback) _ = StageWindows.RestorePlacement(window.Identity, window.Placement);
        else _ = StageWindows.Restore(window.Identity, window.Placement);
    }

    private async Task WarmAsync(IEnumerable<StageMember> values, CancellationToken token, bool initial = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var budget = initial ? 1600 : 105;
        var members = values.Where(member => !StageWindows.IsMinimized(member.Window.Handle)).ToArray();
        foreach (var member in members) member.PreviewRequested = true;
        var captures = members.Select(member => WarmOneAsync(member.Window, member.Icon is null, deadline.Token)).ToArray();
        var all = Task.WhenAll(captures);
        var timeout = Task.Delay(budget, token);
        if (await Task.WhenAny(all, timeout) == all) await all;
        else deadline.Cancel();
        token.ThrowIfCancellationRequested();
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            if (!captures[index].IsCompletedSuccessfully || _members.GetValueOrDefault(member.Window.Handle) != member) continue;
            var (preview, icon) = captures[index].Result;
            if (preview is not null) member.Preview = preview;
            if (icon is not null) member.Icon = icon;
        }
    }

    private async Task<(BitmapSource? Preview, BitmapSource? Icon)> WarmOneAsync(
        StageWindow window, bool needsIcon, CancellationToken token)
    {
        try { await _captures.WaitAsync(token); }
        catch (OperationCanceledException) { return default; }
        try
        {
            return await Task.Run(() =>
            {
                var preview = StagePreviewCapture.Capture(window.Identity, token);
                var icon = needsIcon && StageWindows.Matches(window.Identity) ? StagePreviewCapture.ReadIcon(window.Handle) : null;
                return (preview, icon);
            });
        }
        finally { _captures.Release(); }
    }

    private static async Task WaitUntil(Func<bool> ready, int milliseconds, CancellationToken cancellation)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready() && timeout.ElapsedMilliseconds < milliseconds)
            await Task.Delay(12, cancellation);
        cancellation.ThrowIfCancellationRequested();
    }

    private static async Task WaitForFrame(CancellationToken cancellation)
    {
        // A rendering callback marks the first composition pass. Keep the proxy over
        // the real window through the next pass as well, with a bounded software fallback.
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? first = null;
        void OnRender(object? sender, EventArgs args)
        {
            if (args is not System.Windows.Media.RenderingEventArgs current) return;
            if (first is null) first = current.RenderingTime;
            else if (current.RenderingTime != first) frame.TrySetResult();
        }
        System.Windows.Media.CompositionTarget.Rendering += OnRender;
        try { await Task.WhenAny(frame.Task, Task.Delay(50, cancellation)); }
        finally { System.Windows.Media.CompositionTarget.Rendering -= OnRender; }
        cancellation.ThrowIfCancellationRequested();
    }

    private StageMember[] GroupMembers(StageScreen screen, nint group) => screen.Workspace.Members(group)
        .Select(handle => _members.GetValueOrDefault(handle)).OfType<StageMember>()
        .OrderBy(member => member.ZOrder).ToArray();

    private static void AddMotion(StageScreen screen, StageMember[] members, PixelRect slot, bool opening,
        bool hold = false, bool movingOnly = false)
    {
        var reverse = members.Length - 1;
        foreach (var member in members.Reverse())
        {
            var window = member.Window;
            var index = reverse--;
            var currentPreview = screen.Motion.PreviewFor(window.Handle);
            var preview = currentPreview ?? member.Preview;
            if (preview is null || movingOnly && currentPreview is null || hold && currentPreview is not null) continue;
            var ratio = (double)preview.PixelWidth / preview.PixelHeight;
            var card = StageCardArt.PlaceMember(slot, ratio, index, screen.Display);
            var start = opening ? card : window.Bounds;
            var end = opening ? window.Bounds : card;
            // Use icon placeholders in the rail. A full-size opaque placeholder would
            // obscure the application at the very moment the user asks to interact.
            screen.Motion.Target(window.Handle, member.Art with { Preview = preview }, start, end, opening, hold,
                StageCardArt.PreviewOpacity(index));
        }
    }

    private void UpdateRail(StageScreen screen, nint requested = 0, nint outgoing = 0)
    {
        if (_disposed || screen.Closed) return;
        var workspace = screen.Workspace;
        var cards = workspace.Background.Select(key => key == requested ? outgoing : key)
            .Where(key => key != 0 && key != requested)
            .Select(key => new StageCard(key, GroupMembers(screen, key)
                .Select(member => (member.Window.Handle, member.Art)).ToArray(), key != outgoing && workspace.Active != 0))
            .Where(card => card.Members.Count > 0).ToList();
        var active = workspace.Active;
        if (active != 0 && active != outgoing && active != requested)
        {
            var minimized = GroupMembers(screen, active).Where(IsUserMinimized)
                .Select(member => (member.Window.Handle, member.Art)).ToArray();
            if (minimized.Length > 0) cards.Add(new StageCard(active, minimized, CanMerge: false));
        }
        screen.Rail.Update(cards, _dockBounds);
    }

    private void OnEdge(object? sender, EventArgs args)
    {
        if (_disposed || _paused) return;
        // A partial hook installation or a continuous stream of events can leave the inventory stale.
        if (_selected.Count > 0 && Stopwatch.GetElapsedTime(_lastInventory) >= InventoryInterval) QueueRefresh();
        var cursor = DisplayService.GetCursorPixelPoint();
        var foreground = ForegroundCoverage.GetForegroundBounds();
        var dock = _leftDock();
        if (_dockBounds != dock)
        {
            _dockBounds = dock;
            foreach (var screen in _screens.Values.Where(screen => !screen.Busy && !screen.Rail.IsMenuOpen))
                UpdateRail(screen);
        }
        foreach (var screen in _screens.Values)
        {
            var rail = screen.Rail;
            if (!rail.HasEntries)
            {
                screen.EdgeSince = 0;
                rail.SetRevealed(false, animate: false);
                continue;
            }
            var display = screen.Display;
            var bounds = rail.Bounds;
            var cardArea = rail.CardArea;
            // On an unfocused display, keep tracking its active group using current window rectangles.
            var covered = foreground is { } window && (Intersects(window, display.Bounds)
                ? Intersects(window, cardArea)
                : GroupMembers(screen, screen.Workspace.Active).Any(member =>
                    ForegroundCoverage.GetWindowBounds(member.Window.Handle) is { } memberBounds && Intersects(memberBounds, cardArea)));
            var edgeRevealed = false;
            var left = display.Bounds.X;
            var edgeWidth = display.ToPixels(8);
            var edgePadding = display.ToPixels(12);
            if (covered && cursor is { } point && point.X >= left && point.X < bounds.Right
                && display.Bounds.Contains(point.X, point.Y)
                && point.Y >= bounds.Y - edgePadding && point.Y < bounds.Bottom + edgePadding)
            {
                var hot = point.X < left + edgeWidth;
                if (hot && screen.EdgeSince == 0) screen.EdgeSince = Stopwatch.GetTimestamp();
                edgeRevealed = screen.EdgeSince != 0 && Stopwatch.GetElapsedTime(screen.EdgeSince).TotalMilliseconds >= 90;
                if (!hot && !edgeRevealed) screen.EdgeSince = 0;
            }
            else screen.EdgeSince = 0;
            var revealed = !covered || screen.Requested != 0 || rail.IsMenuOpen || edgeRevealed;
            rail.SetRevealed(revealed, animate: !screen.Busy);
        }
    }

    private static bool Intersects(PixelRect a, PixelRect b) =>
        a.Right > b.X && b.Right > a.X && a.Bottom > b.Y && b.Bottom > a.Y;

    private static DisplayInfo? FindDisplay(IEnumerable<DisplayInfo> displays, PixelRect bounds) =>
        displays.Where(display => Intersects(display.Bounds, bounds))
            .MaxBy(display => (long)(Math.Min(display.Bounds.Right, bounds.Right) - Math.Max(display.Bounds.X, bounds.X))
                             * (Math.Min(display.Bounds.Bottom, bounds.Bottom) - Math.Max(display.Bounds.Y, bounds.Y)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _edge.Stop();
        _events?.Dispose();
        foreach (var screen in _screens.Values) screen.Dispose();
        _parking.RestoreAll();
        _native.Dispose();
        _members.Clear();
        _screens.Clear();
        _lifetime.Dispose();
    }

    private sealed class StageMember(StageWindow window, StageScreen screen)
    {
        public StageWindow Window { get; set; } = window;
        public StageScreen Screen { get; } = screen;
        public bool PreviewRequested { get; set; }
        public BitmapSource? Preview { get; set; }
        public long LastFocus { get; set; }
        public int ZOrder { get; set; }
        public BitmapSource? Icon { get; set; }
        public StageArt Art => new(Window.Title, Preview, Icon);
    }

    private sealed class StageScreen : IDisposable
    {
        private readonly Dictionary<Guid, StageWorkspace> _workspaces = [];
        private CancellationTokenSource? _switch;
        private Guid _desktop;

        public StageScreen(DisplayInfo display, StageManager owner)
        {
            Display = display;
            Rail = new StageRail(display, group => owner.StartSwitch(this, group),
                group => { if (!Busy) _ = owner.SwitchAsync(this, group, 0, merge: true); },
                handle => { if (!Busy) { Workspace.Split(handle); owner.QueueRefresh(); } },
                owner.Deselect, owner._animate);
            Motion = new StageMotionOverlay(display);
        }

        public DisplayInfo Display { get; }
        public StageRail Rail { get; }
        public StageMotionOverlay Motion { get; }
        public StageWorkspace Workspace => WorkspaceFor(_desktop);
        public bool Busy => _switch is not null;
        public bool Collecting { get; set; }
        public bool Committing { get; set; }
        public bool Closed { get; private set; }
        public nint Requested { get; private set; }
        public long EdgeSince { get; set; }

        public StageWorkspace WorkspaceFor(Guid desktop)
        {
            if (!_workspaces.TryGetValue(desktop, out var workspace))
                _workspaces[desktop] = workspace = new();
            return workspace;
        }

        public void ChangeDesktop(Guid desktop)
        {
            Cancel();
            EdgeSince = 0;
            _desktop = desktop;
        }

        public CancellationToken Begin(nint group)
        {
            _switch?.Cancel();
            _switch?.Dispose();
            _switch = new CancellationTokenSource();
            Motion.CancelClosingHandoff();
            Committing = false;
            Requested = group;
            // Window proxies use the resting rail slots as their endpoints.
            if (Rail.IsRevealed) Rail.SetRevealed(true, animate: false);
            Rail.Highlight(group);
            return _switch.Token;
        }

        public bool End(CancellationToken token)
        {
            if (_switch is null || token != _switch.Token) return false;
            Cancel();
            return true;
        }

        public void Cancel()
        {
            _switch?.Cancel();
            _switch?.Dispose();
            _switch = null;
            Requested = 0;
            Committing = false;
            Motion.Reset();
            Rail.MaskMovingPreviews(Array.Empty<nint>());
            Rail.Highlight(0);
        }

        public void Dispose()
        {
            Cancel();
            Closed = true;
            Rail.Close();
            Motion.Close();
        }
    }
}
