using Microsoft.Win32;

namespace GlassDesk.Platform;

/// <summary>
/// 读取用户在操作系统层面的外观选择。
/// </summary>
/// <remarks>
/// 「跟随系统」主题需要这些值，而透明标志还有独立的意义：如果
/// 用户在 Windows 里关闭了透明效果，那么无论我们自己的设置怎么说，
/// 都不该绘制半透明面板。
///
/// 每次读取都是防御性的。值缺失是正常情况——某些版本
/// 和全新用户配置文件上并没有这些键——启动期间不能抛异常。
/// </remarks>
public static class SystemThemeReader
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>当系统设为浅色应用主题时为 true。默认为 true。</summary>
    public static bool IsLightTheme()
    {
        var value = ReadPersonalizeDword("AppsUseLightTheme");
        return value is null or 1;
    }

    /// <summary>
    /// 当 Windows 透明效果开启时为 true。默认为 true，与全新
    /// 安装一致。
    /// </summary>
    public static bool IsTransparencyEnabled()
    {
        var value = ReadPersonalizeDword("EnableTransparency");
        return value is null or 1;
    }

    private static int? ReadPersonalizeDword(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            var raw = key?.GetValue(name);
            return raw switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}