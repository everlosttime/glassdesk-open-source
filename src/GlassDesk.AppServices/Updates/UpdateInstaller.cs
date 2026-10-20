using System.Diagnostics;
using System.Security.Cryptography;
using GlassDesk.Platform.Installation;

namespace GlassDesk.AppServices.Updates;

/// <summary>Prepare a verified standalone Inno installer; the host controls process launch and shutdown.</summary>
public static class UpdateInstaller
{
    public static async Task<ProcessStartInfo> PrepareAsync(UpdateRelease release, string file,
        InstallationInfo installation, CancellationToken token)
    {
        if (installation.State != InstallationState.VerifiedInstalled || string.IsNullOrWhiteSpace(installation.InstallDirectory))
            throw new InvalidOperationException("当前程序不是已核验的安装版，请先手动安装正式安装包。");
        UpdateClient.ValidateInstaller(release);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.InstallDirectory));
        var installer = Path.GetFullPath(file);
        if (installer.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新安装包必须位于安装目录之外。");
        if (!Directory.Exists(directory) || Path.GetFileName(installer) != release.FileName)
            throw new InvalidDataException("安装目录或更新安装包路径无效。");
        await using (var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            if (stream.Length != release.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
                    .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载后的安装包发生变化，请重新下载。");
        }
        var start = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! };
        start.ArgumentList.Add("/SP-");
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/GLASSDESKUPDATE=1");
        start.ArgumentList.Add("/DIR=" + directory);
        start.ArgumentList.Add("/LOG=" + Path.Combine(Path.GetDirectoryName(installer)!, "install.log"));
        return start;
    }
}
