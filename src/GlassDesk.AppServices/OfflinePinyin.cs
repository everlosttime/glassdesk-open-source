using System.Globalization;
using System.Reflection;
using System.Text;

namespace GlassDesk.AppServices;

/// <summary>从固定版本的内置 Unicode Unihan 数据读取普通话读音。</summary>
public static class OfflinePinyin
{
    private static readonly Lazy<Dictionary<int, string>> Readings = new(LoadReadings);

    public static (string Full, string Initials) Index(string? text)
    {
        var full = new StringBuilder();
        var initials = new StringBuilder();
        foreach (var rune in (text ?? string.Empty).Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Readings.Value.TryGetValue(rune.Value, out var reading))
            {
                full.Append(reading);
                initials.Append(reading[0]);
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                var value = rune.ToString().ToLowerInvariant();
                full.Append(value);
                initials.Append(value);
            }
        }
        return (full.ToString(), initials.ToString());
    }

    private static Dictionary<int, string> LoadReadings()
    {
        var result = new Dictionary<int, string>();
        using var stream = typeof(OfflinePinyin).Assembly.GetManifestResourceStream(
            "GlassDesk.AppServices.Resources.unihan-mandarin-17.txt")
            ?? throw new InvalidOperationException("Offline pinyin data is missing from the application.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0].StartsWith("U+", StringComparison.Ordinal)
                && int.TryParse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codepoint)
                && parts[1].Length > 0)
                result[codepoint] = parts[1];
        }
        return result;
    }
}
