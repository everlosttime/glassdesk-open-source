using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

/// <summary>协调启动台布局与共享应用库及布局文件的编辑。</summary>
public sealed class LauncherWorkspace
{
    private readonly AppServiceHost _host;
    private readonly bool _libraryAvailable;

    public LauncherWorkspace(AppServiceHost host, bool libraryAvailable = true)
    {
        _host = host;
        _libraryAvailable = libraryAvailable;
    }

    public LauncherLayout Layout => _host.Layouts.Launcher;

    public bool Reconcile(int capacity)
    {
        if (!_libraryAvailable) return false;
        var draft = LauncherLayoutRules.Reconcile(Layout,
            _host.Library.Items.Where(item => item is { IsUsable: true }).Select(item => item.Id), capacity);
        if (LauncherLayoutRules.Equivalent(Layout, draft)) return false;
        return _host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion);
    }

    public LauncherEditSession BeginEdit()
        => new(_host.Layouts.LauncherVersion, _host.Library.Version, LauncherLayoutRules.DeepClone(Layout));

    public bool Commit(LauncherEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_host.Library.Version != session.LibraryVersion) return false;
        return _host.Layouts.CommitLauncher(session.Draft, session.LayoutVersion);
    }
}

/// <summary>与共享布局分离的草稿；放弃草稿不会修改内存或磁盘。</summary>
public sealed class LauncherEditSession(long layoutVersion, long libraryVersion, LauncherLayout draft)
{
    public long LayoutVersion { get; } = layoutVersion;
    public long LibraryVersion { get; } = libraryVersion;
    public LauncherLayout Draft { get; set; } = draft;
}
