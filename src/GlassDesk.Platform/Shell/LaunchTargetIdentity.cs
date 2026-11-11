using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace GlassDesk.Platform.Shell;

/// <summary>Reads explicit AppsFolder targets without loading or executing a launcher.</summary>
public static class LaunchTargetIdentity
{
    public static string? FromShellTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var value = target.Trim();
        foreach (var prefix in new[] { @"shell:AppsFolder\", @"::{4234d49b-0245-4df3-b780-3893943456e1}\" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        var parts = value.Split('!');
        return parts.Length == 2 && parts[0].Contains('_') && parts.All(part => part.Length > 0)
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '!') ? value : null;
    }

    public static string? ReadManagedShellLauncher(string path)
    {
        try
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var reader = pe.GetMetadataReader();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var explorer = false;
            var handle = MetadataTokens.UserStringHandle(0);
            while (!(handle = reader.GetNextHandle(handle)).IsNil)
            {
                var text = reader.GetUserString(handle);
                explorer |= text.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
                if (text.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase)
                    && FromShellTarget(text) is { } identity) identities.Add(identity);
            }
            return explorer && identities.Count == 1 ? identities.Single() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
