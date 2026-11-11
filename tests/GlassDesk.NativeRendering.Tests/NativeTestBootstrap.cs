using System.Runtime.CompilerServices;

namespace GlassDesk.NativeRendering.Tests;

internal static class NativeTestBootstrap
{
    // Any presenter entry point can be the first P/Invoke in a filtered run.
    // Install the explicit payload resolver before that call, rather than
    // depending on another test having touched Api first.
    [ModuleInitializer]
    internal static void Initialize() => RuntimeHelpers.RunClassConstructor(typeof(Api).TypeHandle);
}
