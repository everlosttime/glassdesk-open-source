namespace GlassDesk.Core.Storage;

/// <summary>
/// 持久化数据写不下去时抛出。携带一条面向用户展示的消息，
/// 并且刻意不继承任何会把宿主进程拖垮的类型——保存失败
/// 必须降级为一条提示，绝不能变成退出。
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(string message, string path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>写入失败的那个文件。</summary>
    public string Path { get; }
}