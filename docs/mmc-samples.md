# MMC P0 样本与离线审计

记录日期：2026-11-08。本文是[实例设计](mmc-instance-design.md)的开发证据，**不是 MMC 导入、启动或修复验收通过声明**。本轮仅完成完整本地 ZIP 的 SHA256、中央目录与有界元数据读取；未运行 Prism、Java 或 Minecraft。

## 固定输入与来源

用户提供了 `D:\User\Downloads` 中两份 2.9.0 原包；审计只读打开这些文件，没有修改它们。[GTNH 官方发行历史](https://www.gtnewhorizons.com/version-history/)列出 2.9.0 于 2026-10-31 发布，并分别链接两种 MultiMC/Prism 客户端。下列字节长度与官方 HTTP 响应长度相同；SHA256 是本地完整文件的实测值，不是官方签名或官方发布的校验值。

| 样本 | 完整文件字节数 | 本地完整文件 SHA256 |
| --- | ---: | --- |
| [Java 8 ZIP](https://downloads.gtnewhorizons.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_8.zip) | 732488324 | `ae94a01a5367065f71ac9b5150966182a5b55762c841749fcb3d603ab5ed78af` |
| [Java 17–26 ZIP](https://downloads.gtnewhorizons.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_17-26.zip) | 747848389 | `6d0ebe7e6be18fb74b4fc920916cc5710e142a3f885072d324da05244d398b16` |

| 观测 | Java 8 包 | Java 17–26 包 |
| --- | ---: | ---: |
| ZIP 条目 / 非目录文件 | 18148 / 17768 | 18157 / 17775 |
| ZIP 声明解包总字节数 | 883235748 | 899495677 |
| 已读取元数据文件 / 字节数 | 2 / 688 | 7 / 71947 |
| 本地补丁 | 0 | 5 |
| 实例根 | `GT New Horizons 2.9.0` | `GT New Horizons 2.9.0` |
| 游戏根 | `.minecraft` | `.minecraft` |

两包的条目数、单份与累计元数据体积均低于设计初始预算；这是中央目录预估与实际已读元数据的证据，不是全部 payload 解压、native JAR 输出量、运行视图峰值或磁盘空间验收。工具没有读取全部 payload 来校验每个 CRC。

## 实际启动声明的差别

Java 8 包只声明 `org.lwjgl:2.9.4-nightly-20150209`、`net.minecraft:1.7.10`、`net.minecraftforge:10.13.4.1614`，没有本地补丁。它需要取得并固定对应远端组件元数据后，才能确定完整入口、Java 策略和库；不能用文件名中的“Java 8”代替读取组件声明。

现代包的五个组件均有本地补丁，作者顺序为 LWJGL3 → Minecraft → Early Classpath → Forge → launch args：

| UID | 有效本地补丁版本 | 实测机制 |
| --- | --- | --- |
| `org.lwjgl3` | `3.4.3` | `libraries` 144 项，含 downloads/rules；与 `org.lwjgl` 冲突；`volatile` 为元数据提示 |
| `net.minecraft` | `1.7.10` | `compatibleJavaMajors=[17,21,25,26]`，以及推荐运行时名、mainJar、assetIndex、游戏参数 |
| `me.eigenraven.lwjgl3ify.forgepatches` | `3.0.42` | `libraries` 中本地早期 JAR 与 `+jvmArgs`；清单未提供 version，本地补丁提供版本 |
| `net.minecraftforge` | `10.13.4.1614` | `com.gtnewhorizons.retrofuturabootstrap.Main` 与 `+tweakers` |
| `me.eigenraven.lwjgl3ify.launchargs` | `3.0.42` | 再覆盖为 `com.gtnewhorizons.retrofuturabootstrap.MainStartOnFirstThread`；`FirstThreadOnMacOS` |

本地 JAR 位于实例根的 `libraries/lwjgl3ify-3.0.42-forgePatches.jar`。这里没有游戏根 `.minecraft/libraries` 文件；两种角色的通用分离仍由自有测试覆盖。上述数据来自这次发行包，不以最新 lwjgl3ify 构建模板替代它。现代候选是四个离散主版本，不能扩展成 17～26 连续范围；本轮尚未实际选择或运行其中任何 Java。

两份 cfg 都含 `InstanceType`、`JoinServerOnLaunch`、`OverrideCommands`、`OverrideConsole`、`OverrideGameTime`、`OverrideJavaArgs`、`OverrideJavaLocation`、`OverrideMemory`、`OverrideNativeWorkarounds`、`OverrideWindow`、`iconKey`、`name`、`notes`。没有 `ConfigVersion`；`OverrideCommands=false`，没有 PreLoad/PreLaunch/Wrapper/PostExit/Env 行。这次工具只记录 cfg 的存在和 UTF-8 字节，不充当 Qt cfg 行为解析器。

## 目录别名暴露的通用输入策略

两份真实包均有六个目录大小写别名组，且没有两个文件映射到相同的 Windows 目标路径。三个父目录别名分别是：

- `Witchery++[WitcheryExtras]` / `Witchery++[witcheryextras]`。
- `Blood Magic_ Alchemical Wizardry[alchemicalwizardryBooks]` / `Blood Magic_ Alchemical Wizardry[alchemicalwizardrybooks]`。
- `Thaumcraft Mob Aspects[ThaumcraftMobAspects]` / `Thaumcraft Mob Aspects[thaumcraftmobaspects]`。

它们都在 `.minecraft/config/txloader/load/` 下，另有三个对应的 `lang` 子目录组。原来“任何大小写差异都拒绝”的规则会误拒绝这两份包。审计器现在构造 Windows 目标树：纯目录别名允许共用逻辑节点，报告保留各原始名称；任意两个文件指向同一目标（即使字节相同）、文件占用目录/父路径、路径穿越及 Windows 特殊名仍拒绝。这是通用目标角色规则，没有整合包或模组名特判。

## 可重复的本地工具

工具[AuditArchive.py](../scripts/mmc/AuditArchive.py)仅使用 Python 标准库，不联网，不执行包内内容，不抽取游戏数据，不访问玩家目录。它读取完整文件计算 SHA256，建立一次 ZIP 索引，限制条目/声明体积与元数据实际读取体积，检查唯一根、重复文件目标、目录角色、链接和保留的 LF 管理路径，再输出组件、补丁字段、Java 声明和来源摘要。未知字段/执行器会列出，`compatibilityStatus` 始终为 `not-evaluated`。

树构建前限制单条路径 32767 个 UTF-16 单元、深度 128、单片段 255；所有原始路径累计限制 16 Mi 单元。隐式目标节点最多 200000，所有目标路径与父前缀候选累计限制 32 Mi UTF-16 单元，重复前缀也计数，在构造前缀字符串前扣除预算，避免零字节 payload 配合深路径占用巨量树内存。元数据路径和目录角色查找使用同一比较 key，原始大小写仍保留，不会漏读 `Patches/X.JSON`、`Instance.CFG` 或漏判占目录位置的 `Minecraft` 普通文件。设备名拒绝包含 `CONIN$`、`CONOUT$` 及 `COM/LPT¹²³`。

Python 工具采用显式受限的 `lf-audit-python-simple-uppercase-v1` 比较配置，JSON 同时记录 Python Unicode 表版本；它不是完整的 .NET/NTFS Unicode 等价判定器，也不授权后续提取。已用 .NET 10.0.14 `OrdinalIgnoreCase` 对照固定向量：不把 `ß/ss`、`ß/ẞ`、`ı/I`、`ſ/S`、`K/K`、`ﬃ/ffi` 或预组合/分解的 `é` 合并；允许 `σ/ς`、`µ/Μ` 等已核对的单字符大写关系，不做 NFC/NFKC。未登记的多字符大写映射（例如希腊 `ᾳ`）明确拒绝并在 JSON 标为 `rejected`，不猜测 title/casefold 结果。生产路径判定以 Core 的 .NET `OrdinalIgnoreCase` 和宿主真实路径验证为准。

输出必须是新文件，工具拒绝覆盖已有报告、原包路径及指向原包的 hardlink/symlink；最终通过独占创建再次防止检查后出现的目标被覆盖。重复审计时选择新的报告名，保留旧证据。

在构造 Python `ZipFile` 索引前，先读取最多 65557 字节的 EOCD 尾部，限制中央目录为 16 MiB，并以固定 46 字节 CDR 头逐条扫描、跳过有界 extra/comment；实际扫描计数也受 100000 限制，不能仅信任 EOCD 的计数。单盘 ZIP64 支持固定 44 字节 EOCD payload 并验证 locator、长度、偏移和普通/64 位声明一致性；多盘、扩展 ZIP64 记录、SFX 偏移调整、目录数字签名及尾部附加数据明确拒绝。`zipPreflight` 记录实际扫描量；畸形输入测试确认拒绝发生于 `ZipFile` 构造之前。

只有直接位于 `patches/<uid>.json` 的补丁参与本地组件匹配；目录名及 `.json` 扩展名按比较配置查找，文件 stem 必须与 `uid/fileId` 精确一致。`patches/backup/dep.json` 等嵌套 JSON 可作为源元数据记录，但不能代替 `dep` 组件或消除远端元数据请求。所有 `PCL/Mmc*` 保留节点及其后代（包括节点本身为文件或空目录）、`PCL/InstanceDefinition.json` 均拒绝外来包注入。

```powershell
python -X utf8 scripts/mmc/AuditArchive.py 'D:\User\Downloads\GT_New_Horizons_2.9.0_Java_8.zip' --source-url https://downloads.gtnewhorizons.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_8.zip --output artifacts/mmc-baseline/java8.audit.json
python -X utf8 scripts/mmc/AuditArchive.py 'D:\User\Downloads\GT_New_Horizons_2.9.0_Java_17-26.zip' --source-url https://downloads.gtnewhorizons.com/Multi_mc_downloads/GT_New_Horizons_2.9.0_Java_17-26.zip --output artifacts/mmc-baseline/java17-26.audit.json
python -X utf8 scripts/tests/Test-MmcAudit.py
```

[离线回归](../scripts/tests/Test-MmcAudit.py)全部使用现场生成的自有微型 ZIP，覆盖缺 cfg/无目录条目/嵌套根/多根、两游戏树与两库角色、目录别名与文件冲突、路径穿越/链接/特殊名、保留管理路径、JSON 重复键与格式、组件状态、离散 Java、未知能力、预算，以及 CLI 不覆盖原包。原包、原始元数据和 JSON 证据放在 Git 已忽略的 `artifacts/mmc-baseline`，不作为 MPL Core 自有代码或 fixture 提交。工具为独立 LF 宿主工具，保留仓库现行许可；未移植上游实现。

## 后续门槛

P0 仍需固定 Java 8 包远端元数据、实际 Prism/OS/Java 版本与脱敏解析对照、正常和失败链的性能基线。P1/P2 的解析或编译测试不能替代 P3 的配置协调、事务、进程会话和库视图保护；P5 两种包的导入、主菜单、世界、退出、修复、再次启动全部未测。不得把本次 `metadata-audited` 解释为产品已支持 GTNH。

2026-11-09 补充：上段是本次 11-08 离线审计的历史范围；后续两包的实机闭环见
[GTNH 验收记录](mmc-gtnh-acceptance.md)。三份 Java 8 历史元数据已按来源/内容摘要
锁定，并已取得固定 Prism 提交实际 C++ 的
[原生解析/计划子集证据](mmc-prism-differential.md)。该工具不运行完整 Prism
resolver、界面、账号或游戏，不能把子集对照扩称全应用等价。
