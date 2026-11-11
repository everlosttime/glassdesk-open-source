namespace GlassDesk.Platform.Battery;
public enum PeripheralBatteryCategory { Mouse, Headset, Keyboard }
/// <summary>A current reading belonging to a physical connection, never a friendly-name cache.</summary>
public sealed record PeripheralBatterySnapshot(string DeviceKey, string Name, PeripheralBatteryCategory Category,
 bool Connected, int? Percent, bool? Charging, DateTimeOffset SampleTime, string Source, string Support,
 string Reason, int? ReceiverSlot = null, string? PhysicalDeviceId = null, bool IsDefaultAudio = false, string? Level = null);
internal interface IPeripheralBatteryProvider
{
 Task<IReadOnlyList<PeripheralBatterySnapshot>> QueryAsync(CancellationToken cancellationToken);
}
