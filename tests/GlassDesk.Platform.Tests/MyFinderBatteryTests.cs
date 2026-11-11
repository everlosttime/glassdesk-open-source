using System.Text.Json;
using System.Diagnostics;
using GlassDesk.Platform.Battery;

namespace GlassDesk.Platform.Tests;

public sealed class MyFinderBatteryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
    private static HidBatteryDevice Device(string physical) => new("path:" + physical, physical, "Same model", 1, 2, 0xff00, 1, 1, 64, 64, 64);
    private static Dictionary<string, object?> Reading(string physical = "one", string id = "unit") => new()
    {
        ["id"] = id, ["physicalId"] = physical, ["kind"] = "mouse", ["name"] = "Same model",
        ["percent"] = 65, ["charging"] = false, ["online"] = true, ["fresh"] = true,
        ["approximate"] = false, ["approxText"] = null, ["unconfirmed"] = false,
        ["measuredAtUtc"] = Now.ToString("O"), ["provider"] = "upstream-reader",
        ["support"] = "upstream supported", ["reason"] = "live read", ["receiverSlot"] = null
    };
    private static string Json(params Dictionary<string, object?>[] devices) => JsonSerializer.Serialize(new
    {
        schema = 1, upstreamCommit = "bbcb779be4633ecf9183f53409de118dc9cb1fd6", devices
    });
    private static IReadOnlyList<PeripheralBatterySnapshot> Parse(string json) => MyFinderBatteryProvider.Parse(json, [Device("one"), Device("two")], Now);

    [Fact] public void ExactPhysicalIdentityPreservesMultipleSameModelDevicesAndSourceTime()
    {
        var result = Parse(Json(Reading(), Reading("two")));
        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.DeviceKey.StartsWith("hid:one:mouse:unit:", StringComparison.Ordinal) && item.Percent == 65 && item.SampleTime == Now);
        Assert.Contains(result, item => item.DeviceKey.StartsWith("hid:two:mouse:unit:", StringComparison.Ordinal));
        Assert.Empty(Parse(Json(Reading("unmapped"))));
    }

    [Fact] public void MultipleLogicalUnitsKeepStableDistinctSuffixesAndDefaultAudio()
    {
        var first = Parse(Json(Reading(id: "left"), Reading(id: "right")));
        var reverse = Parse(Json(Reading(id: "right"), Reading(id: "left")));
        Assert.Equal(2, first.Select(item => item.DeviceKey).Distinct().Count());
        Assert.Equal(first.Select(item => item.DeviceKey).Order(), reverse.Select(item => item.DeviceKey).Order());
        var windows = first[0] with { DeviceKey = "hid:one:mouse", Source = "Windows HID", Percent = null, IsDefaultAudio = true };
        var merged = PeripheralBatteryService.Merge(first.Append(windows));
        Assert.Equal(2, merged.Count);
        Assert.All(merged, item => Assert.True(item.IsDefaultAudio));
    }

    [Fact] public void LogicalUnitIdentitySurvivesOtherUnitsConnectingAndDisconnecting()
    {
        var alone = Assert.Single(Parse(Json(Reading(id: "left"))));
        var together = Parse(Json(Reading(id: "left"), Reading(id: "right")));
        Assert.Contains(together, item => item.DeviceKey == alone.DeviceKey);
        Assert.Contains(":unit:", alone.DeviceKey);
    }

    [Theory]
    [InlineData("online", false)]
    [InlineData("fresh", false)]
    [InlineData("measuredAtUtc", null)]
    [InlineData("measuredAtUtc", "2026-11-06T11:00:00Z")]
    [InlineData("measuredAtUtc", "2026-11-06T12:00:30Z")]
    public void StaleOfflineAndMissingOrFutureSourceTimesNeverShowMeasurements(string key, object? value)
    {
        var entry = Reading(); entry[key] = value;
        var result = Assert.Single(Parse(Json(entry)));
        Assert.Null(result.Percent); Assert.Null(result.Charging); Assert.Null(result.Level);
    }

    [Theory] [InlineData("approximate", "~65%")] [InlineData("unconfirmed", "待确认")]
    public void UncertainMeasurementsCannotLookLikeExactLivePercent(string key, string label)
    {
        var entry = Reading(); entry[key] = true;
        var result = Assert.Single(Parse(Json(entry)));
        Assert.Null(result.Percent); Assert.Equal(label, result.Level); Assert.NotEmpty(result.Reason);
    }

    [Fact] public void UnknownPercentPreservesExternalPowerAndReasonWithoutFabricatingTime()
    {
        var entry = Reading(); entry["percent"] = null; entry["charging"] = true; entry["reason"] = "external power; battery level unavailable";
        var result = Assert.Single(Parse(Json(entry)));
        Assert.Null(result.Percent); Assert.True(result.Charging); Assert.Equal(entry["reason"], result.Reason);
        entry["measuredAtUtc"] = null;
        Assert.Equal(DateTimeOffset.MinValue, Assert.Single(Parse(Json(entry))).SampleTime);
    }

    [Fact] public void MalformedSchemasControllersAndInvalidMeasurementsAreRejected()
    {
        Assert.Empty(Parse("{}")); Assert.Empty(Parse("not json"));
        Assert.Empty(Parse(Json(Reading()).Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal)));
        Assert.Empty(Parse(Json(Reading()).Replace("bbcb779be4633ecf9183f53409de118dc9cb1fd6", "untrusted", StringComparison.Ordinal)));
        foreach (var pair in new (string Key, object? Value)[] { ("kind", "controller"), ("percent", 101), ("percent", 4.5), ("charging", "yes"), ("online", 1), ("id", "") })
        {
            var entry = Reading(); entry[pair.Key] = pair.Value; Assert.Empty(Parse(Json(entry)));
        }
        var incomplete = Reading(); incomplete.Remove("fresh"); Assert.Empty(Parse(Json(incomplete)));
        Assert.Empty(Parse(Json(Enumerable.Range(0, 257).Select(i => Reading(id: i.ToString())).ToArray())));
    }

    [Fact] public void NativeLiveReadingWinsButItsFailedReadDoesNotHideEngineReading()
    {
        var engine = Assert.Single(Parse(Json(Reading()))) with { SampleTime = DateTimeOffset.UtcNow };
        var native = engine with { DeviceKey = "hid:one:mouse", Source = "HID Ikf", Percent = 50 };
        Assert.Equal(50, Assert.Single(PeripheralBatteryService.Merge([engine, native])).Percent);
        var recovered = Assert.Single(PeripheralBatteryService.Merge([native with { Percent = null, Connected = false }, engine]));
        Assert.Equal(65, recovered.Percent); Assert.Equal(native.DeviceKey, recovered.DeviceKey);
        var externalPower = engine with { Percent = null, Charging = true, Reason = "external power" };
        var fallback = Assert.Single(PeripheralBatteryService.Merge([native with { Percent = null, Connected = false }, externalPower]));
        Assert.True(fallback.Connected); Assert.True(fallback.Charging); Assert.Equal("external power", fallback.Reason);
    }

    [Fact] public void VerifiedLogitechSlotMatchesNativeWithoutDroppingUnknownUnitsOrOtherSlots()
    {
        var entry = Reading(); entry["provider"] = "logitech"; entry["receiverSlot"] = 2;
        var engine = Assert.Single(Parse(Json(entry))) with { SampleTime = DateTimeOffset.UtcNow };
        Assert.Equal("hid:one:mouse:slot2", engine.DeviceKey); Assert.Equal(2, engine.ReceiverSlot);
        var native = engine with { Source = "HID Logitech HID++", Percent = 50 };
        var unknown = Assert.Single(Parse(Json(Reading(id: "other-reader")))) with { SampleTime = DateTimeOffset.UtcNow };
        var otherSlot = engine with { DeviceKey = "hid:one:mouse:slot3", ReceiverSlot = 3 };
        var result = PeripheralBatteryService.Merge([engine, native, unknown, otherSlot]);
        Assert.Equal(3, result.Count); Assert.Equal(50, result.Single(item => item.ReceiverSlot == 2).Percent);
        Assert.Contains(result, item => item.DeviceKey == unknown.DeviceKey);
        entry["provider"] = "upstream-reader"; Assert.Empty(Parse(Json(entry)));
        entry["provider"] = "logitech"; entry["receiverSlot"] = 7; Assert.Empty(Parse(Json(entry)));
    }

    [Fact] public void AmbiguousNativeBaseNeverConflatesDistinctEngineUnits()
    {
        var units = Parse(Json(Reading(id: "left"), Reading(id: "right")))
            .Select(item => item with { SampleTime = DateTimeOffset.UtcNow }).ToArray();
        var native = units[0] with { Source = "HID Ikf", DeviceKey = "hid:one:mouse", Percent = 50 };
        var merged = PeripheralBatteryService.Merge(units.Append(native));
        Assert.Equal(3, merged.Count);
        Assert.All(units, unit => Assert.Contains(merged, item => item.DeviceKey == unit.DeviceKey));
        var unproven = native with { DeviceKey = "hid:one:mouse:another-unit" };
        Assert.Equal(2, PeripheralBatteryService.Merge([units[0], unproven]).Count);
    }

    [Fact] public async Task PersistentProcessKeepsStateAndSerializesConcurrentRequests()
    {
        using var client = new MyFinderBatteryProvider.PersistentProcessClient(Command("$n=0; while($null -ne ($line=[Console]::In.ReadLine())) { $n++; [Console]::Out.WriteLine([string]$n + ':' + $line); [Console]::Out.Flush() }"));
        Assert.Equal("1:first", await client.QueryAsync("first", CancellationToken.None));
        var second = client.QueryAsync("second", CancellationToken.None);
        var third = client.QueryAsync("third", CancellationToken.None);
        Assert.Equal("2:second", await second); Assert.Equal("3:third", await third);
    }

    [Fact] public async Task PersistentProcessCancellationInvalidatesLateResponseAndKillsChildren()
    {
        var file = Path.Combine(Path.GetTempPath(), "glassdesk-battery-server-" + Guid.NewGuid().ToString("N"));
        using var client = new MyFinderBatteryProvider.PersistentProcessClient(Command("$line=[Console]::In.ReadLine(); $start=New-Object Diagnostics.ProcessStartInfo; $start.FileName=$env:ComSpec; $start.Arguments='/c ping -n 60 127.0.0.1 >nul'; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $p=[Diagnostics.Process]::Start($start); [IO.File]::WriteAllText('" + file.Replace("'", "''", StringComparison.Ordinal) + "', [string]$p.Id); Start-Sleep 60; [Console]::Out.WriteLine('late')"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.QueryAsync("request", cancel.Token);
        try
        {
            while (!File.Exists(file)) { cancel.Token.ThrowIfCancellationRequested(); if (pending.IsCompleted) Assert.Fail(await pending); await Task.Delay(20, cancel.Token); }
            var childId = int.Parse(await File.ReadAllTextAsync(file));
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync("next", CancellationToken.None));
            AssertProcessExited(childId);
        }
        finally { cancel.Cancel(); File.Delete(file); }
    }

    [Fact] public async Task PersistentProcessDisposalImmediatelyKillsIdleHelper()
    {
        var client = new MyFinderBatteryProvider.PersistentProcessClient(Command("while($null -ne ($line=[Console]::In.ReadLine())) { [Console]::Out.WriteLine([string]$PID); [Console]::Out.Flush() }"));
        var processId = int.Parse(await client.QueryAsync("request", CancellationToken.None));
        client.Dispose(); AssertProcessExited(processId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync("next", CancellationToken.None));
    }

    [Fact] public async Task PersistentProcessRejectsOversizedAndUnterminatedResponses()
    {
        foreach (var command in new[] { "$null=[Console]::In.ReadLine(); [Console]::Out.WriteLine(('x' * 1100000)); Start-Sleep 60", "$null=[Console]::In.ReadLine(); [Console]::Out.Write('partial')" })
        {
            using var client = new MyFinderBatteryProvider.PersistentProcessClient(Command(command));
            var error = await Record.ExceptionAsync(() => client.QueryAsync("request", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.IsType<InvalidDataException>(error);
        }
    }

    private static void AssertProcessExited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); Assert.True(process.WaitForExit(2000)); }
        catch (ArgumentException) { /* Process has already been reaped. */ }
    }

    [Fact] public async Task ProcessBoundaryWritesAndClosesStdinBeforeReadingBoundedOutput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MyFinderBatteryProvider.RunProcessAsync(Command("$inputText=[Console]::In.ReadToEnd(); [Console]::Out.Write($inputText)"), "request-body", timeout.Token);
        Assert.Equal("request-body", result);
    }

    [Fact] public async Task ProcessBoundaryCancelsAndKillsChildTree()
    {
        var file = Path.Combine(Path.GetTempPath(), "glassdesk-battery-process-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = MyFinderBatteryProvider.RunProcessAsync(Command("$null=[Console]::In.ReadToEnd(); $start=New-Object Diagnostics.ProcessStartInfo; $start.FileName=$env:ComSpec; $start.Arguments='/c ping -n 60 127.0.0.1 >nul'; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $p=[Diagnostics.Process]::Start($start); [IO.File]::WriteAllText('" + file.Replace("'", "''", StringComparison.Ordinal) + "', [string]$p.Id); Start-Sleep 60"), "request", cancel.Token);
        try
        {
            while (!File.Exists(file)) { cancel.Token.ThrowIfCancellationRequested(); if (task.IsCompleted) Assert.Fail(await task); await Task.Delay(20, cancel.Token); }
            var childId = int.Parse(await File.ReadAllTextAsync(file));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            try { using var child = Process.GetProcessById(childId); Assert.True(child.HasExited); }
            catch (ArgumentException) { /* Process has already been reaped. */ }
        }
        finally { cancel.Cancel(); File.Delete(file); }
    }

    [Fact] public async Task ExcessiveOutputCannotHoldAnUnboundedProcessOpen()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = MyFinderBatteryProvider.RunProcessAsync(Command("$null=[Console]::In.ReadToEnd(); [Console]::Out.Write(('x' * 1100000)); Start-Sleep 60"), "request", timeout.Token);
        var exception = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.True(exception is OperationCanceledException or InvalidDataException, exception?.ToString());
    }

    private static ProcessStartInfo Command(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(argument);
        return start;
    }
}
