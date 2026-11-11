using GlassDesk.Platform.Network;

namespace GlassDesk.AppServices.Tests;

public sealed class NearbyFirewallTests
{
    [Fact]
    public void MissingRuleFromRealWindowsCollectionDoesNotCrashConfiguration()
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        object collection = policy.Rules;
        try
        {
            dynamic rules = collection;
            NearbyFirewall.IgnoreMissingRule(() => { object rule = rules.Item("GlassDesk.Nonexistent." + Guid.NewGuid().ToString("N")); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(rule); });
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(collection);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(policy);
        }
    }

    [Fact]
    public void MissingRuleHandlingDoesNotHidePermissionFailures()
    {
        Assert.Throws<UnauthorizedAccessException>(() => NearbyFirewall.IgnoreMissingRule(() => throw new UnauthorizedAccessException()));
    }

    private const string Program = @"D:\GlassDesk\GlassDesk.exe";
    private static NearbyFirewallRule[] ValidRules() =>
    [
        new("GlassDesk.Nearby.TCP", Program, 6, "53317-53327", "LocalSubnet", "*", 7, true, 1, 1, false),
        new("GlassDesk.Nearby.UDP", Program, 17, "53317", "LocalSubnet", "*", 7, true, 1, 1, false),
    ];

    [Fact]
    public async Task ValidSubnetRulesRemainReadyAcrossAddressAndProfileChangesWithoutElevation()
    {
        var rules = ValidRules(); var prompts = 0;
        var service = new NearbyFirewall(Program, () => rules, _ => { prompts++; return Task.FromResult(false); });
        Assert.True((await service.EnsureAsync(true)).Ready);
        Assert.True((await service.EnsureAsync(false)).Ready);
        Assert.Equal(0, prompts);
    }

    [Theory]
    [InlineData("fixed-local")]
    [InlineData("fixed-remote")]
    [InlineData("all-remote")]
    [InlineData("private-only")]
    [InlineData("wrong-program")]
    [InlineData("wrong-port")]
    [InlineData("disabled")]
    [InlineData("edge-traversal")]
    public async Task StaleOrOverbroadRuleIsReportedAndBackgroundCheckNeverPrompts(string defect)
    {
        var rules = ValidRules();
        rules[0] = defect switch
        {
            "fixed-local" => rules[0] with { LocalAddresses = "172.20.10.3" },
            "fixed-remote" => rules[0] with { RemoteAddresses = "172.20.10.0/28" },
            "all-remote" => rules[0] with { RemoteAddresses = "*" },
            "private-only" => rules[0] with { Profiles = 2 },
            "wrong-program" => rules[0] with { Program = @"D:\Other\GlassDesk.exe" },
            "wrong-port" => rules[0] with { LocalPorts = "53317" },
            "disabled" => rules[0] with { Enabled = false },
            _ => rules[0] with { EdgeTraversal = true },
        };
        var prompts = 0;
        var service = new NearbyFirewall(Program, () => rules, _ => { prompts++; return Task.FromResult(true); });
        var status = await service.EnsureAsync(false);
        Assert.False(status.Ready);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task ExplicitRepairRereadsSystemRulesInsteadOfTrustingHelperExit()
    {
        var service = new NearbyFirewall(Program, () => [], _ => Task.FromResult(true));
        Assert.False((await service.EnsureAsync(true)).Ready);
    }

    [Fact]
    public async Task ExplicitRepairBecomesReadyOnlyAfterBothRulesAreInstalled()
    {
        NearbyFirewallRule[] rules = [];
        var service = new NearbyFirewall(Program, () => rules, _ => { rules = ValidRules(); return Task.FromResult(true); });
        Assert.True((await service.EnsureAsync(true)).Ready);
    }

    [Fact]
    public async Task DecliningElevationReportsBlockedReceptionWithoutRetryLoop()
    {
        var prompts = 0;
        var service = new NearbyFirewall(Program, () => [], _ => { prompts++; return Task.FromResult(false); });
        Assert.False((await service.EnsureAsync(true)).Ready);
        Assert.False((await service.EnsureAsync(false)).Ready);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task CancelledOperationDoesNotRequestElevation()
    {
        var prompts = 0;
        var service = new NearbyFirewall(Program, () => [], _ => { prompts++; return Task.FromResult(true); });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureAsync(true, cancellation.Token));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task SystemReadFailureProducesAnActionableStatus()
    {
        var service = new NearbyFirewall(Program, () => throw new UnauthorizedAccessException(), _ => Task.FromResult(false));
        var result = await service.EnsureAsync(false);
        Assert.False(result.Ready);
        Assert.Contains("防火墙", result.Message);
    }

    [Fact]
    public void ElevatedEntryRejectsExtraArgumentsBeforeAnySystemChanges()
    {
        Assert.True(NearbyFirewall.RunConfigurationIfRequested(["--configure-nearby-firewall", "--exit"], out var result));
        Assert.Equal(2, result);
        Assert.False(NearbyFirewall.RunConfigurationIfRequested(["--autostart"], out _));
    }
}
