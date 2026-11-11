using System.IO;
using System.Windows.Media.Imaging;

namespace GlassDesk.Launcher;

internal static class LauncherIconDecoder
{
    internal static BitmapImage DecodeFile(string path, int maximumPixels)
    {
        return GlassDesk.UI.Imaging.IconBitmapLoader.DecodeFile(path, maximumPixels);
    }
}
