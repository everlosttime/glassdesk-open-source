using System.IO.Pipes;
using System.Text;

namespace GlassDesk.Platform;

/// <summary>第二次启动可以交给运行中实例的命令。</summary>
public static class InstanceCommands
{
    /// <summary>显示启动台。</summary>
    public const string ShowLauncher = "show-launcher";

    /// <summary>显示设置中心。</summary>
    public const string ShowSettings = "show-settings";

    /// <summary>启动台已打开时将其隐藏。</summary>
    public const string HideLauncher = "hide-launcher";

    /// <summary>关闭运行中的实例。</summary>
    public const string Exit = "exit";
}

/// <summary>获取阶段七之前旧版单实例协议兼容租约的结果。</summary>
public enum LegacyInstanceStatus
{
    /// <summary>旧版实例槽位不存在。</summary>
    NotRunning,

    /// <summary>旧版实例槽位仍存在。</summary>
    Running,

    /// <summary>无法确认旧版实例槽位状态；调用方应按仍运行处理。</summary>
    Unknown,
}

/// <summary>
/// 保证每个用户会话只有一个实例，并让后续启动有办法与它通信。
/// </summary>
/// <remarks>
/// 身份由产品名推导而来，并刻意与参考产品的 mutex 区分开，
/// 这样 GlassDesk 与已安装的 MacDock 或 Ym Launcher 可以并存，
/// 彼此都不被挡住。
///
/// 只用 mutex 还不够：再次启动程序应当把运行中的实例带到前台，
/// 而不是毫无动静，所以首个实例还会监听一个命名管道，
/// 由后续启动向它发送命令。
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string LegacyMutexPrefix = @"Local\GlassDesk.SingleInstance.";
    private readonly string _instanceName;
    private readonly InstanceIdentity _identity;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();

    private Mutex? _legacyMutex;
    private Task? _listener;
    private bool _disposed;

    private SingleInstanceGuard(string instanceName, InstanceIdentity identity, Mutex mutex, bool isFirstInstance)
    {
        _instanceName = instanceName;
        _identity = identity;
        _mutex = mutex;
        IsFirstInstance = isFirstInstance;
    }

    /// <summary>本进程是该拥有 UI 的实例时为 true。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>另一实例发来命令时在后台线程上触发。</summary>
    public event EventHandler<string>? CommandReceived;

    /// <summary>
    /// 为 <paramref name="instanceName"/> 抢占实例槽位。
    /// </summary>
    public static SingleInstanceGuard Acquire(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        // Local\ 限定对象命名空间，SID 与会话号再让锁和管道使用完全相同的产品实例身份。
        var identity = InstanceIdentity.Current;
        var mutex = new Mutex(initiallyOwned: false, identity.GetMutexName(instanceName), out var createdNew);
        return new SingleInstanceGuard(instanceName, identity, mutex, createdNew);
    }

    /// <summary>获取旧版实例名称，并将兼容租约保持到本实例退出。</summary>
    /// <remarks>
    /// 首个新版实例必须在整个生命周期内保留旧协议名称。旧版进程通过
    /// <c>createdNew</c> 判断实例身份；短暂打开再释放只检查“旧版先启动”的情况，
    /// 无法阻止旧版在新版启动后再次创建同名互斥体。
    /// </remarks>
    public bool TryAcquireLegacyProtocolLease(out LegacyInstanceStatus status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsFirstInstance)
        {
            throw new InvalidOperationException("只有首个新版实例可以取得旧协议兼容租约。");
        }

        if (_legacyMutex is not null)
        {
            status = LegacyInstanceStatus.NotRunning;
            return true;
        }

        try
        {
            var mutex = new Mutex(
                initiallyOwned: false,
                LegacyMutexPrefix + _instanceName,
                out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                status = LegacyInstanceStatus.Running;
                return false;
            }

            _legacyMutex = mutex;
            status = LegacyInstanceStatus.NotRunning;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // 无法确认旧版名称是否已被占用时，宿主必须按仍运行处理。
            status = LegacyInstanceStatus.Unknown;
            return false;
        }
    }

    /// <summary>
    /// 开始接受后续启动发来的命令。
    /// 不是首个实例时不做事。
    /// </summary>
    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsFirstInstance || _listener is not null)
        {
            return;
        }

        _listener = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    /// <summary>
    /// 向运行中的实例发送命令。
    /// </summary>
    /// <returns>运行中的实例接受了命令时为 true。</returns>
    /// <remarks>
    /// 会短暂重试，因为第二次启动到达时首个实例可能还在搭建监听器——
    /// 快速连续启动两次是用户的正常行为。
    /// </remarks>
    public static async Task<bool> TrySendCommandAsync(string instanceName, string command, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var pipeName = InstanceIdentity.Current.GetPipeName(instanceName);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                await client.ConnectAsync(250).ConfigureAwait(false);

                var payload = Encoding.UTF8.GetBytes(command + "\n");
                await client.WriteAsync(payload).ConfigureAwait(false);
                await client.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // 监听器还没就绪，或者已经消失。等到截止时间前再试。
                await Task.Delay(60).ConfigureAwait(false);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();

        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // 关闭时的竞态是预期内的，没有需要上报的。
        }

        _cancellation.Dispose();
        _legacyMutex?.Dispose();
        _legacyMutex = null;
        _mutex.Dispose();
        _disposed = true;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        // 每条到来的命令占用一个管道实例，所以循环每轮都重建服务端，
        // 而不是尝试复用一个已被消费的流。
        var server = new NamedPipeServerStream(
            _identity.GetPipeName(_instanceName),
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(command))
                {
                    CommandReceived?.Invoke(this, command.Trim());
                }

                // 断开连接，以便该实例服务下一次启动。
                server.Disconnect();

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch (IOException)
        {
            // 管道已被拆除；宿主正在退出，也没有对象需要告知。
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
