# 来源与许可审计

审计覆盖当前生产工程、实际 NuGet 解析结果、手动引用 DLL、嵌入 JAR／原生库／字体／图标、迁移来源和单文件发行包。审计日期：2026-11-08。此文是可复查的工程清单，不把“源码可见”“写了署名”或“放了一个许可证文件”视为已经解决全部授权问题。

## 原创代码的 MPL 范围

维护者明确选择 MPL-2.0。已确认的 PCLLF 原创独立模块逐文件列在 [清单](../licenses/MPL-COVERED-FILES.txt)，对应源码附有 SPDX 标记和 Mozilla Exhibit A 通知；协议原文位于 [LICENSE-MPL-2.0.txt](../LICENSE-MPL-2.0.txt)。范围包括依赖检查与图谱、整合包更新事务、存档备份、环境还原点、测试副本、键位与个人设置同步、资源管理、收藏存储和 LF 自有更新／工具模块。

继承的 PCL 主程序、混合来源文件、原控件和迁移适配不整体改为 MPL。MeloongCore WPF、CE、Microsoft/CoreCLR 等实际外源实现保留原协议。图片、头像、商标、Minecraft 资源、字体和第三方图形不因代码的 MPL 标记而取得新授权。

当前 MPL 清单含 189 个原创源码／维护脚本文件，包含 LF 新增的工具箱切换流程和资源直接删除／旧回收站清理模块。`PCLCS/Toolbox/LegacyDailyLuck.cs` 的算法及结果文案来自对维护者提供的 PCL 2.99 程序的静态分析，保留原 PCL 许可并排除在该清单之外；具体二进制身份及未移植的主题解锁行为见 [来源记录](../modern/Main/SOURCE_PROVENANCE.md)。

PCL Symbio Edition 仅在用户触发工具箱操作后从其官方 Release 另行下载，不随 LF 的 EXE／ZIP 分发；外部程序不因下载入口或启动流程而变为 LF 原创代码。用于分析的 PCL 2.99 程序及提取依赖同样只保留在忽略目录，不进入发行包。

## 已核实的组件与补充

完整、机器可读的对应关系见 [manifest.json](../licenses/manifest.json)；完整许可文本见 [texts](../licenses/texts)。[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) 为面向分发的索引，并在关于页提供离线许可全文。

| 范围 | 核实依据 | 处理 |
| --- | --- | --- |
| PCL 基线 | `5ef81e490ae46b1100d61e33d0183094c7a80ca8`、根 `LICENCE` | 保留原作、权利人、赞助、独立第三方声明及许可原文；不以 MPL 覆盖。 |
| PCL-CE 参考及选择性适配 | 固定 `1a7653127d578efdb2a9388999a3fdc67d9e49d5` | 保留应用目录 PCL 许可和 Core 根 Apache-2.0，不能把 CE 全部概括为 Apache。单纯结构／行为对照和实际复用继续由文件头及 `SOURCE_PROVENANCE.md` 区分。 |
| MeloongCore Shared／WPF 适配 | 子模块固定 `c774ae4c85fd94e0eaa171db21e78a65870df19b` | 保留 Apache-2.0 全文、来源与修改说明；不改子模块许可。 |
| .NET bundle 读取器 | `modern/ThirdParty/dotnet-runtime-bundle/source-manifest.json` | 保留 Microsoft/.NET MIT 声明和精确来源清单。 |
| CoreCLR 数值转换 | `LegacyPackMetadataNumberParser.cs` 文件头与固定 CoreCLR 提交 | 保留已有 MIT 全文；不把该移植文件当作纯原创。 |
| Microsoft Reference Source | 阴影、路径等来源注释；原仓库 MIT 文件 | 补充 MIT 全文与版权。 |
| uye 剪贴板修复 | LF 历史文件保留的作者特别授权 | 保留“仅此文件 Apache-2.0”的授权原文，不能将整个 MAA 项目概括为 Apache 或将本文件错误归为 AGPL。 |
| Markdig 1.6.0 | 实际 NuGet `.nuspec` 与已有许可 | BSD-2-Clause；保留既有全文并纳入统一索引。 |
| Newtonsoft.Json 13.0.3 | NuGet 包内 `LICENSE.md` | MIT，补充离线全文。 |
| Ookii.Dialogs.Wpf 5.0.1 | NuGet 仓库提交 `edf9e7a...` | BSD-3-Clause，保留贡献者及 C. Augusto Proiete 通知。 |
| NAudio 1.7.3.0 | 实际 DLL 版本与原关于页固定来源 | 使用该历史版本 Ms-PL，不能套用新版本协议。 |
| CacheCow 三个 DLL 2.13.1.0 | DLL 版本与 NuGet 包仓库提交 `d979a288...` | MIT，补充原作者 Ali Kheyrollahi 通知与全文。 |
| ThrottleDebounce 2.0.2 | NuGet 仓库提交 `1c11c61...` | Apache-2.0，补充全文。 |
| Imazen.WebP 10.0.1.0 | DLL 与 wrapper 项目 | 保留 wrapper MIT；与原生 libwebp 分项列出。 |
| 原生 libwebp64.dll | 实际嵌入资源，独立于 .NET wrapper | 补充 Google BSD 条款及 PATENTS；精确编译版本未记录。 |
| Java Launch Wrapper | JAR 内 `oolloo/jlw`、`libjlw-*-1.4.4.dll` | 保留 00ll00 MIT 全文；JAR 当前没有内置 LICENSE，统一分发通知覆盖此缺口。 |
| LWJGL Unsafe Agent | JAR 内 `org/glavo/lwjgl` | Apache-2.0；当前 JAR 没有额外打包 ASM 类，未据名称新增一个未分发的 ASM 组件。 |
| System.Net.Http.Formatting 5.2.9.0 | Microsoft.AspNet.WebApi.Client 5.2.9 原始 NuGet 包 | 二进制包明确指向 `NET_Library_EULA_ENU.txt`，补充该原文，不直接套用源码仓库协议。 |
| System.Runtime.Caching 10.0.0 | 实际 NuGet 解析结果与包内 notices | MIT 与包内完整 THIRD-PARTY-NOTICES 一并保留。 |
| Tabler 两个补充图标 | 固定提交与 `Tabler-Icons-LICENSE.txt` | 保留 MIT；不将原有 iconmonstr／Iconfont 图形归入 Tabler。 |
| PolySharp 1.15.0 | NuGet source generator | 保留 MIT，注明构建期及生成兼容代码；不把没有分发的生成器 DLL 冒列为运行时组件。 |

## 未完成的授权／出处证据

以下项目不能靠新增 MPL 或复制一个相似项目的 LICENSE 解决，保留原署名并明确待核实：

1. **Forge installer bootstrapper**：内嵌 `forge-installer.jar` 含 `com.bangbang93.ForgeInstaller`。对应公开仓库为 [bangbang93/forge-install-bootstrapper](https://github.com/bangbang93/forge-install-bootstrapper)，审计时根目录未见明确 LICENSE，JAR 内亦无许可。需要适用的授权依据与准确版本来源。
2. **历史 HMCL Transformer**：继承 JAR 的 ZIP 时间为 2020-05-03，已有关于页注明 GPLv3 或更新版与附加条款。已补全文本，但仍须确定该二进制的准确对应源码及提供方式；仅指向当前分支的源码不是精确版本证据。
3. **字体**：两处 `Font.ttf` 字节相同，且继承自原 PCL 资源。其 name 表只有 `PCL English`，没有版权、许可或许可 URL。保留 PCL 资产归属，待补字体专属出处，不将其标为 MPL／OFL。
4. **DragHelper**：保留 CSDN 文章出处；本次无法取得该文章的适用代码许可内容，不能凭网站通常使用的协议作推定。
5. **Iconfont、iconmonstr 与其他图像**：iconmonstr 是自定义资产条款，不是 OSI 开源代码协议；Iconfont 还需定位到具体图形／创作者。头像、品牌背景、帮助包与 Minecraft 图像分别保留归属，不笼统归入 MPL。

用于 BUG 验证、仅位于忽略目录的 Mowzie’s Mobs／GeckoLib 文件没有进入程序或发行 ZIP，因此没有把它们误列为启动器分发依赖。

## 发行范围与维护

当前通用公开包为 framework-dependent .NET 10 单文件程序。运行时 DLL 以实际 `LivingPCL.deps.json` 为准；继承资源通过 `LF.Resources.schema.json`、Main 的资源声明和 Core 的嵌入资源核实。仅用于构建的 ILLink、Roslyn、SDK／reference pack 与由用户另行安装的 Desktop Runtime，不能与程序已捆绑的运行时 DLL 混为一谈；若以后改发自包含运行时，应重新核对微软的运行时 notices。

关于页的离线全文应包含项目范围、MPL、各组件索引、实际许可全文和未确认项。发布对应源码应按提交／标签定位，许可声明不能替代实际源码分发义务。`scripts/Check-Licenses.py` 用于检查文件标记、清单、离线通知同步和已解析运行时依赖覆盖。
