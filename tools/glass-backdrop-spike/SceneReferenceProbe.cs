using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;

internal static class SceneReferenceProbe
{
    internal static int Run(string output,bool popupOnly=false,bool includeSecondaryWindows=false,bool printPopup=false)
    {
        DesktopReferenceCapture.EnsurePhysicalCoordinates();
        var originalForeground=GetForegroundWindow();
        var checks=new List<object>(); var passed=true;
        foreach(var kind in new[] {"opaque","alpha","system"})
        {
            using var process=ProbeProcess.Start("--source",kind);
            var currentPhase="initialization";
            try
            {
                var ready=ProbeProcess.ReadLine(process).Split('|');
                if(ready.Length!=3 || ready[0]!="READY") throw new InvalidOperationException("Controlled source not ready.");
                var handle=nint.Parse(ready[1],CultureInfo.InvariantCulture);
                var backing=nint.Parse(ready[2],CultureInfo.InvariantCulture);
                nint popup=0;
                foreach(var phase in popupOnly && (includeSecondaryWindows || printPopup) ? new[] {"popup","popup-outside"} : popupOnly ? new[] {"popup"} : new[] {"baseline","move","resize","popup"})
                {
                    currentPhase=phase;
                    if(phase=="move" && ProbeProcess.Send(process,"move")!="MOVED") throw new InvalidOperationException("Source did not move.");
                    if(phase=="resize" && ProbeProcess.Send(process,"blue-and-resize")!="BLUE-RESIZED") throw new InvalidOperationException("Source did not resize.");
                    if(phase is "popup" or "popup-outside")
                    {
                        var message=ProbeProcess.Send(process,phase).Split('|');
                        if(message.Length!=2 || message[0]!="POPUP") throw new InvalidOperationException("Popup did not open.");
                        popup=nint.Parse(message[1],CultureInfo.InvariantCulture);
                    }
                    Program.Pump(TimeSpan.FromMilliseconds(400));
                    for(var repetition=1;repetition<=3;repetition++)
                    {
                        var bounds=WgcWindowCapture.ReadScreenBounds(handle);
                        var region=new ScreenRect(bounds.X-48,bounds.Y-48,bounds.Width+96,bounds.Height+96);
                        var physical=new PixelBounds(region.X,region.Y,region.X+region.Width,region.Y+region.Height);
                        var backingBounds=WgcWindowCapture.ReadScreenBounds(backing);
                        if(region.X<backingBounds.X || region.Y<backingBounds.Y || region.X+region.Width>backingBounds.X+backingBounds.Width ||
                            region.Y+region.Height>backingBounds.Y+backingBounds.Height)
                            throw new InvalidOperationException("Controlled backing does not cover the entire reference region.");
                        var before=DesktopReferenceCapture.ReadScreenRegion(physical);
                        VerifyWhiteCorners(before);
                        var start=Stopwatch.GetTimestamp();
                        var layers=new List<CapturedLayer>();
                        foreach(var hwnd in popup==0 || includeSecondaryWindows ? new[] {backing,handle} : new[] {backing,handle,popup})
                        {
                            var captureStage="window texture acquisition";
                            var capturedSize="unavailable";
                            try
                            {
                                if(printPopup && hwnd==popup)
                                {
                                    captureStage="PrintWindow popup pixels/alpha";
                                    var printed=DesktopReferenceCapture.ReadWindowFrame(hwnd);
                                    var nonzero=0;var invalidAlpha=0;
                                    for(var i=0;i<printed.Pixels.Length;i+=4)
                                    {
                                        if(printed.Pixels[i]!=0 || printed.Pixels[i+1]!=0 || printed.Pixels[i+2]!=0) nonzero++;
                                        if(Math.Max(printed.Pixels[i],Math.Max(printed.Pixels[i+1],printed.Pixels[i+2]))>printed.Pixels[i+3]) invalidAlpha++;
                                    }
                                    if(nonzero==0 || invalidAlpha>0)
                                        throw new InvalidOperationException($"PrintWindow did not establish compositable popup pixels: coloredPixels={nonzero}, invalidPremultipliedAlphaPixels={invalidAlpha}, totalPixels={printed.Pixels.Length/4}. Alpha cannot be forced opaque without losing transparent corners.");
                                    layers.Add(new(new CapturePixels(printed.Bounds.Width,printed.Bounds.Height,printed.Pixels),
                                        new ScreenRect(printed.Bounds.Left,printed.Bounds.Top,printed.Bounds.Width,printed.Bounds.Height)));
                                    continue;
                                }
                                var pixels=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(4),includeSecondaryWindows:includeSecondaryWindows && hwnd==handle) ?? throw new InvalidOperationException("WGC scene layer unavailable.");
                                capturedSize=$"{pixels.Width}x{pixels.Height}";captureStage="physical DWM bounds lookup";
                                layers.Add(new(pixels,WgcWindowCapture.ReadScreenBounds(hwnd)));
                            }
                            catch(Exception error)
                            {
                                var className=new StringBuilder(256);GetClassName(hwnd,className,className.Capacity);
                                throw new InvalidOperationException($"Scene layer {layers.Count} failed at {captureStage} ({className}, valid={IsWindow(hwnd)}, visible={IsWindowVisible(hwnd)}, HWND={hwnd}, captured={capturedSize}): HRESULT=0x{error.HResult:X8}: {error.Message}",error);
                            }
                        }
                        var composition=GpuBackdropCompositor.Compose(layers,region,region.Width,region.Height);
                        var rebuilt=new PixelFrame(physical,composition.Pixels,start,Stopwatch.GetTimestamp(),DateTimeOffset.UtcNow);
                        // Capture indicators must disappear before comparing the independent screen reference.
                        Program.Pump(TimeSpan.FromMilliseconds(300));
                        var after=DesktopReferenceCapture.ReadScreenRegion(physical);
                        VerifyWhiteCorners(after);
                        if(WgcWindowCapture.ReadScreenBounds(handle)!=bounds) throw new InvalidOperationException("Window moved during stable comparison.");
                        var body=new PixelBounds(bounds.X,bounds.Y,bounds.X+bounds.Width,bounds.Y+bounds.Height);
                        var points=new List<PixelCandidate>();
                        for(var y=physical.Top;y<physical.Bottom;y+=2)
                        for(var x=physical.Left;x<physical.Right;x+=2) points.Add(new(x,y,!body.Contains(x,y),true));
                        var result=PixelComparison.Compare(before,rebuilt,after,points);
                        var popupCornerMismatches=0;
                        int? nativePopupRegionKind=null;
                        if(popup!=0)
                        {
                            var popupBounds=DesktopReferenceCapture.Bounds(popup);
                            popupCornerMismatches=result.Mismatches.Count(p=>popupBounds.Contains(p.X,p.Y) &&
                                Math.Min(p.X-popupBounds.Left,popupBounds.Right-1-p.X)<32 &&
                                Math.Min(p.Y-popupBounds.Top,popupBounds.Bottom-1-p.Y)<32);
                            var nativeRegion=CreateRectRgn(0,0,0,0);
                            if(nativeRegion==0) throw new InvalidOperationException("Popup clipping region allocation failed.");
                            try {nativePopupRegionKind=GetWindowRgn(popup,nativeRegion);}
                            finally {DeleteObject(nativeRegion);}
                        }
                        var observedNeutralRingPixels=0;
                        foreach(var point in points.Where(p=>p.Icon))
                        {
                            var offset=((point.Y-physical.Top)*physical.Width+point.X-physical.Left)*4;
                            var b=before.Pixels[offset];var g=before.Pixels[offset+1];var r=before.Pixels[offset+2];
                            if(b<253 && g<253 && r<253 && Math.Max(b,Math.Max(g,r))-Math.Min(b,Math.Min(g,r))<=2 &&
                                Enumerable.Range(0,3).All(c=>before.Pixels[offset+c]==after.Pixels[offset+c])) observedNeutralRingPixels++;
                        }
                        var ok=result.Wallpaper.Passed && result.Icons.Passed;
                        passed&=ok;
                        checks.Add(new {kind,phase,repetition,passed=ok,physicalRegion=physical,sourceBounds=bounds,
                            windowArea=result.Wallpaper,outsideWindowRing=result.Icons,
                            sourceIsForeground=GetForegroundWindow()==handle,
                            observedNeutralRingPixels,popupCornerMismatches,nativePopupRegionKind,
                            shadowEvidence=kind=="system" && observedNeutralRingPixels==0 ? "native-shadow-not-observed" : "controlled-ring-compared",
                            sceneCaptureMs=(rebuilt.EndTicks-start)*1000d/Stopwatch.Frequency,
                            referenceSpanMs=(after.EndTicks-before.StartTicks)*1000d/Stopwatch.Frequency,
                            borderless=false,includeSecondaryWindows,printPopup,layerCount=layers.Count});
                        Console.WriteLine($"{kind}/{phase}/{repetition}: body {result.Wallpaper.Valid} valid, {result.Wallpaper.Mismatched} mismatch; ring {result.Icons.Valid} valid, {result.Icons.Mismatched} mismatch; passed {ok}");
                    }
                }
            }
            catch(Exception error) {passed=false;checks.Add(new {kind,phase=currentPhase,passed=false,error=error.Message,innerHresult=error.InnerException is null ? null : $"0x{error.InnerException.HResult:X8}"});}
            finally
            {
                if(!process.HasExited)
                {
                    try { ProbeProcess.Send(process,"restore-foreground|"+originalForeground); } catch { }
                    process.StandardInput.WriteLine("exit"); process.StandardInput.Flush();
                    if(!process.WaitForExit(3000)) {process.Kill(); process.WaitForExit(1000);}
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new {passed,checks,originalForegroundRestored=GetForegroundWindow()==originalForeground,
            note="Only aggregate statistics saved. Controlled white backing prevents user desktop content entering compared regions. Full physical regions, stable pre/post GDI references, 2px grid, BGR tolerance 2. Body and 48px outside ring checked separately. Default WGC indicator permitted only during this isolated test, then disposed before reference. No borderless product acceptance, no complete glass sample or external recording acceptance. Any repeatable native shadow/corner discrepancy blocks integration."},new JsonSerializerOptions {WriteIndented=true}));
        return passed ? 0 : 1;
    }
    private static void VerifyWhiteCorners(PixelFrame frame)
    {
        var w=frame.Bounds.Width;var h=frame.Bounds.Height;
        foreach(var offset in new[] {0,(w-1)*4,((h-1)*w)*4,(h*w-1)*4})
            if(Enumerable.Range(0,3).Any(c=>frame.Pixels[offset+c]<253))
                throw new InvalidOperationException("Controlled reference region is not surrounded by white backing; comparison inconclusive.");
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd,nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
