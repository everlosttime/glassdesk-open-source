# GlassDesk 1.6.0 完整功能源码同步

本次取消此前“不公开信息栏”的范围限制，完整同步 1.6.0 的信息栏、公历/农历切换日历、通知入口、控制中心、设备选择、媒体控制、系统状态与外设电量读取，以及全部相关宿主、设置和新手教学集成。

Dock、启动台、快捷键焦点框修复、官网二维码下载入口、附近传输、原生渲染与可选奶蛙组件继续保留。完整导出产品 src、native、tests、tools、build、packaging、components-src 和固定 third_party 依赖；产品源码不做功能删减。

现有许可条文、历史 Apache 权利及各第三方组件自身许可继续保留。独立电量组件保留 AGPL-3.0-only、HeadsetControl 保留 GPL-3.0、Naiwa 保留 MIT。本次新增文件的逐文件来源和哈希重新记录，不合入私有 Git 历史或用户数据。

1.6.0 完整源码正式发布于本仓库 Releases；安装包与更新通知通过独立的 glassdesk-updates 仓库发布。此前 1.4.0 源码 Release 保留为历史快照。

两个历史验收项目（UiAcceptance、DesktopAcceptance）的 Windows 目标框架对齐至当前产品目标，修复完整解决方案的框架引用兼容错误；不改变产品功能源码。
