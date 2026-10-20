using System.Reflection;
using GlassDesk.AppServices;
using GlassDesk.Platform.Storage;
using Microsoft.Win32.SafeHandles;
using GlassDesk.App;

namespace GlassDesk.AppServices.Tests;

public sealed class UserDataMaintenanceSequenceTests
{
    [Fact]
    public void Maintenance_stops_all_writers_before_deletion_and_exits_after_reporting()
    {
        var steps = new List<string>();
        using var workspace = new TempWorkspace();
        var dataRoot = Path.Combine(workspace.Root, "MaintenanceData");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "temporary test data");
        Assert.True(UserDataLease.TryAcquireShared(dataRoot, out var runtimeLease));
        Assert.NotNull(runtimeLease);
        using var lease = runtimeLease;
        var fileSystem = DispatchProxy.Create<IUserDataDeletionFileSystem, RecordingFileSystemProxy>();
        ((RecordingFileSystemProxy)(object)fileSystem).Steps = steps;
        var deletionService = new UserDataDeletionService(dataRoot, dataRoot, Array.Empty<string>(), fileSystem);

        var result = UserDataMaintenanceSequence.Execute(
            closeCommandDispatch: () => steps.Add("close-command-dispatch"),
            prepareSettings: () => steps.Add("block-writes"),
            stopInteractions: () => steps.Add("stop-interactions"),
            stopTray: () => steps.Add("stop-tray"),
            stopServices: () => steps.Add("stop-services"),
            closeMessageWindow: () => steps.Add("close-message-window"),
            releaseDataLease: () =>
            {
                steps.Add("release-data-lease");
                lease.Dispose();
            },
            deleteData: () =>
            {
                steps.Add("delete-service");
                return deletionService.DeleteAll(confirmed: true);
            },
            reportStopFailure: _ => steps.Add("report-stop-failure"),
            presentResult: _ => steps.Add("report-result"),
            shutdown: () => steps.Add("exit"));

        Assert.NotNull(result);
        Assert.Equal(UserDataDeletionStatus.Deleted, result.Status);
        Assert.False(Directory.Exists(dataRoot));
        Assert.Equal(
            [
                "close-command-dispatch",
                "block-writes",
                "stop-interactions",
                "stop-tray",
                "stop-services",
                "close-message-window",
                "release-data-lease",
                "delete-service",
                "rename",
                "delete-file",
                "delete-directory",
                "report-result",
                "exit",
            ],
            steps);
    }

    [Fact]
    public void Stop_failure_never_calls_deletion_and_exits_with_error_report()
    {
        var steps = new List<string>();

        UserDataMaintenanceSequence.Execute(
            closeCommandDispatch: () => steps.Add("close-command-dispatch"),
            prepareSettings: () => steps.Add("block-writes"),
            stopInteractions: () => throw new InvalidOperationException("hook did not stop"),
            stopTray: () => steps.Add("stop-tray"),
            stopServices: () => steps.Add("stop-services"),
            closeMessageWindow: () => steps.Add("close-message-window"),
            releaseDataLease: () => steps.Add("release-data-lease"),
            deleteData: () =>
            {
                steps.Add("delete");
                return new UserDataDeletionResult(UserDataDeletionStatus.Deleted);
            },
            reportStopFailure: _ => steps.Add("report-stop-failure"),
            presentResult: _ => steps.Add("report-result"),
            shutdown: () => steps.Add("exit"));

        Assert.Equal(["close-command-dispatch", "block-writes", "report-stop-failure", "exit"], steps);
    }

    [Fact]
    public void Queued_command_is_discarded_if_maintenance_closes_gate_before_dispatch()
    {
        var gate = new DeferredCommandGate();
        var handled = 0;
        Action queuedCommand = () => gate.TryRun(() => handled++);

        gate.Close();
        queuedCommand();

        Assert.Equal(0, handled);
        Assert.False(gate.TryRun(() => handled++));
    }

    public class RecordingFileSystemProxy : DispatchProxy
    {
        public List<string> Steps { get; set; } = [];
        private readonly UserDataDeletionFileSystem _inner = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || args is null) throw new InvalidOperationException("Missing filesystem invocation details.");
            var name = targetMethod.Name;
            if (name == "DeleteDirectoryByHandle")
            {
                Steps.Add("delete-directory");
                _inner.DeleteDirectoryByHandle((SafeFileHandle)args[0]!);
                return null;
            }

            if (name == "DeleteFileByHandle")
            {
                var filePath = (string)args[1]!;
                Steps.Add("delete-file");
                _inner.DeleteFileByHandle((SafeFileHandle)args[0]!, filePath);
                return null;
            }

            var path = args.OfType<string>().FirstOrDefault()
                ?? throw new InvalidOperationException($"Unexpected filesystem operation: {name}");
            object? value = name switch
            {
                "Exists" => Directory.Exists(path) || File.Exists(path),
                "GetAttributes" => File.GetAttributes(path),
                "EnumerateFileSystemEntries" or "EnumerateEntries" => Directory.EnumerateFileSystemEntries(path).ToArray(),
                "MoveDirectory" or "Move" => Move(path, args),
                _ => throw new InvalidOperationException($"Unexpected filesystem operation: {name}"),
            };
            if (targetMethod.ReturnType == typeof(void)) return null;
            return value;
        }

        private object? Move(string source, object?[] args)
        {
            Steps.Add("rename");
            Directory.Move(source, args.OfType<string>().Skip(1).First());
            return null;
        }

    }
}
