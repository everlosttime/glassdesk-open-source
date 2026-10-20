using System.Runtime.CompilerServices;

namespace GlassDesk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Capture helpers own native resources only. Loading the desktop Application
        // and its theme dictionaries before the pipe handshake delays every cold reveal.
        if (UI.BackdropCaptureWorker.RunIfRequested(args, out var exitCode)) return exitCode;
        return RunDesktop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDesktop()
    {
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
