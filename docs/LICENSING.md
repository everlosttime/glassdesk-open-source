# 授权范围与历史许可

本仓库公开 GlassDesk 1.4.0：最后一个原本不含右上角信息栏的正式版本。

## 当前非商用授权

作者有权授权、此前未按其他条款公开的原创新增内容和修改，以及本次新写的公开说明，按 **PolyForm Noncommercial License 1.0.0** 提供。完整英文条款见根目录 LICENSE 的 Part A；中文说明仅帮助理解。

- 允许在许可范围内学习、研究、修改功能、修复 Bug、Fork、提交 PR 和非商业分发。
- 对受此非商用条款约束的内容，本许可不授权售卖、用于商业项目、商业服务，或包装成自己的收费软件。商业使用须事先另行取得相关权利人的适当授权。
- PolyForm 原文明确允许部分个人用途，以及慈善、教育、公共研究、公共安全或卫生、环保和政府机构的用途。适用这些明确允许的用途时，以许可原文为准；本中文说明不额外收紧标准许可。
- 分发时保留许可、Required Notice 行、历史版权声明和第三方声明；自己的修改应明确标注。项目名称和图标不构成冒充作者或官方版本的授权。
- 本次非商用条款不要求所有修改必须提交回来，也不强制所有非商业衍生版本公开源码；欢迎自愿通过 PR 回馈改进。

## 历史 Apache 授权继续有效

此前公开的 GlassDesk 源码采用 Apache License 2.0。删除旧 Release、标签或重建 main 历史均不会撤销已经授出的权利，也无法删除他人的 Fork、本地副本或平台缓存。依原条款取得的历史代码仍可按 Apache-2.0 使用，包括符合其条件的商业使用。

本快照复用了历史代码。不能将历史 Apache 部分整体声明为“禁止商用”；包含新修改的同一文件也可能同时含有沿用的 Apache 部分。LICENSE 的 Part B 保留完整历史条款。本次公开快照整体没有提供“全部可选 Apache 授权”的双许可选项。

`licensing/FILE-LICENSES.json` 逐项记录导出文件、源提交 Git blob、SHA-256 和与此前公开历史的比较范围：

- `historical-apache`：与此前公开的同路径内容完全相同，保留历史 Apache 授权。
- `original-changes-noncommercial-inherited-apache`：相对历史同路径文件有变动，仅合资格的原创新增/修改适用非商用条款，沿用部分仍保留 Apache 授权。
- `original-addition-noncommercial`：此前公开历史中没有同路径文件，合资格的原创内容适用非商用条款；任何第三方来源内容仍按其自身许可处理。
- `third-party-notice-see-file` / `Unicode-3.0`：第三方许可及数据声明，依对应原文。

这份逐文件比较记录不是逐行版权鉴定，也不会覆盖其他来源的现存授权。上游版权和许可声明优先适用于其相应部分。

## 第三方组件

AndroidLiquidGlass 的改写素材保留 Apache-2.0 声明；Unicode Unihan 数据保留 Unicode License V3；.NET/WPF、Windows SDK 和 Inno Setup 资料保留各自许可。详见 NOTICE 与 third-party 目录。第三方商标和用户自行导入的应用图标不因本项目授权而获得额外使用权。

## 贡献

请先阅读根目录 CONTRIBUTING.md。贡献者保留自己代码的版权；提交 PR 不等于转让版权，也不自动授予维护者绕过非商用限制的商业使用权。
