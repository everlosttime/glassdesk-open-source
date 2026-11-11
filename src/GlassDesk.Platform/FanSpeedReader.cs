using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CSharp.RuntimeBinder;

namespace GlassDesk.Platform;

/// <summary>
/// 从已安装的硬件监控程序所公开的只读 WMI 传感器读取风扇转速。
/// 未找到可信传感器时返回 null，调用方应隐藏对应界面，而不是显示 0 RPM。
/// </summary>
public static class FanSpeedReader
{
    private const int MinimumPlausibleRpm = 100;
    private const int MaximumPlausibleRpm = 30_000;
    private static readonly TimeSpan HelperFreshness = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions HelperJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] HardwareMonitorNamespaces =
    [
        @"root\LibreHardwareMonitor",
        @"root\OpenHardwareMonitor",
        @"root\Hardware",
    ];

    /// <summary>读取当前可用风扇传感器中的最高转速；没有可靠数据时返回 null。</summary>
    public static int? ReadRpm()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var helperValue = ReadHelperRpm(GetHelperStatusPath(), DateTimeOffset.UtcNow);
        if (helperValue is not null) return helperValue;

        foreach (var wmiNamespace in HardwareMonitorNamespaces)
        {
            var value = ReadHardwareMonitorRpm(wmiNamespace);
            if (value is not null) return value;
        }

        return null;
    }

    /// <summary>用于传感器提供方和测试共同执行同一套有效性判定。</summary>
    public static bool IsPlausibleRpm(double value)
        => double.IsFinite(value) && value is >= MinimumPlausibleRpm and <= MaximumPlausibleRpm;

    /// <summary>
    /// 读取管理员级只读助手最近写入的快照。快照过期、损坏或不可信时返回 null，
    /// 从而让非华硕或不支持该接口的电脑自动隐藏风扇信息。
    /// </summary>
    public static int? ReadHelperRpm(string path, DateTimeOffset utcNow)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var snapshot = JsonSerializer.Deserialize<FanHelperSnapshot>(stream, HelperJsonOptions);
            if (snapshot is not { Version: 1, Rpm: { } rpm }) return null;

            var age = utcNow - snapshot.TimestampUtc;
            if (age < TimeSpan.FromSeconds(-5) || age > HelperFreshness) return null;
            return IsPlausibleRpm(rpm) ? rpm : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static string GetHelperStatusPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GlassDesk",
            "FanSensor",
            "fan-status.json");

    private static int? ReadHardwareMonitorRpm(string wmiNamespace)
    {
        object? locatorObject = null;
        object? serviceObject = null;
        object? resultsObject = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return null;

            locatorObject = Activator.CreateInstance(locatorType);
            if (locatorObject is null) return null;

            dynamic locator = locatorObject;
            serviceObject = locator.ConnectServer(".", wmiNamespace);
            dynamic service = serviceObject;
            resultsObject = service.ExecQuery(
                "SELECT Name, SensorType, Value, Identifier FROM Sensor WHERE SensorType = 'Fan'",
                "WQL",
                0x30);

            var best = 0;
            if (resultsObject is not IEnumerable results) return null;
            foreach (var sensorObject in results)
            {
                try
                {
                    dynamic sensor = sensorObject;
                    var rawValue = Convert.ToDouble(sensor.Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!IsPlausibleRpm(rawValue)) continue;
                    best = Math.Max(best, (int)Math.Round(rawValue));
                }
                catch (Exception ex) when (ex is COMException
                    or InvalidCastException
                    or FormatException
                    or OverflowException
                    or RuntimeBinderException)
                {
                    // 单个传感器字段异常不应影响其他传感器或信息栏。
                }
                finally
                {
                    ReleaseCom(sensorObject);
                }
            }

            return best > 0 ? best : null;
        }
        catch (Exception ex) when (ex is COMException
            or UnauthorizedAccessException
            or InvalidCastException
            or RuntimeBinderException)
        {
            // 命名空间不存在、提供方未运行或访问被 OEM 拒绝时均视为“不支持”。
            return null;
        }
        finally
        {
            ReleaseCom(resultsObject);
            ReleaseCom(serviceObject);
            ReleaseCom(locatorObject);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { _ = Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    private sealed record FanHelperSnapshot(int Version, DateTimeOffset TimestampUtc, int? Rpm);
}
