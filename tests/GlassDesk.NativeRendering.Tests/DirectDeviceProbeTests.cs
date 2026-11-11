using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectDeviceProbeTests
{
    [Theory]
    [InlineData("shared",1)]
    [InlineData("independent",4)]
    public void OwnedProbeRecordsDeviceCountsAndNormalRelease(string mode,int devices)
    {
        var output=Run(mode,0);
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"direct-device.json")));
        var root=report.RootElement;
        Assert.False(root.GetProperty("wholeProductAcceptance").GetBoolean());
        Assert.False(root.GetProperty("forcedGc").GetBoolean());
        var stages=root.GetProperty("samples").EnumerateArray().ToArray();
        var warm=stages.First(s=>s.GetProperty("phase").GetString()=="warm");
        Assert.Equal(devices,warm.GetProperty("liveDevices").GetInt32());
        Assert.Equal(4,warm.GetProperty("livePresenters").GetInt32());
        var last=stages.Last();Assert.Equal("released",last.GetProperty("phase").GetString());
        Assert.Equal(0,last.GetProperty("livePresenters").GetInt32());
        Assert.True(warm.GetProperty("privateCommitBytes").GetInt64()>0);
        Assert.True(root.GetProperty("peakPrivateCommitBytes").GetInt64()>=warm.GetProperty("privateCommitBytes").GetInt64());
    }

    [Fact]
    public void InvalidProbeModeIsRejected()=>Run("typo",64);

    private static string Run(string mode,int exit)
    {
        var output=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"direct-device-tests",Guid.NewGuid().ToString("N"));
        var executable=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"probe","GlassDesk.NativeRenderingProbe.exe");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"--direct-device-mode",mode,"--direct-device-observe-ms","200","--duration-seconds","1","--output",output})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)!;
        if(!child.WaitForExit(20000)){child.Kill(entireProcessTree:true);throw new TimeoutException("Owned device probe did not exit.");}
        Assert.Equal(exit,child.ExitCode);
        return output;
    }
}
