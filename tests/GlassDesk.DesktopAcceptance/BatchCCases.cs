using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace GlassDesk.DesktopAcceptance;

internal static class BatchCCases
{
    internal static readonly (string Id, string Name, string Requirement)[] Catalog =
    [
        ("launch", "启动、优先激活及多窗口选择", "D-08；D-09；D-10"),
        ("reorder", "真实重排、Esc 取消及取消固定持久化", "D-02；D-03；R-08"),
        ("focus", "菜单与文件夹面板的取消和外部焦点", "D-04；D-11"),
        ("magnification", "放大边界与溢出滚动命中", "D-05；D-17"),
        ("autohide", "延迟隐藏、快速进出和全屏抑制", "D-19"),
        ("desktop", "Dock 系统入口仅保留启动台", "移除显示桌面与回收站入口"),
        ("packaged", "打包应用激活", "D-09"),
        ("settings", "四种位置、偏移即时设置；尺寸固定", "D-15；D-16"),
    ];

    internal static string Name(string name, int round, int rounds) => $"{name}（第 {round}/{rounds} 轮）";

    internal static CaseRecord Run(DriverContext context, string id, int round, int rounds)
    {
        var item = Catalog.Single(x => x.Id == id);
        var evidence = new List<string>();
        string? screenshot = null;
        var status = CaseStatus.NotRun;
        var actual = "该必验组尚未实现完整黑盒步骤，保留 NotRun。";
        try
        {
            if (id is "launch" or "reorder" or "focus" or "desktop" or "settings" or "magnification" or "autohide" or "packaged")
            {
                using var session = new ProductSession(context, id + "-" + round, id == "magnification" ? 18 : 3);
                if (id == "autohide")
                {
                    session.Settings["dock"]!["autoHide"] = true;
                    session.Settings["dock"]!["edgeRevealEnabled"] = true;
                    session.Settings["dock"]!["blockRevealWhenCovered"] = true;
                    session.Settings["dock"]!["autoHideLeaveDelayMs"] = 300;
                }
                if (id == "focus")
                {
                    var folder = Path.Combine(session.Work, "folder"); Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "sample.txt"), "owned sample");
                    session.Entries[2]!["target"] = folder; session.Entries[2]!["kind"] = "Folder";
                }
                if (id == "packaged")
                {
                    var aumid = CalculatorAumid();
                    session.Entries[2]!["target"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    session.Entries[2]!["arguments"] = "shell:AppsFolder\\" + aumid;
                    session.Entries[2]!["appUserModelId"] = aumid;
                    session.Entries[2]!["displayName"] = "Packaged Calculator";
                }
                session.Start(settings: id == "settings");
                evidence.Add("本次数据备份及靶子目录：" + session.Work);
                evidence.Add("产品 pid=" + session.ProcessId + "; Dock=" + Uia.Describe(session.Dock));
                switch (id)
                {
                    case "launch": Launch(session, evidence); break;
                    case "reorder": Reorder(session, evidence); break;
                    case "focus": Focus(session, evidence); break;
                    case "desktop": Desktop(session, evidence); break;
                    case "settings": Settings(session, evidence); break;
                    case "magnification": Magnification(session, evidence); break;
                    case "autohide": AutoHide(session, evidence); break;
                    case "packaged": Packaged(session, evidence); break;
                }
                screenshot = ScreenShot.TryCapture(Path.Combine(session.Work, "passed.png"));
                status = CaseStatus.Passed;
                actual = string.Join("；", evidence.Where(e => e.StartsWith("PASS ")));
            }
        }
        catch (InputBlockedException ex) { status = CaseStatus.Blocked; actual = ex.Message; }
        catch (Exception ex) { status = CaseStatus.Failed; actual = ex.ToString(); }
        if (status is CaseStatus.Failed or CaseStatus.Blocked)
            screenshot = ScreenShot.TryCapture(Path.Combine(context.WorkRoot, $"{id}-{round}-failed.png"));
        return new CaseRecord(Name(item.Name, round, rounds), item.Requirement,
            "UIA 身份定位 + SendInput；读取真实 HWND 与落盘文件", item.Name,
            actual, status, evidence, screenshot);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static nint Handle(string marker, int index = 0)
    {
        Require(Uia.Until(() => File.Exists(marker), 15000), "Target did not create receipt: " + marker);
        var receipt = JsonNode.Parse(File.ReadAllText(marker))!;
        return (nint)receipt["handles"]![index]!.GetValue<long>();
    }

    private static void ClickWindow(nint handle)
    {
        var facts = Win32.Describe(handle) ?? throw new InvalidOperationException("Target HWND gone");
        NativeInput.MoveTo(new ScreenPoint(facts.Rect.Left + 100, facts.Rect.Top + 70));
        NativeInput.LeftDown(); NativeInput.LeftUp();
        Require(Uia.Until(() => Win32.GetForegroundWindow() == handle, 4000), "Target click did not produce expected foreground HWND");
    }

    private static void Launch(ProductSession session, List<string> evidence)
    {
        session.ClickTile("target-0");
        var first = Handle(session.Marker);
        Require(Uia.Until(() => Win32.GetForegroundWindow() == first, 5000), "Launch did not foreground owned window");
        var firstReceipt = File.ReadAllText(session.Marker);
        var outsideMarker = Path.Combine(session.Work, "outside.json");
        using var outside = ProcessLease.Launch(Environment.ProcessPath!, ["--owned-target", outsideMarker, "1", "normal"]);
        var outsideHandle = Handle(outsideMarker);
        ClickWindow(outsideHandle);
        session.ClickTile("target-0");
        Require(Uia.Until(() => Win32.GetForegroundWindow() == first, 5000), "Second click did not activate existing HWND");
        Require(File.ReadAllText(session.Marker) == firstReceipt, "Second click created another target instance");
        evidence.Add($"PASS 启动与再激活 HWND=0x{first:X}；靶子进程回执未变");
        var secondMarker = Path.Combine(session.Work, "second.json");
        using var second = ProcessLease.Launch(session.TargetExe, ["--owned-target", secondMarker, "1", "normal"]);
        var secondHandle = Handle(secondMarker);
        Thread.Sleep(1000);
        ClickWindow(outsideHandle);
        session.ClickTile("target-0");
        var list = FindList(session);
        var rows = Uia.Descendants(list, ControlType.ListItem);
        Require(rows.Count == 2, "Chooser must contain both actual windows");
        NativeInput.PressEscape();
        Require(Uia.Until(() => Win32.GetForegroundWindow() == outsideHandle, 5000), "Esc did not restore previous outside HWND");
        evidence.Add("PASS 多窗口选择器显示 2 项，Esc 恢复先前 HWND");
        session.ClickTile("target-0"); FindList(session);
        NativeInput.PressEnter();
        Require(Uia.Until(() => new[] { first, secondHandle }.Contains(Win32.GetForegroundWindow()), 5000), "Enter did not activate a candidate HWND");
        evidence.Add("PASS Enter 激活候选真实 HWND=0x" + Win32.GetForegroundWindow().ToString("X"));
        ClickWindow(outsideHandle);
        session.ClickTile("target-0"); list = FindList(session);
        var row = Uia.Descendants(list, ControlType.ListItem).First(e => Uia.Name(e).Contains("second"));
        Require(Uia.Click(row, "second target row").Clicked, "Could not click chooser row");
        Require(Uia.Until(() => Win32.GetForegroundWindow() == secondHandle, 5000), "Chooser click activated wrong HWND");
        evidence.Add("PASS 点击选择行激活第二个 HWND=0x" + secondHandle.ToString("X"));
    }

    private static AutomationElement FindList(ProductSession session)
    {
        AutomationElement? Find() => Uia.TopWindowsOf(session.ProcessId)
            .SelectMany(w => Uia.Descendants(w, ControlType.List)).FirstOrDefault();
        Require(Uia.Until(() => Find() != null, 5000), "Window chooser did not appear");
        return Find()!;
    }

    private static ScreenPoint Center(AutomationElement e)
    {
        var r = Uia.RectOf(e); return new(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private static void Reorder(ProductSession session, List<string> evidence)
    {
        var file = ProductLibrary.DockLayoutFile(ProductLibrary.DefaultRoot);
        var original = File.ReadAllText(file);
        var from = Center(session.Tile("target-0")); var to = Center(session.Tile("target-2"));
        NativeInput.MoveTo(from); Thread.Sleep(200); NativeInput.LeftDown();
        try
        {
            foreach (var p in NativeInput.Interpolate(from, to, 16)) { NativeInput.MoveTo(p); Thread.Sleep(30); }
            Thread.Sleep(200);
            Require(File.ReadAllText(file) == original, "Drag preview wrote the layout");
            NativeInput.PressEscape();
        }
        finally { NativeInput.LeftUp(); }
        Thread.Sleep(400);
        Require(File.ReadAllText(file) == original, "Esc cancellation wrote the layout");
        evidence.Add("PASS 拖拽预览与 Esc 取消不写盘");
        NativeInput.MoveTo(new ScreenPoint(30, 30)); Thread.Sleep(250);
        from = Center(session.Tile("target-0")); to = Center(session.Tile("target-2"));
        NativeInput.DragPath(NativeInput.Interpolate(from, to, 20));
        Require(Uia.Until(() => !ProductLibrary.ReadPinned(ProductLibrary.DefaultRoot).SequenceEqual(new[] { "target-0", "target-1", "target-2" }), 5000), "Real drop did not reorder");
        var ordered = ProductLibrary.ReadPinned(ProductLibrary.DefaultRoot).ToArray();
        Require(File.Exists(file + ".bak") && File.ReadAllText(file + ".bak") == original, "Commit backup is not pre-drag layout; possible multiple writes");
        session.Restart();
        Require(ProductLibrary.ReadPinned(ProductLibrary.DefaultRoot).SequenceEqual(ordered), "Restart lost reorder");
        evidence.Add("PASS 真实重排落盘，备份保留提交前版本；新进程重载顺序=" + string.Join(",", ordered));
        from = Center(session.Tile("target-0"));
        NativeInput.DragPath(NativeInput.Interpolate(from, new ScreenPoint(from.X, from.Y - 180), 20));
        Require(Uia.Until(() => !ProductLibrary.ReadPinned(ProductLibrary.DefaultRoot).Contains("target-0"), 5000), "Dragging away did not unpin");
        session.Restart();
        Require(!ProductLibrary.ReadPinned(ProductLibrary.DefaultRoot).Contains("target-0") &&
            ProductLibrary.ReadEntries(ProductLibrary.DefaultRoot).Any(e => e.Id == "target-0"), "Unpin removed library entry or failed persistence");
        evidence.Add("PASS 移出取消固定并重启，应用库记录仍保留");
    }

    private static void Focus(ProductSession session, List<string> evidence)
    {
        var marker = Path.Combine(session.Work, "outside.json");
        using var outside = ProcessLease.Launch(Environment.ProcessPath!, ["--owned-target", marker, "2", "normal"]);
        var first = Handle(marker); var second = Handle(marker, 1);
        ClickWindow(first);
        NativeInput.RightClick(Center(session.Tile("target-0"))); Thread.Sleep(250);
        Require(Uia.TopWindowsOf(session.ProcessId).Any(w => Uia.Descendants(w, ControlType.MenuItem).Count > 0), "Context menu did not appear");
        NativeInput.PressEscape();
        Require(Uia.Until(() => Win32.GetForegroundWindow() == first, 4000), "Menu Esc did not return foreground");
        NativeInput.RightClick(Center(session.Tile("target-0"))); Thread.Sleep(200); ClickWindow(second); Thread.Sleep(400);
        Require(Win32.GetForegroundWindow() == second, "Menu dismissal stole external foreground");
        evidence.Add("PASS 右键菜单 Esc 恢复原窗口，外部点击保留新前台");
        ClickWindow(first); session.ClickTile("target-2"); Thread.Sleep(300);
        Require(Uia.TopWindowsOf(session.ProcessId).SelectMany(Uia.Descendants).Any(e => Uia.Name(e).Contains("sample.txt")), "Folder panel did not show its file");
        NativeInput.PressEscape();
        Require(Uia.Until(() => Win32.GetForegroundWindow() == first, 4000), "Folder Esc did not return foreground");
        session.ClickTile("target-2"); Thread.Sleep(200); ClickWindow(second); Thread.Sleep(400);
        Require(Win32.GetForegroundWindow() == second, "Folder dismissal stole external foreground");
        evidence.Add("PASS 文件夹面板显示文件；Esc 恢复原窗口，外部点击保留新前台");
    }

    private static void Desktop(ProductSession session, List<string> evidence)
    {
        var automationIds = Uia.Descendants(session.Dock).Select(Uia.AutomationId).ToHashSet(StringComparer.Ordinal);
        Require(automationIds.Contains("dock-tile:entry:launcher"), "Launcher tile is missing from the Dock");
        Require(!automationIds.Contains("dock-tile:entry:showdesktop"), "Show desktop tile is still present in the Dock");
        Require(!automationIds.Contains("dock-tile:entry:recyclebin"), "Recycle bin tile is still present in the Dock");
        evidence.Add("PASS Dock 系统入口仅含启动台，不含显示桌面和回收站入口");
    }

    private static void Settings(ProductSession session, List<string> evidence)
    {
        AutomationElement? FindWindow() => Uia.TopWindowsOf(session.ProcessId)
            .FirstOrDefault(w => Uia.Name(w).Contains("设置中心", StringComparison.Ordinal));
        Require(Uia.Until(() => FindWindow() != null, 10000), "Settings window did not appear");
        var window = FindWindow()!;
        var dockHandle = Uia.Handle(session.Dock);
        Require(dockHandle != 0, "Dock HWND missing");

        AutomationElement Control(string name)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var element = Uia.Descendants(window).FirstOrDefault(e => Uia.Name(e) == name);
                if (element is not null && !Uia.IsOffscreen(element)) return element;
                var rect = Uia.RectOf(window);
                NativeInput.MoveTo(new ScreenPoint(rect.Left + rect.Width * 3 / 4, rect.Top + rect.Height / 2));
                NativeInput.WheelDown(2);
                Thread.Sleep(180);
            }
            throw new InvalidOperationException("Settings control remained offscreen: " + name);
        }

        void ClickAt(string name, double fraction)
        {
            var element = Control(name);
            var rect = Uia.RectOf(element);
            Require(!rect.IsEmpty, "Settings control has no rect: " + name);
            var point = new ScreenPoint(rect.Left + (int)(rect.Width * fraction), rect.Top + rect.Height / 2);
            NativeInput.MoveTo(point);
            Thread.Sleep(120);
            var top = Win32.RootAncestor(Win32.WindowAt(point));
            if (Win32.ProcessOf(top) != session.ProcessId)
                throw new InputBlockedException(name + " is covered by another process");
            NativeInput.LeftDown(); NativeInput.LeftUp();
        }

        JsonNode Read() => JsonNode.Parse(File.ReadAllText(Path.Combine(ProductLibrary.DefaultRoot, "settings.json")))!;
        string Placement() => Read()["dock"]!["placement"]!.GetValue<string>();
        void RequireFixedMetrics()
        {
            var dock = Read()["dock"]!;
            Require(Math.Abs(dock["iconSize"]!.GetValue<double>() - 48) < 0.001, "Dock icon size is not fixed at 48 DIP");
            Require(Math.Abs(dock["iconSpacing"]!.GetValue<double>() - 8) < 0.001, "Dock spacing is not fixed at 8 DIP");
            Require(Math.Abs(dock["magnificationPeak"]!.GetValue<double>() - 1.35) < 0.001, "Dock magnification is not fixed at 1.35x");
            Require(Math.Abs(Read()["appearance"]!["dockCornerRadius"]!.GetValue<double>() - 40) < 0.001,
                "Dock corners are not fixed to the rounded glass style");
        }

        RequireFixedMetrics();
        var combo = Control("Dock 位置");
        Require(!Uia.Descendants(window).Any(e =>
                Uia.Name(e) is "Dock 图标尺寸" or "Dock 图标间距" or "Dock 放大倍数" or "Dock 圆角"),
            "Dock size or corner adjustment controls are still present");
        var rects = new Dictionary<int, ScreenRect>();
        foreach (var index in new[] { 1, 2, 3, 0 })
        {
            Require(Uia.Click(combo, "Dock placement").Clicked, "Could not open placement control");
            NativeInput.PressHome();
            for (var i = 0; i < index; i++) NativeInput.PressDown();
            NativeInput.PressEnter();
            var expected = new[] { "BottomCenter", "BottomLeft", "BottomRight", "TopCenter" }[index];
            Require(Uia.Until(() => Placement() == expected, 4000), "Placement did not persist: " + expected);
            var bounds = Win32.Describe(dockHandle)?.Rect ?? default;
            Require(!bounds.IsEmpty && Win32.Monitors().Any(m =>
                    m.Work.Contains(new ScreenPoint(bounds.Left, bounds.Top)) &&
                    m.Work.Contains(new ScreenPoint(bounds.Right - 1, bounds.Bottom - 1))),
                "Dock is not fully inside a monitor work area after " + expected);
            rects[index] = bounds;
            evidence.Add("PASS 位置 " + expected + " 窗口=" + bounds.Describe());
            combo = Control("Dock 位置");
        }
        Require(rects[1].Left < rects[0].Left && rects[0].Left < rects[2].Left && rects[3].Top < rects[0].Top,
            "Four placement anchors did not produce distinct expected positions");

        ClickAt("Dock 水平偏移", 0.75);
        Require(Uia.Until(() => Read()["dock"]!["offsetX"]!.GetValue<double>() > 0, 4000), "Horizontal offset was not saved");
        ClickAt("Dock 垂直偏移", 0.75);
        Require(Uia.Until(() => Read()["dock"]!["offsetY"]!.GetValue<double>() > 0, 4000), "Vertical offset was not saved");
        RequireFixedMetrics();
        var saved = Read().ToJsonString();
        session.Restart();
        Require(Read().ToJsonString() == saved, "Restart changed persisted Dock settings");
        evidence.Add("PASS Dock 尺寸、放大和圆角固定；位置与双轴偏移落盘并在重启后保持");
    }

    private static void Magnification(ProductSession session, List<string> evidence)
    {
        AutomationElement Tile(string id) => Uia.Descendants(session.Dock).First(e => Uia.AutomationId(e) == "dock-tile:" + id);
        var first = Tile("target-0");
        var neighbor = Tile("target-1");
        var last = Tile("target-17");
        if (!Uia.IsOffscreen(last))
            throw new InputBlockedException("18 tiles did not overflow this display; scroll hit case needs a narrower viewport.");
        var dock = Win32.Describe(Uia.Handle(session.Dock)) ?? throw new InvalidOperationException("Dock HWND missing");
        Require(Win32.Monitors().Any(m => dock.Rect.Width <= m.Rect.Width), "Dock viewport exceeded monitor width");

        var firstRect = Uia.RectOf(first);
        NativeInput.MoveTo(new ScreenPoint(firstRect.Left + firstRect.Width / 2, firstRect.Top + firstRect.Height / 2));
        Thread.Sleep(600);
        session.ClickTile("target-0");
        Require(Uia.Until(() => File.Exists(session.Marker), 5000), "Center hit launched wrong target");
        evidence.Add("PASS 放大后中心点击启动 target-0，pid=" + JsonNode.Parse(File.ReadAllText(session.Marker))!["pid"]);

        var adjacentRect = Uia.RectOf(neighbor);
        NativeInput.MoveTo(new ScreenPoint(adjacentRect.Left + adjacentRect.Width / 2, adjacentRect.Top + adjacentRect.Height / 2));
        Thread.Sleep(600);
        session.ClickTile("target-1");
        var neighborMarker = Path.Combine(session.Work, "target-1.json");
        Require(Uia.Until(() => File.Exists(neighborMarker), 5000), "Adjacent magnified hit launched wrong target");
        evidence.Add("PASS 相邻放大图标点击启动 target-1");

        var hovered = Tile("target-2");
        var next = Tile("target-3");
        var hoveredRect = Uia.RectOf(hovered);
        NativeInput.MoveTo(new ScreenPoint(hoveredRect.Left + hoveredRect.Width / 2, hoveredRect.Top + hoveredRect.Height / 2));
        Thread.Sleep(600);
        var a = Uia.RectOf(hovered); var b = Uia.RectOf(next);
        var overlapLeft = Math.Max(a.Left, b.Left);
        var overlapRight = Math.Min(a.Right, b.Right);
        var overlapTop = Math.Max(a.Top, b.Top);
        var overlapBottom = Math.Min(a.Bottom, b.Bottom);
        Require(overlapRight - overlapLeft >= 4 && overlapBottom > overlapTop,
            "Magnified adjacent tiles did not expose an overlap boundary");
        var overlapPoint = new ScreenPoint(overlapLeft + 2, (overlapTop + overlapBottom) / 2);
        NativeInput.MoveTo(overlapPoint); Thread.Sleep(80);
        Require(Win32.ProcessOf(Win32.RootAncestor(Win32.WindowAt(overlapPoint))) == session.ProcessId,
            "Overlap point was covered by another process");
        NativeInput.LeftDown(); NativeInput.LeftUp();
        var overlapMarker = Path.Combine(session.Work, "target-2.json");
        Require(Uia.Until(() => File.Exists(overlapMarker), 5000), "Overlap hit did not launch the visually enlarged tile");
        evidence.Add("PASS 重叠边界点击启动放大的 target-2，点击=" + overlapPoint);

        var dockRect = Win32.Describe(Uia.Handle(session.Dock))!.Rect;
        NativeInput.MoveTo(new ScreenPoint(dockRect.Left + dockRect.Width / 2, dockRect.Top + dockRect.Height / 2));
        NativeInput.WheelDown(24);
        Require(Uia.Until(() => !Uia.IsOffscreen(Tile("target-17")), 5000), "Overflow wheel did not reveal the last tile");
        session.ClickTile("target-17");
        var lastMarker = Path.Combine(session.Work, "target-17.json");
        Require(Uia.Until(() => File.Exists(lastMarker), 5000), "Scrolled hit launched wrong target");
        evidence.Add("PASS 溢出滚动后点击启动 target-17，Dock 视口=" + dock.Rect.Describe());
    }

    private static void AutoHide(ProductSession session, List<string> evidence)
    {
        var handle = Uia.Handle(session.Dock);
        Require(handle != 0, "Dock HWND missing");
        ScreenRect Rect() => Win32.Describe(handle)?.Rect ?? default;
        var away = new ScreenPoint(100, 100);
        NativeInput.MoveTo(away);
        Thread.Sleep(1200);
        Require(Uia.Until(() => Rect().Top > Win32.VirtualScreen().Top + Win32.VirtualScreen().Height / 2, 5000),
            "Dock did not slide to its bottom edge");
        var hidden = Rect();
        var edge = new ScreenPoint(hidden.Left + hidden.Width / 2, hidden.Top + 1);
        NativeInput.MoveTo(edge);
        Require(Uia.Until(() => Rect().Top < hidden.Top - 20, 4000), "Edge hotzone did not reveal Dock");
        var shown = Rect();
        NativeInput.MoveTo(new ScreenPoint(shown.Left + shown.Width / 2, shown.Top + shown.Height / 2));
        Thread.Sleep(300);
        shown = Rect();
        NativeInput.MoveTo(away);
        Thread.Sleep(150);
        Require(Math.Abs(Rect().Top - shown.Top) < 5, "Dock hid before the 300 ms leave delay");
        Require(Uia.Until(() => Rect().Top > shown.Top + 20, 4000), "Dock did not hide after leave delay");
        evidence.Add("PASS 离开后 150 ms 保持，延迟后滑出：显示=" + shown.Describe() + " 隐藏=" + Rect().Describe());

        hidden = Rect(); edge = new ScreenPoint(hidden.Left + hidden.Width / 2, hidden.Top + 1);
        NativeInput.MoveTo(edge);
        Require(Uia.Until(() => Rect().Top < hidden.Top - 20, 4000), "Dock did not reveal for rapid entry");
        shown = Rect();
        NativeInput.MoveTo(away);
        Thread.Sleep(100);
        NativeInput.MoveTo(new ScreenPoint(shown.Left + shown.Width / 2, shown.Top + shown.Height / 2));
        Thread.Sleep(500);
        Require(Math.Abs(Rect().Top - shown.Top) < 5, "Rapid re-entry did not cancel pending hide");
        evidence.Add("PASS 快速离开再进入取消隐藏");

        NativeInput.MoveTo(away);
        Require(Uia.Until(() => Rect().Top > shown.Top + 20, 4000), "Dock did not hide before fullscreen check");
        hidden = Rect();
        var marker = Path.Combine(session.Work, "fullscreen-autohide.json");
        using var fullscreen = ProcessLease.Launch(Environment.ProcessPath!, ["--owned-target", marker, "1", "fullscreen"]);
        var fullscreenHandle = Handle(marker);
        ClickWindow(fullscreenHandle);
        edge = new ScreenPoint(hidden.Left + hidden.Width / 2, hidden.Top + 1);
        NativeInput.MoveTo(edge); Thread.Sleep(750);
        Require(Rect().Top >= hidden.Top - 5, "Fullscreen foreground window did not suppress edge reveal");
        evidence.Add("PASS 全屏靶子前台 HWND=0x" + fullscreenHandle.ToString("X") + " 时碰边未唤出");
    }

    private static string CalculatorAumid()
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = ProcessLease.Launch(shell,
            ["-NoProfile", "-NonInteractive", "-Command",
             "$app=Get-StartApps | Where-Object { $_.AppID -like '*WindowsCalculator*' } | Select-Object -First 1; if($app){'AUMID='+$app.AppID}"],
            captureOutput: true);
        var output = process.WaitForOutputLine("AUMID=", 10000);
        if (string.IsNullOrWhiteSpace(output))
            throw new InputBlockedException("Windows Calculator package was not available in this test account");
        return output.Trim();
    }

    private static void Packaged(ProductSession session, List<string> evidence)
    {
        var before = Win32.TopLevelWindows().ToHashSet();
        nint packageHandle = 0;
        try
        {
            session.ClickTile("target-2");
            Require(Uia.Until(() =>
            {
                var hwnd = Win32.GetForegroundWindow();
                var facts = Win32.Describe(hwnd);
                if (facts is null || before.Contains(hwnd) || facts.ProcessId == session.ProcessId ||
                    !facts.Title.Contains("Calculator", StringComparison.OrdinalIgnoreCase) &&
                    !facts.Title.Contains("计算器", StringComparison.OrdinalIgnoreCase)) return false;
                packageHandle = hwnd;
                return true;
            }, 15000), "Packaged Calculator did not create an identifiable foreground window");
            evidence.Add("PASS 打包应用首次启动 HWND=0x" + packageHandle.ToString("X") + "；" + Win32.Describe(packageHandle)!.Describe());

            var outsideMarker = Path.Combine(session.Work, "outside-packaged.json");
            using var outside = ProcessLease.Launch(Environment.ProcessPath!, ["--owned-target", outsideMarker, "1", "normal"]);
            ClickWindow(Handle(outsideMarker));
            session.ClickTile("target-2");
            Require(Uia.Until(() => Win32.GetForegroundWindow() == packageHandle, 6000),
                "Second package click did not activate existing HWND");
            evidence.Add("PASS 第二次点击按 AUMID 激活同一 HWND");
        }
        finally
        {
            if (packageHandle != 0 && !before.Contains(packageHandle))
                Win32.RequestClose(packageHandle);
        }
    }
}
