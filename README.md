# GlassDesk 1.6.0（无信息栏源码版）

玻璃 Dock、启动台、文件夹、附近传输与统一设置中心。该源码版从 1.6.0 产品代码同步，继续保留此前“不公开信息栏”的范围与许可政策。

[下载源码 ZIP](https://github.com/everlosttime/glassdesk-open-source/archive/refs/heads/main.zip) · [源码版变更说明](docs/Release-notes-v1.6.0-public.md) · [参与贡献](CONTRIBUTING.md)

## 公开范围

- 保留 1.6.0 的 Dock、启动台及分组、桌面入口、原生渲染源码、玻璃背景优化、快捷键焦点框修复、官网二维码下载入口、附近传输和可选奶蛙组件。
- 不包含信息栏及其日历、控制中心、设备选择、媒体控制、外设电量读取实现。公开版教学使用模拟托盘菜单打开设置。
- 部分共享设置模型中保留旧 JSON 兼容字段，不提供与之对应的信息栏功能。
- 来源与适配文件见 [SOURCE-PROVENANCE.json](SOURCE-PROVENANCE.json)。不带私有开发历史、用户 Data、日志、截图和验收证据。

本次只同步源码，暂不发布 1.6.0 正式 Release，也不触发客户端更新通知。此前 1.4.0 Release 仍是历史源码快照；当前 1.6.0 源码从仓库 main 下载。

## 许可

继续采用原来的分范围许可：合资格原创新增及修改采用 PolyForm Noncommercial 1.0.0；历史 Apache-2.0 部分、第三方组件及其现存权利保留。完整许可见 [LICENSE](LICENSE)、[授权说明](docs/LICENSING.md)、[逐文件记录](docs/licensing/FILE-LICENSES.json) 和 [NOTICE](NOTICE)。这属于非商业源码公开，新增条款不属于 OSI 标准开源许可。

`contrib/Naiwa` 独立上游组件保留 MIT；Unicode 数据、AndroidLiquidGlass、.NET/WPF、ASP.NET Core 与其他上游内容保留对应许可。修改与分发时保留声明，注明自己的修改。

## 编译

需要 Windows x64 和 .NET 10 SDK。`global.json` 固定 10.0.401 并允许 latestFeature；项目目标 Windows SDK 为 10.0.26100.0。实际效果取决于 Windows、显卡与桌面环境。

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release --filter "FullyQualifiedName~PublicDistributionTests|FullyQualifiedName~LauncherKeyboardFocusVisualTests|FullyQualifiedName~DownloadEntryTests"
```

其他 WPF 验收可能显示窗口、采集桌面或操作鼠标键盘，应在专门测试会话运行。编译通过不代表所有设备均已实测。

程序使用运行目录旁的 Data；源码构建输出使用各自输出目录的数据。运行开发版前请从托盘退出其他 GlassDesk 实例，避免相同产品实例冲突。

## 从源码生成便携版

```powershell
dotnet publish src/GlassDesk.App/GlassDesk.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:InformationalVersion=1.6.0 -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/publish
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item NOTICE artifacts/publish/NOTICE.txt
Copy-Item docs/LICENSING.md artifacts/publish/LICENSING.md
Copy-Item docs/licensing artifacts/publish/licensing -Recurse
Copy-Item docs/third-party artifacts/publish/third-party -Recurse
```

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
