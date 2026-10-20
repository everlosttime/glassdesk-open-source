using System.Reflection;
using GlassDesk.Platform.Displays;
namespace GlassDesk.AppServices.Tests;
public sealed class Phase6NativeTests
{
    private static Type Native(string name) => typeof(PixelRect).Assembly.GetType(name, throwOnError: true)!;
    [Fact] public void HotkeyOwnerCannotUnregisterAnUnownedId()
    {
        var type = Native("GlassDesk.Platform.Input.GlobalHotkeyRegistration");
        using var instance = (IDisposable)Activator.CreateInstance(type, (nint)0)!;
        Assert.False((bool)type.GetMethod("Unregister")!.Invoke(instance, [19765])!);
        instance.Dispose();
        object?[] args = [19765, (uint)0, (uint)0x87, 0];
        Assert.False((bool)type.GetMethod("TryRegister")!.Invoke(instance, args)!);
        Assert.NotEqual(0, args[3]);
    }
    [Fact] public void SideButtonConsumesMatchingReleaseAndIgnoresInjectedInput()
    {
        var type = Native("GlassDesk.Platform.Input.MouseSideButtonHook");
        int calls = 0;
        using var hook = (IDisposable)Activator.CreateInstance(type, [(Func<int,bool>)(button => { calls++; return button == 1; }), null])!;
        var method = type.GetMethod("HandleButton", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool Handle(int button, bool down, bool injected) => (bool)method.Invoke(hook,[button,down,injected])!;
        Assert.False(Handle(1,true,true)); Assert.Equal(0,calls);
        Assert.False(Handle(1,false,false));
        Assert.True(Handle(1,true,false)); Assert.True(Handle(1,false,false));
        Assert.False(Handle(1,false,false)); Assert.Equal(1,calls);
        Assert.False(Handle(2,true,false)); Assert.False(Handle(2,false,false));
        Assert.Equal(2,calls);
    }
    [Fact] public void CallbackExceptionDoesNotConsumeButtonOrEscape()
    {
        var type = Native("GlassDesk.Platform.Input.MouseSideButtonHook");
        using var hook = (IDisposable)Activator.CreateInstance(type, [(Func<int,bool>)(_ => throw new InvalidOperationException()), null])!;
        var method = type.GetMethod("HandleButton",BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)method.Invoke(hook,[1,true,false])!);
        Assert.False((bool)method.Invoke(hook,[1,false,false])!);
    }
}
