# 项目许可范围

PCL Living Fork 由不同来源、不同许可的代码与资产组成。

- **PCLLF 确认原创的独立源码文件**采用 [MPL-2.0](../LICENSE-MPL-2.0.txt)。范围逐文件列在 [MPL-COVERED-FILES.txt](MPL-COVERED-FILES.txt)，相应 C#／XAML 文件附有 MPL 通知与 SPDX 标记。
- **PCL 继承代码、混合来源文件及其资产**保留根目录 [LICENCE](../LICENCE) 与各文件已有声明。文件清单没有覆盖的内容，不能仅因位于此仓库就按 MPL 使用。
- **实际复用的第三方代码、二进制和生成代码**按 [manifest.json](manifest.json) 逐项记录，并保留 [texts](texts) 中的完整协议、版权及特别授权。CE 应用目录的 PCL 许可与 CE Core 的 Apache 许可分别保留。
- 对外提供包含 MPL 代码的程序时，应同时告知如何取得对应源码与协议；项目源码入口为 <https://github.com/DDZmumo/PCL-Living-Fork>。正式发行应使用对应提交或标签定位源码。

MPL 的文件范围不改变 PCL、第三方、商标、图标、字体或 Minecraft 资产的权利归属。MPL 正文没有附加限制或被修改；未加入 Exhibit B 的次级许可不兼容声明。

源码中的旧提交号、迁移来源、对照与第三方署名继续保留。对于清单中确认属于 PCLLF 原创的模块，原先笼统的“LF LICENCE”说明已明确为 MPL；PCL／CE／Microsoft 等外源代码通知不因此被删除。

## 尚需补充的证据

[manifest.json](manifest.json) 的 `unresolved` 以及 [审计说明](../docs/licensing-audit.md) 记录了未能确认的素材授权、Forge 安装辅助包许可和历史 GPL 组件对应源码问题。这些条目没有被擅自改为 MPL，也不因补充一份通用协议文本而自动获得授权。

新增依赖或复制代码时，应记录实际版本、来源路径、适用协议和修改情况；新增原创文件应加入 MPL 文件通知及清单。只参考接口、数据格式或行为，与实际复制实现，应在来源说明中区分。
