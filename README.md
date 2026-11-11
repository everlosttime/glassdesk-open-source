# GlassDesk 1.6.0（完整功能源码）

从 GlassDesk 1.6.0 产品代码同步完整功能源码：玻璃 Dock、启动台、信息栏、农历日历、控制中心、设备与媒体控制、外设电量、附近传输和统一设置中心。此次取消此前“不公开信息栏”的范围限制，保留现有许可及第三方授权。

[下载源码 ZIP](https://github.com/everlosttime/glassdesk-open-source/archive/refs/heads/main.zip) · [源码版变更说明](docs/Release-notes-v1.6.0-public.md) · [参与贡献](CONTRIBUTING.md)

## 公开范围

- 包含 1.6.0 的全部产品功能源码：Dock、启动台及分组、桌面入口、信息栏、日历与公历/农历切换、控制中心、设备选择、媒体控制、系统状态与外设电量读取。
- 包含完整 AppHost、设置与新手教学集成、原生渲染、背景优化、快捷键焦点框修复、官网二维码下载入口、附近传输和可选奶蛙组件。
- 包含全部 src / native / tests / tools / build / packaging 源码，以及 components-src 电量桥接源码、third_party 固定依赖与对应来源归档。产品源码直接从 1.6.0 提交导出，未删减信息栏或其他功能。
- 来源及逐文件哈希见 [SOURCE-PROVENANCE.json](SOURCE-PROVENANCE.json) 和 [逐文件记录](docs/licensing/FILE-LICENSES.json)。公开仓库保留自身历史，不导入私有 Git 开发历史、个人 Data、日志或验收截图。

本次只同步源码，暂不发布 1.6.0 正式 Release，也不触发客户端更新通知。此前 1.4.0 Release 仍是历史源码快照；当前 1.6.0 源码从仓库 main 下载。

## 许可

继续采用原来的分范围许可：合资格原创新增及修改采用 PolyForm Noncommercial 1.0.0；历史 Apache-2.0 部分、第三方组件及其现存权利保留。完整许可见 [LICENSE](LICENSE)、[授权说明](docs/LICENSING.md)、[逐文件记录](docs/licensing/FILE-LICENSES.json) 和 [NOTICE](NOTICE)。这属于非商业源码公开，新增条款不属于 OSI 标准开源许可。

独立电量组件 `components-src/battery-engine` 与 `third_party/myfinder-device-battery` 保留 AGPL-3.0-only 及其内部上游许可；HeadsetControl 保留 GPL-3.0。这些组件通过独立进程接口调用，原许可不被项目非商用条款替代。

`contrib/Naiwa` 独立上游组件保留 MIT；Unicode 数据、AndroidLiquidGlass、.NET/WPF、ASP.NET Core 与其他上游内容保留对应许可。修改与分发时保留声明，注明自己的修改。

## 编译

需要 Windows x64 和 .NET 10 SDK。`global.json` 固定 10.0.401 并允许 latestFeature；项目目标 Windows SDK 为 10.0.26100.0。实际效果取决于 Windows、显卡与桌面环境。

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release --filter "FullyQualifiedName~LunarCalendarTests|FullyQualifiedName~LauncherKeyboardFocusVisualTests|FullyQualifiedName~DownloadEntryTests"
```

整套解决方案可使用 `dotnet build GlassDesk.slnx -c Release` 编译。两个历史验收项目的 Windows 目标框架已对齐为 `net10.0-windows10.0.26100.0`；仅修正验收工程编译配置，产品源码保持原样。

其他 WPF 验收可能显示窗口、采集桌面或操作鼠标键盘，应在专门测试会话运行。编译通过不代表所有设备均已实测。

程序使用运行目录旁的 Data；源码构建输出使用各自输出目录的数据。运行开发版前请从托盘退出其他 GlassDesk 实例，避免相同产品实例冲突。

## 从源码生成便携版

```powershell
dotnet publish src/GlassDesk.App/GlassDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:InformationalVersion=1.6.0 -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/publish
pwsh -File build/Copy-BatteryEngine.ps1 -OutputDirectory artifacts/publish
pwsh -File build/Copy-HeadsetControl.ps1 -OutputDirectory artifacts/publish
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item NOTICE artifacts/publish/NOTICE.txt
Copy-Item docs/LICENSING.md artifacts/publish/LICENSING.md
Copy-Item docs/licensing artifacts/publish/licensing -Recurse
Copy-Item docs/third-party artifacts/publish/third-party -Recurse
```

电量组件使用仓库中的固定离线依赖与对应源码归档，必须使用 PowerShell 7 以保持构建脚本的哈希一致。HeadsetControl 脚本下载固定版本二进制、对应源码与许可，并校验 SHA-256；这一步需要网络。完整发行必须保留上述独立组件各自的许可证、声明与对应源码。

不要把个人 Data 放入分发包。原生渲染 C++ 源码位于 native；重建原生渲染 DLL 使用 `build/Build-NativeRendering.ps1`，需要配置相应 MSVC / Windows SDK 工具链路径，并通过 `NativeRenderingDllPath` 传入 App 的 publish。

奶蛙是默认关闭的可选独立组件，源码在 `contrib/Naiwa`。构建它需要 .NET Framework 4.8 开发环境：

```powershell
dotnet build contrib/Naiwa/GlassDesk.Naiwa.csproj -c Release -o artifacts/publish/components/Naiwa
Copy-Item contrib/Naiwa/LICENSE artifacts/publish/components/Naiwa/LICENSE.txt
```

生成安装包另需 Inno Setup 7：

```powershell
$env:GLASSDESK_PACKAGE_STAGE = (Resolve-Path artifacts/publish).Path
ISCC /DAppIdValue=GlassDesk.Independent /DProductVersion=1.6.0 /DFileVersion=1.6.0.0 /DPackageBaseName=GlassDesk-1.6.0-public-win-x64 /Oartifacts packaging/GlassDesk.iss
```

安装标识仍为 GlassDesk.Independent，安装自己编译的包会影响相同标识的现有安装，请在测试环境核对。修改着色器后使用 `build/compile-transition-shader.ps1` 重建对应着色器资源。
