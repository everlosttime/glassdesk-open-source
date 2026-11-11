using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopWindowAttachmentTests
{
    private static object Create(nint window, Action<nint> setOwner, Func<nint> provider)
    {
        var type=typeof(GlassDesk.Platform.WindowStyles).Assembly.GetType("GlassDesk.Platform.DesktopWindowAttachment");
        Assert.NotNull(type);
        return Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[window,setOwner,provider],null)!;
    }
    private static bool Call(object session,string name)=>(bool)session.GetType().GetMethod(name)!.Invoke(session,null)!;

    [Fact]
    public void NativeOwnershipPreservesDpiBoundsAndTopLevelStylesAndRestoresOriginalOwner()
    {
        StaTest.Run(()=>
        {
            var desktop=new Window();var bar=new Window{ShowInTaskbar=false,ShowActivated=false};
            var parent=new WindowInteropHelper(desktop).EnsureHandle();var helper=new WindowInteropHelper(bar);
            var handle=helper.EnsureHandle();var original=GetWindow(handle,4);var dpi=GetWindowDpiAwarenessContext(handle);
            GetWindowRect(handle,out var before);var style=GetWindowLong(handle,-16);
            var session=Create(handle,owner=>helper.Owner=owner,()=>parent);
            try
            {
                Assert.True(Call(session,"TryAttach"));Assert.Equal(parent,GetWindow(handle,4));
                Assert.True(AreDpiAwarenessContextsEqual(dpi,GetWindowDpiAwarenessContext(handle)));
                Assert.Equal(style,GetWindowLong(handle,-16));GetWindowRect(handle,out var after);Assert.Equal(before,after);
                Assert.Equal(0,GetWindowLong(handle,-20)&8);
                ((IDisposable)session).Dispose();Assert.Equal(original,GetWindow(handle,4));
            }
            finally{((IDisposable)session).Dispose();bar.Close();desktop.Close();}
        });
    }

    [Fact]
    public void MissingAndReplacementDesktopAreHandledWithoutShowingOrActivatingTheBar()
    {
        StaTest.Run(()=>
        {
            var first=new Window();var second=new Window();var bar=new Window{ShowInTaskbar=false,ShowActivated=false};
            var firstHandle=new WindowInteropHelper(first).EnsureHandle();var secondHandle=new WindowInteropHelper(second).EnsureHandle();
            var helper=new WindowInteropHelper(bar);var handle=helper.EnsureHandle();nint host=0;
            var session=Create(handle,owner=>helper.Owner=owner,()=>host);
            try
            {
                Assert.False(Call(session,"TryAttach"));host=firstHandle;Assert.True(Call(session,"TryAttach"));
                Assert.Equal(firstHandle,GetWindow(handle,4));host=secondHandle;Assert.True(Call(session,"TryAttach"));
                Assert.Equal(secondHandle,GetWindow(handle,4));Assert.False(bar.IsVisible);
                host=0;Assert.False(Call(session,"TryAttach"));Assert.False(bar.IsVisible);
            }
            finally{((IDisposable)session).Dispose();bar.Close();first.Close();second.Close();}
        });
    }

    [Fact]
    public void DestroyedNativeDesktopOwnerNotifiesTheBarLifetime()
    {
        StaTest.Run(() =>
        {
            var desktop = new Window();
            var bar = new Window { ShowInTaskbar = false, ShowActivated = false };
            var host = new WindowInteropHelper(desktop).EnsureHandle();
            var helper = new WindowInteropHelper(bar);
            var handle = helper.EnsureHandle();
            var closed = false;
            bar.Closed += (_, _) => closed = true;
            using var attachment = (IDisposable)Create(handle, owner => helper.Owner = owner, () => host);
            try
            {
                Assert.True(Call(attachment, "TryAttach"));
                desktop.Close();
                Assert.False(GlassDesk.Platform.WindowControl.IsAlive(handle));
                Assert.True(closed);
            }
            finally { bar.Close(); desktop.Close(); }
        });
    }

    [Fact]
    public void DisposeNeverOverwritesAnExternallyChangedOwner()
    {
        StaTest.Run(()=>
        {
            var desktop=new Window();var replacement=new Window();var bar=new Window();
            var parent=new WindowInteropHelper(desktop).EnsureHandle();var other=new WindowInteropHelper(replacement).EnsureHandle();
            var helper=new WindowInteropHelper(bar);var handle=helper.EnsureHandle();var session=Create(handle,owner=>helper.Owner=owner,()=>parent);
            try{Assert.True(Call(session,"TryAttach"));helper.Owner=other;((IDisposable)session).Dispose();Assert.Equal(other,GetWindow(handle,4));}
            finally{bar.Close();desktop.Close();replacement.Close();}
        });
    }
    [StructLayout(LayoutKind.Sequential)]private record struct NativeRect(int Left,int Top,int Right,int Bottom);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")]private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")]private static extern bool AreDpiAwarenessContextsEqual(nint first,nint second);
}
