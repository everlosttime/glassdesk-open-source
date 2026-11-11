namespace GlassDesk.Nearby;

internal sealed class DiscoveryThrottle
{
    private readonly Dictionary<string, DateTimeOffset> _attempts = new(StringComparer.Ordinal);
    internal bool TryBegin(string endpoint, string fingerprint, DateTimeOffset now)
    {
        var key = endpoint + "/" + fingerprint;
        lock (_attempts)
        {
            if (_attempts.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30)) return false;
            if (_attempts.Count >= 1024)
            {
                foreach (var expired in _attempts.Where(p => now - p.Value > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _attempts.Remove(expired);
                if (_attempts.Count >= 1024 && !_attempts.ContainsKey(key)) return false;
            }
            _attempts[key] = now; return true;
        }
    }
    internal void Clear() { lock (_attempts) _attempts.Clear(); }
}
