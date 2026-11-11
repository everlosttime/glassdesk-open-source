using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;

internal static partial class Program
{
    private static void VerifyInputDeviceProbe(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        var buffer = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.Copy(new byte[48], 0, buffer, 48);
            Marshal.WriteInt32(buffer, 4, 48);
            Marshal.WriteIntPtr(buffer, 8, 0x123);
            Marshal.WriteInt16(buffer, 28, 1);
            Marshal.WriteInt32(buffer, 36, -17);
            Marshal.WriteInt32(buffer, 40, 29);
            var decoded = InputDeviceProbe.TryDecodeMouse(buffer, 48, out var packet);
            Check("原始鼠标包保留设备和位移及按键信息", "diagnostics/input-device",
                decoded && packet.Device == 0x123 && packet.X == -17 && packet.Y == 29 && packet.ButtonFlags == 1,
                "device=0x123,x=-17,y=29,buttons=1", $"decoded={decoded},packet={packet}");
            var truncated = InputDeviceProbe.TryDecodeMouse(buffer, 32, out _);
            Marshal.WriteInt32(buffer, 0, 1);
            var keyboard = InputDeviceProbe.TryDecodeMouse(buffer, 48, out _);
            Check("截断包和非鼠标输入不计入鼠标频率", "diagnostics/input-device", !truncated && !keyboard,
                "两种输入都忽略", $"truncated={truncated},keyboard={keyboard}");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        using var probe = new InputDeviceProbe(window, evidence);
        Check("原始鼠标观察器在独立线程注册成功", "diagnostics/input-device", probe.Ready,
            "独立线程注册成功", probe.Ready.ToString());
        var before = probe.BackgroundPulses;
        Thread.Sleep(350); // Deliberately block only the UI; the observation thread must keep advancing.
        var pulses = probe.BackgroundPulses - before;
        Check("界面阻塞时后台输入观察时钟仍前进", "diagnostics/input-device", pulses >= 2,
            "至少2次后台时钟", pulses.ToString());
        VerifyCaptureDragInput(window, grid, data, evidence);
        Check("跨窗口OLE拖动反馈被观察且本地松手取消正常", "diagnostics/input-device",
            probe.FeedbackCount > 0 && probe.QueryCount > 0,
            "收到反馈与继续拖动事件，现有拖动用例通过", $"feedback={probe.FeedbackCount},query={probe.QueryCount}");
        probe.Dispose();
        Check("诊断结束后独立观察线程退出并保存报告", "diagnostics/input-device",
            probe.Stopped && File.Exists(Path.Combine(evidence, "input-device-timing.json")),
            "线程退出且报告存在", $"stopped={probe.Stopped}");
    }
}
