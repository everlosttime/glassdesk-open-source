using System.Text.Json;

namespace GlassDesk.Core.Storage;

/// <summary>Validates the optional sync document without depending on Windows application services.</summary>
public static class DesktopSyncDocumentValidator
{
    public static bool IsValid(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return false;
            if (root.TryGetProperty("baselineEstablished", out var baseline) && baseline.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            foreach (var field in new[] { "suppressedKeys", "manuallyManagedAppIds", "desktopRemovedAppIds" })
                if (root.TryGetProperty(field, out var keys)
                    && (keys.ValueKind != JsonValueKind.Array || keys.EnumerateArray().Any(key => key.ValueKind != JsonValueKind.String))) return false;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("sourcePath", out var path)
                    || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()) || !paths.Add(path.GetString()!)) return false;
                foreach (var field in new[] { "fingerprint", "logicalKey", "outcome" })
                    if (source.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String) return false;
                if (source.TryGetProperty("outcome", out var outcome) && outcome.GetString() is not ("seen" or "imported" or "pending")) return false;
                if (source.TryGetProperty("appId", out var appId) && appId.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return false;
                if (source.TryGetProperty("failureCount", out var count) && (!count.TryGetInt32(out var failures) || failures < 0)) return false;
                foreach (var field in new[] { "lastAttemptUtc", "firstFailureUtc", "firstMissingUtc" })
                    if (source.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null
                        && (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out _))) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return false; }
    }
}
