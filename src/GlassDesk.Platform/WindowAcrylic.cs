using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 给窗口套上亚克力（Acrylic）背景模糊。
/// </summary>
/// <remarks>
/// <b>为什么用未公开的 API。</b>阶段 2 实测确认：文档化的 DWM 背景材质
/// （<c>DWMWA_SYSTEMBACKDROP_TYPE</c>）在 WPF 窗口上**完全不可见**——
/// 属性调用返回成功，画面却是纯黑。
///
/// 原因是 WPF 窗口默认是非分层窗口，客户区渲染进一张重定向表面，
/// 而透明像素在那张表面上落成不透明黑，把 DWM 画的材质整个盖住。
/// 实测四组配置（Mica／延伸边框／Acrylic／带填充）中心像素均为 <c>#000000</c>，
/// 而分层窗口配合本 API 能拿到真实的背景模糊（实测 <c>#222222</c>，
/// 与「60% 深色叠在模糊后的桌面上」的计算值精确吻合）。
///
/// 所以 WPF 上要真实玻璃效果，只有这一条路。它是未公开 API，
/// 自 Windows 10 起被广泛使用且稳定，但既然是未公开，就必须：
/// <list type="bullet">
/// <item>调用失败时安静回退到自绘填充，绝不假定可用；</item>
/// <item>按 Windows 构建号记录验证结果，未验证的构建不宣称支持。</item>
/// </list>
/// </remarks>
public static class WindowAcrylic
{
    private const int WcaAccentPolicy = 19;

    /// <summary>亚克力模糊：真实模糊窗口背后的内容。</summary>
    private const int AccentEnableAcrylicBlurBehind = 4;

    /// <summary>仅仅模糊，不做色调混合。</summary>
    private const int AccentEnableBlurBehind = 3;

    /// <summary>
    /// 给窗口套上亚克力。
    /// </summary>
    /// <param name="window">目标窗口句柄。</param>
    /// <param name="tintAlpha">色调层的不透明度，0 为完全透明。</param>
    /// <param name="red">色调的红色分量。</param>
    /// <param name="green">色调的绿色分量。</param>
    /// <param name="blue">色调的蓝色分量。</param>
    /// <returns>系统是否接受了这次设置。</returns>
    /// <remarks>
    /// 刻意只收 R/G/B 三个字节，不收 WPF 的 <c>Color</c>：本程序集不引用 WPF，
    /// 这样互操作层才能在没有界面框架的情况下也被使用与测试。
    /// </remarks>
    public static bool TryApply(nint window, byte tintAlpha, byte red, byte green, byte blue)
    {
        if (window == 0)
        {
            return false;
        }

        var policy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            // 2 表示向四周扩展模糊区域，避免边缘出现一圈未模糊的亮边。
            AccentFlags = 2,
            // GradientColor 是 **ABGR** 顺序，不是 ARGB。写反会让红蓝互换。
            GradientColor = (tintAlpha << 24) | (blue << 16) | (green << 8) | red,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return Composition32.SetWindowCompositionAttribute(window, ref data) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 未公开 API 在未来的构建里可能消失，这属于预期内的失败。
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>去掉窗口上的亚克力效果。</summary>
    public static bool TryRemove(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var policy = new AccentPolicy
        {
            AccentState = 0, // ACCENT_DISABLED
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return Composition32.SetWindowCompositionAttribute(window, ref data) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 已经**实测验证过**亚克力可用的 Windows 构建号。
    /// </summary>
    /// <remarks>
    /// 这是一份白名单，不是「大于等于某个版本」的判断。两者的区别是要害：
    ///
    /// <list type="bullet">
    /// <item>
    /// 接口未公开，微软没有承诺它在未来构建里仍然存在或行为不变。用「>= 22621」
    /// 这种判断，等于对**所有未来构建**做了未经验证的承诺。
    /// </item>
    /// <item>
    /// 白名单则相反：未列入的构建走自绘降级——界面照样能用，只是没有背景模糊。
    /// 这与计划书对实验功能的要求一致（「只在实际验证过的构建上开放；未知构建显示不支持」）。
    /// </item>
    /// </list>
    ///
    /// <b>只列有证据的构建。</b>凭「它是 Windows 11 的某个版本」把构建号加进来，
    /// 只是把「>= 22621」换个写法——同样是对未验证的构建做了承诺。加一个构建号之前，
    /// 必须在**那台机器**上做过对照实验并留下记录：
    ///
    /// <list type="number">
    /// <item>造一个分层窗口，套上亚克力，截图取窗口中心像素；</item>
    /// <item>同一窗口、同一位置，不套亚克力再取一次；</item>
    /// <item>两次的像素值与观感都应明显不同（本机 26100 的对照数据见
    /// <c>docs/阶段2-系统能力报告.md</c> 第三节）；</item>
    /// <item>把该构建号、日期与对照结果记进那份报告，再往上加。</item>
    /// </list>
    /// </remarks>
    private static readonly int[] VerifiedBuilds =
    [
        // 26100 = Windows 11 24H2 —— 本机实测验证（2026-09-18，阶段 2）。
        // 对照数据：分层窗口 + 亚克力得到 #222222，与「60% 深色叠在模糊后桌面上」的
        // 计算值精确吻合；不套亚克力时同一窗口能看到未模糊的壁纸细节。
        26100,
    ];

    /// <summary>
    /// 本机是否在已验证构建名单内。
    /// </summary>
    /// <remarks>
    /// 返回 false 不是错误，只是「没有验证过」，调用方据此走自绘降级。
    /// </remarks>
    public static bool IsSupportedOnThisBuild =>
        Array.IndexOf(VerifiedBuilds, WindowsCapabilities.BuildNumber) >= 0;

    /// <summary>
    /// 为什么当前构建不被亚克力支持，用于诊断输出。
    /// </summary>
    public static string DescribeSupport()
    {
        var build = WindowsCapabilities.BuildNumber;

        return IsSupportedOnThisBuild
            ? $"Build {build} 在已验证名单内，使用亚克力背景模糊"
            : $"Build {build} 未经验证，走自绘面板降级（已验证：{string.Join('、', VerifiedBuilds)}）";
    }

    /// <summary>
    /// 仅模糊、不叠加色调的变体，供需要完全自控色调的场合使用。
    /// </summary>
    public static bool TryApplyBlurOnly(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var policy = new AccentPolicy
        {
            AccentState = AccentEnableBlurBehind,
            AccentFlags = 2,
            GradientColor = 0,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return Composition32.SetWindowCompositionAttribute(window, ref data) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowCompositionAttributeData
{
    internal int Attribute;
    internal nint Data;
    internal int SizeOfData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AccentPolicy
{
    internal int AccentState;
    internal int AccentFlags;
    internal int GradientColor;
    internal int AnimationId;
}

internal static partial class Composition32
{
    [LibraryImport("user32.dll")]
    internal static partial int SetWindowCompositionAttribute(nint window, ref WindowCompositionAttributeData data);
}