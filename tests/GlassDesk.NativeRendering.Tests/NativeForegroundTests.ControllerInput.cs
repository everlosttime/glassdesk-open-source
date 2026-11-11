using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.NativeRenderingProbe;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [ControllerInputFact]
    public void RealDockLauncherButtonAndCrossProcessCornerWorkBeforeNativeDuringNativeAndAfterFallback()=>NativeEngineTests.OnSta(()=>
    {
        var output=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"dock-controller-input",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        var start=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"probe/GlassDesk.NativeRenderingProbe.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var argument in new[]{"--direct-input-receiver","floating","--output",output})start.ArgumentList.Add(argument);
        using var receiverProcess=Process.Start(start)!;
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass});
        var dock=new DockWindow{Left=120,Top=150};ControllerGetCursorPos(out var original);DirectInputProbe.PointI? last=null;
        try
        {
            var ready=Path.Combine(output,"receiver-ready.json");var deadline=Stopwatch.StartNew();
            while(!File.Exists(ready) && !receiverProcess.HasExited && deadline.Elapsed<TimeSpan.FromSeconds(10))global::Program.Pump(TimeSpan.FromMilliseconds(10));
            Assert.True(File.Exists(ready));using var receiverReport=JsonDocument.Parse(File.ReadAllText(ready));
            var receiver=(nint)receiverReport.RootElement.GetProperty("hwnd").GetInt64();Assert.Equal(receiverProcess.Id,receiverReport.RootElement.GetProperty("pid").GetInt32());
            ((LiquidGlassSurface)dock.FindName("GlassSurface")).IsBackdropActive=()=>false;
            dock.Show();dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(100));
            var contract=ControllerWindowContract.Read(dock);var hwnd=new WindowInteropHelper(dock).Handle;
            var launcher=(Button)dock.FindName("LauncherButton");var clicks=0;launcher.Click+=(_,_)=>clicks++;
            var frames=new List<object>();
            int Lower()=>int.Parse(File.ReadAllText(Path.Combine(output,"receiver-count.txt")));
            nint Click(DirectInputProbe.PointI point)
            {
                last=point;var operation=DirectInputProbe.Click(point,hwnd,receiver);
                while(!operation.IsCompleted)global::Program.Pump(TimeSpan.FromMilliseconds(5));return operation.GetAwaiter().GetResult();
            }
            foreach(var phase in new[]{"legacy","native","fallback"})
            {
                if(phase=="native")Assert.True(dock.StartNativeForegroundExperiment(),dock.NativeForegroundExperimentStatus.Failure);
                if(phase=="fallback")
                {
                    var root=(Grid)dock.FindName("DockVisualRoot");root.Effect=new BlurEffect{Radius=3};var wait=Stopwatch.StartNew();
                    while(dock.NativeForegroundExperimentStatus.Active && wait.Elapsed<TimeSpan.FromSeconds(3)){dock.TryPresentNativeForegroundExperiment();global::Program.Pump(TimeSpan.FromMilliseconds(5));}
                    Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.NotNull(dock.NativeForegroundExperimentStatus.Failure);
                }
                var bounds=DisplayService.GetWindowPixelBounds(hwnd)!.Value;
                Assert.True(ControllerSetWindowPos(receiver,hwnd,bounds.X-20,bounds.Y-20,bounds.Width+40,bounds.Height+40,0x0210));
                global::Program.Pump(TimeSpan.FromMilliseconds(100));
                var center=launcher.PointToScreen(new Point(launcher.ActualWidth/2,launcher.ActualHeight/2));var corner=dock.PointToScreen(new Point(1,1));
                var before=clicks;var lower=Lower();var foreground=ControllerForeground();
                var panelTarget=Click(new((int)Math.Round(center.X),(int)Math.Round(center.Y)));
                var keptForeground=foreground==ControllerForeground();Assert.Equal(hwnd,panelTarget);Assert.Equal(before+1,clicks);Assert.Equal(lower,Lower());Assert.True(keptForeground);
                var holeTarget=Click(new((int)Math.Round(corner.X),(int)Math.Round(corner.Y)));
                Assert.Equal(receiver,holeTarget);Assert.Equal(lower+1,Lower());Assert.Equal(before+1,clicks);Assert.Equal(contract,ControllerWindowContract.Read(dock));
                frames.Add(new{phase,panelTarget=(long)panelTarget,holeTarget=(long)holeTarget,clicks,lowerClicks=Lower(),keptForeground,status=dock.NativeForegroundExperimentStatus});
            }
            File.WriteAllText(Path.Combine(output,"actual-dock-input.json"),JsonSerializer.Serialize(new{contract,receiverPid=receiverProcess.Id,hostPid=Environment.ProcessId,frames,
                actualDockWindow=true,actualLauncherButtonClick=true,topLeftCrossProcessCorner=true,manuallyDriven=true,desktopLayer=false,winDMeasured=false,
                displayedFpsMeasured=false,wholeProductMemoryMeasured=false,productionBackendSelected=false}));
        }
        finally
        {
            dock.Close();File.WriteAllText(Path.Combine(output,"receiver-stop"),"");
            if(last is { } expected && ControllerGetCursorPos(out var current) && current.Equals(expected))ControllerSetCursorPos(original.X,original.Y);
            if(!receiverProcess.WaitForExit(3000))receiverProcess.Kill(entireProcessTree:true);
        }
    });
    private sealed class ControllerInputFactAttribute:FactAttribute
    {public ControllerInputFactAttribute(){if(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_INPUT_ACCEPTANCE")!="1")Skip="Requires explicit real-input acceptance for owned actual Dock and receiver.";}}
    [DllImport("user32.dll",EntryPoint="SetWindowPos")]private static extern bool ControllerSetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="GetCursorPos")]private static extern bool ControllerGetCursorPos(out DirectInputProbe.PointI point);
    [DllImport("user32.dll",EntryPoint="SetCursorPos")]private static extern bool ControllerSetCursorPos(int x,int y);
    [DllImport("user32.dll",EntryPoint="GetForegroundWindow")]private static extern nint ControllerForeground();
}
