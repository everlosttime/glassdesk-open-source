using System.Runtime.InteropServices;

// Used on unverified Windows 11 builds only inside a recoverable helper process.
// The private DWM ABI is probed at runtime; failures fall back to documented capture.
// Prototype provenance: https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247
// Public COM ABI: microsoft/win32metadata generation/WinSDK/RecompiledIdlHeaders/um/dcomp.h
internal sealed unsafe class SharedDesktopSceneCapture : IDesktopSceneCapture
{
    private nint library,device,context,dxgi,composition,target,visual,thumbnail,carrier,carrierOwner;
    private nint _rootVisual,_desktopVisual,_desktopThumbnail;
    private nint _shellWindow;
    private PersistentWindowCapture? _capture;
    private readonly ScreenRect _region;
    private nint[]? _lastExcluded;
    private SceneWindow[]? _lastMembers;
    private long _exclusionBarrier;
    private long _motionBarrier;
    private long _exclusionGeneration;
    private readonly Action<CaptureTrace>? _trace;
    private readonly int _excludedParentProcess;
    private readonly Func<int[]>? _excludedProcesses;
    internal nint CarrierHandle => carrier;
    nint IDesktopSceneCapture.CarrierHandle => CarrierHandle;
    public long SceneRevision => _exclusionBarrier;
    bool IDesktopSceneCapture.UpdateExclusions() => UpdateExclusions();
    CapturePixels? IDesktopSceneCapture.TryRead(TimeSpan wait, CancellationToken cancellation, long notBefore, bool reuse)
        => TryRead(wait, cancellation, notBefore, reuse);
    void IDesktopSceneCapture.ReleaseReadbackBuffers() => ReleaseReadbackBuffers();
    // Controlled isolation fixture only; never enabled by the candidate feed.
    internal bool DiagnosticFreezeExclusions {get;set;}
    internal void DiagnosticRecommitExclusions(bool commit=true) => ApplyExclusions(_lastExcluded!,commit);
    internal void DiagnosticCommitOnly()
    {
        Check(((delegate* unmanaged[Stdcall]<nint,int>)(*(nint**)composition)[3])(composition));
        _trace?.Invoke(new("Composition", "CommitSubmitted", System.Diagnostics.Stopwatch.GetTimestamp()));
    }
    internal void DiagnosticReplaceVisual()
    {
        var snapshot=WindowSnapshot();ReplaceVisual(snapshot.Excluded,snapshot.Members);
    }
    private void ReplaceVisual(nint[] excluded,SceneWindow[] members)
    {
        nint nextVisual=0,nextThumbnail=0;
        var create=(delegate* unmanaged[Stdcall]<nint,nint,nint*,nint*,int>)GetProcAddress(library,163);
        try
        {
            Check(create(carrier,composition,&nextVisual,&nextThumbnail));
            _trace?.Invoke(new("VisualSwap",nextThumbnail==thumbnail ? "SameThumbnail" : "NewThumbnail",System.Diagnostics.Stopwatch.GetTimestamp()));
            _trace?.Invoke(new("VisualIdentity",nextVisual==visual ? "Same" : "New",System.Diagnostics.Stopwatch.GetTimestamp()));
            SetNativeExclusions(nextThumbnail,excluded);
            // Prepare a new visual without updating the currently captured root.
            DiagnosticCommitOnly();
            // The desktop plane stays attached below the window scene during replacement.
            AddChild(nextVisual,true,visual);
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)(*(nint**)_rootVisual)[17])(_rootVisual,visual));
            DiagnosticCommitOnly();Check(DwmFlush());
            _trace?.Invoke(new("Composition", "DwmFlushReturned", System.Diagnostics.Stopwatch.GetTimestamp()));
            DwmUnregisterThumbnail(thumbnail);Release(ref visual);
            thumbnail=nextThumbnail;nextThumbnail=0;visual=nextVisual;nextVisual=0;
            RecordExclusions(excluded);
            _capture?.RefreshSessionAtSceneBoundary();
            _lastMembers=members;
        }
        catch
        {
            ((delegate* unmanaged[Stdcall]<nint,int>)(*(nint**)_rootVisual)[18])(_rootVisual);
            if(_desktopVisual!=0) AddChild(_desktopVisual,false,0);
            AddChild(visual,true,_desktopVisual);
            DiagnosticCommitOnly();throw;
        }
        finally {if(nextThumbnail!=0) DwmUnregisterThumbnail(nextThumbnail);Release(ref nextVisual);}
    }
    internal static CapturePixels? Capture(ScreenRect region,TimeSpan timeout,Action<CaptureTrace>? trace=null)
    {
        using var source=new SharedDesktopSceneCapture(region,trace:trace);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(watch.Elapsed<timeout)
        {
            var frame=source.TryRead();if(frame is not null) return frame;
            Program.Pump(TimeSpan.FromMilliseconds(10));
        }
        return null;
    }
    internal SharedDesktopSceneCapture(ScreenRect region,bool diagnosticOnScreen=false,Action<CaptureTrace>? trace=null,
        int isolatedParentProcess=0,Func<int[]>? excludedProcesses=null)
    {
        _trace=trace;
        _excludedParentProcess=isolatedParentProcess;
        _excludedProcesses=excludedProcesses;
        if(Environment.OSVersion.Version.Build!=26100 && isolatedParentProcess<=0)
            throw new NotSupportedException("Private ABI probe is restricted to the inspected local OS build.");
        if(GlassDesk.Platform.WindowsCapabilities.BuildNumber<22000)
            throw new PlatformNotSupportedException("Windows 11 is required.");
        if(region.Width<=0 || region.Height<=0 || (long)region.Width*region.Height>32_000_000)
            throw new ArgumentOutOfRangeException(nameof(region));
        _region=region;
        try
        {
            library=NativeLibrary.Load("dwmapi.dll");
            var createAddress=GetProcAddress(library,163);var updateAddress=GetProcAddress(library,164);
            if(createAddress==0 || updateAddress==0) throw new NotSupportedException("Private shared visual ordinals unavailable.");
            var create=(delegate* unmanaged[Stdcall]<nint,nint,nint*,nint*,int>)createAddress;
            Check(D3D11CreateDevice(0,1,0,0x20,0,0,7,out device,out _,out context));
            var dxgiId=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Check(Marshal.QueryInterface(device,in dxgiId,out dxgi));
            var desktopId=new Guid("5F4633FE-1E08-4CB8-8C75-CE24333F5602");
            Check(DCompositionCreateDevice3(dxgi,in desktopId,out composition));
            // WGC rejects TOOLWINDOW capture items. A never-shown popup owner
            // excludes this auxiliary popup from the taskbar before its first show.
            // After creating the WGC item, NOACTIVATE excludes it from Alt+Tab.
            carrierOwner=CreateWindowExW(0,"STATIC","GlassDesk capture owner",0x80000000,0,0,1,1,0,0,0,0);
            if(carrierOwner==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            carrier=CreateWindowExW(0x00200008,"STATIC","GlassDesk controlled shared desktop carrier",0x80000000,
                diagnosticOnScreen ? region.X : GetSystemMetrics(76)-region.Width-64,
                diagnosticOnScreen ? region.Y : GetSystemMetrics(77),region.Width,region.Height,carrierOwner,0,0,0);
            if(carrier==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var enabled=1;
            var attribute=new CompositionAttribute {Attribute=13,Data=(nint)(&enabled),Size=4};
            if(!SetWindowCompositionAttribute(carrier,ref attribute)) throw new InvalidOperationException("Carrier live-preview exclusion failed.");
            nint createdVisual=0,createdThumbnail=0;
            Check(create(carrier,composition,&createdVisual,&createdThumbnail));
            visual=createdVisual;thumbnail=createdThumbnail;
            UpdateExclusions();
            CreateDesktopPlane();
            nint createdRoot=0;
            Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)(*(nint**)composition)[6])(composition,&createdRoot));
            _rootVisual=createdRoot;
            if(_desktopVisual!=0) AddChild(_desktopVisual,false,0);
            AddChild(visual,true,_desktopVisual);
            nint createdTarget=0;
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int,nint*,int>)(*(nint**)composition)[24])(composition,carrier,0,&createdTarget));
            target=createdTarget;
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)(*(nint**)target)[3])(target,_rootVisual));
            Check(((delegate* unmanaged[Stdcall]<nint,int>)(*(nint**)composition)[3])(composition));
            ShowWindow(carrier,4);
            Check(DwmFlush());
            _capture=new PersistentWindowCapture(carrier,trace,device,context);
            // CreateForWindow requires a visible, activation-eligible window. Once
            // the persistent session owns its item, exclude the carrier from shell
            // activation/task switching without rebuilding that capture item.
            ShowWindow(carrier,0);
            if(SetWindowLongPtrW(carrier,-20,new nint(0x08200008))==0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ShowWindow(carrier,4);
        }
        catch {Dispose();throw;}
    }
    private void AddChild(nint child,bool above,nint reference) =>
        Check(((delegate* unmanaged[Stdcall]<nint,nint,int,nint,int>)(*(nint**)_rootVisual)[16])(_rootVisual,child,above ? 1 : 0,reference));
    private void CreateDesktopPlane()
    {
        // The multi-window visual omits the shell background. Its transparent holes are
        // valid content, not missing frames. Supply the live shell visual underneath,
        // including wallpaper-provider child surfaces; never replace it with a still image.
        var shell=GetShellWindow();
        if(shell==0) throw new InvalidOperationException("Desktop shell is temporarily unavailable.");
        var bounds=WgcWindowCapture.ReadScreenBounds(shell);
        var properties=new ThumbnailProperties
        {
            Flags=0x0400001f,Destination=new(0,0,_region.Width,_region.Height),
            Source=new(_region.X-bounds.X,_region.Y-bounds.Y,_region.X-bounds.X+_region.Width,_region.Y-bounds.Y+_region.Height),
            Opacity=255,Visible=1,ClientOnly=0
        };
        var create=(delegate* unmanaged[Stdcall]<nint,nint,uint,ThumbnailProperties*,nint,nint*,nint*,int>)GetProcAddress(library,147);
        if(create==null) throw new NotSupportedException("Shared desktop-plane visual unavailable.");
        nint desktopVisual=0,desktopThumbnail=0;
        Check(create(carrier,shell,2,&properties,composition,&desktopVisual,&desktopThumbnail));
        _desktopVisual=desktopVisual;_desktopThumbnail=desktopThumbnail;
        _shellWindow=shell;
    }
    internal CapturePixels? TryRead(TimeSpan waitForFrame=default,CancellationToken cancellation=default,long sourceNotBeforeTicks=0,bool reusePixels=false)
    {
        UpdateExclusions();
        var barrier=_exclusionBarrier;
        var frame=_capture!.TryRead(Math.Max(Math.Max(barrier,_motionBarrier),sourceNotBeforeTicks),waitForFrame,cancellation,reusePixels);
        // Reject acquisition across own exclusions or external scene membership changes.
        var changed=UpdateExclusions() || barrier!=_exclusionBarrier;
        _trace?.Invoke(new("Scene",changed ? "SceneWindowsChanged" : frame is null ? "Unavailable" : "Delivered",
            System.Diagnostics.Stopwatch.GetTimestamp(),frame?.SourceQpcTicks ?? 0,
            BarrierTicks:_exclusionBarrier,Generation:_exclusionGeneration));
        return changed ? null : frame;
    }
    internal bool UpdateExclusions()
    {
        if (_desktopThumbnail != 0 && GetShellWindow() != _shellWindow)
            throw new InvalidOperationException("Desktop shell changed; the capture scene must be recreated.");
        if(DiagnosticFreezeExclusions) return false;
        var snapshotStart=System.Diagnostics.Stopwatch.GetTimestamp();
        var snapshot=WindowSnapshot();var excluded=snapshot.Excluded;
        _trace?.Invoke(new("SceneSnapshot","Completed",System.Diagnostics.Stopwatch.GetTimestamp(),EndTicks:snapshotStart));
        if(_lastExcluded is not null && _lastExcluded.AsSpan().SequenceEqual(excluded)
            && _lastMembers is not null && _lastMembers.AsSpan().SequenceEqual(snapshot.Members)) return false;
        if (_lastExcluded is not null && _lastExcluded.AsSpan().SequenceEqual(excluded)
            && _lastMembers is not null && CanFollowLiveWindows(_lastMembers, snapshot.Members))
        {
            // This live shared visual follows the same HWNDs' DWM transforms.
            // Replacing it for every bounds change blocks acquisition needlessly.
            // Hidden/closed HWNDs also stop contributing to this live visual.
            // Track every removal/bounds change and retire pre-change frames;
            // maximize/restore can replace a foreign redirected surface while keeping
            // its HWND. Rebind those transitions, as well as additions/z-order/exclusions.
            _lastMembers = snapshot.Members;
            _motionBarrier=System.Diagnostics.Stopwatch.GetTimestamp();
            _capture?.RetireQueuedFramesAtSceneBoundary(_motionBarrier);
            _trace?.Invoke(new("SceneMotion","BoundsOrRemoval",_motionBarrier,BarrierTicks:_motionBarrier));
            // DWM follows transforms/removals on this live visual. Preserve the
            // hard scene epoch so the parent can publish a fresh acquired frame
            // while motion continues; the next read still requires fresh pixels.
            return false;
        }
        if(_trace is not null && _lastExcluded is not null)
        {
            var reason="OwnHandles";SceneWindow changedWindow=default;
            if(_lastExcluded.AsSpan().SequenceEqual(excluded) && _lastMembers is not null)
            {
                reason="ExternalSet";
                var index=0;while(index<Math.Min(_lastMembers.Length,snapshot.Members.Length) && _lastMembers[index]==snapshot.Members[index]) index++;
                if(index<snapshot.Members.Length) changedWindow=snapshot.Members[index];
                if(index<_lastMembers.Length && index<snapshot.Members.Length && _lastMembers[index].Handle==changedWindow.Handle)
                    reason=_lastMembers[index].Visible==changedWindow.Visible ? "ExternalBounds" : "ExternalVisibility";
            }
            var name=new System.Text.StringBuilder(128);
            if(changedWindow.Handle!=0) GetClassName(changedWindow.Handle,name,name.Capacity);
            _trace(new("SceneChange",reason,System.Diagnostics.Stopwatch.GetTimestamp(),
                WindowHandle:changedWindow.Handle.ToInt64(),WindowProcess:changedWindow.ProcessId,WindowClass:name.ToString()));
        }
        // Updating an attached shared visual causes observed blank capture textures.
        // Build a replacement root, retaining the carrier/device/WGC pool and cadence.
        if(target!=0) ReplaceVisual(excluded,snapshot.Members);
        else {ApplyExclusions(excluded);_lastMembers=snapshot.Members;}
        return true;
    }
    internal void ReleaseReadbackBuffers() => _capture?.ReleaseReadbackBuffers();
    private static bool CanFollowLiveWindows(SceneWindow[] previous, SceneWindow[] current)
    {
        var index = 0;
        foreach (var window in current)
        {
            while (index < previous.Length && (previous[index].Handle != window.Handle
                || previous[index].ProcessId != window.ProcessId || previous[index].Visible != window.Visible)) index++;
            if (index == previous.Length) return false;
            if (previous[index].Maximized != window.Maximized) return false;
            index++;
        }
        return true;
    }
    private (nint[] Excluded,SceneWindow[] Members) WindowSnapshot()
    {
        var exclusions=new List<nint>();
        var members=new List<SceneWindow>();
        var family=_excludedProcesses?.Invoke() ?? [];
        if(!EnumWindows((hwnd,_)=>
        {
            GetWindowThreadProcessId(hwnd,out var pid);
            if(pid==Environment.ProcessId || pid==_excludedParentProcess || family.Contains((int)pid)) exclusions.Add(hwnd);
            else if(pid!=0 && IsWindowVisible(hwnd) && !IsIconic(hwnd)
                && !(DwmGetWindowAttribute(hwnd,14,out int cloaked,sizeof(int))>=0 && cloaked!=0)
                && GetWindowRect(hwnd,out var rect)
                && rect.Right>rect.Left && rect.Bottom>rect.Top
                && rect.Left<_region.X+_region.Width && rect.Top<_region.Y+_region.Height
                && rect.Right>_region.X && rect.Bottom>_region.Y)
                // Hidden pointer helpers and offscreen windows cannot contribute pixels.
                // Rebuilding the shared visual for their motion retires valid live frames.
                members.Add(new(hwnd,pid,rect.Left,rect.Top,rect.Right,rect.Bottom,true,IsZoomed(hwnd)));
            return true;
        },0))
            throw new InvalidOperationException("Own-process HWND enumeration failed.");
        return (exclusions.Order().ToArray(),members.ToArray());
    }
    private void ApplyExclusions(nint[] excluded,bool commit=true)
    {
        SetNativeExclusions(thumbnail,excluded);
        if(commit) DiagnosticCommitOnly();
        RecordExclusions(excluded);
    }
    private void SetNativeExclusions(nint thumbnailHandle,nint[] excluded)
    {
        var update=(delegate* unmanaged[Stdcall]<nint,nint*,uint,nint*,uint,Rect*,Size*,uint,int>)GetProcAddress(library,164);
        var source=new Rect {Left=_region.X,Top=_region.Y,Right=_region.X+_region.Width,Bottom=_region.Y+_region.Height};
        var size=new Size {Width=_region.Width,Height=_region.Height};
        fixed(nint* exclude=excluded) Check(update(thumbnailHandle,null,0,exclude,(uint)excluded.Length,&source,&size,1));
    }
    private void RecordExclusions(nint[] excluded)
    {
        _motionBarrier=0;
        _exclusionBarrier=System.Diagnostics.Stopwatch.GetTimestamp();
        _exclusionGeneration++;
        _lastExcluded=excluded;
        _trace?.Invoke(new("Exclusions","Changed",_exclusionBarrier,BarrierTicks:_exclusionBarrier,Generation:_exclusionGeneration));
        _capture?.RetireQueuedFramesAtSceneBoundary(_exclusionBarrier);
    }
    public void Dispose()
    {
            _capture?.Dispose();_capture=null;
            if(target!=0) ((delegate* unmanaged[Stdcall]<nint,nint,int>)(*(nint**)target)[3])(target,0);
            if(thumbnail!=0) {DwmUnregisterThumbnail(thumbnail);thumbnail=0;}
            if(_desktopThumbnail!=0) {DwmUnregisterThumbnail(_desktopThumbnail);_desktopThumbnail=0;}
            Release(ref visual);Release(ref _desktopVisual);Release(ref _rootVisual);Release(ref target);Release(ref composition);
            if(carrier!=0) {DestroyWindow(carrier);carrier=0;}
            if(carrierOwner!=0) {DestroyWindow(carrierOwner);carrierOwner=0;}
            Release(ref dxgi);Release(ref context);Release(ref device);
            if(library!=0) {NativeLibrary.Free(library);library=0;}
    }
    private static void Check(int hr)=>Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint pointer) {if(pointer!=0) Marshal.Release(pointer);pointer=0;}
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left,Top,Right,Bottom;
        public Rect(int left,int top,int right,int bottom) {Left=left;Top=top;Right=right;Bottom=bottom;}
    }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    {public uint Flags;public Rect Destination,Source;public byte Opacity;public int Visible,ClientOnly;}
    private readonly record struct SceneWindow(nint Handle,uint ProcessId,int Left,int Top,int Right,int Bottom,bool Visible,bool Maximized);
    [StructLayout(LayoutKind.Sequential)] private struct Size {public int Width,Height;}
    [StructLayout(LayoutKind.Sequential)] private struct CompositionAttribute {public int Attribute;public nint Data;public nuint Size;}
    private delegate bool EnumWindowProc(nint hwnd,nint parameter);
    [DllImport("kernel32.dll",ExactSpelling=true)] private static extern nint GetProcAddress(nint module,nint ordinal);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,uint type,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("dcomp.dll")] private static extern int DCompositionCreateDevice3(nint device,in Guid iid,out nint composition);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint CreateWindowExW(uint extendedStyle,[MarshalAs(UnmanagedType.LPWStr)] string className,[MarshalAs(UnmanagedType.LPWStr)] string name,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWindowLongPtrW(nint hwnd,int index,nint value);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(nint hwnd,ref CompositionAttribute data);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback,nint parameter);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd,System.Text.StringBuilder text,int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
