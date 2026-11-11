# GlassDesk 游戏休眠候选验收工具

使用 PowerShell 7（`pwsh`）。安装包沿用标准 GlassDesk 身份，可选择目录；不包含用户 Data。候选包尚未正式发布。

`Run-GameMemoryAcceptance.ps1` 会先正常退出当前安装，再将配置复制到独立测试目录。测试使用真实 AppHost、Dock、启动台及生产捕获链；结束后核对原数据并恢复原安装运行。不要同时运行两套验收，也不要在采样期间操作启动台或安装升级。

```powershell
pwsh -File build/Run-GameMemoryAcceptance.ps1 -CandidateDirectory <候选目录> -OutputDirectory <新的验收目录> -LongRunSeconds 1800
pwsh -File build/Summarize-GameMemoryAcceptance.ps1 -AcceptanceDirectory <验收目录> -OutputPath <汇总.json>
```

候选目录需包含 App、Testing 和 Probe；`-Quick -LongRunSeconds 0` 用于快速检查，不证明稳定内存、隐藏 1/10 分钟或 30 分钟稳定性。`-SkipBaseline` 仅在已经保留同配置正式基线时使用。

内存为整组私有工作集，并包括验收宿主的控制台子进程；同时记录私有提交、总工作集、CPU、GPU 引擎活动和进程数。采样约每秒一次，峰值为观察到的采样峰值，不能据此声称捕获了瞬时动画峰值。不执行强制 GC 或工作集裁剪。录像阶段不计入稳定状态数据。

`Run-GameMemoryProductMeasurement.ps1` 另外运行真实 `GlassDesk.exe`，通过现有命名管道打开/关闭启动台，排除控制台验收宿主带来的差别；必须等待完整宿主测试结束，并传入其 input-profile 复制目录。`Measure-GlassDeskAnimationMemory.ps1` 在支持 EX2 计数结构的机器上以目标 50 ms 间隔测反复动画的私有工作集，记录实际采样间隙；旧 Win11 仍使用 CIM 工具，产品本身不依赖 EX2。

首帧证据包含桌面录制 GIF、WPF 渲染 GIF、时间与材质状态记录。完整宿主性能测试的按钮翻页不是人工拖动验收；`--full-host-input` 是现有完整宿主原生鼠标输入入口，必须在标记过的复制配置测试载荷中运行。

`Test-GameMemoryUpgrade.ps1` 用两个实际安装包验证复制配置升级，随后恢复原卸载登记和快捷方式。原安装目录不被替换。不得把测试目录中的复制 Data、日志中的私人内容或屏幕录像上传到公共仓库。

真实游戏验收由试用者提供已有游戏和固定场景。全屏与无边框全屏分别测试 DX11、DX12、Vulkan；退出 GlassDesk / 开启候选自动休眠交替各 5 次，每次 120 秒，预热后用系统级帧时间工具记录平均 FPS 和 1% low。没有游戏数据时只报告后台工作停止，不能宣布游戏性能达标。
