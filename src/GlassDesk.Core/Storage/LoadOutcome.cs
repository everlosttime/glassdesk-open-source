namespace GlassDesk.Core.Storage;

/// <summary>一次加载尝试的结局。</summary>
public enum LoadStatus
{
    /// <summary>文档成功从磁盘读出。</summary>
    Loaded = 0,

    /// <summary>磁盘上还没有内容，已生成默认值。</summary>
    FirstRun = 1,

    /// <summary>
    /// 主文档不可用，但并列的备份可读。用户至多
    /// 丢失最近一次改动。
    /// </summary>
    RecoveredFromBackup = 2,

    /// <summary>
    /// 文档与其备份都无法读取。已生成默认值，并把
    /// 损坏的文件保留下来供排查。
    /// </summary>
    Unrecoverable = 3,
}

/// <summary>
/// 持久化文档的加载结果，包含调用方该向用户说明的内容。
/// </summary>
public sealed class LoadOutcome<T>
    where T : class
{
    internal LoadOutcome(T value, LoadStatus status, string? preservedFile, Exception? error)
    {
        Value = value;
        Status = status;
        PreservedFile = preservedFile;
        Error = error;
    }

    /// <summary>始终可用：要么是磁盘上的内容，要么是新建的默认值。</summary>
    public T Value { get; }

    public LoadStatus Status { get; }

    /// <summary>
    /// 发现损坏文件时，它被移到何处。非 null 表示应当告知用户，
    /// 也意味着没有任何东西被悄悄丢弃。
    /// </summary>
    public string? PreservedFile { get; }

    /// <summary>导致 <see cref="LoadStatus.Unrecoverable"/> 的异常，可能为空。</summary>
    public Exception? Error { get; }

    /// <summary>为 true 时调用方应给出解释，而不是安静地继续。</summary>
    public bool NeedsUserAttention => Status is LoadStatus.RecoveredFromBackup or LoadStatus.Unrecoverable;
}