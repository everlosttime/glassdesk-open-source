using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

/// <summary>
/// 这份 Windows 安装能做什么。
/// </summary>
/// <remarks>
/// 只探测一次并缓存。每个依赖特定 Windows 版本的功能
/// 都在这里询问，而不是就地判断版本，
/// 这样不支持的组合会一致地报告自身状态，
/// 「不支持的版本如实说明而不是猜测」这条规则也只需在一个地方生效。
///
/// 版本取自 <c>RtlGetVersion</c> 而不是 <see cref="Environment.OSVersion"/>，
/// 因为后者可能被应用程序兼容性 shim 改写，
/// 而这些检查的意义恰恰在于确认版本号。
/// </remarks>
public static partial class WindowsCapabilities
{
    /// <summary>Windows 11 首个版本。圆角窗口开始可用。</summary>
    private const int Windows11Build = 22000;

    /// <summary>Windows 11 22H2。<c>DWMWA_SYSTEMBACKDROP_TYPE</c> 开始可用。</summary>
    private const int SystemBackdropBuild = 22621;

    /// <summary>官方支持深色模式窗口特性的起始版本。</summary>
    private const int ImmersiveDarkModeBuild = 18985;

    /// <summary>此发行路线最低 Windows 11 21H2。</summary>
    private const int MinimumSupportedBuild = 22000;

    static WindowsCapabilities()
    {
        var (major, minor, build) = ReadVersion();
        OsVersion = new Version(major, minor, build);
        BuildNumber = build;
    }

    /// <summary>运行中操作系统的真实版本，例如 10.0.26100。</summary>
    public static Version OsVersion { get; }

    public static int BuildNumber { get; }

    /// <summary>产品最低系统门槛；未来 Build 不设置上限。</summary>
    public static bool IsSupported => IsProductBuildSupported(BuildNumber);
    public static bool IsProductBuildSupported(int build) => build >= MinimumSupportedBuild;

    public static bool IsWindows11OrGreater => BuildNumber >= Windows11Build;

    /// <summary>
    /// DWM 是否会响应背景材质请求。
    /// 为 false 时窗口必须自行绘制半透明面板。
    /// </summary>
    public static bool SupportsSystemBackdrop => BuildNumber >= SystemBackdropBuild;

    /// <summary>能否向 DWM 请求窗口圆角。</summary>
    public static bool SupportsWindowCornerPreference => BuildNumber >= Windows11Build;

    /// <summary>非客户区能否跟随应用主题。</summary>
    public static bool SupportsImmersiveDarkMode => BuildNumber >= ImmersiveDarkModeBuild;

    /// <summary>
    /// 供设置窗口的「关于」页和诊断使用的可读摘要。
    /// </summary>
    public static string Describe()
    {
        var backdrop = SupportsSystemBackdrop ? "支持系统背景材质" : "不支持系统背景材质（使用自绘面板）";
        return $"Windows {OsVersion}（Build {BuildNumber}）— {backdrop}";
    }

    private static (int Major, int Minor, int Build) ReadVersion()
    {
        try
        {
            var info = new RtlOsVersionInfo
            {
                Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>(),
            };

            // 在所有受支持的 Windows 上都返回 STATUS_SUCCESS (0)。
            // 返回值非零表示调用本身失败，
            // 此时托管的回退值仍好过报告一个荒唐的版本号。
            if (NtDll.RtlGetVersion(ref info) == 0)
            {
                return ((int)info.MajorVersion, (int)info.MinorVersion, (int)info.BuildNumber);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 继续走托管的回退值。
        }

        var fallback = Environment.OSVersion.Version;
        return (fallback.Major, fallback.Minor, fallback.Build);
    }

    /// <summary>
    /// 对应 <c>RTL_OSVERSIONINFOW</c>。固定长度的字符缓冲区让结构体保持可直接封送，
    /// 这也正是下面能使用源生成封送的原因。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct RtlOsVersionInfo
    {
        public uint Size;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public fixed char ServicePack[128];
    }

    private static partial class NtDll
    {
        [LibraryImport("ntdll.dll")]
        internal static partial int RtlGetVersion(ref RtlOsVersionInfo versionInfo);
    }
}
