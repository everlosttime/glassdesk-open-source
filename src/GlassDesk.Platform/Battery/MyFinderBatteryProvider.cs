using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.Platform.Battery;

internal sealed class MyFinderBatteryProvider : IPeripheralBatteryProvider
{
    internal const string UpstreamCommit = "bbcb779be4633ecf9183f53409de118dc9cb1fd6";
    private const int MaximumOutput = 1024 * 1024;
    private static readonly string[] EntryFields = ["id", "physicalId", "kind", "name", "percent", "charging", "online", "fresh", "approximate", "approxText", "unconfirmed", "measuredAtUtc", "provider", "support", "reason", "receiverSlot"];
    private readonly object _lifetimeGate = new();
    private CancellationTokenSource? _lifetime;
    private PersistentProcessClient? _client;
    internal IReadOnlyCollection<PeripheralBatteryCategory>? Categories { get; set; }

    internal void SetActive(bool active)
    {
        lock (_lifetimeGate)
        {
            if (active == (_lifetime is not null)) return;
            var previous = _lifetime;
            _lifetime = active ? new() : null;
            previous?.Cancel();
            _client?.Dispose(); _client = null;
            previous?.Dispose();
        }
    }

    public async Task<IReadOnlyList<PeripheralBatterySnapshot>> QueryAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "components", "battery-engine");
        var executable = Path.Combine(directory, "runtime", "python.exe");
        var bridge = Path.Combine(directory, "glassdesk_bridge.py");
        if (!File.Exists(executable) || !File.Exists(bridge)) return [];
        CancellationToken lifetime;
        bool persistent;
        lock (_lifetimeGate) { persistent = _lifetime is not null; lifetime = _lifetime?.Token ?? CancellationToken.None; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var inventory = await Task.Run(NativeHidTransport.Enumerate, timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        var categories = (Categories ?? Enum.GetValues<PeripheralBatteryCategory>()).Distinct().ToArray();
        if (categories.Length == 0) return [];
        var request = JsonSerializer.Serialize(new
        {
            schema = 1, categories = categories.Select(category => category.ToString().ToLowerInvariant()),
            inventory = inventory.Select(device => new { path = device.Path, physicalId = device.PhysicalId, vendorId = device.VendorId, productId = device.ProductId })
        });
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-I", "-B", bridge, persistent ? "--serve" : "--once" }) start.ArgumentList.Add(argument);
        string? json;
        if (persistent)
        {
            PersistentProcessClient client;
            lock (_lifetimeGate)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_client?.IsStopped == true) { _client.Dispose(); _client = null; }
                client = _client ??= new(start);
            }
            json = await client.QueryAsync(request, timeout.Token).ConfigureAwait(false);
        }
        else json = await RunProcessAsync(start, request, timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        return json is null ? [] : Parse(json, inventory, DateTimeOffset.UtcNow)
            .Where(snapshot => categories.Contains(snapshot.Category)).ToArray();
    }

    /// <summary>One request and one bounded JSON line at a time, retaining upstream reader state.</summary>
    internal sealed class PersistentProcessClient : IDisposable
    {
        private readonly SemaphoreSlim _queryGate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _stopped;
        private readonly Process _process;
        private readonly ProcessJob _job;
        private readonly StreamReader _output;
        private readonly StreamReader _error;
        private readonly StreamWriter _input;
        private int _disposed;
        internal bool IsStopped => _stopped.IsCancellationRequested;

        internal PersistentProcessClient(ProcessStartInfo start)
        {
            _stopped = _stop.Token;
            _process = new() { StartInfo = start };
            _job = ProcessJob.Create();
            try
            {
                if (!_process.Start()) throw new IOException("Battery helper could not start.");
                // The helper waits for stdin. Assign its kill-on-close job before sending any request.
                _job.Assign(_process);
                _output = _process.StandardOutput; _input = _process.StandardInput; _error = _process.StandardError;
                _ = DrainErrorAsync();
            }
            catch { Dispose(); throw; }
        }

        internal async Task<string> QueryAsync(string request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await _queryGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                // A canceled or timed-out exchange must never donate its late line to the next query.
                using var canceled = timeout.Token.Register(Dispose);
                timeout.Token.ThrowIfCancellationRequested();
                await _input.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                await _input.FlushAsync(timeout.Token).ConfigureAwait(false);
                var json = await ReadResponseAsync(_output, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                return json;
            }
            catch
            {
                var canceled = timeout.IsCancellationRequested;
                Dispose();
                if (canceled) timeout.Token.ThrowIfCancellationRequested();
                throw;
            }
            finally { _queryGate.Release(); }
        }

        private async Task DrainErrorAsync()
        {
            try { await ReadBoundedAsync(_error, 64 * 1024, _stop).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException) { Dispose(); }
        }

        private static async Task<string> ReadResponseAsync(StreamReader reader, CancellationToken token)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) throw new InvalidDataException("Battery helper ended without a complete response.");
                var newline = Array.IndexOf(buffer, '\n', 0, count);
                if (text.Length + (newline < 0 ? count : newline) > MaximumOutput)
                    throw new InvalidDataException("Battery component response exceeded its limit.");
                text.Append(buffer, 0, newline < 0 ? count : newline);
                if (newline < 0) continue;
                if (newline != count - 1) throw new InvalidDataException("Battery helper emitted an unsolicited response.");
                var json = text.ToString().TrimEnd('\r');
                if (Encoding.UTF8.GetByteCount(json) > MaximumOutput) throw new InvalidDataException("Battery component response exceeded its limit.");
                return json;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _job.Dispose();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            // Process.Dispose does not own callers' redirected stream references. Closing the pipe
            // first avoids a synchronous writer flush during cancellation of an in-flight request.
            CloseStream(_input?.BaseStream); CloseStream(_input); CloseStream(_output); CloseStream(_error);
            _process.Dispose();
        }

        private static void CloseStream(IDisposable? stream)
        {
            try { stream?.Dispose(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
    }

    internal static async Task<string?> RunProcessAsync(ProcessStartInfo start, string request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        using var job = ProcessJob.Create();
        if (!process.Start()) return null;
        Task<string>? stdout = null, stderr = null;
        try
        {
            // The bridge waits for stdin before loading readers. Protect the entire child tree first.
            job.Assign(process);
            stdout = ReadBoundedAsync(process.StandardOutput, MaximumOutput, timeout);
            stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, timeout);
            await process.StandardInput.WriteAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? json : null;
        }
        finally
        {
            // Disposing the kill-on-close job also stops children if the parent has already exited.
            job.Dispose();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            foreach (var read in new[] { stdout, stderr })
                if (read is not null) try { await read.ConfigureAwait(false); }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationTokenSource timeout)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (text.Length + count > limit) throw new InvalidDataException("Battery component output exceeded its limit.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch { await timeout.CancelAsync().ConfigureAwait(false); throw; }
    }

    internal static IReadOnlyList<PeripheralBatterySnapshot> Parse(string json, IReadOnlyList<HidBatteryDevice> inventory, DateTimeOffset now)
    {
        if (json.Length > MaximumOutput) return [];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!ExactFields(root, ["schema", "upstreamCommit", "devices"]) || !root.GetProperty("schema").TryGetInt32(out var schema) || schema != 1
                || !Text(root, "upstreamCommit", out var commit) || commit != UpstreamCommit
                || root.GetProperty("devices").ValueKind != JsonValueKind.Array || root.GetProperty("devices").GetArrayLength() > 256) return [];
            var physicalIds = inventory.Select(device => device.PhysicalId).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
            var parsed = new List<(string Id, PeripheralBatterySnapshot Snapshot)>();
            foreach (var entry in root.GetProperty("devices").EnumerateArray())
            {
                if (!ExactFields(entry, EntryFields) || !Text(entry, "id", out var id) || string.IsNullOrWhiteSpace(id)
                    || !Text(entry, "physicalId", out var physical) || !physicalIds.TryGetValue(physical, out var physicalId)
                    || !Text(entry, "kind", out var kind) || !TryCategory(kind, out var category)
                    || !Text(entry, "name", out var name) || !Text(entry, "provider", out var provider)
                    || !Text(entry, "support", out var support) || !Text(entry, "reason", out var reason)
                    || !NullableText(entry, "approxText", out var approxText) || !NullableText(entry, "measuredAtUtc", out var measured)
                    || !Boolean(entry, "online", out var online) || !Boolean(entry, "fresh", out var fresh)
                    || !Boolean(entry, "approximate", out var approximate) || !Boolean(entry, "unconfirmed", out var unconfirmed)
                    || !NullableBoolean(entry.GetProperty("charging"), out var charging) || !Percent(entry.GetProperty("percent"), out var percent)
                    || !ReceiverSlot(entry.GetProperty("receiverSlot"), provider, out var slot)) continue;
                var timestamp = DateTimeOffset.MinValue;
                if (measured is not null && (!DateTimeOffset.TryParse(measured, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
                    || timestamp.Offset != TimeSpan.Zero)) continue;
                var current = online && fresh && measured is not null && timestamp <= now && now - timestamp <= TimeSpan.FromMinutes(2);
                string? level = null;
                if (current && unconfirmed) { percent = null; level = "待确认"; reason = Append(reason, "上游电量字段尚未确认"); }
                else if (current && approximate)
                {
                    level = percent.HasValue ? "~" + percent.Value.ToString(CultureInfo.InvariantCulture) + "%" : CompactApproximation(approxText);
                    percent = null; reason = Append(reason, "上游近似电量" + (approxText is null ? "" : "：" + approxText));
                }
                if (!current)
                {
                    percent = null; charging = null;
                    reason = Append(reason, !online ? "设备未连接或休眠" : "电量状态缺少当前有效采样时间，等待刷新");
                }
                var key = $"hid:{physicalId}:{kind}" + (slot.HasValue ? ":slot" + slot.Value.ToString(CultureInfo.InvariantCulture)
                    : ":unit:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..24].ToLowerInvariant());
                parsed.Add((id, new(key, name, category, online, percent, charging,
                    timestamp, "MyFinder 1.3.9 / " + provider, support, reason, ReceiverSlot: slot, PhysicalDeviceId: physicalId, Level: level)));
            }
            return parsed.GroupBy(item => item.Snapshot.DeviceKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1).Select(group => group.Single().Snapshot).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return []; }
    }

    private static string Append(string reason, string evidence) => reason.Length == 0 ? evidence : reason + "；" + evidence;
    private static string? CompactApproximation(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Length <= 12 ? "~" + text.TrimStart('~', '≈') : "约值";
    private static bool ExactFields(JsonElement element, string[] names) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Count() == names.Length && element.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == names.Length
        && names.All(name => element.TryGetProperty(name, out _));
    private static bool Text(JsonElement entry, string key, out string text)
    {
        var value = entry.GetProperty(key); text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return value.ValueKind == JsonValueKind.String && text.Length <= 4096;
    }
    private static bool NullableText(JsonElement entry, string key, out string? text)
    {
        text = null; var value = entry.GetProperty(key);
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (!Text(entry, key, out var result)) return false;
        text = result; return true;
    }
    private static bool Boolean(JsonElement entry, string key, out bool value)
    {
        var element = entry.GetProperty(key); value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
    private static bool NullableBoolean(JsonElement element, out bool? value)
    {
        value = element.ValueKind == JsonValueKind.Null ? null : element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
    }
    private static bool Percent(JsonElement element, out int? percent)
    {
        percent = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value is < 0 or > 100) return false;
        percent = value; return true;
    }
    private static bool ReceiverSlot(JsonElement element, string provider, out int? slot)
    {
        slot = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (!string.Equals(provider, "logitech", StringComparison.Ordinal) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value) || value is < 1 or > 6) return false;
        slot = value; return true;
    }
    private static bool TryCategory(string kind, out PeripheralBatteryCategory category)
    {
        category = kind switch { "mouse" => PeripheralBatteryCategory.Mouse, "headset" => PeripheralBatteryCategory.Headset, "keyboard" => PeripheralBatteryCategory.Keyboard, _ => (PeripheralBatteryCategory)(-1) };
        return (int)category >= 0;
    }

    private sealed class ProcessJob : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private ProcessJob(SafeFileHandle handle) => _handle = handle;
        internal static ProcessJob Create()
        {
            var handle = CreateJobObject(0, null);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var information = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
            }
            return new(handle);
        }
        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => _handle.Dispose();
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
        {
            internal long PerProcessTime, PerJobTime;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
        {
            internal BasicLimitInformation Basic;
            internal IoCounters Io;
            internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(nint securityAttributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    }
}
