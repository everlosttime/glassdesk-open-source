using GlassDesk.Core.Imaging;
using GlassDesk.Core.Contracts;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public class ShortcutIconCompatibilityTests
{
    [Fact]
    public void ExtensionlessInstallerIconUsesEmbeddedImage()
    {
        using var workspace = new TempWorkspace();
        var iconPath = WriteIcon(workspace, "application.ico");
        var installerIconPath = Path.Combine(workspace.Root, "ProductIcon");
        File.Copy(iconPath, installerIconPath);

        var expected = IconExtractor.Extract(iconPath + ",0");
        var actual = IconExtractor.Extract(installerIconPath);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.True(actual.VisiblePixelCount > 0);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Bgra, actual.Bgra);
    }

    [Fact]
    public void IndexedInstallerIconKeepsHighResolution()
    {
        using var workspace = new TempWorkspace();
        var iconPath = WriteIcon(workspace, "ProductIcon", 256);

        var extracted = IconExtractor.Extract(iconPath + ",0");

        Assert.NotNull(extracted);
        Assert.Equal(256, extracted.Width);
        Assert.Equal(256, extracted.Height);
        var center = ((128 * extracted.Width) + 128) * 4;
        Assert.Equal(new byte[] { 220, 80, 30, 255 }, extracted.Bgra.AsSpan(center, 4).ToArray());
    }

    [Fact]
    public void MissingInstallerIconFallsBackToExistingTarget()
    {
        using var workspace = new TempWorkspace();
        var targetPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var shortcutPath = Path.Combine(workspace.Root, "application.lnk");
        Assert.True(ShortcutWriter.Write(shortcutPath, new ShortcutDefinition(
            targetPath, IconLocation: Path.Combine(workspace.Root, "missing", "ProductIcon"))));

        var result = AppEntryFactory.TryCreate(new ImportRequest { Source = shortcutPath });

        Assert.NotNull(result.Entry);
        Assert.Equal(targetPath, result.Entry.IconSource, ignoreCase: true);
        Assert.False(result.Entry.IsTargetMissing);
    }

    [Fact]
    public void MissingTargetAndInstallerIconStillProduceVisibleCachedIcon()
    {
        using var workspace = new TempWorkspace();
        var shortcutPath = Path.Combine(workspace.Root, "application.lnk");
        Assert.True(ShortcutWriter.Write(shortcutPath, new ShortcutDefinition(
            Path.Combine(workspace.Root, "missing", "application.exe"),
            IconLocation: Path.Combine(workspace.Root, "missing", "ProductIcon"))));
        var result = AppEntryFactory.TryCreate(new ImportRequest { Source = shortcutPath });

        Assert.NotNull(result.Entry);
        Assert.True(result.Entry.IsTargetMissing);
        var cached = new IconCache(workspace.DataPaths).EnsureFor(result.Entry.IconSource);
        Assert.NotNull(cached);
        Assert.True(File.Exists(cached.Path));
    }

    [Fact]
    public void ExistingCustomInstallerIconIsPreserved()
    {
        using var workspace = new TempWorkspace();
        var iconPath = WriteIcon(workspace, "ProductIcon");
        var shortcutPath = Path.Combine(workspace.Root, "application.lnk");
        Assert.True(ShortcutWriter.Write(shortcutPath, new ShortcutDefinition(
            Path.Combine(Environment.SystemDirectory, "notepad.exe"), IconLocation: iconPath)));

        var result = AppEntryFactory.TryCreate(new ImportRequest { Source = shortcutPath });

        Assert.NotNull(result.Entry);
        Assert.Equal(iconPath, result.Entry.IconSource);
    }

    private static string WriteIcon(TempWorkspace workspace, string name, int size = 32)
    {
        var pixels = new byte[size * size * 4];
        for (var row = 4; row < size - 4; row++)
        {
            for (var column = 4; column < size - 4; column++)
            {
                var offset = (row * size + column) * 4;
                pixels[offset] = 220;
                pixels[offset + 1] = 80;
                pixels[offset + 2] = 30;
                pixels[offset + 3] = 255;
            }
        }

        var png = PngWriter.EncodeBgra(pixels, size, size);
        var path = Path.Combine(workspace.Root, name);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(22);
        writer.Write(png);
        return path;
    }
}
