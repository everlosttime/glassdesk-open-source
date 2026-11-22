# GlassDesk 1.7.7 完整功能源码

本次同步 1.7.7 正式版的产品实现和回归测试。公开仓库沿用现有分范围许可和第三方声明。两个历史验收项目继续使用与产品一致的 Windows SDK 目标框架。

## 更新

- 液态玻璃和磨砂玻璃的动态背景按显示器与画面活动调整采集节奏，静止时减少重复处理。
- 改善固定桌面层下拖动普通窗口的流畅度。启动台开合动画由显示时钟驱动，玻璃背景独立更新，设置中移除了「启动台高刷新模式（120Hz 目标）」。
- 启动台图标仅归属一个分组，移动时自动从原分组移出。
- 「设置 → 回收站」支持导入和移除官方 ZIP 素材包，用于启用黄色奶蛙回收站。

`packaging/release.json` 默认构建奶蛙运行组件。动画与声音素材由用户从官方 ZIP 导入。

[源码发布](https://github.com/everlosttime/glassdesk-open-source/releases/tag/v1.7.7) · [软件发布](https://github.com/everlosttime/glassdesk-updates/releases/tag/v1.7.7)
