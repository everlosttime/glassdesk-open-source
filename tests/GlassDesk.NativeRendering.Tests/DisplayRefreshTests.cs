using System.Reflection;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DisplayRefreshTests
{
    private static MethodInfo Query()
    {
        var method=typeof(DisplayService).GetMethod("RefreshForWindow",[typeof(nint)]);
        Assert.NotNull(method);return method;
    }
    [Fact]
    public void RefreshQueryRejectsMissingWindowRatherThanSilentlyChoosingPrimary()=>NativeEngineTests.OnSta(()=>
    {
        Assert.Null(Query().Invoke(null,[nint.Zero]));
        Assert.Null(Query().Invoke(null,[(nint)(-12345)]));
    });
    [Fact]
    public void RefreshQueryFollowsTheActualOwnedWindowOnEachPhysicalDisplay()=>NativeEngineTests.OnSta(()=>
    {
        var query=Query();var displays=DisplayService.Enumerate();Assert.NotEmpty(displays);
        var hwnd=CreateWindowEx(0x08000080,"STATIC","Owned refresh query",unchecked((uint)0x80000000),0,0,80,80,0,0,0,0);
        Assert.NotEqual(0,hwnd);
        try
        {
            foreach(var display in displays)
            {
                Assert.True(DisplayService.MoveWindowToPixelRect(hwnd,new(display.WorkArea.X+40,display.WorkArea.Y+40,80,80)));
                var value=query.Invoke(null,[hwnd]);Assert.NotNull(value);dynamic result=value;
                Assert.Equal(display.DeviceName,(string)result.DeviceName);
                Assert.True((uint)result.Numerator>0);Assert.True((uint)result.Denominator>0);
                Assert.InRange((double)result.Hertz,1d,2000d);
            }
        }
        finally{DestroyWindow(hwnd);}
    });
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint arg);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
}
