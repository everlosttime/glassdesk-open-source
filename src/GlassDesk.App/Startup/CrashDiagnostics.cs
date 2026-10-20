using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GlassDesk.App.Startup;

/// <summary>Records fatal managed failures without marking them handled or changing shutdown.</summary>
internal sealed class CrashDiagnostics : IDisposable
{
    private readonly Application _application;
    private readonly string _path;
    private readonly object _writeLock = new();

    internal CrashDiagnostics(Application application, string logDirectory)
    {
        _application = application;
        _path = Path.Combine(logDirectory, "crash.log");
        application.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        lock (_writeLock) TryWrite(_path, "Dispatcher", args.Exception);
        // Keep Handled unchanged: diagnostic collection must not conceal a fatal error.
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs args)
    {
        lock (_writeLock) TryWrite(_path, "AppDomain", args.ExceptionObject);
    }

    internal static string FormatReport(string source, object exception) => string.Join(Environment.NewLine,
        DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        $"Source: {source}",
        $"Executable: {Environment.ProcessPath}",
        $"Base directory: {AppContext.BaseDirectory}",
        $"Version: {typeof(CrashDiagnostics).Assembly.GetName().Version}",
        $"Runtime: {Environment.Version}; OS: {Environment.OSVersion}",
        exception.ToString(), "");

    internal static void TryWrite(string path, string source, object exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, FormatReport(source, exception) + Environment.NewLine);
        }
        catch (Exception)
        {
            // Disk/access failures while reporting must not replace the original exception.
        }
    }

    public void Dispose()
    {
        _application.DispatcherUnhandledException -= OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
    }
}
