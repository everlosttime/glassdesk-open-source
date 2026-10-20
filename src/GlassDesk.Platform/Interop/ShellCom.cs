using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// 外壳的 COM 接口：快捷方式、以及按尺寸取图标。
/// </summary>
/// <remarks>
/// 用源生成的 COM 互操作（<c>[GeneratedComInterface]</c>）而不是
/// <c>[ComImport]</c>，与本目录其余声明「优先源生成」的约定一致，
/// 也不要求进程启用内置 COM 互操作。
///
/// <para>
/// <b>踩过的坑：返回 <c>int</c> 的方法必须标 <c>[PreserveSig]</c>。</b>
/// 阶段 3 的探针实测，<c>IShellItemImageFactory.GetImage</c> 不标它时返回值是
/// 垃圾（实测 0x04F9C058、0x94971370），而 <c>bitmap</c> 出参完全没被写入；
/// 标上之后与 <c>[ComImport]</c> 对照组得到逐像素一致的结果。
/// </para>
///
/// <para>
/// 方法顺序即 vtable 槽位顺序，因此<strong>不能只声明用得上的那几个</strong>——
/// 少写一个，后面的方法就会落到错误的槽位上，而且不会报错。
/// </para>
/// </remarks>
internal static class ShellComIds
{
    /// <summary>ShellLink 组件类。</summary>
    internal static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    internal static readonly Guid ShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    internal static readonly Guid PersistFile = new("0000010B-0000-0000-C000-000000000046");

    internal static readonly Guid ShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary><c>IPropertyStore</c> 的接口标识。</summary>
    internal static readonly Guid PropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
}

/// <summary>属性系统的键：格式 id 加属性编号。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    internal PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    internal Guid FormatId;

    internal int PropertyId;
}

/// <summary><c>System.AppUserModel.ID</c> 属性的键。</summary>
/// <remarks>
/// 快捷方式与窗口上显式设置的「应用身份」。任务栏与「开始」菜单
/// 用来把窗口、快捷方式与通知归到同一个应用名下的就是它。
/// </remarks>
internal static class PropertyKeys
{
    internal static readonly PropertyKey AppUserModelId = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
}

/// <summary><c>VARENUM</c> 里本产品用到的类型标记。</summary>
internal static class VarType
{
    /// <summary>没有值。</summary>
    internal const short Empty = 0;

    /// <summary>以 null 结尾的 Unicode 字符串，缓冲区归属性分配器管。</summary>
    internal const short Lpwstr = 31;
}

/// <summary>Win32 <c>PROPVARIANT</c>。</summary>
/// <remarks>
/// <b>字段数与总量必须与原生一致。</b>x64 上原生是 24 字节：2 字节类型标记
/// 加 6 字节保留，再加一个 16 字节的联合体。这里用两个 <see cref="nint"/>
/// 撑出联合体的完整大小——少写一半（比如只放一个指针，总量 16 字节）时，
/// 被调用方写的是 24 字节，多出的 8 字节会踩到调用方内存，
/// 而 GetValue 照样返回「成功」。这正是阶段 3 在结构体上栽过的那类错。
/// 读出 <c>VT_LPWSTR</c> 后指针在 <see cref="Pointer"/>，须用
/// <c>CoTaskMemFree</c> 释放。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    /// <summary><c>VARENUM</c>。<c>VT_EMPTY</c>=0（没有值），<c>VT_LPWSTR</c>=31（Unicode 字符串）。</summary>
    internal short Type;

    private short _reserved1;

    private short _reserved2;

    private short _reserved3;

    /// <summary>联合体前半：字符串指针、64 位整数或接口指针。</summary>
    internal nint Pointer;

    /// <summary>联合体后半：只为撑满原生联合体的 16 字节，不直接使用。</summary>
    private nint _unionTail;
}

/// <summary><c>IShellLinkW</c>：读出一个 <c>.lnk</c> 的目标、参数、工作目录与图标位置。</summary>
/// <remarks>
/// 也用于写快捷方式——测试样本要能被重复造出来，见 <c>ShortcutWriter</c>。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint fileOut, int maxPath, nint findData, uint flags);

    void GetIDList(out nint itemIdList);

    void SetIDList(nint itemIdList);

    void GetDescription(nint nameOut, int maxName);

    void SetDescription(string name);

    void GetWorkingDirectory(nint dirOut, int maxPath);

    void SetWorkingDirectory(string dir);

    void GetArguments(nint argsOut, int maxPath);

    void SetArguments(string args);

    void GetHotkey(out ushort hotkey);

    void SetHotkey(ushort hotkey);

    void GetShowCmd(out int showCommand);

    void SetShowCmd(int showCommand);

    /// <summary>
    /// 读图标位置。索引为 0 时表示「用目标文件自身的图标」，
    /// 非 0 时表示 <paramref name="iconOut"/> 里的文件（通常是 dll 或 exe）中的第 N 个图标。
    /// </summary>
    void GetIconLocation(nint iconOut, int maxPath, out int iconIndex);

    void SetIconLocation(string iconPath, int iconIndex);

    void SetRelativePath(string pathRel, uint reserved);

    void Resolve(nint window, uint flags);

    void SetPath(string path);
}

/// <summary><c>IPersistFile</c>：把 <c>IShellLink</c> 与磁盘上的 <c>.lnk</c> 绑定起来。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);

    /// <summary>返回 <c>S_FALSE</c> 表示未修改，所以必须保留原始返回值而不是让它抛异常。</summary>
    [PreserveSig]
    int IsDirty();

    void Load(string fileName, uint mode);

    void Save(string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted(string fileName);

    void GetCurFile(out nint fileName);
}

/// <summary>
/// <c>IShellItemImageFactory</c>：按请求的尺寸取外壳缩略图或图标。
/// </summary>
/// <remarks>
/// 比 <c>SHGetFileInfo</c> 强的地方是尺寸：后者只给系统「大图标」尺寸
/// （实测 32×32），在高 DPI 的程序坞里放大后会糊。本接口实测能给出 256×256。
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
internal partial interface IShellItemImageFactory
{
    /// <summary>
    /// <c>SIZE</c> 必须<strong>整块</strong>传。实测把它拆成两个 <c>int</c> 会让
    /// 后续参数整体错位，调用直接以 0xC0000005 结束。
    /// </summary>
    [PreserveSig]
    int GetImage(Size size, int flags, out nint bitmap);
}

/// <summary>Win32 <c>SIZE</c>。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Size
{
    internal int Width;

    internal int Height;
}

/// <summary>
/// <c>IPropertyStore</c>：读属性系统里的一个值。
/// </summary>
/// <remarks>
/// 两个用途：读 <c>.lnk</c> 上显式设置的 <c>System.AppUserModel.ID</c>（ShellLink
/// 对象本身实现这个接口），以及读窗口上显式设置的应用身份
/// （<c>SHGetPropertyStoreForWindow</c> 返回这个接口）。
///
/// <para>
/// 声明从 vtable 槽位 1 开始按原生顺序排；本产品只<strong>读</strong>值，
/// 所以声明到 GetValue（槽位 3）为止，后面的 SetValue/Commit 用不到。
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    /// <summary>读一个属性。无值时返回 <see cref="Ole32.SoK"/> 并把类型置为 <c>VT_EMPTY</c>。</summary>
    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant value);

    /// <summary>写一个属性。值必须是完整拷贝（字符串按属性分配器规则复制）。</summary>
    [PreserveSig]
    int SetValue(in PropertyKey key, in PropVariant value);

    /// <summary>把挂起的写入落盘。没有它，<see cref="SetValue"/> 的内容不生效。</summary>
    [PreserveSig]
    int Commit();
}

/// <summary><c>IShellItemImageFactory::GetImage</c> 的标志位。</summary>
internal static class Siigbf
{
    /// <summary>只取图标，不取缩略图——我们要的是应用图标，不是 .exe 的预览图。</summary>
    internal const int IconOnly = 0x00000004;

    /// <summary>允许返回比请求更大的图，由调用方自己缩。</summary>
    internal const int BiggerSizeOk = 0x00000001;

    /// <summary>源图小于请求尺寸时允许放大。</summary>
    internal const int ScaleUp = 0x00000100;
}