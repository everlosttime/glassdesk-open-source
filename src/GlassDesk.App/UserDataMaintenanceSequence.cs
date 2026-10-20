using GlassDesk.AppServices;

namespace GlassDesk.App;

internal static class UserDataMaintenanceSequence
{
    public static UserDataDeletionResult? Execute(
        Action closeCommandDispatch,
        Action prepareSettings,
        Action stopInteractions,
        Action stopTray,
        Action stopServices,
        Action closeMessageWindow,
        Action releaseDataLease,
        Func<UserDataDeletionResult> deleteData,
        Action<Exception> reportStopFailure,
        Action<UserDataDeletionResult> presentResult,
        Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(closeCommandDispatch);
        ArgumentNullException.ThrowIfNull(prepareSettings);
        ArgumentNullException.ThrowIfNull(stopInteractions);
        ArgumentNullException.ThrowIfNull(stopTray);
        ArgumentNullException.ThrowIfNull(stopServices);
        ArgumentNullException.ThrowIfNull(closeMessageWindow);
        ArgumentNullException.ThrowIfNull(releaseDataLease);
        ArgumentNullException.ThrowIfNull(deleteData);
        ArgumentNullException.ThrowIfNull(reportStopFailure);
        ArgumentNullException.ThrowIfNull(presentResult);
        ArgumentNullException.ThrowIfNull(shutdown);

        try
        {
            closeCommandDispatch();
            prepareSettings();
            stopInteractions();
            stopTray();
            stopServices();
            closeMessageWindow();
            releaseDataLease();
        }
        catch (Exception ex)
        {
            reportStopFailure(ex);
            shutdown();
            return null;
        }

        var result = deleteData();
        presentResult(result);
        shutdown();
        return result;
    }
}

internal sealed class DeferredCommandGate
{
    private int _closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void Close() => Interlocked.Exchange(ref _closed, 1);

    public bool TryRun(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (IsClosed) return false;
        callback();
        return true;
    }
}
