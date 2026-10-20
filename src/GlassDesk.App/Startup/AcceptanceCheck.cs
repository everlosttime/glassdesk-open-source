namespace GlassDesk.App.Startup;

/// <summary>一条验收检查的结论。</summary>
/// <remarks>
/// <strong>「跳过」与「通过」是两回事，必须分开统计。</strong>
/// 跳过意味着这台机器缺这个条件（没有副屏、用户桌面已占着靶子窗口、
/// 缺打包应用……），它既没有证明什么，也不该被算进通过数——
/// 把它记成通过等于用环境缺失冒充验证结果。
/// </remarks>
internal enum CheckStatus
{
    /// <summary>断言跑了并通过。</summary>
    Passed = 0,

    /// <summary>断言跑了但没通过。</summary>
    Failed = 1,

    /// <summary>条件不具备，没跑。既不算通过也不算失败。</summary>
    Skipped = 2,
}