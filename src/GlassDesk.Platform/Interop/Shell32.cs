using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// Shell 通知区域（托盘）、用户通知状态与任务栏状态的入口点，以及它们交换的结构体。
/// </summary>
/// <remarks>
/// 结构体使用固定大小的字符缓冲区而不是
/// <c>ByValTStr</c> 封送，从而保持 blittable。这样源生成的 P/Invoke
/// 才可用，也免去运行时封送器为每次调用分配内存的开销。
/// </remarks>
internal static partial class Shell32
{
    /// <summary>外壳向我们的窗口投递托盘事件时使用的消息。</summary>
    internal const int WmTrayCallback = 0x0400 + 1024; // WM_APP + 1024

    // 通知区域标志位（NOTIFY_ICON_* / NIF_*）。
    internal const int NifMessage = 0x00000001;
    internal const int NifIcon = 0x00000002;
    internal const int NifTip = 0x00000004;

    // Shell_NotifyIcon 的消息。
    internal const int NimAdd = 0x00000000;
    internal const int NimModify = 0x00000001;
    internal const int NimDelete = 0x00000002;
    internal const int NimSetVersion = 0x00000004;

    // 版本 3 语义下通过 lParam 传递的鼠标通知。
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmLButtonDblClk = 0x0203;

    /// <summary>能把点击信息放进 lParam 的通知区域图标版本。</summary>
    internal const int NotifyIconVersion3 = 3;

    /// <summary>外壳接受的图标提示文本最大字符数。</summary>
    internal const int TipLength = 128;

    /// <summary>任务栏状态查询（<c>SHAppBarMessage</c> 的消息之一）。</summary>
    internal const uint AbmGetState = 0x00000004;

    /// <summary>任务栏处于自动隐藏。返回值里的这一位决定了「铺满整屏」与「最大化」是否可区分。</summary>
    internal const uint AbsAutoHide = 0x00000001;

    /// <summary>任务栏总在最前。</summary>
    internal const uint AbsAlwaysOnTop = 0x00000002;

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    /// <summary>
    /// 询问外壳「此刻是否应当避免打扰用户」，也就是 Windows 自己判断全屏应用的那条路径。
    /// </summary>
    /// <remarks>
    /// 返回 HRESULT，取值经 <paramref name="state"/> 带回；负值表示调用失败。
    /// 已知它会返回 <c>E_FAIL</c>（例如外壳尚未初始化完成），因此调用方必须
    /// 把失败当作「这条路径没有结论」，而不是当作异常。
    /// </remarks>
    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out int state);

    /// <summary>查询任务栏状态。<see cref="AppBarData.Size"/> 必须先填好。</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHAppBarMessage")]
    internal static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    // ---- 已知文件夹 ----

    /// <summary>取已知文件夹的路径。返回的内存必须用 <see cref="Ole32.CoTaskMemFree"/> 释放。</summary>
    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    /// <summary>当前用户的「开始」菜单程序目录（<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>）。</summary>
    internal static readonly Guid FolderIdPrograms = new("a77f5d77-2e2b-44c3-a6a2-aba601054a51");

    /// <summary>所有用户的「开始」菜单程序目录（<c>%ProgramData%\...\Start Menu\Programs</c>）。</summary>
    internal static readonly Guid FolderIdCommonPrograms = new("0139d44e-6afe-49f2-8690-3dafcae6ffb8");

    // ---- 图标 ----

    /// <summary>取图标句柄。返回的句柄必须用 <c>DestroyIcon</c> 释放。</summary>
    internal const uint ShgfiIcon = 0x000000100;

    /// <summary>取系统「大图标」尺寸，实测 32×32（随 DPI 缩放）。</summary>
    internal const uint ShgfiLargeIcon = 0x000000000;

    /// <summary>路径不存在时也按扩展名推断图标种类，而不是直接失败。</summary>
    internal const uint ShgfiUseFileAttributes = 0x000000010;

    /// <summary>请求一个 32 位带 alpha 的图标，而不是 16 色或掩码版。</summary>
    internal const uint ShgfiShellIconSize = 0x000000000;

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint infoSize, uint flags);

    /// <summary>
    /// 按索引从可执行文件或 DLL 里取图标，对应快捷方式图标位置的
    /// <c>C:\Windows\system32\shell32.dll,3</c> 这一形式。
    /// </summary>
    /// <remarks>
    /// 两个返回的句柄都必须用 <c>DestroyIcon</c> 释放。返回 0 表示没有这个索引。
    /// </remarks>
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHDefExtractIcon(string file, int index, uint flags, out nint large, nint small, uint iconSize);

    // ---- 取外壳项与图片 ----

    /// <summary>按路径取得外壳项并直接索取指定接口。</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, out nint instance);

    /// <summary>
    /// 要求以 URL 形式给出外壳项的名称（<c>SIGDN_URL</c>）。
    /// </summary>
    /// <remarks>
    /// 这是把「目标是网址」的快捷方式读出来的唯一办法。实测
    /// <c>IShellLink::GetPath</c> 在四种标志下<strong>全部返回空串</strong>——
    /// 网址存在 ID 列表里，不在路径字段里。见 <c>ShortcutResolver</c>。
    /// </remarks>
    internal const uint SigdnUrl = 0x80068000;

    /// <summary>
    /// 把 PIDL 转成一个名称字符串。
    /// </summary>
    /// <remarks>
    /// 返回的字符串是任务分配器给的，要用 <c>CoTaskMemFree</c> 释放。
    /// 传进来的 PIDL 归调用方所有，本函数不释放它。
    /// </remarks>
    [LibraryImport("shell32.dll", EntryPoint = "SHGetNameFromIDList")]
    internal static partial int SHGetNameFromIDList(nint itemIdList, uint sigdn, out nint name);

    /// <summary>
    /// 让外壳解析一个目标名（含 <c>shell:</c> 形式），成功时给回 PIDL。
    /// </summary>
    /// <remarks>
    /// 只解析、不打开：用来验证「外壳认不认这个目标」，而不会在用户桌面上开窗口，
    /// 也不会像 <c>ShellExecute</c> 那样在目标无效时弹错误框。
    /// 返回的 PIDL 必须用 <see cref="ILFree"/> 释放。
    /// </remarks>
    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(string name, nint bindContext, out nint itemIdList, uint attributesIn, out uint attributesOut);

    /// <summary>释放外壳分配的 PIDL。</summary>
    /// <remarks>
    /// 必须用它而不是 <c>CoTaskMemFree</c>：PIDL 是外壳自己组织的结构，
    /// 用错释放函数会破坏堆。
    /// </remarks>
    [LibraryImport("shell32.dll")]
    internal static partial void ILFree(nint itemIdList);

    // ---- 执行 ----

    /// <summary><c>SHELLEXECUTEINFO</c> 的 <c>fMask</c> 位。</summary>
    internal const uint SeeMaskNoCloseProcess = 0x00000040;

    /// <summary>不要让外壳自己弹错误对话框——提示由我们给出，措辞才统一。</summary>
    internal const uint SeeMaskFlagNoUi = 0x00000400;

    /// <summary>目标不存在时不要弹「找不到文件」。</summary>
    internal const int SeErrSuccess = 0;

    /// <summary>找不到文件。</summary>
    internal const int SeErrFileNotFound = 2;

    /// <summary>找不到关联（我们把它当作「不知道该怎么打开」）。</summary>
    internal const int SeErrNoAssociation = 31;

    /// <summary>拒绝访问。</summary>
    internal const int SeErrAccessDenied = 5;

    /// <summary>用户取消了 UAC 提示。</summary>
    internal const int SeErrCancelled = 1223;

    /// <summary>打开一个文件、文件夹或网址，走外壳关联。</summary>
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShellExecuteEx(ref ShellExecuteInfo info);

    /// <summary>
    /// <c>SHELLEXECUTEINFOW</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>字段数必须与原生完全一致。</b>第一版在 <c>hkeyClass</c> 后面多写了一个
    /// 同名的「HKLM 那一半」——原生<strong>没有</strong>这个字段，
    /// <c>hkeyClass</c> 本身就是一个 <c>HKEY</c>（指针）。多出来的 8 字节把
    /// <c>dwHotKey</c> 及其后的 <c>hProcess</c> 各推后一格，
    /// 于是 <c>cbSize</c> 报成 120（应为 112），进程句柄也永远读成 0——
    /// 结果是「启动看起来失败、pid 拿不到」，而调用点本身完全看不出问题。
    /// </para>
    /// <para>
    /// x64 下布局：cbSize 0、fMask 4、hwnd 8、lpVerb 16、lpFile 24、
    /// lpParameters 32、lpDirectory 40、nShow 48、hInstApp 56、lpIDList 64、
    /// lpClass 72、hkeyClass 80、dwHotKey 88、hIcon/hMonitor 96、hProcess 104，共 112 字节。
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal nint Window;
        internal nint Verb;
        internal nint File;
        internal nint Parameters;
        internal nint Directory;
        internal int Show;
        internal nint InstApp;
        internal nint IdList;
        internal nint ClassName;
        internal nint ClassKey;
        internal uint HotKey;
        internal nint IconOrMonitor;
        internal nint Process;
    }

    /// <summary>
    /// <c>SHFILEINFOW</c>。
    /// </summary>
    /// <remarks>
    /// 这个结构体<strong>没有</strong> <c>cbSize</c> 字段，长度由
    /// <c>SHGetFileInfo</c> 的 <c>cbFileInfo</c> 参数另外给出。阶段 3 的探针
    /// 一开始照着别的结构体习惯在最前面加了一个 <c>int Size</c>，
    /// 结果整个布局后移 4 字节，读出来的图标句柄是 <c>0x4000014500000032</c>
    /// 这种明显不对的值，而返回值仍然是「成功」。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ShellFileInfo
    {
        internal nint Icon;
        internal int IconIndex;
        internal uint Attributes;

        internal fixed char DisplayName[260];
        internal fixed char TypeName[80];
    }

    /// <summary>
    /// 取一个窗口的属性存储，用于读窗口上显式设置的 <c>System.AppUserModel.ID</c>。
    /// </summary>
    /// <remarks>
    /// 返回的是 <strong>显式</strong>属性存储：窗口自己（或宿主框架）设置过的属性。
    /// 系统按包身份推导的「隐式身份」不在这个存储里——打包应用的窗口
    /// 是否因此读不到、读不到时得改走进程侧的
    /// <c>GetApplicationUserModelId</c>，由阶段 4 的探针实测结论决定，不凭印象。
    /// 返回的接口指针归调用方所有，用完必须 <c>Marshal.Release</c>。
    /// </remarks>
    [LibraryImport("shell32.dll")]
    internal static partial int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId, out nint store);

    /// <summary><c>APPBARDATA</c>。查询任务栏状态时只用到 <see cref="Size"/> 与返回值。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        internal uint Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal Rect Rectangle;
        internal nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NotifyIconData
    {
        internal int Size;
        internal nint WindowHandle;
        internal int Id;
        internal int Flags;
        internal int CallbackMessage;
        internal nint IconHandle;

        internal fixed char Tip[TipLength];

        internal int State;
        internal int StateMask;

        internal fixed char Info[256];

        /// <summary>
        /// <c>uTimeout</c> 与 <c>uVersion</c> 的联合体；我们只用到版本那一半。
        /// </summary>
        internal int VersionOrTimeout;

        internal fixed char InfoTitle[64];
        internal int InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIconHandle;

        /// <summary>
        /// 把托管字符串复制进某个固定缓冲区，超长时截断，
        /// 并始终保留结尾的 null。
        /// </summary>
        internal static void SetText(char* destination, int capacity, string? value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

            if (string.IsNullOrEmpty(value))
            {
                destination[0] = '\0';
                return;
            }

            var length = Math.Min(value.Length, capacity - 1);
            for (var i = 0; i < length; i++)
            {
                destination[i] = value[i];
            }

            destination[length] = '\0';
        }
    }
}
