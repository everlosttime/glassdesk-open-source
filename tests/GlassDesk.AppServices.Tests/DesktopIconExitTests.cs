using System.Reflection;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class DesktopIconExitTests
{
    private static DesktopIconSession Session(Func<bool> read, Action<bool> write, bool forceVisible)
    {
        var constructor=typeof(DesktopIconSession).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,
            null,[typeof(Func<bool>),typeof(Action<bool>),typeof(bool)],null);
        Assert.NotNull(constructor);
        return (DesktopIconSession)constructor.Invoke([read,write,forceVisible]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractiveHostExitAlwaysShowsIconsEvenIfTheyStartedHidden(bool initiallyVisible)
    {
        var visible=initiallyVisible; var writes=0;
        var session=Session(()=>visible,value=>{visible=value;writes++;},true);
        Assert.True(session.TrySetVisible(false,out _));
        session.Dispose();
        Assert.True(visible);
        var after=writes; session.Dispose(); Assert.Equal(after,writes);
    }

    [Fact]
    public void ExternalHideAndAReadOnlySessionDoNotPreventExplicitExitRestoration()
    {
        var visible=false;
        using(var readOnly=Session(()=>visible,value=>visible=value,false)) Assert.False(readOnly.AreIconsVisible());
        Assert.False(visible);
        using(var owner=Session(()=>visible,value=>visible=value,true)) Assert.False(owner.AreIconsVisible());
        Assert.True(visible);
    }

    [Fact]
    public void ExplorerUnavailableDoesNotThrowDuringExit()
    {
        var session=Session(()=>throw new InvalidOperationException("Explorer restarting"),_=>throw new InvalidOperationException(),true);
        session.Dispose(); session.Dispose();
    }
}
