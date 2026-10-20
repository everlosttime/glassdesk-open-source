namespace GlassDesk.Core.Models;

/// <summary>
/// UI 该用哪套配色。
/// </summary>
/// <remarks>
/// 三种状态，对应计划书的要求。因为存在设置里，取值必须
/// 保持稳定：<see cref="System"/> 表示「跟随系统的浅色/深色设置」。
/// </remarks>
public enum ThemeMode
{
    /// <summary>跟随 Windows 应用主题，并随其变化而调整。</summary>
    System = 0,

    /// <summary>始终浅色。</summary>
    Light = 1,

    /// <summary>始终深色。</summary>
    Dark = 2,
}