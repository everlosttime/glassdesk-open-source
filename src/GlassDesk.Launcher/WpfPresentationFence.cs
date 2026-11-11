using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDesk.Launcher;

// WPF exposes Rendering before it commits/presents the scene. Its own HWND
// resize path uses MediaContext.CompleteRender for this cross-renderer boundary.
// Resolve this optional runtime capability before selecting unified output;
// runtimes without it retain the complete existing WPF renderer.
internal static class WpfPresentationFence
{
    internal static Action Create(Dispatcher dispatcher)
    {
        var context = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext");
        var from = context?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Dispatcher)]);
        var complete = context?.GetMethod("CompleteRender", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
        if (from is null || complete is null)
            throw new NotSupportedException("WPF presentation synchronization is unavailable.");
        var owner = from.Invoke(null, [dispatcher]) ?? throw new NotSupportedException("WPF presentation context is unavailable.");
        return () =>
        {
            dispatcher.VerifyAccess();
            try { complete.Invoke(owner, null); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            { ExceptionDispatchInfo.Capture(inner).Throw(); }
        };
    }
}
