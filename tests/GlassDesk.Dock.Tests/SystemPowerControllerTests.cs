using System.Diagnostics;
using System.IO;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class SystemPowerControllerTests
{
    [Theory]
    [InlineData(SystemPowerAction.Shutdown)]
    [InlineData(SystemPowerAction.Restart)]
    public async Task CancelingConfirmationDoesNotStartAnyProcess(SystemPowerAction action)
    {
        var confirmed = new List<SystemPowerAction>();
        var controller = new SystemPowerController(selected =>
        {
            confirmed.Add(selected);
            return false;
        }, _ => throw new Xunit.Sdk.XunitException("Canceled action started a process."));

        Assert.False(await controller.RequestAsync(action));
        Assert.Equal(new[] { action }, confirmed);
    }

    [Theory]
    [InlineData(SystemPowerAction.Shutdown, "/s")]
    [InlineData(SystemPowerAction.Restart, "/r")]
    public async Task ConfirmedActionStartsTheMatchingSystemCommandWithoutForcingApplicationsClosed(
        SystemPowerAction action, string expectedSwitch)
    {
        ProcessStartInfo? command = null;
        var confirmed = new List<SystemPowerAction>();
        var controller = new SystemPowerController(selected =>
        {
            confirmed.Add(selected);
            return true;
        }, info => { command = info; return Task.CompletedTask; });

        Assert.True(await controller.RequestAsync(action));
        Assert.Equal(new[] { action }, confirmed);
        Assert.NotNull(command);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), command.FileName);
        Assert.Equal(new[] { expectedSwitch, "/t", "0" }, command.ArgumentList);
        Assert.False(command.UseShellExecute);
        Assert.True(command.CreateNoWindow);
    }

    [Fact]
    public async Task WindowsFailureIsReportedToTheCaller()
    {
        var controller = new SystemPowerController(_ => true,
            _ => Task.FromException(new InvalidOperationException("Access denied")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RequestAsync(SystemPowerAction.Shutdown));
        Assert.Equal("Access denied", error.Message);
    }

    [Fact]
    public async Task UnknownActionNeverRequestsConfirmationOrStartsAProcess()
    {
        var controller = new SystemPowerController(
            _ => throw new Xunit.Sdk.XunitException("Unknown action requested confirmation."),
            _ => throw new Xunit.Sdk.XunitException("Unknown action started a process."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.RequestAsync((SystemPowerAction)99));
    }
}
