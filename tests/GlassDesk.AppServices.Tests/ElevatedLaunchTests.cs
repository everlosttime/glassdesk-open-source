using System.Reflection;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class ElevatedLaunchTests
{
    private static LaunchService Create(AppLibraryStore library, Func<AppEntry, bool, ShellLaunchOutcome> open)
    {
        var constructor = typeof(LaunchService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(AppLibraryStore), typeof(Func<AppEntry, bool, ShellLaunchOutcome>)], null);
        Assert.NotNull(constructor);
        return (LaunchService)constructor.Invoke([library, open]);
    }

    [Fact]
    public void ExplicitAdministratorLaunchPreservesArgumentsAndDoesNotElevateNormalLaunch()
    {
        using var workspace = new TempWorkspace();
        var data = new GlassDeskDataStore(workspace.DataPaths); data.LoadAll();
        var target = workspace.CreateFile("My Application.exe");
        var entry = new AppEntry { Id="app", Kind=AppItemKind.Program, Target=target, Arguments="--profile \"a b\"", WorkingDirectory=workspace.Root };
        data.Library.Add(entry);
        var requests = new List<(AppEntry entry, bool elevated)>();
        var service = Create(data.Library, (item, elevated) => { requests.Add((item, elevated)); return new(true, 0, 123); });
        Assert.True(service.LaunchItem(entry.Id).Succeeded);
        var method = typeof(LaunchService).GetMethod("LaunchAsAdministrator"); Assert.NotNull(method);
        Assert.True(((LaunchResult)method.Invoke(service, [entry.Id])!).Succeeded);
        Assert.Equal(new[] { false, true }, requests.Select(request => request.elevated));
        Assert.All(requests, request => { Assert.Equal(target, request.entry.Target); Assert.Equal("--profile \"a b\"", request.entry.Arguments); Assert.Equal(workspace.Root, request.entry.WorkingDirectory); });
        Assert.Equal(123u, service.LastLaunchedProcessId);
    }

    [Theory]
    [InlineData(AppItemKind.Url, "https://example.com")]
    [InlineData(AppItemKind.Folder, "folder")]
    [InlineData(AppItemKind.File, "document.txt")]
    public void UnsupportedTargetsNeverRequestElevation(AppItemKind kind, string target)
    {
        using var workspace = new TempWorkspace();
        var data = new GlassDeskDataStore(workspace.DataPaths); data.LoadAll();
        data.Library.Add(new AppEntry { Id="item", Kind=kind, Target=target });
        var service = Create(data.Library, (_, _) => throw new InvalidOperationException("Must not launch"));
        var method = typeof(LaunchService).GetMethod("LaunchAsAdministrator"); Assert.NotNull(method);
        var result = (LaunchResult)method.Invoke(service, ["item"])!;
        Assert.False(result.Succeeded); Assert.Equal(LaunchFailureReason.Unsupported, result.Failure);
    }

    [Fact]
    public void CancelledElevationLeavesTheLibraryIntact()
    {
        using var workspace = new TempWorkspace();
        var data = new GlassDeskDataStore(workspace.DataPaths); data.LoadAll();
        var target = workspace.CreateFile("app.exe");
        data.Library.Add(new AppEntry { Id="app", Target=target }); data.Library.Save();
        var before = File.ReadAllBytes(data.Paths.AppLibraryFile);
        var service = Create(data.Library, (_, elevated) => { Assert.True(elevated); return new(false, 1223, 0); });
        var method = typeof(LaunchService).GetMethod("LaunchAsAdministrator"); Assert.NotNull(method);
        var result = (LaunchResult)method.Invoke(service, ["app"])!;
        Assert.False(result.Succeeded); Assert.Contains("取消", result.Message);
        Assert.Equal(before, File.ReadAllBytes(data.Paths.AppLibraryFile));
    }
}
