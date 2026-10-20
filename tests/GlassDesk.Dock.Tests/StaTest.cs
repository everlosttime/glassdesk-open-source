namespace GlassDesk.Dock.Tests;

/// <summary>
/// 在专门的 STA 线程上运行测试体。
/// </summary>
/// <remarks>
/// WPF 窗口必须在 STA 线程上创建，而 xunit 默认在线程池（MTA）上跑用例。
/// 测试体里抛出的异常原样带回测试线程，失败原因不会丢。
/// </remarks>
internal static class StaTest
{
    public static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }
}
