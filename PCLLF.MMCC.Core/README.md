# MMC metadata core

本目录是按 [实例兼容设计](../docs/mmc-instance-design.md) 独立编写的读取、组件解析、逻辑编译与定义绑定核心，使用 C# / `net10.0`。它接受宿主提供的条目描述与已解码的 UTF-8 元数据；不打开 ZIP、读取本机路径、联网、探测 Java、读取设置或账号、执行命令。没有 PCLCS、Main、WPF 或第三方包依赖。

调用 `MmcPackageReader.Parse(MmcPackageInput, MmcReadLimits?)`，取得不可变 `MmcParseResult`、来源摘要、原组件顺序、保留的字段、缺失元数据请求与结构化诊断。`Package != null` 仍可能伴随错误，调用者必须检查 `HasErrors`。**解析成功不表示已经形成可安装/可执行的配置。** Core 另提供编译与不可变绑定结果；实际路径、文件事务和启动会话的授权由宿主执行。

实现范围：

- 唯一实例根；可选 `instance.cfg`；不依赖 ZIP 目录条目的 `minecraft` / `.minecraft` 选择；源条目原始拼写保留。
- Windows 逻辑路径树：纯目录大小写别名允许并诊断；重复叶文件、文件/目录角色冲突、隐式父文件冲突、穿越、设备名和源包伪造的 LF 管理路径拒绝。路径最多 128 段、32767 字符，每段最多 255 字符；逻辑节点最多条目预算的两倍。源条目与元数据键累计最多 16 Mi 字符，逻辑文件及父目录前缀的构造累计最多 32 Mi 字符，在生成前缀前扣计，重复前缀也计入。这些是输入检查，实际路径授权和真实解压预算仍归宿主。
- 清单 formatVersion 1；组件 UID 唯一，保留 requested / cached / local effective version；important / dependencyOnly 不因 disabled 被静默移除。
- 本地 `patches/<uid>.json` 的内容 UID 校验，兼容 `fileId`，严格检查重复 JSON 键、字段类型、依赖/冲突和已知能力。启用的未知关键能力拒绝；禁用来源保留并告警，启用前须重新验证。
- 保留 mainClass、mainJar、assetIndex、参数数组、libraries / +libraries / mavenFiles、Java 离散候选与推荐名。嵌套下载、rules、extract 字段有显式读取表；数组 token 不再分词或去重。有嵌套库配置时 parser 附 `MMC_DEFERRED_LIBRARY_SEMANTICS` 信息诊断：该读取阶段仅检查形状和注册字段；后续 compiler 与宿主已分别接入平台筛选、classifier 决策及受限解压。不能只据 parser 的 `HasErrors=false` 断言完整执行可用；字段和阶段范围见[当前兼容范围](../docs/mmc-compatibility-scope.md)。
- 基础依赖检查包括精确版本、循环、禁用提供者、冲突和缺失请求；已有组件保持作者顺序。
- 元数据逐项与累计、条目/组件/依赖/深度/声明制品预算。源文本摘要是传入文本的 UTF-8 SHA256，不冒充原始 ZIP 或未解码原件摘要。

cfg 只支持明确的 scalar 子集，未宣称完整 Qt/QSettings：没有 ConfigVersion 时按旧 MMC 转义/条件引号规则读取，并接受常见 `[General]` 标记；1.1–1.3 读取 Qt scalar 引号、转义、UTF-16 十六进制转义与续行。未知版本、重复键、无效布尔、活动未知配置、未实现的 list / QVariant 类型明确拒绝。旧格式悬空转义、非 General 分组及重复键采用 LF 保守拒绝策略，不复制上游容错缺陷。

活动 PreLoad / PreLaunch / Wrapper / PostExit、Env，以及尚未适配的 NativeWorkarounds / GameTime 覆盖拒绝。活动命令的旧别名 `OverrideLaunchCmd` 只在新键缺失时生效；非活动值保留。宿主继承行为仍必须由后续预览检查，不可因为源 cfg 没有命令就跳过宿主有效设置验证。

`MmcComponentResolver.Resolve` 根据固定来源的元数据快照返回缺失批次，宿主取得内容后再次调用；支持推荐索引、依赖插入和有边界的自动依赖重选。`MmcProfileCompiler.Compile` 生成带来源的入口、参数、Java 策略与逻辑制品计划，保持作者顺序和本地覆盖。logging 是独立辅助制品，主 JAR 位于普通 classpath 之后，下载专用制品不混入 classpath；平台/native 与 Java 候选按显式兼容配置计算。

Core 本身仍不执行远端获取、制品/原生库物化、文件事务、会话、安装、启动或修复。逻辑编译结果须由宿主校验路径、物化摘要并通过事务与会话门禁后使用，不能仅据 `MissingMetadata` 为空或编译通过宣称游戏已可运行。当前宿主已接入受管理 MMC 导入、启动和修复；GTNH 2.9.0 Java 8 与 Java 21 的真实世界闭环及未测范围见[验收记录](../docs/mmc-gtnh-acceptance.md)，其余功能状态见[全任务清单](../docs/mmc-completion-ledger.md)。

## 机制依据与独立实现

本目录新增 C# 类型、数据结构、校验流程与测试为本轮独立编写。只参考以下格式/机制，不包含 Prism/HMCL/CE 实现的复制、逐行翻译或机械改写；这不是正式 clean-room 或整套产品许可审计声明。贡献记录以 Git 历史为准。

| 机制来源 | 本目录设计与验证 |
| --- | --- |
| [MultiMC JSON Patches](https://github.com/MultiMC/Launcher/wiki/JSON-Patches) | UID、补丁引用与字段背景；文档不作为完整现代语义依据 |
| [Prism OneSixVersionFormat，固定 899d2f3](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/OneSixVersionFormat.cpp) | 字段格式参考；独立 capability table 与不可变 JSON 值，MMC-01/02/03/20 |
| [Prism Component，固定 899d2f3](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/Component.cpp) | 源 enabled/dependencyOnly/important 状态与本地覆盖，MMC-02 |
| [Prism INIFile，固定 899d2f3](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/settings/INIFile.cpp) | 确认旧格式与 ConfigVersion 分流；自有 scalar 状态机和保守拒绝策略，MMC-01/20 |
| [Windows 文件命名规则](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file) | 普通设备名、COM/LPT 上标数字与 CONIN$/CONOUT$ 防护；未借此替代宿主路径边界 |

`tests/PCLLF.Migration.Tests/MmcProfileTests.cs` 使用自有小型输入，实际 ProjectReference 由集成分支接入。24 个聚合用例覆盖首行/转义、可选 cfg、目录别名、重复/路径/角色冲突、JSON 歧义、local 版本/禁用、依赖、未知能力、累计路径/元数据预算与结果不可变。没有提交 GTNH 包、上游源代码或完整游戏元数据。

P0 小边界实验先在忽略的 artifacts 中验证内容输入、来源摘要、有序不可变输出与宿主补齐回传，不访问玩家数据。之后才建立本项目。正式 parser 已只读检查本机两份官方 GTNH 2.9.0 原包：Java 8 的 18148 条目 / 3 组件 / 3 个缺失元数据请求；Java 17–26 的 18157 条目 / 5 本地组件 / 0 请求；各有 6 个纯目录别名告警，无解析错误。该记录仅证明源读取，未验证下载、最终合成、安装或游戏。原包身份与后续完整验收记录由宿主的 P0 样本基线维护。

## 许可边界

本目录新原创源文件以 `SPDX-License-Identifier: MPL-2.0` 标识，适用 [LICENSE](LICENSE)。完整文本取自 [Mozilla 官方纯文本](https://www.mozilla.org/media/MPL/2.0/index.txt)，LF 原件及 Git blob 的 SHA256 为 `3f3d9e0024b1921b067d6f7f88deb4a60cbe7a78e76c64e3f1d7fc3b779b9d04`；Windows checkout 可能转换为 CRLF，核对该摘要时须先归一换行。

宿主、既有测试工程及其改动保留各自现行许可；根 LICENCE / NOTICE 未被本目录替换。第三方输入/资产不会因被读取而变为 MPL。若未来需要复用上游代码，须先解决逐文件授权与组合分发条件，不得在本目录直接改标 MPL。对外分发此 Core 的二进制（包括 single-file 内嵌）时仍需提供对应 MPL 源码、修改和必要声明的取得方式，见 [官方 FAQ](https://www.mozilla.org/en-US/MPL/2.0/FAQ/)。
