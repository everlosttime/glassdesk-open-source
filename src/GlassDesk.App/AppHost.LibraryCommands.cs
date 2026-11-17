using GlassDesk.App.Startup;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Storage;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private LibraryCommandBroker? _libraryCommandBroker;
    private bool _libraryCommandsSuspended;
    private void SuspendLibraryCommands() => _libraryCommandsSuspended = true;
    private void ResumeLibraryCommands() => _libraryCommandsSuspended = false;
    private bool InitializeLibraryCommandBroker()
    {
        var root = AppPaths.ForCurrentUser().RootDirectory;
        if (!LibraryWriterLease.TryAcquire(root, out var lease) || lease is null) return false;
        _libraryCommandBroker = new(root, lease, request => _application.Dispatcher.InvokeAsync(() =>
        {
            if (request.ExecutionCancellation.IsCancellationRequested || _disposed || _shuttingDown || _libraryCommandsSuspended || _services is null || _libraryCommandBroker is null) return new LibraryCommandReply(1, "GlassDesk 正在启动、维护或退出，请稍后重试。");
            return AppServiceRunner.ExecuteOnHost(_services, CommandLineOptions.Parse(request.Arguments));
        }).Task);
        _libraryCommandBroker.Start();
        return true;
    }
    private void StopLibraryCommandBroker()
    {
        _libraryCommandBroker?.Dispose();
        _libraryCommandBroker = null;
    }
}
