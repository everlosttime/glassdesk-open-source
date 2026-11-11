using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

// All audio readers must use the same managed coclass. Windows can return the
// same COM identity; duplicate coclass types then conflict in the RCW cache.
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class AudioDeviceEnumerator;
