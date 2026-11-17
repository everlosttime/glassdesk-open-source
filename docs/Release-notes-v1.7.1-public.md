# GlassDesk 1.7.1 完整功能源码

本次从产品提交 `7a8ee85a97d3f5e1d99ba6aacd0f1756f3125755` 同步公开源码，保留公开仓库历史、现有分范围许可与第三方声明。包含信息栏等完整功能，无产品功能源码删减；仅保留两个历史验收项目的 Windows SDK 目标框架对齐。

## 更新

- 修复固定桌面层的 Dock、信息栏与动态壁纸采集兼容：支持独立和嵌套壁纸宿主，排除自身所在桌面宿主，避免黑色壁纸子窗口与自身画面进入背景。
- 实时采集不可用时使用半透明背景；恢复后自动恢复玻璃，不再将 Windows 静态壁纸底图当成有效实时画面。
- 同步 1.6.0 之后的窗口置顶、ATK 等外设电量兼容、桌面同步、启动台自动分组、Steam 图标修复与 Dock 图标间距等产品更新。
- `packaging/release.json` 为 1.7.1，`naiwaCompanion` 默认关闭，安装包与便携包不捆绑奶蛙。可选奶蛙源码和 MIT 许可保留，动画及声音资源不包含在公开源码包内。

## 验证

产品发布前相关回归测试 111 项通过，包含本机 Windows 11 + Wallpaper Engine 与构造的跨进程宿主结构。反馈设备的具体 Windows、驱动与壁纸组合仍需设备实测；编译和测试不能代表全部设备均已验证。公开快照另行执行解决方案构建与相关测试，结果记录在本次 GitHub Release。

[源码发布](https://github.com/everlosttime/glassdesk-open-source/releases/tag/v1.7.1) · [软件发布](https://github.com/everlosttime/glassdesk-updates/releases/tag/v1.7.1)
