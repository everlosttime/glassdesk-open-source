using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlassDesk.NativeRendering.Tests;

// Deliberately separate from routed-event tests: both sides of the transparent
// hole are real HWNDs in different processes, and clicks use the OS input queue.
public sealed class DirectInputProbeTests
{
    [Theory]
    [InlineData("floating")]
    [InlineData("desktop")]
    public void PreflightRecordsVisibleAlphaAndCrossProcessHitTargetsWithoutClaimingInput(string mode)
    {
        var output=Run(mode,true,0);
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"direct-input.json")));
        var root=report.RootElement;
        Assert.True(root.GetProperty("crossProcessReceiver").GetBoolean());
        Assert.False(root.GetProperty("wholeProductAcceptance").GetBoolean());
        Assert.Equal("not-run",root.GetProperty("inputStatus").GetString());
        Assert.Equal(JsonValueKind.Null,root.GetProperty("actualInputPassed").ValueKind);
        Assert.False(root.GetProperty("productionCandidateSelected").GetBoolean());
        var stages=root.GetProperty("stages").EnumerateArray().ToArray();
        Assert.Equal(new[]{"legacy","native-empty","native-mask-candidate","legacy-recovery"},stages.Select(s=>s.GetProperty("name").GetString()));
        foreach(var stage in stages)
        {
            Assert.Equal(JsonValueKind.Null,stage.GetProperty("panelReceivedByHost").ValueKind);
            Assert.True(stage.GetProperty("hwndContractRetained").GetBoolean());
            Assert.True(stage.GetProperty("retainedInputTree").GetBoolean());
            if(stage.GetProperty("captureAvailable").GetBoolean())Assert.True(stage.GetProperty("visiblePixelCount").GetInt32()>100);
            else
            {
                Assert.Equal("desktop",mode);
                Assert.Equal(JsonValueKind.Null,stage.GetProperty("visiblePixelCount").ValueKind);
                Assert.Contains("0x80070057",stage.GetProperty("captureError").GetString());
            }
        }
        if(mode=="floating")
        {
            Assert.True(stages[0].GetProperty("panelHitTestHost").GetBoolean());
            Assert.False(stages[1].GetProperty("panelHitTestHost").GetBoolean());
            Assert.True(stages[2].GetProperty("panelHitTestHost").GetBoolean());
            Assert.True(stages[3].GetProperty("panelHitTestHost").GetBoolean());
            Assert.All(stages,s=>Assert.True(s.GetProperty("holeHitTestReceiver").GetBoolean()));
            Assert.All(stages,s=>Assert.True(s.GetProperty("captureAvailable").GetBoolean()));
            Assert.InRange(root.GetProperty("maskCandidateMaximumChannelDelta").GetInt32(),0,1);
            Assert.InRange(root.GetProperty("legacyNativeMaximumChannelDelta").GetInt32(),0,8);
            Assert.InRange(root.GetProperty("legacyNativeMeanChannelDelta").GetDouble(),0,1.5);
            Assert.InRange(root.GetProperty("legacyRecoveryMaximumChannelDelta").GetInt32(),0,1);
        }
        else Assert.NotEqual(0,root.GetProperty("contract").GetProperty("Owner").GetInt64());
    }

    [RealInputFact]
    public void RealMouseInputMustReachBothProcessesAndRecoverLegacy()
    {
        var output=Run("floating",false,0);
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"direct-input.json")));
        var root=report.RootElement;
        Assert.Equal("measured",root.GetProperty("inputStatus").GetString());
        Assert.True(root.GetProperty("actualInputPassed").GetBoolean());
        Assert.Equal("Button.Click",root.GetProperty("hostClickEvent").GetString());
    }

    [RealInputFact]
    public void RealMouseInputRetainsTheDockNoActivateStyleAcrossNativeAndRecovery()
    {
        var output=Run("floating",false,0,noActivate:true);
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"direct-input.json")));
        var root=report.RootElement;
        Assert.NotEqual(0,root.GetProperty("contract").GetProperty("ExtendedStyle").GetInt64()&0x08000000);
        Assert.Equal("Button.Click",root.GetProperty("hostClickEvent").GetString());
        Assert.True(root.GetProperty("actualInputPassed").GetBoolean());
        Assert.Equal("measured",root.GetProperty("inputStatus").GetString());
        Assert.False(root.GetProperty("wholeProductAcceptance").GetBoolean());
        Assert.All(root.GetProperty("stages").EnumerateArray(),stage=>
        {
            Assert.True(stage.GetProperty("hwndContractRetained").GetBoolean());
            Assert.True(stage.GetProperty("captureAvailable").GetBoolean());
            var kind=stage.GetProperty("captureKind").GetString();Assert.Contains(kind,new[]{"window-wgc","monitor-crop"});
            if(kind=="monitor-crop")Assert.Contains("0x80070057",stage.GetProperty("captureError").GetString());
            Assert.True(stage.GetProperty("panelKeptForeground").GetBoolean());
        });
        Assert.InRange(root.GetProperty("legacyNativeMaximumChannelDelta").GetInt32(),0,8);
        Assert.InRange(root.GetProperty("legacyRecoveryMaximumChannelDelta").GetInt32(),0,1);
    }

    private static string Run(string mode,bool preflight,int exit,bool noActivate=false)
    {
        var output=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"direct-input-tests",Guid.NewGuid().ToString("N"));
        var executable=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"probe","GlassDesk.NativeRenderingProbe.exe");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"--direct-input",mode,"--output",output})start.ArgumentList.Add(arg);
        if(preflight)start.ArgumentList.Add("--input-preflight-only");
        if(noActivate)start.ArgumentList.Add("--input-noactivate");
        using var process=Process.Start(start)!;
        if(!process.WaitForExit(35000)){process.Kill(entireProcessTree:true);throw new TimeoutException("Owned input probe did not exit.");}
        Assert.True(process.ExitCode==exit,$"Expected exit {exit}, got {process.ExitCode}. Evidence: {output}");
        return output;
    }

    private sealed class RealInputFactAttribute:FactAttribute
    {
        public RealInputFactAttribute()
        {
            if(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_INPUT_ACCEPTANCE")!="1")
                Skip="Requires explicitly enabled interactive input acceptance. Preflight is not a real-click pass.";
        }
    }
}
