using System.Collections;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class AudioOutputControllerTests
{
    [Fact]
    public void ConcurrentAudioReadersKeepTheirComReferencesAlive()
    {
        var initial = AudioOutputController.GetDevices();
        if (initial.Count == 0) return;
        Parallel.For(0, 80, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            var outputs = AudioOutputController.GetDevices();
            Assert.True(outputs.Count(device => device.IsDefault) == 1, AudioOutputController.LastDiagnostic);
            Assert.True(SystemStatusReader.Read().VolumePercent is not null, SystemStatusReader.LastAudioDiagnostic);
            _ = ActiveAudioApplicationReader.Read();
        });
    }

    [Fact]
    public void RealOutputCanSwitchAndRestoreWhenAcceptanceIsEnabled()
    {
        if (Environment.GetEnvironmentVariable("GLASSDESK_AUDIO_SWITCH_ACCEPTANCE") != "1") return;
        var before = AudioOutputController.GetDevices();
        var original = Assert.Single(before, device => device.IsDefault);
        var alternate = before.First(device => !device.IsDefault);
        try
        {
            Assert.True(AudioOutputController.SetDefault(alternate.Id), AudioOutputController.LastDiagnostic);
            Assert.Equal(alternate.Id, Assert.Single(AudioOutputController.GetDevices(), device => device.IsDefault).Id);
        }
        finally
        {
            Assert.True(AudioOutputController.SetDefault(original.Id), "Restore: " + AudioOutputController.LastDiagnostic);
            Assert.Equal(original.Id, Assert.Single(AudioOutputController.GetDevices(), device => device.IsDefault).Id);
        }
    }

    [Fact]
    public void AvailableOutputsHaveDistinctIdsAndNamesAndInvalidSelectionDoesNotChangeDefault()
    {
        var type = typeof(SystemStatusReader).Assembly.GetType("GlassDesk.Platform.AudioOutputController");
        Assert.NotNull(type);
        var read = type.GetMethod("GetDevices")!;
        var before = ((IEnumerable)read.Invoke(null, null)!).Cast<object>().ToArray();
        if (SystemStatusReader.Read().VolumePercent is not null)
        {
            Assert.True(before.Length > 0, "The default endpoint has readable volume, so output enumeration must include it. "
                + type.GetProperty("LastDiagnostic")?.GetValue(null));
            Assert.Single(before, device => (bool)device.GetType().GetProperty("IsDefault")!.GetValue(device)!);
        }
        string Value(object device, string property) => (string)device.GetType().GetProperty(property)!.GetValue(device)!;
        foreach (var device in before)
        {
            Assert.False(string.IsNullOrWhiteSpace(Value(device, "Id")));
            Assert.False(string.IsNullOrWhiteSpace(Value(device, "Name")));
        }
        Assert.Equal(before.Length, before.Select(device => Value(device, "Id")).Distinct().Count());
        var set = type.GetMethod("SetDefault")!;
        Assert.Equal(false, set.Invoke(null, [""]));
        Assert.Equal(false, set.Invoke(null, ["GlassDesk-missing-device-" + Guid.NewGuid()]));
        var after = ((IEnumerable)read.Invoke(null, null)!).Cast<object>().ToArray();
        string[] Defaults(object[] devices) => devices.Where(device => (bool)device.GetType().GetProperty("IsDefault")!.GetValue(device)!)
            .Select(device => Value(device, "Id")).ToArray();
        Assert.Equal(Defaults(before), Defaults(after));
    }
}
