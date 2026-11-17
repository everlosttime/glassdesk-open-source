namespace GlassDesk.AppServices;

/// <summary>Known game-launch actions, deliberately separate from the website URL contract.</summary>
public static class GameShortcutProtocol
{
    public static string? Read(string path)
    {
        foreach (var line in InternetShortcutReader.ReadShortcutLines(path) ?? [])
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return NormalizeLaunch(trimmed[4..]);
        }
        return null;
    }

    public static string? NormalizeLaunch(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        if (uri.Scheme == "steam" && uri.Host is "run" or "rungameid" && uri.Query.Length == 0)
        {
            var appId = uri.AbsolutePath.Trim('/');
            return appId.Length > 0 && appId.All(char.IsAsciiDigit) && ulong.TryParse(appId, out var id) && id > 0
                ? uri.AbsoluteUri : null;
        }
        if (uri.Scheme == "com.epicgames.launcher" && uri.Host == "apps" && uri.AbsolutePath.Trim('/').Length > 0)
        {
            var actions = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0].Equals("action", StringComparison.OrdinalIgnoreCase))
                .Select(pair => Uri.UnescapeDataString(pair[1])).ToArray();
            if (actions.Length == 1 && actions[0].Equals("launch", StringComparison.OrdinalIgnoreCase)) return uri.AbsoluteUri;
        }
        return null;
    }
}
