# MMC 当前兼容范围

以下按当前实现区分“已处理的格式”“当前拒绝的能力”和“验收范围”。2026-11-09 用户明确将
包内命令、环境变量、Wrapper 等高级兼容放到二期；一期收口不等于这些能力已实现。
总体状态与证据见[完成清单](mmc-completion-ledger.md)，后续安排见[二期待办](mmc-phase2-todo.md)。

## 格式与组件

| 输入 | 当前处理 | 依据 |
| --- | --- | --- |
| `mmc-pack.json` | `formatVersion=1`；定位唯一根，保留组件顺序；根歧义、重复 UID、未知关键字段拒绝 | [包读取器](../PCLLF.MMCC.Core/Parsing/MmcPackageReader.cs) |
| `instance.cfg` | 可缺失；旧 MMC scalar、Qt ConfigVersion 1.1–1.3 scalar，首行、转义、引号与续行有专用解析 | [cfg 读取器](../PCLLF.MMCC.Core/Parsing/MmcConfigurationReader.cs) |
| 启用状态 | 综合 `disabled`、`important`、`dependencyOnly`，保留无效来源；不能仅按文件名或单个禁用标志跳过重要组件 | [包读取器](../PCLLF.MMCC.Core/Parsing/MmcPackageReader.cs) |
| 本地补丁与依赖 | 本地优先；UID／旧 `fileId` 核对；精确 `equals`、建议 `suggests`、缺失批次与推荐索引、依赖插入／重选、循环与冲突诊断 | [解析器](../PCLLF.MMCC.Core/Resolution/MmcComponentResolver.cs) |
| 不认识的能力 | 活动关键字段／trait／下载规则错误；无效来源中的未实现能力保留并告警，启用时重新验证 | [能力表](../PCLLF.MMCC.Core/Parsing/MmcPatchReader.cs) |

cfg 不是完整的 QSettings 实现。未知 ConfigVersion、非支持分组、重复键、非法布尔、
尚未解码的 list／QVariant 等输入会明确拒绝。描述字段被保留不等于执行了其行为。

## 启动字段与制品

| 字段／机制 | 当前处理 |
| --- | --- |
| `mainClass`、`mainJar` | 按有效组件顺序合成最后的非空入口；主 JAR 放在普通 classpath 之后，保留来源 |
| `assetIndex`、`assets`、游戏版本与类型 | 从 `net.minecraft` 取得；其他组件的游戏元数据保留并诊断；资源由宿主验证、物化 |
| `minecraftArguments` | 采用固定 `prism-v1` 的 legacy 字面空格拆分，再展开变量；引号／反斜杠／tab 不套用旧 PCL shell 分词 |
| `+jvmArgs`、`+tweakers` | JVM 数组元素保持单 token；Tweaker 按顺序合成；不经过旧参数去重再次改写 |
| `libraries`、`+libraries`、`mavenFiles` | 合成有序库与制品来源；同库版本替换保持原槽位；`mavenFiles` 只下载，不混入 classpath |
| `MMC-hint: local`、`MMC-filename` | 本地平面文件名与共享 Maven 路径分别处理；未知同名文件不成为修复来源 |
| 下载 | 支持声明 URL／绝对 URL、artifact／classifier 下载信息、摘要、大小；下载后测量并固定实际字节 |
| `natives`、`${arch}`、`extract.exclude` | 按实际 Java 架构与位数选择，受限解压到 owned 视图；拒绝穿越、链接、冲突及超预算输出 |
| `rules` | 固定 `prism-v1` 顺序与默认动作，按 `os.name`／平台 classifier 匹配；`os.arch`／`os.version` 保留但不应用，并给诊断；未知条件（如未注册的 `features`）拒绝 |
| `logging` | 支持当前直接描述符的 `log4j2-xml` 文件、下载与 `${path}` 参数绑定，作为辅助制品；不声称接受任意 Mojang JSON 包装形状 |
| `compatibleJavaMajors` | 保留离散正整数候选，再与宿主硬约束过滤；缺失／空声明、声明无解、本机无适用 Java 分别处理 |
| `compatibleJavaName` | 推荐运行时名称，不代替兼容候选，不按 GTNH 名称或 MC 版本硬编码选择 |
| traits | 当前注册 `FirstThreadOnMacOS`、`texturepacks`、`no-texturepacks`；未知活动 trait 拒绝。首线程参数仅在对应 OS 生效 |

字段语义见[编译器](../PCLLF.MMCC.Core/Planning/MmcProfileCompiler.cs)和
[制品规则](../PCLLF.MMCC.Core/Planning/MmcArtifactRules.cs)。包内实例 `libraries`、游戏子目录的
`libraries` 与共享缓存是不同角色；执行绑定经过宿主路径验证及 owned 物化，不能把三个目录合并。
读取、编译通过也不能替代下载校验、发布事务、会话门和实际游戏验收。

## 二期高级兼容

活动 `PreLoadCommand`、`PreLaunchCommand`、`PostExitCommand`、cfg `WrapperCommand` 与有效
`OverrideEnv / Env` 已按 [阶段契约](mmc-phase2-command-contract.md) 与 [设计 7.1](mmc-instance-design.md#71-cfg-的有效行为能力表) 接通；
同时保持 `OverrideGameTime`、`OverrideNativeWorkarounds`、`@Variant` 等未解码 Env、`MMC_CFG_COMMAND_CREDENTIAL`
及重声明 `TEMP`/`TMP`/执行通道的环境变量拒绝。非活动原值（如 `OverrideCommands=false`／`OverrideEnv=false`）继续保留但不生效；
`WrapperCommand` 与 LF `JavaWrapper`／`DirectMain` 按内外组合而非替代方式协作；主动包内命令与主动 `OverrideEnv/Env` 在可移植导出中明确拒绝，避免静默丢失运行要求。
宿主中玩家手工设置的全局／实例启动前命令（`LaunchAdvanceRun`／`VersionAdvanceRun`）继续在包阶段命令之外按原有次序独立运行。
`PermGen` 与 console 覆盖保持原始声明与差错/差异诊断，导入经 `ModMain.Hint` 直达用户，导出写入明确调整记录，永不替换为 Metaspace 或启动器窗口可见性。

MMC 专用 Auth／Nide 认证代理已接通 owned 运行文件与 Main 登录结果绑定，
实际自编 agent JVM premain、取消排空及退出清理已通过；真实认证账号与外部服务仍未验收，
不能用受控程序测试或普通微软／离线账号的通过来代替。

## 其他当前边界

MMC 实例设置使用独立的侧栏工具页：“组件与补丁”接入 MMC 组件编辑，
“MMC 实例工具”集中提供 ZIP 更新、MMC 导出、环境记录、克隆、同卷移动及平台来源与更新。
概览页原有六个 MMC 重复按钮已移除；普通版本继续使用原来的修改、导出、还原点与克隆页面。
识别依据已安装的管理记录；包名、MC 版本或零散 `mmc-pack.json` 不决定管理方式。
需要修复、操作未完成、格式不支持或原管理记录暂时不可读时，保留受限入口；
旧页面刷新不能接管同一目录中被替换的实例，需重新选择。

- 格式版本与未知关键语义超出注册能力时拒绝，不自动套用普通 Forge 安装覆盖补丁。
- 原版／普通 PCL 实例不自动写入 MMC ID；旧实例仅通过主动选择原包创建管理副本。
- 本地 ZIP 与在线更新使用已声明的游戏根；游戏根切换、普通作者文件与 mapped asset 的历史角色迁移须有显式迁移，当前保持拒绝。
- 实例移动已有同卷入口；跨卷与实验 Relauncher 没有已验收支持结论。
- 永久清理仅用于已完成隔离的实例，要求本地 NTFS／TxF；其他环境保留隔离与恢复，不回退普通删除。
- 在线 MMC 更新需要完整原包测量及平台证据；普通 mrpack／CurseForge ZIP 不因来自同一平台就变成 MMC。

## 证据边界与后续开发

GTNH 2.9.0 两份原包已分别在 Java 8／Java 21 完成用户操作的世界闭环；现代 Java26 随后通过
用户开发包的主菜单及正常退出，仍不具有该 Java 的完整世界／修复结论。Java17／25、其他作者包
及外部平台在线接口各自待证。用户已决定非 GTNH 实包后续自行测试；性能工程后置。
Prism 原生解析／计划子集差分和目录分页已取得各自定向证据，具体边界见对应记录。

新增能力应同时更新读取能力表、语义编译、宿主执行／来源记录、修复与生命周期保持，
并增加能观察实际行为的最小回归。只把字段加入 HashSet、只写保留值或只让安装返回成功，都不能算完成兼容。
Core 保持独立 C#／MPL-2.0；实际复用的上游工具或源码另保留许可，纯机制参考记录来源。
