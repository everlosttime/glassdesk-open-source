using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRenderingProbe;

// Real input on disposable owned HWNDs only. This is a component experiment,
// never an installed-product, performance, Win+D, or general input acceptance.
internal static class DirectInputProbe
{
    private const double Width=360,Height=200,Left=120,Top=150;
    private static readonly Color Panel=Color.FromArgb(180,30,80,140);
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};

    internal static int Run(string[] args,string output)
    {
        var receiver=args.Contains("--direct-input-receiver");
        var mode=Value(args,receiver?"--direct-input-receiver":"--direct-input");
        if(mode is not ("floating" or "desktop"))return 64;
        if(!receiver && (File.Exists(Path.Combine(output,"receiver-ready.json")) || File.Exists(Path.Combine(output,"direct-input.json"))))return 65;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        // Never load/save installed settings or start AppHost services.
        theme.Update(new AppearanceSettings{PinBarsToDesktop=mode=="desktop",GlassMaterial=GlassMaterialMode.PlainGlass});
        var exit=0;
        app.Startup+=async (_,_)=>
        {
            try{if(receiver)await Receive(mode,output);else await Observe(mode,output,args.Contains("--input-preflight-only"),args.Contains("--input-external-panel"),args.Contains("--input-noactivate"));}
            catch(InputUnavailableException error)
            {
                exit=3;File.WriteAllText(Path.Combine(output,"input-blocked.json"),JsonSerializer.Serialize(new
                {status="blocked",actualInputPassed=false,wholeProductAcceptance=false,reason=error.Message},Json));
            }
            catch(Exception error){exit=1;File.WriteAllText(Path.Combine(output,receiver?"receiver-error.txt":"direct-input-error.txt"),error.ToString());}
            finally{app.Shutdown(exit);}
        };
        app.Run();return exit;
    }

    private static string Value(string[] args,string key)
    {var i=Array.IndexOf(args,key);return i>=0 && i+1<args.Length?args[i+1]:"";}

    private static async Task Receive(string mode,string output)
    {
        var window=new Window{Width=Width+40,Height=Height+40,Left=Left-20,Top=Top-20,
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Background=Brushes.MidnightBlue,
            ShowActivated=false,ShowInTaskbar=false,Topmost=mode=="floating"};
        DesktopWindowAttachment? attachment=null;
        var count=0;
        window.MouseLeftButtonDown+=(_,_)=>
        {
            count++;var temp=Path.Combine(output,"receiver-count.tmp");
            File.WriteAllText(temp,count.ToString());File.Move(temp,Path.Combine(output,"receiver-count.txt"),true);
        };
        try
        {
            var hwnd=new WindowInteropHelper(window).EnsureHandle();
            if(mode=="desktop")
            {
                attachment=new(hwnd,owner=>new WindowInteropHelper(window).Owner=owner);
                if(!attachment.TryAttach())throw new InvalidOperationException("Receiver could not attach to desktop.");
            }
            window.Show();attachment?.Reconcile(presenting:true);window.UpdateLayout();
            File.WriteAllText(Path.Combine(output,"receiver-count.txt"),"0");
            File.WriteAllText(Path.Combine(output,"receiver-ready.json"),JsonSerializer.Serialize(new{hwnd=(long)hwnd,pid=Environment.ProcessId}));
            var until=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(output,"receiver-stop")) && until.Elapsed<TimeSpan.FromMinutes(4))await Task.Delay(40);
        }
        finally{attachment?.Dispose();window.Close();}
    }

    private static async Task Observe(string mode,string output,bool preflightOnly,bool externalPanel,bool noActivate)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"--direct-input-receiver",mode,"--output",output})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)??throw new InvalidOperationException("Receiver process did not start.");
        InputWindow? host=null;NativeDirectPresenter? presenter=null;
        GetCursorPos(out var originalCursor);PointI? lastCursor=null;
        try
        {
            var watch=Stopwatch.StartNew();var ready=Path.Combine(output,"receiver-ready.json");
            while(!File.Exists(ready))
            {
                if(child.HasExited || watch.Elapsed>TimeSpan.FromSeconds(10))throw new InvalidOperationException("Receiver readiness unavailable.");
                await Task.Delay(40);
            }
            using var data=JsonDocument.Parse(File.ReadAllText(ready));
            var receiver=(nint)data.RootElement.GetProperty("hwnd").GetInt64();
            GetWindowThreadProcessId(receiver,out var receiverPid);
            if(receiverPid!=child.Id || receiverPid==Environment.ProcessId)throw new InvalidOperationException("Receiver ownership mismatch.");
            host=new InputWindow(noActivate){ShowInTaskbar=externalPanel};host.Show();host.UpdateLayout();
            var hwnd=new WindowInteropHelper(host).Handle;
            if(mode=="desktop" && !host.RefreshDesktopAttachment())throw new InvalidOperationException("Host could not attach to desktop.");
            SetWindowDisplayAffinity(hwnd,0);await Task.Delay(250);
            var contract=Contract.Read(hwnd);var stages=new List<object>();
            var scale=PresentationSource.FromVisual(host)!.CompositionTarget!.TransformToDevice;
            var width=(int)Math.Round(host.ActualWidth*scale.M11);var height=(int)Math.Round(host.ActualHeight*scale.M22);
            byte[]? emptyPixels=null,legacyPixels=null;int? nativeDelta=null,recoveryDelta=null;
            double? nativeMean=null;var candidateDelta=0;
            foreach(var name in new[]{"legacy","native-empty","native-mask-candidate","legacy-recovery"})
            {
                if(name=="native-empty")
                {
                    host.InputRoot.Opacity=0;
                    presenter=new(hwnd,width,height);
                    presenter.SetVector(1,host.Clip,new(host.ActualWidth,host.ActualHeight),Panel);
                    presenter.Clear(Colors.Transparent);
                    presenter.DrawVectors(new(host.ActualWidth,host.ActualHeight),[new(1,new(0,0,host.ActualWidth,host.ActualHeight))]);
                    presenter.Present();
                }
                else if(name=="native-mask-candidate")
                {
                    // Experimental input-alpha carrier only. The real WPF input
                    // tree remains laid out/hit-testable at opacity zero. This
                    // candidate is NOT selected by any production window.
                    host.Root.Background=new SolidColorBrush(Color.FromArgb(1,0,0,0));
                }
                else{presenter?.Dispose();presenter=null;host.InputRoot.Opacity=1;host.Root.Background=Brushes.Transparent;}
                // A real hole click can activate/raise the independent receiver.
                // Restore the same controlled two-window arrangement for each
                // stage; never reorder the host or alter either owner's order.
                if(!SetWindowPos(receiver,hwnd,0,0,0,0,0x0213))throw new System.ComponentModel.Win32Exception();
                host.UpdateLayout();await Task.Delay(250);
                CapturePixels? capture=null;string? captureError=null;var captureKind="window-wgc";
                try{capture=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("No visible probe frame.");}
                catch(COMException error) when((mode=="desktop" || noActivate) && error.HResult==unchecked((int)0x80070057))
                {
                    captureError=error.Message;
                    if(noActivate && mode=="floating"){capture=OwnedWindowMonitorCrop.Capture(hwnd,width,height);captureKind="monitor-crop";}
                }
                int? visible=null;
                if(capture is not null)
                {
                    visible=0;for(var i=3;i<capture.Pixels.Length;i+=4)if(capture.Pixels[i]>24)visible++;
                    File.WriteAllBytes(Path.Combine(output,name+".bgra"),capture.Pixels);
                    if(name=="legacy")legacyPixels=capture.Pixels;
                    if(name=="native-empty")
                    {
                        emptyPixels=capture.Pixels;
                        if(legacyPixels is not null){var d=Difference(legacyPixels,capture.Pixels);nativeDelta=d.Max;nativeMean=d.Mean;}
                    }
                    if(name=="legacy-recovery" && legacyPixels is not null)recoveryDelta=Difference(legacyPixels,capture.Pixels).Max;
                    if(name=="native-mask-candidate" && emptyPixels is not null)
                        for(var i=0;i<emptyPixels.Length;i++)candidateDelta=Math.Max(candidateDelta,Math.Abs(emptyPixels[i]-capture.Pixels[i]));
                }
                var panelPoint=Screen(host,new(host.ActualWidth/2,host.ActualHeight/2));
                var holePoint=Screen(host,new(1,1));
                var before=host.Clicks;var lowerBefore=ReceiverCount(output);
                var foregroundBeforePanel=GetForegroundWindow();var foregroundAfterPanel=foregroundBeforePanel;
                var panelTarget=RootAt(panelPoint);var holeTarget=RootAt(holePoint);
                bool? panelReceived=null,holeReceived=null,lowerPanelReceived=null;
                if(externalPanel && name!="native-empty")
                {
                    host.Title="GlassDesk direct input probe: "+name;
                    File.WriteAllText(Path.Combine(output,"external-stage.json"),JsonSerializer.Serialize(new{name,hwnd=(long)hwnd,
                        panelPoint,before,expectedHostClicks=before+1}));
                    var wait=Stopwatch.StartNew();
                    while(host.Clicks==before && wait.Elapsed<TimeSpan.FromSeconds(60))await Task.Delay(40);
                    if(host.Clicks!=before+1)throw new InputUnavailableException("External panel click was not observed exactly once: "+name);
                    panelReceived=true;lowerPanelReceived=ReceiverCount(output)!=lowerBefore;
                    foregroundAfterPanel=GetForegroundWindow();
                }
                else if(!preflightOnly && !externalPanel)
                {
                    // Set the restoration point before injection, including a
                    // blocked move. Never leave an injected button held down.
                    lastCursor=panelPoint;panelTarget=await Click(panelPoint,hwnd,receiver);
                    foregroundAfterPanel=GetForegroundWindow();
                    panelReceived=host.Clicks==before+1;var lowerAfterPanel=ReceiverCount(output);
                    lowerPanelReceived=lowerAfterPanel==lowerBefore+1;
                    if(panelReceived!=true && lowerPanelReceived!=true)throw new InputUnavailableException($"{name}: No owned observer received the panel click; host nativeDown={host.NativeDown}, previewDown={host.PreviewDown}.");
                    lastCursor=holePoint;holeTarget=await Click(holePoint,hwnd,receiver);
                    holeReceived=ReceiverCount(output)==lowerAfterPanel+1 && host.Clicks==before+(panelReceived==true?1:0);
                }
                stages.Add(new{name,panelReceivedByHost=panelReceived,panelReceivedByOtherProcess=lowerPanelReceived,
                    foregroundBeforePanel=(long)foregroundBeforePanel,foregroundAfterPanel=(long)foregroundAfterPanel,
                    panelKeptForeground=panelReceived is null?(bool?)null:foregroundBeforePanel==foregroundAfterPanel,
                    holeReceivedByOtherProcess=holeReceived,hwndContractRetained=contract==Contract.Read(hwnd),visiblePixelCount=visible,
                    width=capture?.Width,height=capture?.Height,center=capture?.Center,captureAvailable=capture is not null,captureError,captureKind,
                    panelPoint,holePoint,panelTarget=(long)panelTarget,holeTarget=(long)holeTarget,
                    panelHitTestHost=panelTarget==hwnd,holeHitTestReceiver=holeTarget==receiver,retainedInputTree=host.InputRoot.IsHitTestVisible && host.InputRoot.IsVisible});
            }
            // Values are taken from observed routed mouse events, not SendInput
            // success, hit-test messages, compositor callbacks, or WGC visibility.
            using var measured=JsonDocument.Parse(JsonSerializer.Serialize(stages));
            var eligible=preflightOnly || externalPanel?(bool?)null:measured.RootElement[1].GetProperty("panelReceivedByHost").GetBoolean();
            File.WriteAllText(Path.Combine(output,"direct-input.json"),JsonSerializer.Serialize(new
            {
                mode,noActivate,hostClickEvent="Button.Click",processId=Environment.ProcessId,receiverPid,crossProcessReceiver=true,wholeProductAcceptance=false,
                displayedFpsMeasured=false,winDMeasured=false,emptyHostInputEligible=eligible,contract,stages,
                inputStatus=externalPanel?"panel-only":preflightOnly?"not-run":"measured",actualInputPassed=preflightOnly || externalPanel?(bool?)null:
                    measured.RootElement.EnumerateArray().Where(s=>s.GetProperty("name").GetString()!="native-empty")
                        .All(s=>s.GetProperty("panelReceivedByHost").GetBoolean() && s.GetProperty("holeReceivedByOtherProcess").GetBoolean()),
                maskCandidateMaximumChannelDelta=emptyPixels is null?(int?)null:candidateDelta,productionCandidateSelected=false,
                ownedReceiverZOrderReestablishedEachStage=true,
                legacyNativeMaximumChannelDelta=nativeDelta,legacyNativeMeanChannelDelta=nativeMean,legacyRecoveryMaximumChannelDelta=recoveryDelta,
                scope=externalPanel?"External desktop-tool panel clicks only; holes use WindowFromPoint preflight. Full input acceptance is pending. No installed process/Data or Win+D.":
                    preflightOnly?"Owned GlassWindow and independent receiver; WindowFromPoint and available WGC only. No actual input, installed process/Data, or Win+D.":
                    "Owned GlassWindow and independent receiver; actual SendInput, available WGC, existing rounded Clip. No installed process/Data or Win+D."
            },Json));
        }
        finally
        {
            presenter?.Dispose();host?.Close();File.WriteAllText(Path.Combine(output,"receiver-stop"),"");
            // Restore only if the user has not moved the pointer since our click.
            if(lastCursor is { } last && GetCursorPos(out var current) && current.Equals(last))SetCursorPos(originalCursor.X,originalCursor.Y);
            var end=Stopwatch.StartNew();while(!child.HasExited && end.Elapsed<TimeSpan.FromSeconds(3))await Task.Delay(40);
            if(!child.HasExited)child.Kill(entireProcessTree:true);
        }
    }

    private sealed class InputWindow:GlassWindow
    {
        private readonly bool _noActivate;
        internal readonly Border InputRoot=new(){Background=new SolidColorBrush(Panel)};
        internal readonly Grid Root=new(){Background=Brushes.Transparent};
        internal int Clicks;
        internal int NativeDown,PreviewDown;
        protected override bool SupportsDesktopLayer=>true;
        internal InputWindow(bool noActivate)
        {
            _noActivate=noActivate;
            Width=DirectInputProbe.Width;Height=DirectInputProbe.Height;Left=DirectInputProbe.Left;Top=DirectInputProbe.Top;
            Title="GlassDesk direct input probe";ShowActivated=false;ShowInTaskbar=false;Root.Children.Add(InputRoot);Content=Root;
            var face=new FrameworkElementFactory(typeof(Border));face.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            var button=new Button{Width=120,Height=80,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
                FocusVisualStyle=null,Template=new ControlTemplate(typeof(Button)){VisualTree=face}};
            button.Click+=(_,_)=>Clicks++;InputRoot.Child=button;
            PreviewMouseDown+=(_,_)=>PreviewDown++;
            SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook((nint window,int message,nint w,nint l,ref bool handled)=>{if(message==0x201)NativeDown++;return 0;});
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if(_noActivate && !WindowStyles.MakeNoActivate(Handle))throw new InvalidOperationException("Owned probe could not retain Dock's no-activate style.");
        }
    }

    private sealed record Contract(long Window,long Owner,long Parent,long Style,long ExtendedStyle)
    {
        internal static Contract Read(nint hwnd)=>new((long)hwnd,(long)GetWindow(hwnd,4),(long)GetParent(hwnd),
            (long)GetWindowLongPtr(hwnd,-16),(long)GetWindowLongPtr(hwnd,-20));
    }
    private static int ReceiverCount(string output)=>int.Parse(File.ReadAllText(Path.Combine(output,"receiver-count.txt")));
    private static (int Max,double Mean) Difference(byte[] first,byte[] second)
    {
        if(first.Length!=second.Length)throw new InvalidOperationException("Capture extents changed during parity observation.");
        var max=0;long sum=0;
        for(var i=0;i<first.Length;i++){var delta=Math.Abs(first[i]-second[i]);max=Math.Max(max,delta);sum+=delta;}
        return(max,sum/(double)first.Length);
    }
    private static PointI Screen(Window window,Point local){var p=window.PointToScreen(local);return new((int)Math.Round(p.X),(int)Math.Round(p.Y));}
    private static nint RootAt(PointI point)=>GetAncestor(WindowFromPoint(point),2);
    private sealed class InputUnavailableException(string message):Exception(message);
    internal static async Task<nint> Click(PointI point,nint host,nint receiver)
    {
        var target=await MoveToOwned(point,host,receiver);
        try{Send([new(){Type=0,Data=new(){Mouse=new(){Flags=2}}}]);await Task.Delay(40);}
        finally{Send([new(){Type=0,Data=new(){Mouse=new(){Flags=4}}}]);}
        await Task.Delay(150);return target;
    }
    internal static async Task<nint> MoveToOwned(PointI point,nint host,nint receiver)
    {
        foreach(var key in new[]{1,2,0x10,0x11,0x12,0x5b,0x5c})
            if((GetAsyncKeyState(key)&0x8000)!=0)throw new InputUnavailableException("Input experiment blocked by an active user key/button.");
        var target=RootAt(point);
        if(target!=host && target!=receiver)throw new InputUnavailableException($"Input experiment blocked by unowned HWND {target:X} at {point}.");
        Move(point);GetCursorPos(out var immediate);await Task.Delay(60);
        if(!GetCursorPos(out var moved) || Math.Abs(moved.X-point.X)>1 || Math.Abs(moved.Y-point.Y)>1)
        {
            GetClipCursor(out var clip);
            var inputDesktop=OpenInputDesktop(0,false,1);var threadDesktop=GetThreadDesktop(GetCurrentThreadId());
            static string Name(nint d){var b=new byte[512];return GetUserObjectInformation(d,2,b,b.Length,out var n)?System.Text.Encoding.Unicode.GetString(b,0,Math.Max(0,n-2)):"unavailable";}
            var desktops=$"{Name(inputDesktop)}/{Name(threadDesktop)}";if(inputDesktop!=0)CloseDesktop(inputDesktop);
            throw new InputUnavailableException($"SendInput returned success but cursor did not reach requested physical point {point}: {immediate}->{moved}. CursorClip={clip}, desktops(input/thread)={desktops}, virtual bounds={GetSystemMetrics(76)},{GetSystemMetrics(77)},{GetSystemMetrics(78)},{GetSystemMetrics(79)}, inputSize={Marshal.SizeOf<Input>()}. No click was sent.");
        }
        if(RootAt(point)!=target)throw new InputUnavailableException("Owned input target changed before click.");
        return target;
    }
    private static void Move(PointI p)
    {
        var left=GetSystemMetrics(76);var top=GetSystemMetrics(77);var width=GetSystemMetrics(78);var height=GetSystemMetrics(79);
        Send([new(){Type=0,Data=new(){Mouse=new(){X=(int)Math.Round((p.X-left)*65535.0/(width-1)),
            Y=(int)Math.Round((p.Y-top)*65535.0/(height-1)),Flags=0xc001}}}]);
    }
    private static void Send(Input[] inputs)
    {if(Marshal.SizeOf<Input>()!=40)throw new InvalidOperationException("Unexpected INPUT ABI size.");if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>())!=inputs.Length)throw new InputUnavailableException("SendInput blocked; not a passing observation.");}
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct PointI(int X,int Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct RectI(int Left,int Top,int Right,int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Input{internal uint Type;internal InputUnion Data;}
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion{[FieldOffset(0)]internal MouseInput Mouse;}
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput{internal int X,Y;internal uint Data,Flags,Time;internal nuint Extra;}
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointI point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window,uint affinity);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointI point);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RectI rect);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint obj,int index,byte[] buffer,int length,out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] input,int size);
}
