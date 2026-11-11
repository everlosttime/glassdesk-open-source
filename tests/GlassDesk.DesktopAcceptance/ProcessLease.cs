using System.Diagnostics;
using System.IO;
using System.Text;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 对「本次测试自己启动的进程」的租约。
/// </summary>
/// <remarks>
/// 测试卫生的全部要点在这里：只结束<strong>自己启动的</strong>进程，而且结束前
/// 拿 <see cref="ProcessId"/>、启动时刻、映像完整路径三项核对。
/// 这三项对不上就说明 pid 已经被系统回收给了别的进程——那样杀掉的会是别人的程序。
/// 绝不按进程名批量清理：那正是「测试把用户正在用的程序关掉」的成因。
/// </remarks>
internal sealed class ProcessLease : IDisposable
{
    private readonly Process _process;
    private readonly DateTime _startedAt;
    private readonly List<string> _output = [];
    private readonly object _outputGate = new();
    private bool _disposed;

    private ProcessLease(Process process, DateTime startedAt, string imagePath)
    {
        _process = process;
        _startedAt = startedAt;
        ImagePath = imagePath;
    }

    internal int ProcessId => _process.Id;

    internal string ImagePath { get; }

    /// <summary>完整命令行，写进证据里，便于事后核对到底跑的是什么。</summary>
    internal string CommandLine { get; private init; } = string.Empty;

    internal bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    internal static ProcessLease Launch(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        bool captureOutput = false)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"要启动的产物不存在：{executable}", executable);
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!,
        };

        // The encoding properties are only legal together with redirection: setting
        // StandardOutputEncoding without RedirectStandardOutput makes Process.Start throw
        // InvalidOperationException, which is how every case failed the first time this driver
        // ran for real. They are therefore set together, and only when output is captured.
        if (captureOutput)
        {
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"启动 {executable} 没有返回进程对象。");

        ProcessLease lease;

        try
        {
            // 启动时刻与映像路径必须在进程还活着的时候取，
            // 结束后 MainModule 就未必读得到了。
            var startedAt = process.StartTime;
            var imagePath = process.MainModule?.FileName ?? executable;
            lease = new ProcessLease(process, startedAt, imagePath)
            {
                CommandLine = string.Join(' ', new[] { executable }.Concat(arguments)),
            };
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (Exception)
            {
                // 这里已经在异常路径上，结束不成也不能掩盖原始异常。
            }

            process.Dispose();
            throw;
        }

        if (captureOutput)
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                {
                    return;
                }

                lock (lease._outputGate)
                {
                    lease._output.Add(eventArgs.Data);
                }
            };

            process.BeginOutputReadLine();
        }

        return lease;
    }

    /// <summary>到目前为止进程输出的所有行（顺序保持）。</summary>
    internal IReadOnlyList<string> Output()
    {
        lock (_outputGate)
        {
            return [.. _output];
        }
    }

    /// <summary>在输出里等一行以给定前缀开头的文本。</summary>
    internal string? WaitForOutputLine(string prefix, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        do
        {
            lock (_outputGate)
            {
                var match = _output.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match[prefix.Length..].Trim();
                }
            }

            Thread.Sleep(200);
        }
        while (Environment.TickCount64 < deadline);

        return null;
    }

    internal bool WaitForExit(int timeoutMs)
    {
        try
        {
            return _process.WaitForExit(timeoutMs);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>pid、启动时刻、映像路径三项都对得上，才认为这个进程还是我们启动的那一个。</summary>
    internal bool StillOurs()
    {
        try
        {
            if (_process.HasExited)
            {
                return false;
            }

            return _process.StartTime == _startedAt
                && string.Equals(_process.MainModule?.FileName, ImagePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>结束这个进程。核对不过就什么都不做，并把原因交回调用方。</summary>
    internal string Stop(string reason)
    {
        if (HasExited)
        {
            return $"{reason}：进程 {ProcessId} 已经退出。";
        }

        if (!StillOurs())
        {
            return $"{reason}：进程 {ProcessId} 的三项核对不通过（启动时刻或映像路径变了），"
                + "无法确认它还是本次启动的那一个，因此<不结束>它。";
        }

        try
        {
            _process.Kill(entireProcessTree: false);
            _ = _process.WaitForExit(10000);
            return $"{reason}：已结束自己启动的进程 {ProcessId}（{ImagePath}）。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"{reason}：结束进程 {ProcessId} 失败：{ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!HasExited)
        {
            _ = Stop("收尾");
        }

        _process.Dispose();
    }
}