using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.App;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI;

internal static class StageManagerProbe
{
    public static int Run(string report, bool focusOnly = false, bool delayedRecallOnly = false, bool hoverOnly = false,
        bool captureOnly = false, bool compatibilityOnly = false)
    {
        var display = DisplayService.Enumerate().FirstOrDefault(item => hoverOnly || !item.IsPrimary);
        if (display is null)
        {
            Console.Error.WriteLine(hoverOnly ? "A display is required for the Stage Manager hover probe."
                : "A secondary display is required for the Stage Manager probe.");
            return 2;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var trace = new TextWriterTraceListener(Console.Out);
        Trace.Listeners.Add(trace);
        var result = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                if (hoverOnly)
                {
                    await VerifyRailHover(display);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { Pass = true, Display = display }));
                    result = 0;
                    return;
                }
                if (captureOnly)
                {
                    using var target = await Fixture.Start(report + ".capture", 1);
                    await VerifyCaptureWithoutBorder(target);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { Pass = true, Display = display }));
                    result = 0;
                    return;
                }
                using var first = await Fixture.Start(report + ".first", 0);
                using var second = await Fixture.Start(report + ".second", 1);
                using var third = await Fixture.Start(report + ".third", 2);
                using var untouched = await Fixture.Start(report + ".untouched", 3);
                Require(display.Bounds.Contains(first.Bounds.X, first.Bounds.Y)
                    && display.Bounds.Contains(second.Bounds.X, second.Bounds.Y)
                    && display.Bounds.Contains(third.Bounds.X, third.Bounds.Y)
                    && display.Bounds.Contains(untouched.Bounds.X, untouched.Bounds.Y), "All fixtures stay on the secondary display.");
                if (compatibilityOnly)
                {
                    await VerifyFocusChangeDuringRestore(display, report, first, second, untouched);
                    await VerifyInventoryDuringEventStorm(display, report, first, untouched);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { Pass = true, Display = display }));
                    result = 0;
                    return;
                }
                if (focusOnly || delayedRecallOnly)
                {
                    if (delayedRecallOnly) await VerifyDelayedFirstRecall(display, report, first, untouched);
                    else await VerifyRecallFocus(display, report, first, second, third, untouched);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { Pass = true, Display = display }));
                    result = 0;
                    return;
                }
                Click(first.Bounds.X + 40, first.Bounds.Y + 16);
                await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == first.Handle, "first fixture focused");

                var parking = new StageParking(report + ".recovery");
                using (var stage = new StageManager(app.Dispatcher, parking, () => null, () => true,
                           member => member.Handle == first.Handle || member.Handle == second.Handle || member.Handle == third.Handle
                               || member.Handle == untouched.Handle,
                           item => item.DeviceName == display.DeviceName))
                {
                    await Task.Delay(450);
                    Require(!StageWindows.IsMinimized(first.Handle) && !StageWindows.IsMinimized(second.Handle)
                        && !StageWindows.IsMinimized(third.Handle) && !StageWindows.IsMinimized(untouched.Handle)
                        && Rail(display)?.IsVisible != true, "enabling leaves every window alone until selected");

                    using var picker = new WindowTopmostPicker();
                    await Choose(first, expected: true);
                    await Choose(first, expected: false);
                    Require(!StageWindows.IsMinimized(first.Handle) && Rail(display)?.IsVisible != true,
                        "selecting the same window twice releases it");
                    await Choose(first, expected: true);
                    _ = PostMessage(first.Handle, 0x0112, 0xF020, 0); // WM_SYSCOMMAND / SC_MINIMIZE
                    await Until(() => StageWindows.IsMinimized(first.Handle), "selected fixture minimized through Windows");
                    await Until(() => Rail(display) is { IsVisible: true } minimizedRail && FindCard(minimizedRail, "Terracotta") is not null,
                        "active minimized fixture appears in the rail");
                    Require(!parking.Contains(first.Handle), "user minimization is not managed parking");
                    File.WriteAllText(first.MarkerPath + ".delay-restore", "delay");
                    await ClickCard(display, "Terracotta");
                    await Until(() => File.Exists(first.MarkerPath + ".restore-delayed"), "fixture delayed its first native restore");
                    await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == first.Handle
                        && DisplayService.GetWindowPixelBounds(first.Handle) == first.Bounds, "one click restores and focuses the slow user-minimized fixture");
                    await Until(() => FindCard(Rail(display)!, "Terracotta") is null
                        && !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "restored active fixture leaves the rail");
                    await Task.Delay(240);
                    Require(!StageWindows.IsMinimized(first.Handle) && StageWindows.RootOwner(StageWindows.Foreground) == first.Handle,
                        "first recall remains visible and focused after the animation");

                    await Choose(second, expected: true);
                    await Choose(third, expected: true);
                    await Until(() => StageWindows.IsMinimized(second.Handle), "second fixture parked");
                    Require(!StageWindows.IsMinimized(untouched.Handle)
                        && DisplayService.GetWindowPixelBounds(untouched.Handle) == untouched.Bounds,
                        "unselected window survives enrollment unchanged");

                    async Task Choose(Fixture fixture, bool expected)
                    {
                        bool? result = null;
                        nint picked = 0;
                        picker.Begin(target => { picked = target.Handle; result = stage.ToggleSelection(target); },
                            target => stage.IsSelected(target) ? "点击移出台前调度" : "点击加入台前调度",
                            "台前调度（测试版）", item => item.DeviceName == display.DeviceName);
                        var overlays = Application.Current.Windows.Cast<Window>().Where(window => window.Title == "GlassDesk 台前调度（测试版）选择").ToArray();
                        Require(overlays.Length == 1 && DisplayService.GetWindowPixelBounds(new WindowInteropHelper(overlays[0]).Handle) == display.Bounds,
                            "shared picker stays on the secondary display");
                        Click(fixture.Bounds.X + 40, fixture.Bounds.Y + 16);
                        await Until(() => !picker.IsSelecting, "shared picker accepted the click");
                        Require(picked == fixture.Handle && result == expected,
                            expected ? "explicitly chose a fixture" : "picked the fixture again to release it");
                    }
                    await Until(() => Rail(display)?.IsVisible == true, "secondary rail visible");
                    await Until(() => StageWindows.IsMinimized(third.Handle), "third fixture parked");
                    await Until(() => Rail(display)!.PreviewCount >= 1, "rail contains a captured preview");
                    // Explicitly click the named card, accounting for EnumWindows Z order.
                    var rail = Rail(display)!;
                    await Until(() => TryCardBounds(rail, display, "Cobalt") is not null, "preview card is laid out");
                    await VerifyRailCoverageByUnselectedWindow(display, rail, untouched);
                    Click(first.Bounds.X + 40, first.Bounds.Y + 16);
                    await Until(() => StageWindows.Foreground == first.Handle, "first fixture regains focus after the coverage check");
                    await ClickCard(display, "Cobalt");
                    await Until(() => Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "animated proxy is visible");
                    Require(Application.Current.Windows.OfType<StageMotionOverlay>().Where(window => window.IsVisible)
                        .All(window => DisplayService.GetWindowPixelBounds(new WindowInteropHelper(window).Handle) == display.Bounds),
                        "motion overlay is positioned on the secondary display");
                    var recentPreviewPoses = new Queue<Matrix>();
                    await Until(() => ShowsBluePreviewInFlight(display, second.Bounds, recentPreviewPoses),
                        "the secondary display shows the moving preview between the card and native window");
                    await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == second.Handle
                                      && !StageWindows.IsMinimized(second.Handle), "second fixture restored and focused");
                    Require(StageWindows.IsMinimized(first.Handle), "first fixture parked after switch");
                    await Until(() => DisplayService.GetWindowPixelBounds(second.Handle) == second.Bounds,
                        "second fixture placement preserved");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "first handoff completed");
                    await ClickCard(display, "Terracotta");
                    await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == first.Handle
                                      && !StageWindows.IsMinimized(first.Handle), "first fixture restored and focused");
                    Require(DisplayService.GetWindowPixelBounds(first.Handle) == first.Bounds, "first fixture placement preserved");
                    await Task.Delay(160);

                    // Retarget between three groups while the prior request is still moving.
                    var clicked = new List<string>();
                    var names = new[] { "Cobalt", "Jade", "Terracotta" };
                    for (var index = 0; index < 20; index++)
                    {
                        var name = names[index % names.Length];
                        if (TryCardBounds(Rail(display)!, display, name) is not { } next) continue;
                        Click(next.X + display.ToPixels(62), next.Y + display.ToPixels(49));
                        clicked.Add(name);
                        await Task.Delay(85);
                    }
                    Require(clicked.Count >= 18, "rapid requests reached all three groups");
                    var final = clicked[^1] == "Cobalt" ? second : clicked[^1] == "Jade" ? third : first;
                    await Until(() => !StageWindows.IsMinimized(final.Handle)
                        && StageWindows.RootOwner(StageWindows.Foreground) == final.Handle, "last rapid request wins");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "rapid switch removed its animation overlay");

                    var background = final == second ? first : second;
                    var backgroundTitle = background == second ? "Cobalt" : "Terracotta";
                    InvokeMerge(Rail(display)!, backgroundTitle);
                    await Until(() => !StageWindows.IsMinimized(background.Handle) && !StageWindows.IsMinimized(final.Handle),
                        "merge restores both group members");
                    await Until(() => FindCard(Rail(display)!, backgroundTitle) is null, "merged group committed");
                    _ = PostMessage(background.Handle, 0x0112, 0xF020, 0);
                    await Until(() => StageWindows.IsMinimized(background.Handle)
                        && Rail(display) is { IsVisible: true } memberRail && FindCard(memberRail, backgroundTitle) is not null,
                        "minimized member of the active group appears in the rail");
                    Require(!StageWindows.IsMinimized(final.Handle), "the active sibling stays visible");
                    await ClickCard(display, backgroundTitle);
                    await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == background.Handle
                        && !StageWindows.IsMinimized(final.Handle)
                        && DisplayService.GetWindowPixelBounds(background.Handle) == background.Bounds,
                        "clicking the member card restores that window in place");
                    await Until(() => FindCard(Rail(display)!, backgroundTitle) is null
                        && !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "restored member leaves the rail");
                    var other = final != third && background != third ? third : final != first && background != first ? first : second;
                    var otherTitle = other == third ? "Jade" : other == first ? "Terracotta" : "Cobalt";
                    await ClickCard(display, otherTitle);
                    await Until(() => !StageWindows.IsMinimized(other.Handle)
                        && StageWindows.IsMinimized(background.Handle) && StageWindows.IsMinimized(final.Handle),
                        "outgoing group parks both members");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "group animation handed back to the real windows");
                    await Task.Delay(120);
                    await ClickCard(display, backgroundTitle);
                    await Until(() => !StageWindows.IsMinimized(background.Handle) && !StageWindows.IsMinimized(final.Handle)
                        && StageWindows.IsMinimized(other.Handle), "both group members returned together");
                    await Until(() => DisplayService.GetWindowPixelBounds(background.Handle) == background.Bounds
                        && DisplayService.GetWindowPixelBounds(final.Handle) == final.Bounds, "both group placements preserved");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "group handoff completed");
                    // One member is restored through the OS while its group is in the background.
                    await ClickCard(display, otherTitle);
                    await Until(() => StageWindows.IsMinimized(background.Handle) && StageWindows.IsMinimized(final.Handle)
                        && StageWindows.RootOwner(StageWindows.Foreground) == other.Handle, "group prepared for OS activation");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "standalone handoff completed");
                    _ = PostMessage(background.Handle, 0x0112, 0xF120, 0); // WM_SYSCOMMAND / SC_RESTORE
                    await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == background.Handle, "Windows restored the selected member");
                    await Until(() => !StageWindows.IsMinimized(final.Handle) && StageWindows.IsMinimized(other.Handle),
                        "OS activation brings back the rest of the group");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "OS group handoff completed");

                    using var native = new StageWindows();
                    var railBefore = Rail(display)!;
                    var restingX = Cards(railBefore).First().PointToScreen(new Point()).X;
                    _ = SetCursorPos(display.Bounds.Right - 32, display.Bounds.Bottom - 32);
                    _ = PostMessage(background.Handle, 0x0112, 0xF030, 0); // SC_MAXIMIZE
                    await Until(() => RailIsSliding(railBefore, restingX), "covered rail slides left before hiding");
                    await Until(() => native.Inspect(background.Handle)?.Placement.ShowCommand == 3, "fixture maximized");
                    var maximized = DisplayService.GetWindowPixelBounds(background.Handle);
                    await Until(() => !railBefore.IsVisible, "maximized window hides the rail");
                    var edgeX = display.Bounds.X + display.ToPixels(6);
                    var edgeY = railBefore.Bounds.Y + display.ToPixels(40);
                    _ = SetCursorPos(edgeX, edgeY);
                    await Until(() => RailIsSliding(railBefore, restingX), "left edge slides the rail into view");
                    await Until(() => railBefore.IsVisible && !RailIsSliding(railBefore, restingX), "revealed rail returns to its original position");
                    var revealedCard = CardBounds(railBefore, display, otherTitle);
                    _ = SetCursorPos(revealedCard.X + revealedCard.Width / 2, revealedCard.Y + revealedCard.Height / 2);
                    await Task.Delay(250);
                    Require(railBefore.IsVisible, "the rail stays visible while the pointer moves from the edge onto a card");
                    _ = SetCursorPos(display.Bounds.Right - 32, display.Bounds.Bottom - 32);
                    await Until(() => !railBefore.IsVisible, "leaving the revealed rail hides it while the window still covers it");
                    _ = SetCursorPos(edgeX, edgeY);
                    await ClickCard(display, otherTitle);
                    await Until(() => StageWindows.IsMinimized(background.Handle)
                        && StageWindows.RootOwner(StageWindows.Foreground) == other.Handle, "maximized group parked");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "maximize exit handoff completed");
                    await ClickCard(display, backgroundTitle);
                    await Until(() => native.Inspect(background.Handle)?.Placement.ShowCommand == 3
                        && !StageWindows.IsMinimized(final.Handle), "maximized group restored");
                    await Until(() => DisplayService.GetWindowPixelBounds(background.Handle) == maximized, "maximized placement preserved");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "maximized handoff completed");
                    _ = PostMessage(background.Handle, 0x0112, 0xF020, 0);
                    await Until(() => StageWindows.IsMinimized(background.Handle)
                        && Rail(display) is { IsVisible: true } maxRail && FindCard(maxRail, backgroundTitle) is not null,
                        "user-minimized maximized member appears in the rail");
                    await ClickCard(display, backgroundTitle);
                    await Until(() => native.Inspect(background.Handle)?.Placement.ShowCommand == 3
                        && DisplayService.GetWindowPixelBounds(background.Handle) == maximized,
                        "left card restores maximized placement after user minimization");
                    await Until(() => !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible),
                        "user-minimized maximized member handed off");
                    Require(!StageWindows.IsMinimized(untouched.Handle)
                        && DisplayService.GetWindowPixelBounds(untouched.Handle) == untouched.Bounds,
                        "unselected window survives all switches unchanged");
                    InvokeCardCommand(Rail(display)!, otherTitle, "移出台前调度");
                    await Until(() => !StageWindows.IsMinimized(other.Handle) && FindCard(Rail(display)!, otherTitle) is null,
                        "removing a parked window restores it and removes its card");
                    Require(Application.Current.Windows.OfType<StageOverlay>().All(window => !window.IsVisible
                        || DisplayService.GetWindowPixelBounds(new WindowInteropHelper(window).Handle) is { } bounds
                        && display.Bounds.Contains(bounds.X, bounds.Y)), "all Stage Manager windows stay on the secondary display");
                }
                await Until(() => !StageWindows.IsMinimized(first.Handle) && !StageWindows.IsMinimized(second.Handle)
                    && !StageWindows.IsMinimized(third.Handle),
                    "disabling restores parked windows");
                using (var native = new StageWindows())
                {
                    var original = native.Inspect(third.Handle) ?? throw new InvalidOperationException("Fixture cannot be inspected.");
                    var originalNativeBounds = DisplayService.GetWindowPixelBounds(third.Handle);
                    var recoveryPath = report + ".simulated-crash";
                    await new StageParking(recoveryPath).ParkAsync([original]);
                    await Until(() => StageWindows.IsMinimized(third.Handle), "window parked with a recovery journal");
                    var recovered = new StageParking(recoveryPath);
                    recovered.Recover();
                    await Until(() => !StageWindows.IsMinimized(third.Handle), "new session recovers journaled window");
                    var immediateBounds = DisplayService.GetWindowPixelBounds(third.Handle);
                    Console.WriteLine($"Recovery native bounds before {originalNativeBounds}, after restore event {immediateBounds}");
                    await Until(() => DisplayService.GetWindowPixelBounds(third.Handle) == originalNativeBounds,
                        "recovery preserves window geometry");
                    recovered.ObserveRestored();
                }
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    Pass = true, Display = display, First = first.Bounds, Second = second.Bounds, Third = third.Bounds,
                    InterruptionClicks = 20, RequestedClickIntervalMilliseconds = 85
                }));
                result = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                File.WriteAllText(report, JsonSerializer.Serialize(new { Pass = false, Display = display, Error = error.ToString() }));
            }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static async Task VerifyFocusChangeDuringRestore(DisplayInfo display, string report, Fixture recalled,
        Fixture originalForeground, Fixture newForeground)
    {
        var activationAttempts = 0;
        using var stage = new StageManager(Dispatcher.CurrentDispatcher, new StageParking(report + ".focus-recovery"),
            () => null, () => false, candidate => candidate.Handle == recalled.Handle,
            item => item.DeviceName == display.DeviceName, target =>
            {
                activationAttempts++;
                StageWindows.Activate(target);
            });
        Require(stage.ToggleSelection(WindowTopmostWindows.Inspect(recalled.Handle)!) == true, "slow recall target selected");
        using var native = new StageWindows();
        Require(StageWindows.Minimize(native.Inspect(recalled.Handle)!.Identity), "slow recall target minimized");
        Click(originalForeground.Bounds.X + 40, originalForeground.Bounds.Y + 16);
        await Until(() => StageWindows.Foreground == originalForeground.Handle, "original window holds focus");
        await Until(() => Rail(display) is { IsVisible: true } rail && FindCard(rail, "Terracotta") is not null,
            "slow recall card visible");

        File.WriteAllText(recalled.MarkerPath + ".delay-restore", "delay");
        await ClickCard(display, "Terracotta", async () =>
        {
            await Task.Delay(120);
            Require(!File.Exists(recalled.MarkerPath + ".restore-delayed"), "holding the card does not start the recall");
        });
        await Until(() => File.Exists(recalled.MarkerPath + ".restore-delayed"), "native restore is pending");
        Click(newForeground.Bounds.X + 40, newForeground.Bounds.Y + 16);
        await Until(() => StageWindows.Foreground == newForeground.Handle, "user focuses another window during restore");
        await Until(() => !StageWindows.IsMinimized(recalled.Handle) && FindCard(Rail(display)!, "Terracotta") is null,
            "the requested window finishes restoring");
        Require(activationAttempts == 0 && StageWindows.Foreground == newForeground.Handle,
            $"the pending recall preserves the user's newer focus (activation attempts: {activationAttempts})");
    }

    private static async Task VerifyInventoryDuringEventStorm(DisplayInfo display, string report, Fixture selected, Fixture other)
    {
        using var stage = new StageManager(Dispatcher.CurrentDispatcher, new StageParking(report + ".inventory-recovery"),
            () => null, () => false, candidate => candidate.Handle == selected.Handle, item => item.DeviceName == display.DeviceName);
        Require(stage.ToggleSelection(WindowTopmostWindows.Inspect(selected.Handle)!) == true, "event-storm target selected");
        Click(other.Bounds.X + 40, other.Bounds.Y + 16);
        await Until(() => StageWindows.Foreground == other.Handle, "unmanaged fixture stays in the foreground");
        await Task.Delay(250);

        var sent = 0;
        var storm = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
        storm.Tick += (_, _) =>
        {
            NotifyWinEvent(0x800B, other.Handle, 0, 0); // EVENT_OBJECT_LOCATIONCHANGE
            NotifyWinEvent(0x8002, other.Handle, 0, 0); // EVENT_OBJECT_SHOW
            sent++;
        };
        storm.Start();
        try
        {
            await Until(() => sent >= 5, "window events are arriving continuously");
            using var native = new StageWindows();
            Require(StageWindows.Minimize(native.Inspect(selected.Handle)!.Identity), "background target minimized");
            await Until(() => Rail(display) is { IsVisible: true } rail && FindCard(rail, "Terracotta") is not null,
                "the rail reflects minimization before the event stream ends");
        }
        finally { storm.Stop(); }
    }

    private static async Task VerifyDelayedFirstRecall(DisplayInfo display, string report, Fixture window, Fixture other)
    {
        Click(window.Bounds.X + 40, window.Bounds.Y + 16);
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == window.Handle, "recall target focused");
        var holdActivation = true;
        var blockedActivations = 0;
        using var stage = new StageManager(Dispatcher.CurrentDispatcher, new StageParking(report + ".recovery"),
            () => null, () => true, candidate => candidate.Handle == window.Handle,
            item => item.DeviceName == display.DeviceName, target =>
            {
                if (holdActivation && target.Hwnd == window.Handle) blockedActivations++;
                else StageWindows.Activate(target);
            });
        Require(stage.ToggleSelection(WindowTopmostWindows.Inspect(window.Handle)!) == true, "recall target selected");
        await Task.Delay(900);
        using var native = new StageWindows();
        Require(StageWindows.Minimize(native.Inspect(window.Handle)!.Identity), "native minimize request accepted");
        await Until(() => StageWindows.IsMinimized(window.Handle), "recall target minimized through Windows");
        var cover = window.Bounds with { X = window.Bounds.X + 80, Y = window.Bounds.Y + 50 };
        await other.MoveTo(cover);
        Click(cover.X + 40, cover.Y + 16);
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == other.Handle,
            "unmanaged window owns the foreground after minimization");
        await Until(() => Rail(display) is { IsVisible: true, PreviewCount: > 0 } rail && FindCard(rail, "Terracotta") is not null,
            "minimized target appears in the rail with an animation preview");

        await ClickCard(display, "Terracotta");
        await Until(() => Application.Current.Windows.OfType<StageMotionOverlay>().Any(overlay => overlay.IsVisible),
            "the first click starts the restore animation");
        await Until(() => blockedActivations > 0 && !StageWindows.IsMinimized(window.Handle)
            && DisplayService.GetWindowPixelBounds(window.Handle) == window.Bounds,
            "one click restores the native window and attempts to focus it");
        Require(StageWindows.RootOwner(StageWindows.Foreground) == other.Handle, "activation is delayed during the first handoff");
        await Task.Delay(420);
        Require(!StageWindows.IsMinimized(window.Handle)
            && Application.Current.Windows.OfType<StageMotionOverlay>().Any(overlay => overlay.IsVisible),
            "the window remains restored and the preview stays visible while activation is temporarily denied");

        holdActivation = false;
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == window.Handle
            && FindCard(Rail(display)!, "Terracotta") is null
            && !Application.Current.Windows.OfType<StageMotionOverlay>().Any(overlay => overlay.IsVisible),
            "first click focuses the window and completes the animation after activation is allowed");
        Require(StageWindows.RootOwner(WindowFromPoint(new PointPixel(cover.X + 70, cover.Y + 60))) == window.Handle,
            "the first recall covers the overlapping window");
        keybd_event(0x87, 0, 0, 0);
        keybd_event(0x87, 0, 2, 0);
        await Until(() => File.Exists(window.MarkerPath + ".key"), "keyboard input reaches the first recall without a second click");
    }

    private static async Task VerifyRecallFocus(DisplayInfo display, string report,
        Fixture primary, Fixture sibling, Fixture switchAway, Fixture cover)
    {
        await Until(() => new[] { primary, sibling, switchAway, cover }.All(fixture => !StageWindows.IsMinimized(fixture.Handle)),
            "focus fixtures are visible");
        await primary.MoveTo(primary.Bounds);
        await sibling.MoveTo(primary.Bounds with { X = primary.Bounds.X + 110, Y = primary.Bounds.Y + 95 });
        await cover.MoveTo(primary.Bounds with { X = primary.Bounds.X + 160, Y = primary.Bounds.Y + 125 });

        Click(primary.Bounds.X + 45, primary.Bounds.Y + 16);
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == primary.Handle, "overlap target focused");
        using var stage = new StageManager(Dispatcher.CurrentDispatcher, new StageParking(report + ".focus-recovery"),
            () => null, () => true, member => member.Handle == primary.Handle || member.Handle == sibling.Handle
                || member.Handle == switchAway.Handle, item => item.DeviceName == display.DeviceName);
        foreach (var fixture in new[] { primary, sibling, switchAway })
            Require(stage.ToggleSelection(WindowTopmostWindows.Inspect(fixture.Handle)!) == true, "enrolled focus fixture");

        await Until(() => StageWindows.IsMinimized(sibling.Handle) && StageWindows.IsMinimized(switchAway.Handle),
            "overlap fixtures parked");
        await Until(() => Rail(display) is { IsVisible: true } rail && FindCard(rail, "Cobalt") is not null, "overlap card visible");
        InvokeMerge(Rail(display)!, "Cobalt");
        await Until(() => !StageWindows.IsMinimized(sibling.Handle) && FindCard(Rail(display)!, "Cobalt") is null,
            "overlapping sibling joined the active group");
        Click(primary.Bounds.X + 45, primary.Bounds.Y + 16);
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == primary.Handle, "preferred group member focused");
        await ClickCard(display, "Jade");
        await Until(() => StageWindows.IsMinimized(primary.Handle) && StageWindows.IsMinimized(sibling.Handle)
            && !Application.Current.Windows.OfType<StageMotionOverlay>().Any(window => window.IsVisible), "overlapping group tucked away");

        var overlap = new PointPixel(primary.Bounds.X + 270, primary.Bounds.Y + 205);
        Require(display.Bounds.Contains(overlap.X, overlap.Y), "overlap point is on the secondary display");
        Click(overlap.X, overlap.Y);
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == cover.Handle, "unmanaged overlapping window is foreground");
        File.WriteAllText(sibling.MarkerPath + ".delay-raise", "delay");
        await ClickCard(display, "Terracotta");
        await Until(() => StageWindows.RootOwner(StageWindows.Foreground) == primary.Handle
            && !StageWindows.IsMinimized(sibling.Handle), "rail recall activates the preferred member");
        await Task.Delay(550);
        Require(StageWindows.RootOwner(WindowFromPoint(overlap)) == primary.Handle,
            "recalled foreground member covers the overlapping windows after delayed z-order changes");
        Require(StageWindows.RootOwner(StageWindows.Foreground) == primary.Handle, "recalled window keeps actual foreground focus");
        keybd_event(0x87, 0, 0, 0);
        keybd_event(0x87, 0, 2, 0);
        await Until(() => File.Exists(primary.MarkerPath + ".key"), "keyboard input reaches the recalled window without another click");
    }

    public static int RunTarget(string marker, int index)
    {
        var display = DisplayService.Enumerate().FirstOrDefault(item => !item.IsPrimary);
        if (display is null) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var area = display.WorkArea;
        var bounds = new PixelRect(area.X + display.ToPixels(180 + index * 26), area.Y + display.ToPixels(160 + index * 320),
            Math.Min(display.ToPixels(480), area.Width - display.ToPixels(215)), display.ToPixels(290));
        var window = new Window
        {
            Title = $"Stage Probe / {new[] { "Terracotta", "Cobalt", "Jade", "Untouched" }[index]}",
            Width = display.ToDips(bounds.Width), Height = display.ToDips(bounds.Height),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border
            {
                Background = new[] { Brushes.IndianRed, Brushes.RoyalBlue, Brushes.SeaGreen, Brushes.DarkSlateGray }[index],
                Child = new TextBox { Text = "Stage Manager fixture", HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, FontSize = 24 }
            }
        };
        app.MainWindow = window;
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.F24) File.WriteAllText(marker + ".key", "received");
        };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(handle)!.AddHook(DelayRaise);
        DisplayService.MoveWindowToPixelRect(handle, bounds);
        window.Show();
        DisplayService.MoveWindowToPixelRect(handle, bounds);
        var measured = DisplayService.GetWindowPixelBounds(handle) ?? throw new InvalidOperationException("Fixture has no placement.");
        File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(new Marker(handle, measured)));
        File.Move(marker + ".tmp", marker, true);
        app.Run();
        return 0;

        nint DelayRaise(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message != 0x0046 || lParam == 0) return 0; // WM_WINDOWPOSCHANGING
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & 0x0002) == 0 && position.X != -32000 && position.Y != -32000
                && File.Exists(marker + ".delay-restore"))
            {
                File.Delete(marker + ".delay-restore");
                File.WriteAllText(marker + ".restore-delayed", "delayed");
                Thread.Sleep(520);
            }
            const uint existingRaiseFlags = 0x0010 | 0x0200 | 0x0002 | 0x0001;
            if (position.InsertAfter != 0 || (position.Flags & (existingRaiseFlags | 0x0004)) != existingRaiseFlags
                || !File.Exists(marker + ".delay-raise")) return 0;
            File.Delete(marker + ".delay-raise");
            File.WriteAllText(marker + ".delay-applied", "delayed");
            // Model an application that delays applying the incoming async Z-order request.
            position.Flags |= 0x0004;
            Marshal.StructureToPtr(position, lParam, false);
            app.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(280);
                _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0, existingRaiseFlags);
            });
            return 0;
        }
    }

    private static async Task VerifyCaptureWithoutBorder(Fixture target)
    {
        Click(target.Bounds.X + 40, target.Bounds.Y + 16);
        await Until(() => StageWindows.Foreground == target.Handle, "capture fixture is in front");
        using var native = new StageWindows();
        var identity = native.Inspect(target.Handle)?.Identity ?? throw new InvalidOperationException("Capture fixture has no identity.");
        Require(!HasYellowBorder(target.Bounds), "capture fixture starts without a yellow border");

        var captures = Task.Run(async () =>
        {
            var blueFrames = 0;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var preview = StagePreviewCapture.Capture(identity, CancellationToken.None);
                if (preview is not null)
                {
                    var pixel = new byte[4];
                    preview.CopyPixels(new Int32Rect(preview.PixelWidth / 4, preview.PixelHeight / 3, 1, 1), pixel, 4, 0);
                    if (pixel[0] >= 160 && pixel[0] - pixel[2] >= 80) blueFrames++;
                }
                await Task.Delay(20);
            }
            return blueFrames;
        });
        var sawBorder = false;
        var timer = Stopwatch.StartNew();
        do
        {
            sawBorder |= HasYellowBorder(target.Bounds);
            await Task.Delay(5);
        } while (!captures.IsCompleted || timer.ElapsedMilliseconds < 450);

        var blueFrames = await captures;
        Require(blueFrames >= 8, $"window capture returns real preview pixels ({blueFrames}/10)");
        Require(!sawBorder, "capturing the preview does not show a yellow border on the source window");
    }

    private static bool HasYellowBorder(PixelRect bounds)
    {
        var dc = GetDC(0);
        if (dc == 0) throw new InvalidOperationException("Screen pixels are unavailable.");
        try
        {
            for (var offset = -6; offset <= 6; offset++)
            {
                if (IsYellow(GetPixel(dc, bounds.X + bounds.Width / 3, bounds.Y + offset))
                    && IsYellow(GetPixel(dc, bounds.X + 2 * bounds.Width / 3, bounds.Y + offset))) return true;
            }
            return false;
        }
        finally { _ = ReleaseDC(0, dc); }

        static bool IsYellow(uint pixel) => (pixel & 255) >= 180 && ((pixel >> 8) & 255) >= 155 && ((pixel >> 16) & 255) <= 100;
    }

    private static bool ShowsBluePreviewInFlight(DisplayInfo display, PixelRect nativeBounds, Queue<Matrix> recentPoses)
    {
        var overlay = Application.Current.Windows.OfType<StageMotionOverlay>().FirstOrDefault(window => window.IsVisible);
        if (overlay?.Content is not FrameworkElement surface || VisualTreeHelper.GetChildrenCount(surface) != 2
            || VisualTreeHelper.GetChild(surface, 1) is not ContainerVisual entering || entering.Children.Count == 0
            || entering.Children[0] is not ContainerVisual preview || preview.Transform is not { } transform)
            return false;

        // The desktop compositor can still be displaying a pose from an earlier render callback.
        recentPoses.Enqueue(transform.Value);
        if (recentPoses.Count > 4) recentPoses.Dequeue();
        var railRight = Rail(display)?.Bounds.Right ?? display.Bounds.X;
        var dc = GetDC(0);
        if (dc == 0) return false;
        try
        {
            foreach (var pose in recentPoses)
            foreach (var sample in new Point[] { new(.08, .42), new(.15, .72), new(.30, .60), new(.55, .34) })
            {
                var screen = surface.PointToScreen(pose.Transform(sample));
                var x = (int)Math.Round(screen.X);
                var y = (int)Math.Round(screen.Y);
                if (x <= railRight || !display.Bounds.Contains(x, y) || nativeBounds.Contains(x, y)) continue;
                var pixel = GetPixel(dc, x, y);
                if (pixel == uint.MaxValue) continue;
                var red = (int)(pixel & 0xff);
                var green = (int)((pixel >> 8) & 0xff);
                var blue = (int)((pixel >> 16) & 0xff);
                if (blue >= 160 && blue - red >= 80 && blue - green >= 55) return true;
            }
            return false;
        }
        finally { _ = ReleaseDC(0, dc); }
    }

    private static StageRail? Rail(DisplayInfo display) => Application.Current.Windows.OfType<StageRail>()
        .SingleOrDefault(rail => display.Bounds.Contains(rail.Bounds.X, rail.Bounds.Y));

    private static async Task VerifyRailCoverageByUnselectedWindow(DisplayInfo display, StageRail rail, Fixture window)
    {
        var railBounds = rail.Bounds;
        var covering = new PixelRect(display.WorkArea.X, railBounds.Y, window.Bounds.Width, window.Bounds.Height);
        await window.MoveTo(covering);
        Click(covering.X + covering.Width / 2, covering.Y + display.ToPixels(40));
        await Until(() => StageWindows.Foreground == window.Handle, "unselected window becomes foreground over the rail");
        await Until(() => !rail.IsVisible, "the rail hides when an unselected foreground window covers it");

        await window.MoveTo(window.Bounds);
        await Until(() => rail.IsVisible, "the rail returns when the foreground window moves away");
        Require(StageWindows.Foreground == window.Handle, "moving away reveals the rail without changing window focus");
    }

    private static IEnumerable<System.Windows.Controls.Border> Cards(StageRail rail) =>
        ((StackPanel)((ScrollViewer)rail.Content).Content).Children.OfType<System.Windows.Controls.Border>();

    private static bool RailIsSliding(StageRail rail, double restingX) => rail.IsVisible
        && Cards(rail).FirstOrDefault() is { IsVisible: true } card && card.PointToScreen(new Point()).X < restingX - 2;

    private static FrameworkElement? FindCard(StageRail rail, string title) =>
        Cards(rail).FirstOrDefault(card => System.Windows.Automation.AutomationProperties.GetName(card)
            .Contains(title, StringComparison.Ordinal));

    private static PixelRect? TryCardBounds(StageRail rail, DisplayInfo display, string title)
    {
        if (FindCard(rail, title) is not { IsVisible: true } card || card.ActualHeight <= 0) return null;
        var origin = card.PointToScreen(new Point());
        return new((int)origin.X, (int)origin.Y, display.ToPixels(card.ActualWidth), display.ToPixels(card.ActualHeight));
    }

    private static PixelRect CardBounds(StageRail rail, DisplayInfo display, string title) =>
        TryCardBounds(rail, display, title) ?? throw new InvalidOperationException($"Missing card {title}.");

    private static async Task VerifyRailHover(DisplayInfo display)
    {
        const string title = "Hover preview";
        nint selected = 0;
        var rail = new StageRail(display, group => selected = group, _ => { }, _ => { }, _ => { }, () => true);
        var previousCursor = DisplayService.GetCursorPixelPoint();
        try
        {
            var preview = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 160, 160, 160, 255, 160, 160, 160, 255 }, 8);
            rail.Update([new StageCard(1, [(1, new StageArt(title, preview, null))], false)], null);
            rail.SetRevealed(true, animate: false);
            await Until(() => TryCardBounds(rail, display, title) is not null, "preview card is visible");
            await VerifyCardHover(rail, display, title);
            var bounds = CardBounds(rail, display, title);
            _ = SetCursorPos(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            mouse_event(0x0002, 0, 0, 0, 0);
            mouse_event(0x0004, 0, 0, 0, 0);
            await Until(() => selected == 1, "clicking the highlighted card still selects its window");
        }
        finally
        {
            rail.Close();
            if (previousCursor is { } cursor) _ = SetCursorPos(cursor.X, cursor.Y);
        }
    }

    private static async Task VerifyCardHover(StageRail rail, DisplayInfo display, string title)
    {
        var card = FindCard(rail, title) ?? throw new InvalidOperationException($"Missing card {title}.");
        var bounds = CardBounds(rail, display, title);
        var foreground = StageWindows.Foreground;
        var outsideX = rail.Bounds.Right + display.ToPixels(12);
        var y = bounds.Y + bounds.Height / 2;
        _ = SetCursorPos(outsideX, y);
        await Until(() => !card.IsMouseOver, "pointer starts outside the preview card");
        var resting = ReadCardPixels(card);

        _ = SetCursorPos(bounds.X + bounds.Width / 2, y);
        await Until(() => card.IsMouseOver, "pointer reaches the preview card");
        await Until(() => ReadCardPixels(card).PreviewBlue >= resting.PreviewBlue + 16, "hover brightens the dimmed preview image");
        await Until(() => ReadCardPixels(card).EdgeBlue >= resting.EdgeBlue + 10, "hover lights the original edge of the preview image");
        Require(ReadCardPixels(card).OuterAlpha == resting.OuterAlpha, "hover does not paint an outer card border");
        Require(bounds == CardBounds(rail, display, title) && StageWindows.Foreground == foreground,
            "hover keeps the card in place and leaves window focus alone");

        _ = SetCursorPos(outsideX, y);
        await Until(() => !card.IsMouseOver && ReadCardPixels(card) == resting,
            "leaving the card clears the highlight");
    }

    private static (byte PreviewBlue, byte EdgeBlue, byte OuterAlpha) ReadCardPixels(FrameworkElement card)
    {
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        rendered.Render(card);
        var pixel = new byte[4];
        // The centered 2:1 fixture has its straight top edge at y=17 DIP and center x=76 DIP.
        var edgeX = rendered.PixelWidth / 2 - 4;
        return (Sample(edgeX, 50, 0), Sample(edgeX, 16, 0), Sample(edgeX, 8, 3));

        byte Sample(int x, int y, int channel)
        {
            rendered.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel[channel];
        }
    }

    private static async Task ClickCard(DisplayInfo display, string title, Func<Task>? whilePressed = null)
    {
        PixelRect? slot = null;
        var lastHit = nint.Zero;
        var lastRail = nint.Zero;
        try
        {
            await Until(() =>
            {
                if (Rail(display) is not { } rail) return false;
                lastRail = new WindowInteropHelper(rail).Handle;
                slot = TryCardBounds(rail, display, title);
                if (slot is not { } rect) return false;
                lastHit = WindowFromPoint(new PointPixel(rect.X + display.ToPixels(62), rect.Y + display.ToPixels(49)));
                return lastHit == lastRail;
            }, $"card {title} is hittable");
        }
        catch
        {
            Console.WriteLine($"Card {title} bounds {slot}, hit {lastHit}, rail {lastRail}, native rail {DisplayService.GetWindowPixelBounds(lastRail)}, visible {Rail(display)?.IsVisible}");
            Console.WriteLine($"Rail hit window fits at {DisplayService.GetWindowPixelBounds(lastHit)}");
            throw;
        }
        if (slot is not { } bounds) return;
        _ = SetCursorPos(bounds.X + display.ToPixels(62), bounds.Y + display.ToPixels(49));
        mouse_event(0x0002, 0, 0, 0, 0);
        try { if (whilePressed is not null) await whilePressed(); }
        finally { mouse_event(0x0004, 0, 0, 0, 0); }
    }

    private static void InvokeMerge(StageRail rail, string title) => InvokeCardCommand(rail, title, "加入当前组");

    private static void InvokeCardCommand(StageRail rail, string title, string command)
    {
        var card = FindCard(rail, title) ?? throw new InvalidOperationException("Group card is missing.");
        var item = card.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(menu => Equals(menu.Header, command))
                   ?? throw new InvalidOperationException($"Missing card command {command}.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static async Task Until(Func<bool> condition, string expectation)
    {
        var timeout = Stopwatch.StartNew();
        var satisfied = condition();
        while (!satisfied && timeout.Elapsed < TimeSpan.FromSeconds(4))
        {
            await Task.Delay(20);
            satisfied = condition();
        }
        Require(satisfied, expectation);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine(name);
    }

    private static void Click(int x, int y)
    {
        var secondary = DisplayService.Enumerate().First(item => !item.IsPrimary);
        if (!secondary.Bounds.Contains(x, y)) throw new InvalidOperationException("Probe click outside secondary display.");
        _ = SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, 0);
        mouse_event(0x0004, 0, 0, 0, 0);
    }

    private sealed record Marker(long Handle, PixelRect Bounds);
    private sealed class Fixture(Process child, Marker marker, string markerPath) : IDisposable
    {
        public nint Handle => (nint)marker.Handle;
        public PixelRect Bounds => marker.Bounds;
        public string MarkerPath => markerPath;

        public async Task MoveTo(PixelRect bounds)
        {
            _ = PostMessage(Handle, 0x0112, 0xF120, 0);
            await Until(() => !StageWindows.IsMinimized(Handle), "overlap fixture restored");
            _ = SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x4000 | 0x10 | 0x4);
            await Until(() => DisplayService.GetWindowPixelBounds(Handle) == bounds, "overlap fixture positioned");
        }

        public static async Task<Fixture> Start(string markerPath, int index)
        {
            File.Delete(markerPath);
            File.Delete(markerPath + ".key");
            File.Delete(markerPath + ".delay-raise");
            File.Delete(markerPath + ".delay-applied");
            File.Delete(markerPath + ".delay-restore");
            File.Delete(markerPath + ".restore-delayed");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(typeof(StageManagerProbe).Assembly.Location);
            info.ArgumentList.Add("--stage-manager-target");
            info.ArgumentList.Add(markerPath);
            info.ArgumentList.Add(index.ToString(CultureInfo.InvariantCulture));
            var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch Stage Manager fixture.");
            try
            {
                await Until(() => File.Exists(markerPath) || process.HasExited, "fixture started");
                var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath))
                             ?? throw new InvalidOperationException("Malformed Stage Manager fixture marker.");
                return new Fixture(process, marker, markerPath);
            }
            catch
            {
                if (!process.HasExited) process.Kill();
                process.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!child.HasExited)
            {
                _ = PostMessage(Handle, 0x0010, 0, 0);
                if (!child.WaitForExit(1500)) child.Kill();
            }
            child.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct PointPixel(int X, int Y);
    [StructLayout(LayoutKind.Sequential)]
    private record struct WindowPosition(nint Window, nint InsertAfter, int X, int Y, int Width, int Height, uint Flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointPixel point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scanCode, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint kind, nint window, int objectId, int childId);
}
