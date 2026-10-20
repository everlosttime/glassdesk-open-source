# GlassDesk 第三方声明

本文按 GlassDesk `0.2.0-preview.1` 本地候选包的实际自包含发布内容整理。它记录组件来源和随包许可材料，不替代各组件的完整许可条款，也不构成法律意见。清单针对本次 `win-x64` 候选，其他 RID 或版本需要重新核对。

| 组件或数据 | 候选内容与版本 | 许可或声明材料 | 来源及核对方式 |
| --- | --- | --- | --- |
| .NET Runtime | `Microsoft.NETCore.App` 10.0.12，随 .NET SDK 10.0.401 的 `win-x64` 自包含发布 | `LICENSE.txt`、`ThirdPartyNotices.txt` | 取自与候选 runtime 版本匹配的 .NET SDK 根目录；两文件原样复制到 `docs/third-party/dotnet-10.0.12/`，以 SHA-256 固定来源。 |
| Windows Desktop / WPF | `Microsoft.WindowsDesktop.App` 10.0.12；包括 `PresentationNative_cor3.dll`、`vcruntime140_cor3.dll`、`wpfgfx_cor3.dll` 等 | .NET Library License 及 .NET `ThirdPartyNotices.txt` | 依据 .NET for Windows 许可分类；实际发布目录按 `GlassDesk.runtimeconfig.json` 检查 framework 版本。 |
| WPF Direct3D 编译器 | `D3DCompiler_47_cor3.dll` | `WINDOWS-SDK-LICENSE.txt` | .NET for Windows 许可分类将该文件列为 Windows SDK License；随包文件为 Microsoft Learn 当前 Windows SDK 条款的英文文本副本，源链接和获取日期见文件头。 |
| Unicode Unihan 数据 | 由 Unihan 17.0.0 派生的离线拼音/排序数据 | `UNICODE-LICENSE.txt` | 原样复制项目保存的 `docs/third-party/unicode-license-v3.txt`；数据来源与生成方式继续见[依赖与来源记录](依赖与来源记录.md)。 |
| 安装器构建工具 | Inno Setup 7.1.0 x64；用于本地生成 Setup | `INNO-SETUP-LICENSE.txt` | 来自已验证官方 Inno Setup 安装器中的 `license.txt`；安装器下载、签名、attestation 和哈希记录于该批次 `toolchain.json`。 |

产品项目未引用第三方运行时 NuGet 包；自包含候选仍包含 Microsoft .NET/WPF 运行时文件和 Unicode 许可数据。`ThirdPartyNotices.txt` 是匹配运行时版本的上游完整声明文件。上表与实际 manifest 应共同审阅；manifest 会列出每个分发文件的长度和 SHA-256。

候选包和安装后的程序目录均显式携带以上许可材料。`THIRD-PARTY-NOTICES.md` 是本汇总的机器可见副本；`INNO-SETUP-LICENSE.txt` 也随 ZIP 和安装载荷提供。若未来版本增加组件、更新 Runtime/WPF、变更 RID 或调整打包方式，必须重新盘点产物并替换匹配的许可材料，不能仅凭本文件推断覆盖完整。

## 官方依据

- [.NET Asset Licensing Model](https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md)：二进制分发随附许可和第三方声明；Windows 产品分发使用 .NET Library License。
- [.NET for Windows 许可分类](https://github.com/dotnet/core/blob/main/license-information-windows.md)：分类 WPF、CoreCLR 与 `D3DCompiler_47_cor3.dll` 的适用许可。
- [Microsoft .NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm)。
- [Windows SDK License](https://learn.microsoft.com/en-us/legal/windows-sdk/license)；对应英文条款副本见 `docs/third-party/windows-sdk-license-en-us.txt`。
- [Inno Setup 下载验证说明](https://jrsoftware.org/isdl-verify.php)和[商业许可说明](https://jrsoftware.org/isorder.php)。本地候选仅记录所用版本与条款；此工作不购买许可，也不发布候选包。
