using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed class NativeCadenceWait : ICadenceWait
{
    private readonly SafeWaitHandle timer;
    internal NativeCadenceWait()
    {
        timer=new SafeWaitHandle(CreateWaitableTimerExW(0,null,2,0x1F0003),true);
        if(timer.IsInvalid) {timer.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error(),"High-resolution waitable timer unavailable.");}
    }
    public void Wait(double milliseconds,CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested || milliseconds<=0) return;
        var due=-Math.Max(1,(long)Math.Ceiling(milliseconds*10_000));
        if(!SetWaitableTimer(timer,ref due,0,0,0,false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var handles=cancellation.CanBeCanceled
            ? new[] {cancellation.WaitHandle.SafeWaitHandle.DangerousGetHandle(),timer.DangerousGetHandle()}
            : new[] {timer.DangerousGetHandle()};
        if(WaitForMultipleObjects((uint)handles.Length,handles,false,uint.MaxValue)==uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => timer.Dispose();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateWaitableTimerExW(nint attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long due,int period,nint callback,nint argument,bool resume);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForMultipleObjects(uint count,nint[] handles,bool waitAll,uint timeout);
}
