using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace GlassDesk.Platform.Battery;

/// <summary>Reads the independent, pinned upstream CLI; no setters or development commands are exposed.</summary>
internal sealed class HeadsetControlBatteryProvider(string? executable = null) : IPeripheralBatteryProvider
{
    private readonly string _executable = executable ?? Path.Combine(AppContext.BaseDirectory,
        "components", "headsetcontrol", "headsetcontrol-windows-x86_64.exe");

    public async Task<IReadOnlyList<PeripheralBatterySnapshot>> QueryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executable)) return [];
        var devices = await Task.Run(NativeHidTransport.Enumerate, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-b", "-o", "json" }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return [];
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? Parse(json, devices, DateTimeOffset.UtcNow) : [];
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<PeripheralBatterySnapshot> Parse(string json,
        IReadOnlyList<HidBatteryDevice> devices, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<PeripheralBatterySnapshot>();
        if (!document.RootElement.TryGetProperty("devices", out var entries)
            || entries.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryId(entry, "id_vendor", out var vendor) || !TryId(entry, "id_product", out var product)) continue;
            var matches = devices.Where(device => device.VendorId == vendor && device.ProductId == product)
                .DistinctBy(device => device.PhysicalId, StringComparer.OrdinalIgnoreCase).ToArray();
            var name = entry.TryGetProperty("device", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString() ?? "无线耳机" : "无线耳机";
            var status = string.Empty;
            int? percent = null;
            if (entry.TryGetProperty("battery", out var battery) && battery.ValueKind == JsonValueKind.Object)
            {
                if (battery.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String)
                    status = state.GetString() ?? string.Empty;
                if (status is "BATTERY_AVAILABLE" or "BATTERY_CHARGING"
                    && battery.TryGetProperty("level", out var level) && level.TryGetInt32(out var number)
                    && number is >= 0 and <= 100) percent = number;
            }
            foreach (var device in matches)
            {
                var ambiguous = matches.Length != 1;
                var reading = ambiguous ? null : percent;
                result.Add(new($"hid:{device.PhysicalId}:headset", name, PeripheralBatteryCategory.Headset,
                    reading.HasValue, reading, status == "BATTERY_CHARGING" ? true : null, now,
                    "HeadsetControl 4.2.0", "上游支持 · 待本机验证",
                    ambiguous ? "同时存在多个同型号设备，无法可靠关联电量" : reading.HasValue
                        ? "当前设备上报电量" : "设备休眠、未连接或暂时无法读取电量",
                    PhysicalDeviceId: device.PhysicalId));
            }
        }
        return PeripheralBatteryService.Merge(result);
    }

    private static bool TryId(JsonElement entry, string key, out ushort id)
    {
        id = 0;
        if (!entry.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString() ?? string.Empty;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }
}
