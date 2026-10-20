using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.Platform.Storage;

/// <summary>一次验收的具名输出；只允许创建新文件。</summary>
public sealed record AcceptanceOutput(string Name, string Path);

/// <summary>保护根仅作边界比较；数据根必须全新，全部父目录必须已经存在。</summary>
public sealed record AcceptanceRequest(string ProtectedDataRoot, string NewDataRoot, IReadOnlyList<AcceptanceOutput> Outputs);

/// <summary>持有验收目录身份和独占输出，避免按路径检查后再次解析到别处。</summary>
/// <remarks>
/// 仅支持本地固定 NTFS 卷的规范绝对路径。所有静态拒绝发生在创建前。
/// 分配过程中的竞争或 I/O 失败可能留下本次空目录/文件；不会递归回收它们。
/// 逐段持有目录句柄，打开时拒绝重解析点；目录允许读写共享但禁止删除共享，
/// 因此保持对象身份而不阻止其它程序写目录。不会阻止之后修改目录元数据或
/// 重解析属性；本类型之外的路径式访问不能借此获得竞态隔离保证。
/// 新建使用相对已持有父句柄的 FILE_CREATE
/// （即 CreateNew），而非先检查再按完整路径打开。输出 ShareAccess=0（FileShare.None）。
/// 本类型不约束调用者之后对 DataRoot 内部子项的任意路径式访问，也不是抵御管理员、
/// 内核驱动或恶意同用户进程篡改整棵数据树的通用沙箱。不要据此实现真实数据删除。
/// 调用者不得提前关闭输出流；Dispose 后所有身份与共享保护结束。
/// </remarks>
public sealed partial class AcceptanceWorkspace : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x100000;
    private const uint OpenReparsePoint = 0x200000;
    private const uint SynchronousNonAlert = 0x20;
    private const int NameNotFound = unchecked((int)0xC0000034);
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Stream> _outputs = new(StringComparer.Ordinal);
    private bool _disposed;

    private AcceptanceWorkspace(string dataRoot) => DataRoot = dataRoot;

    public string DataRoot { get; }

    /// <summary>先验证全部输入和既有父目录，再原子创建新叶子；失败不会覆盖既有项。</summary>
    public static AcceptanceWorkspace Create(AcceptanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("验收目录只支持 Windows。");
        var protectedRoot = CanonicalPath(request.ProtectedDataRoot);
        var dataRoot = CanonicalPath(request.NewDataRoot);
        if (request.Outputs is null) throw new IOException("缺少验收输出清单。");
        var outputs = request.Outputs.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<string> { dataRoot };
        foreach (var output in outputs)
        {
            if (output is null || string.IsNullOrWhiteSpace(output.Name) || !names.Add(output.Name))
                throw new IOException("验收输出名称为空或重复。");
            targets.Add(CanonicalPath(output.Path));
        }
        for (var i = 0; i < targets.Count; i++)
        {
            if (Overlaps(targets[i], protectedRoot)) throw new IOException("验收路径与真实用户数据目录重叠。");
            for (var j = 0; j < i; j++)
                if (Overlaps(targets[i], targets[j])) throw new IOException("验收数据与输出路径互相重叠。");
        }

        var workspace = new AcceptanceWorkspace(dataRoot);
        try
        {
            // 保护根不存在时只持有其既有祖先；不创建、读取文档或自愈真实数据。
            workspace.PinDirectoryChain(protectedRoot, allowMissing: true);
            foreach (var target in targets)
            {
                var parent = workspace.PinDirectoryChain(Path.GetDirectoryName(target)!, allowMissing: false)!;
                using var existing = OpenRelative(parent, Path.GetFileName(target), create: false, directory: false, probe: true);
                if (existing is not null) throw new IOException("验收目标已经存在，拒绝复用或覆盖：" + target);
            }

            var dataParent = workspace._directories[Path.GetDirectoryName(dataRoot)!];
            var dataHandle = OpenRelative(dataParent, Path.GetFileName(dataRoot), create: true, directory: true)!;
            workspace._directories.Add(dataRoot, dataHandle);
            VerifyDirectory(dataHandle, dataRoot);
            for (var i = 0; i < outputs.Length; i++)
            {
                var path = targets[i + 1];
                var handle = OpenRelative(workspace._directories[Path.GetDirectoryName(path)!], Path.GetFileName(path), create: true, directory: false)!;
                try { workspace._outputs.Add(outputs[i].Name, new FileStream(handle, FileAccess.Write)); }
                catch { handle.Dispose(); throw; }
            }
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public Stream GetOutput(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _outputs[name];
    }

    /// <summary>在已持有的数据根句柄下新建并固定一个独立验收数据子根。</summary>
    public string CreateChildDataRoot(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.IsPathRooted(name) ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new IOException("验收子数据根必须是单个目录名。");

        var path = CanonicalPath(Path.Combine(DataRoot, name));
        if (!Path.GetDirectoryName(path)!.Equals(DataRoot, StringComparison.OrdinalIgnoreCase) || _directories.ContainsKey(path))
            throw new IOException("验收子数据根必须是本工作区内的全新直属目录。");

        var parent = _directories[DataRoot];
        var handle = OpenRelative(parent, name, create: true, directory: true)!;
        try
        {
            VerifyDirectory(handle, path);
            _directories.Add(path, handle);
            return path;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        foreach (var stream in _outputs.Values)
        {
            try { stream.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure ??= ex; }
        }
        foreach (var handle in _directories.Values.Reverse()) handle.Dispose();
        if (failure is not null) throw new IOException("验收输出关闭失败。", failure);
    }

    private SafeFileHandle? PinDirectoryChain(string path, bool allowMissing)
    {
        if (_directories.TryGetValue(path, out var cached)) return cached;
        var root = Path.GetPathRoot(path)!;
        if (!_directories.TryGetValue(root, out var parent))
        {
            parent = OpenRelative(null, @"\??\" + root, create: false, directory: true)!;
            _directories.Add(root, parent);
            VerifyDirectory(parent, root);
        }
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (_directories.TryGetValue(current, out var pinned)) { parent = pinned; continue; }
            var next = OpenRelative(parent, segment, create: false, directory: true, probe: allowMissing);
            if (next is null) return null;
            _directories.Add(current, next);
            VerifyDirectory(next, current);
            parent = next;
        }
        return parent;
    }

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/'))
            throw new IOException("验收路径必须是本地固定卷上的规范绝对路径。");
        var normalized = path.TrimEnd('\\');
        if (normalized.Length == 2 || !string.Equals(Path.GetFullPath(normalized), normalized, StringComparison.OrdinalIgnoreCase))
            throw new IOException("拒绝卷根或非规范验收路径。");
        foreach (var segment in normalized[3..].Split('\\'))
        {
            var stem = segment.Split('.')[0];
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
                throw new IOException("验收路径包含不安全的名称。");
        }
        var drive = new DriveInfo(normalized[..3]);
        if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("验收目录只支持本地固定 NTFS 卷。");
        return normalized;
    }

    private static bool Overlaps(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase);

    private static unsafe SafeFileHandle? OpenRelative(SafeFileHandle? parent, string name, bool create, bool directory, bool probe = false)
    {
        fixed (char* buffer = name)
        {
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = (IntPtr)buffer };
            var attributes = new ObjectAttributes { Length = (uint)sizeof(ObjectAttributes), RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero, ObjectName = (IntPtr)(&unicode), Attributes = 0x40 };
            // FILE_LIST_DIRECTORY 参与共享访问检查；仅 FILE_READ_ATTRIBUTES 的句柄
            // 不能可靠阻止目录改名（由真实 Directory.Move 回归用例验证）。
            var access = Synchronize | (create && !directory ? 0x40000000u : ReadAttributes | (directory && parent is not null ? 1u : 0u));
            // 目录允许读写共享，只拒绝删除共享；输出完全独占；探测既有叶子只读属性。
            // 卷根不通过名字再次访问；其持有句柄是后续相对访问的锚点。
            // 不锁住全卷根的写入/删除共享，以免与系统卷句柄冲突。
            var share = parent is null ? 7u : directory ? 3u : probe ? 7u : 0u;
            var options = OpenReparsePoint | SynchronousNonAlert | (directory ? 1u : create ? 0x40u : 0u);
            var status = Native.NtCreateFile(out var rawHandle, access, ref attributes, out _, IntPtr.Zero, 0, share, create ? 2u : 1u, options, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                if (probe && status == NameNotFound) return null;
                var error = Native.RtlNtStatusToDosError(status);
                throw new IOException("无法安全取得验收路径对象：" + name, new Win32Exception(unchecked((int)error)));
            }
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
    }

    private static unsafe void VerifyDirectory(SafeFileHandle handle, string expectedPath)
    {
        if (!Native.GetFileInformationByHandleEx(handle, 9, out var info, (uint)sizeof(AttributeTagInfo)))
            throw new IOException("无法验证验收目录属性。", new Win32Exception(Marshal.GetLastPInvokeError()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
            throw new IOException("拒绝重解析点或非目录的验收祖先。");
        var buffer = stackalloc char[1024];
        var length = Native.GetFinalPathNameByHandle(handle, buffer, 1024, 0);
        if (length == 0 || length >= 1024) throw new IOException("无法验证验收目录最终路径。");
        var actual = new string(buffer, 0, (int)length);
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !actual[4..].TrimEnd('\\').Equals(expectedPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("验收目录最终路径与请求不一致，拒绝别名或重定向。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public uint Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint Tag; }

    private static partial class Native
    {
        [LibraryImport("ntdll.dll")]
        internal static partial int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
        [LibraryImport("ntdll.dll")]
        internal static partial uint RtlNtStatusToDosError(int status);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTagInfo info, uint size);
        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        internal static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint length, uint flags);
    }
}
