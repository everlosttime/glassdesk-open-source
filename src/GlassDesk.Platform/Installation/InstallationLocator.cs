using System.Buffers.Binary;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace GlassDesk.Platform.Installation;

public enum InstallationState
{
    Portable,
    VerifiedInstalled,
    RegistrationMismatch,
}

public sealed record InstallationInfo(
    InstallationState State,
    string? InstallDirectory = null,
    string? UninstallerPath = null,
    string? Error = null);

/// <summary>只读核验当前程序是否与固定的 GlassDesk Inno Setup 安装登记相符。</summary>
/// <remarks>读取 UninstallString 仅用于提取候选路径；本类从不执行登记字符串或其参数。</remarks>
public sealed class InstallationLocator
{
    public const string AppId = "GlassDesk.Independent";
    public const string UninstallRegistrySubKey =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\GlassDesk.Independent_is1";

    private readonly string _registrySubKey;

    public InstallationLocator() : this(UninstallRegistrySubKey)
    {
    }

    internal InstallationLocator(string registrySubKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrySubKey);
        _registrySubKey = registrySubKey;
    }

    public InstallationInfo Inspect(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Mismatch("当前 GlassDesk 可执行文件路径不可用。");
        }

        RegistryKey? key;
        try
        {
            key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Mismatch("无法读取当前用户的 GlassDesk 安装登记：" + ex.Message);
        }

        if (key is null)
        {
            return new InstallationInfo(InstallationState.Portable);
        }

        using (key)
        {
            try
            {
                var displayName = ReadString(key, "DisplayName");
                var installValue = ReadString(key, "InstallLocation");
                var uninstallValue = ReadString(key, "UninstallString");
                if (!string.Equals(displayName, "GlassDesk", StringComparison.Ordinal))
                {
                    return Mismatch("固定产品登记的显示名称与 GlassDesk 不一致。");
                }

                if (string.IsNullOrWhiteSpace(installValue) || string.IsNullOrWhiteSpace(uninstallValue))
                {
                    return Mismatch("固定产品登记缺少安装位置或卸载程序路径。");
                }

                var installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installValue));
                var executable = Path.GetFullPath(executablePath);
                if (!PathsEqual(Path.GetDirectoryName(executable) ?? string.Empty, installDirectory)
                    || !string.Equals(Path.GetFileName(executable), "GlassDesk.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return Mismatch("当前进程不在登记的 GlassDesk 安装目录中。");
                }

                if (!Directory.Exists(installDirectory) || !File.Exists(executable)
                    || !HasNoReparsePointInPath(executable))
                {
                    return Mismatch("当前程序安装路径缺失或包含联接/符号链接。");
                }

                var uninstaller = ParseExecutablePath(uninstallValue);
                if (uninstaller is null)
                {
                    return Mismatch("卸载登记不是可核验的可执行文件路径。");
                }

                uninstaller = Path.GetFullPath(uninstaller);
                if (!PathsEqual(Path.GetDirectoryName(uninstaller) ?? string.Empty, installDirectory)
                    || !IsInnoUninstallerName(Path.GetFileName(uninstaller))
                    || !File.Exists(uninstaller)
                    || !HasNoReparsePointInPath(uninstaller)
                    || !IsPortableExecutable(uninstaller))
                {
                    return Mismatch("卸载程序不存在，或不属于当前 GlassDesk 安装目录。");
                }

                return new InstallationInfo(InstallationState.VerifiedInstalled, installDirectory, uninstaller);
            }
            catch (Exception ex) when (IsRegistryFailure(ex) || ex is IOException or ArgumentException or NotSupportedException)
            {
                return Mismatch("GlassDesk 安装登记无法安全核验：" + ex.Message);
            }
        }
    }

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    private static string? ParseExecutablePath(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0) return null;
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1 || trimmed[(closingQuote + 1)..].Trim().Length != 0) return null;
            return trimmed[1..closingQuote];
        }

        var separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : null;
    }

    private static bool IsInnoUninstallerName(string fileName)
    {
        if (fileName.Length != 12 || !fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return char.IsAsciiDigit(fileName[5]) && char.IsAsciiDigit(fileName[6]) && char.IsAsciiDigit(fileName[7]);
    }

    private static bool HasNoReparsePointInPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathsEqual(parent, current)) break;
            current = parent;
        }

        return true;
    }

    private static bool IsPortableExecutable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> dosHeader = stackalloc byte[64];
        try
        {
            stream.ReadExactly(dosHeader);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..0x40]);
        if (peOffset < dosHeader.Length || peOffset > stream.Length - 4) return false;
        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        try
        {
            stream.ReadExactly(signature);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        return signature[0] == (byte)'P' && signature[1] == (byte)'E'
            && signature[2] == 0 && signature[3] == 0;
    }

    private static bool IsRegistryFailure(Exception ex) => ex is SecurityException or UnauthorizedAccessException
        or IOException or SystemException;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static InstallationInfo Mismatch(string message) =>
        new(InstallationState.RegistrationMismatch, Error: message);
}
