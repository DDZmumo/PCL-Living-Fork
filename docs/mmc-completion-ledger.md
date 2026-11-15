# MMC 一期收口与完成记录

2026-11-09 最新用户决定：一期按基本可用的 MMC 兼容收口，当前补齐 Java 自动识别、实例分组、
按钮文案和必要验证后完成 goal；高级兼容明确进入[二期待办](mmc-phase2-todo.md)。
本清单保留 `mmc-instance-design.md` 的完整设计覆盖记录。单项通过不代表所有 MMC 特性完成；
工具、规则测试、真实 Main 接线、真实 Minecraft 和正式产品构建分别取证。
按用户随后要求，已将通过检查的批次推送至远端 `codex/mmc-compatibility`，不合并 main、不发布 Release。
以下实现与测试状态基于 2026-11-09 集成工作树；最终收口提交和构建另行记录。
字段与当前拒绝／未实测范围见[兼容边界](mmc-compatibility-scope.md)；二期待办不会被登记为一期已实现。

| 要求 | 当前已取得证据 | 后续工作／未测边界 |
| --- | --- | --- |
| 独立 C# Core／许可 | net10.0、MPL-2.0 文件声明与完整许可、原许可宿主；来源记录和实际依赖许可已接入 | 最终变更与构建重新审计 |
| MMC 根／cfg／组件／依赖 | 有界读取、首行／转义／缺 cfg、组件续解及未知关键语义诊断；标准 Forge/Fabric 真实后端离线修复两项根集成通过 | 非 GTNH 作者包真实游戏启动与 Prism 实差分 |
| 完整启动计划 | main/mainJar/assets/JVM/game/tweaker/library/native/platform/Java，顺序及来源；raw tweaks、原生 legacy 参数规则 | 最终集成规则组与未支持能力清单 |
| Java 与自定义启动链 | 声明集合、显式采用变体、RFB、逐项旧推断替代、其他硬约束保留 | 更多已声明 Java 的实包证据，明示未测集合 |
| 三根运行库及本地原件 | 实例 local、游戏子目录、shared 来源分开；CAS／owned view／native／asset 物化与摘要 | 最终跨实例共享资源回归及磁盘成本 |
| 下载／缓存／取消 | 真实 NetFile／Loader、批次校验、取消后真实 drain；不重下已有 GTNH ZIP | 最终缓存失效、故障与性能矩阵 |
| 设置协调／文件事务 | q→跨进程准入、普通键重基、结构代数、Prepared/CommitDecided 恢复、真实 Windows sharing | 后续新增写入口都须接入并回归 |
| 持续会话／Java pin | 实际 PID／时间／镜像／nonce；退出收尾、宿主死亡恢复、短 mutable 可信布局 | 最终新增生命周期与产品退出接线回归 |
| 导入／启动／修复 | 两份 GTNH 官方包实机闭环；最终 Main 导入／补全／恢复组通过；真实 Prepared／CommitDecided 进程终止后的回滚、前滚及变化字节拒绝通过 | 非 GTNH 作者包由用户拿新构建自行验收，暂未反馈 |
| 克隆 | 新 ID、来源关系、完整来源／原件／偏好；实际 Main 任务及早取消回归 | 最终产品入口与大包回归 |
| 改名 | 同 ID 新代数、投影与路径偏好迁移；确认框前固定身份；实际 Main 回归 | 与最终其他生命周期合并后回归 |
| 同卷移动 | 后端事务／配置迁移／向前恢复、真实 source/target 边界；根 Main 移动入口与偏好保持通过；真实 RelocateAsync child 在 Prepared／CommitDecided 终止后的回滚／前滚已通过，见 `lifecycle-recovery.log` 8/8 | 大实包及完整宿主 UI 中断场景未测 |
| 删除／恢复 | 隔离区与无覆盖恢复、精确列表过滤、双 UI 入口；永久清理后端 13/13 与长路径专项通过，Main 四项确认／取消／清理任务通过；分页后端 3/3、实际 Main 2/2 与选择器 173 项通过，Sol max 定向审查无新增可证 P1/P2 | 真实玩家隔离区与断电未测；本地 NTFS／TxF 能力不足明确保留内容 |
| 环境还原 | Coverage 5 完整安全库存／私有原件、受限作者范围恢复和完整 undo；同 ID 单调代数、世界／偏好保留；Main 环境入口实际组通过 | 真实宿主中断与保留清理；普通／mapped 角色变换明确拒绝 |
| 组件／用户 patch 修改 | 固定候选 preview/commit、来源修订、同名库权威保留、依赖/native、mapped resource 保护；Main 十九组分批通过，含真实下载取消、Java 21 探测和世界备份／锁／外部变化；无新游戏进程 | 新根 UID 的来源变更由 ZIP 更新实现；真实作者包组件修改后的游戏未测 |
| MMC ZIP 更新 | 本地 ZIP 产品入口、差异选择、固定预览、备份与提交已接；11 项实际 Main 场景通过，保留原 60 秒预算；Coverage5 定向 red/green 和审查通过；在线路径见下一行 | 其余后端独有聚合／真实大包更新未测 |
| 可靠平台身份／在线更新 | 原包摘要与完整发布文件证据绑定、官方严格 TLS 元数据、来源修订及在线 ZIP 后端已接；根平台 10/10、选择器 171 项通过；Main 控件／早取消两组及绑定／在线任务七组通过，Sol max 定向审查无新增可证 P1/P2；已进入 `e059eee4` 开发包 | 外部平台接口与实际作者在线包更新未测 |
| MMC ZIP 导出 | 真生产依赖图导入→导出→重导，审查 2 项 P2 修复后根工作树 12/12；新 Main 导出入口真实组通过，固定预览及个人文件默认排除 | 最终产品冷启动 |
| 旧实例身份迁移／转换 | 主动原 ZIP 创建副本，旧实例保留；后端 4/4，根 Main 实际按钮／预览／工作任务与空列表恢复入口组通过 | 最终交付构建收口；未做转换后真实游戏验收 |
| 性能（后期工程） | 2026-11-08 12 点初版基线与已撤回比较见[探针说明](mmc-performance-probe.md)；2026-11-15 Package B/C 已基于冻结真实 `e0b87974` 运行时补齐 12 点 GTNH C 盘基线、取消排空、2,003 文件导入与 1,024 条损坏目录分页基准，并完成 Core PE 纯度门禁与存储／缓存／跨卷协议评估；Package D（提交 `32106473`）移除 `MmcInstanceBackend.LaunchFiles` 冗余 `ResolveRelative` 预扫（保留 `ValidatePreparedLaunch` 全部物理与 `SHA256` 核验并增 `DeepTargetLaunchValidation`），经迁至 `E:` 的 24 点交错 A/B 矩阵实测按预注册门禁**采纳**：Java 8 冷/热总准备 `-10.86%` / `-7.72%`（`-1,541.1` / `-1,001.7 ms`）、Java 21 冷 `-13.93%`（`-2,098.4 ms`）、Java 21 热 `+0.45%`（`+55.3 ms`，基本持平，数据中存在单点较大波动，未超过预注册回归门槛，CPU `-6.02%`），全四组 `launch-bind` 下降 `-33.78%` 至 `-48.68%`、CPU 全面下降 `-6.02%` 至 `-17.45%`，取消排空 `119.0` / `94.0 ms` 且 `--mmc-launch-validation-checks` 4/4、`Probe.exe` 0 违规通过（`--private-payload-path-checks` 9 通过 / 2 非特权 symlink 不可用），详见[性能与架构决策记录](mmc-performance-20261115.md) | `e0b87974` 既有自包含测试包未覆盖新提交（需重打包由 Root 安排）；GTNH 完整大包导入、真实 Main 列表扫描及后续更广泛优化实验仍留待后续立项 |
| Prism 对照 | 固定 `899d2f3` 原生 C++ 解析／计划子集与 LF Core 六样例实际执行；两实包各 15/16 字段原值相同，差异及 native 10 个 DLL 字节复核完整记录于 `mmc-prism-differential.md` | 完整 Prism 应用／resolver／启动／提取器没有执行；日志注入层级、native 输入顺序及 LF 明确 null 扩展不伪装为全部等价 |
| dev 构建交付 | `81796b2c` 已完成六模块／ZIP 条目／37 个 Core Git blob 核对；用户实际完成现代包导入、Java26 主菜单与正常退出；后续源码已补阶段细分、文件数、私有路径消重、MMC 独立分组与 Java 识别接线 | 本轮最终构建与代码检查收口后另行记录；非正式 Release，新包冷启动仍由用户反馈 |

包 cfg 的四阶段命令／Wrapper／Env 按最新决定放到二期，默认能力门继续关闭。
MMC 专用 Auth／Nide 已接入 Main 和受控 JVM，真实外部账号仍待实测。
`PermGen`／console 的有效覆盖行为或差异诊断也列入二期待办。

## 一期最终代码收口（2026-11-09）

- MMC 设置页与实际启动共用组件声明、保留来源和真实 Java 检查；手动指定、范围、实例目录模式和
  已发布环境规则保留。普通实例沿用旧选择器，MMC 不再显示由普通 MC1.7.10 推断出的 Java8。
- 查找失败／取消／无兼容候选显示真实状态，不承诺尚未接通的自动下载；切换实例后不会借用旧实例的结果或登录类型约束。
- 实例选择页单独显示 MMC 正常、收藏、隐藏和需要修复的卡片；未知名称或 loose `mmc-pack.json`
  不取得 MMC 身份。冷刷新损坏投影时仍保留身份绑定的隐藏／收藏偏好。
- 启动页底部按钮改为“实例选择”“实例设置”；启动早退提示补实际 Java、十进制／十六进制退出码及日志位置。
- 导入阶段细分、真实文件数／字节进度、取消后排空与私有路径检查消重均已集成。

根集成实际 Main 78 个既有警告／0 错误，Native 0 警告／0 错误。
`mmc-java-settings-discovery`、`mmc-selection-groups`、`mmc-import-stage-details`、`mmc-authentication`
四组按各自原 60 秒子进程分别通过；独立 Runtime `authentication` 构建 0／0 并通过。
Java 识别新增用例执行本机 Java8／26 的干净探测，原 `mmc-launch-java` 三项由作者分支独立通过；
认证组执行自编 agent JVM，不使用真实认证账号。没有据此新增 Minecraft 游戏验收结论。

GPT-6.1 Sol max 定向审查发现的账号类型、旧请求终态和普通实例重试三项串台问题均已修复并静态关闭；
损坏实例的冷刷新展示偏好和认证 agent 路径 `=` 问题也已关闭。
最终开发包回执保留于 `artifacts/mmc-goal/phase1-final-delivery.json`；
包的构建／对应源码核对不等于该新 EXE 已由用户完成冷启动。

## 实机矩阵

| 原始官方包 | 实际 Java | 已完成 |
| --- | --- | --- |
| GTNH 2.9.0 Java 8 | 本机 Java 8u491 x64 | 导入→主菜单→创建世界→正常退出→修复→重启同一世界→正常退出 |
| GTNH 2.9.0 Java 17–26 | 本机 Java 21.0.12 x64 | 相同完整闭环；两轮短 mutable scope 均正确退出和清理 |

两包四次游戏退出码均为 0，Watcher 为 Ended，receipt 归档、mutable 清理和 Java pin 释放均有原始记录。
用户手动操作界面；代理在用户按 Esc 后只读进程与日志。具体证据见
[GTNH 验收记录](mmc-gtnh-acceptance.md)。现代包声明 Java17/21/25/26；17/25 尚未实测，
Java26 后续已有主菜单与退出证据，但尚无该 Java 的完整世界／修复闭环。

## 统一约束

所有后续入口仍须固定用户确认的身份／代数／摘要，验证来源与本地实际选择，不因 UI 选择绕过所有权。
活动／无法确认会话、未知路径和第三方改写必须保留内容；文件取消等待真实 I/O 结束。
重型验证单独串行，避免已观测到的机器 commit 压力，不修改系统 pagefile 或结束用户程序。
一期 goal 的完成依据是本页开头最新分期范围及其实际验证；未完成的二期能力与外部实测继续明确列出。

## 2026-11-09 初始导入失败恢复

`PCL/MmcImportOperations` 保存精确预留归属；非线程亲和的生产者文件租约覆盖真实准备、下载与发布，
进程仍工作时不允许恢复抢占。恢复重放原事务决定，校验已保存来源、作者载荷、运行库和最终身份后完成无覆盖发布。
未提交或无法证明完整的现场保留，只允许匹配持久记录 SHA256 的原包使用不同名称创建新实例。
旧版缺少生产者退出依据的残留不强行恢复；克隆记录不当作初始作者载荷。

后端四项真实文件测试通过。独立 high 发现的 preview 可修改 SHA／旧路径 P2 已修复并静态关闭；
新增三个篡改负例均拒绝，权威数据来自重读的 owner 或旧标记。根 Main 78 个既有警告／0 错误，Native 0／0。
`mmc-import-recovery-ui` 与 `mmc-import-retry-ui` 在各自原 60 秒子进程通过实际按钮、目录、Loader 与后端；
覆盖同 ID/G1 恢复、新 ID/G1 重试、未知状态只读、旧现场与世界保留、目标占名、Prepared 取消真实排空和决定后取消。
这些是自有小包、缓存命中的程序接线证据，没有新游戏或外网传输结论。

2026-11-09 根集成追加：`mmc-import-recovery`、`mmc-legacy-adoption`、`mmc-standard-managed`
三组共 12 项通过，覆盖选择 `MC/PCL` 为游戏根、无 `versions`、尾分隔符时的私有存储删除保护，
并验证普通无恢复记录目录继续允许旧管理入口。

本地 ZIP 更新旧聚合曾触发 60 秒超时；阶段诊断未复现 Java 死锁，原功能场景完整通过。
其余十项按取消／拒绝／世界备份触发拆为独立进程，全部通过，最长 39.04 秒，27 处检查与时限保留。
所有场景引用同一真实 Main DLL；这些是小包和真实本机 Java 探测证据，没有启动 Minecraft。

## 2026-11-09 组件编辑与投影重建接线

版本管理页新增“MMC 组件与补丁”入口，沿用 LF 对话框与后台任务；可查看作者声明及个人覆盖，
启用／禁用／恢复原组件、选择精确版本、补丁文件、本地运行库与 Java，再确认固定的最终差异。
更换 Minecraft 时先按现有机制备份世界，并在最终提交前重新核对世界库存与本地输入。
首次 Main 构建为 78 个既有警告／0 错误，Native 测试宿主为 0／0。
三个独立实际 Main 场景 `mmc-component-edits`、`mmc-component-version`、`mmc-component-patch`
均在原 60 秒预算内通过；没有启动游戏，也没有据此宣称其余编辑场景完成。

手工改写的最终版本 JSON 可先查看差异，明确确认后保留完整私有副本并由已保存来源重建。
审查发现已存在的同摘要副本若被跳过事务，会留下确认后被删除／替换的竞态；`786af904`
将该副本无条件纳入同一事务，两项实际 Main 回归分别制造晚删除与晚替换，均拒绝发布并保留
当前 JSON、主定义与玩家数据。该项定向审查已关闭。

组件编辑的最终存档／来源扫描现在仍可取消；完成扫描与最后输入复核后才进入提交屏障。
`mmc-component-cancel-final-validation` 与 `mmc-component-cancel-after-decision` 分别在独立
原 60 秒子进程通过，前者保持原代数且不新增 CommitDecided，后者保留成功提交结果。
平台证据的最终来源检查同时覆盖修复／启动与组件修订；平台新增负例覆盖两路径的晚删除／晚替换，
根集成全部九项通过。后续审查按用户最新要求使用 GPT-6.1 Sol max。

## 2026-11-13 二期命令、Env、Wrapper 与 UI 差异落地（PR #23 收口）

在分支 `codex/mmc-compatibility` 合入并补齐了二期高级兼容四项核心实现，包括：

- `OverrideEnv / Env` 与按阶段分化的 D15 环境映射（`PreLoadCommand` 保持宿主基础环境，`PreLaunchCommand`、`PostExitCommand` 与受管游戏进程继承包环境并由宿主注入会话独占 `TEMP`/`TMP`）。
- `PreLoadCommand`、`PreLaunchCommand`、`PostExitCommand` 执行阶段：接入 D13 脱敏模板指纹确认键 `MmcPackageCommandConfirmed`、`MmcConfigurationCoordinator.Quiesce` 与准入并发协同、`EnsureGameRootDirectory` 提前物化、`cmd.exe /d /s /c "<command>"` 对含引号路径参数的边界修复、`PreLaunch` 执行后输入指纹重验（D6/F-06），以及 `PostExit` 在 `Exited.record` 持久化后、会话收尾前运行的 D9 顺序。
- `WrapperCommand`：通过 `MmcSessionProcessTree`（无 `KILL_ON_JOB_CLOSE` 标志的 Windows Job Object、`CREATE_SUSPENDED` 入口镜像核验、`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` 匿名管道重定向）、nonce 内存命令行发现受管 Java 子进程、双身份登记、`WrapperManagedExit` 退出证明、失败空组回收（持久化 `Exited.record` 并调 `MmcSessions.Reconcile`），以及与 `LegacyWatcher` / `ModWatcher` 的管道日志脱敏流和组退出跟踪。
- console／PermGen 与通用差异通知：`MmcInstances` 按诊断严重级别自动路由（`Severity >= Warning` 经 `ModMain.Hint` 以 `Blue` 提示用户，`Information` 记入 `Logger.Info`），并在 `MmcInstanceExportPreferences` 中对主动包内命令及主动 `OverrideEnv/Env` 执行可移植导出拒绝。

以上改动已通过 `PCLLF.Mmc.Runtime.Tests`、`PCLLF.Mmc.Export.Tests`、`PCLLF.Migration.Tests` 与 `PCLLF.Native.Facade.Tests` 的自动化闭环回归验证（具体通过项及权限边界见提交后最新一次全套运行记录）；外部真实整合包游玩、真实认证账号与外部平台在线接口仍保留在[二期待办](mmc-phase2-todo.md)继续跟踪。

## 2026-11-15 代码审查修复与 Package A/B/C 收口

- 在 `codex/mmc-compatibility` 合入本地提交 `bf4176c2`（移除 `MmcInstancePaths` 祖先目录校验缓存并在 `File.Move` 边界恢复物理重检）与 `e0b87974`（持久化布局同步至三阶段 `commandContext`、活动 `PostExit` 跨进程所有权文件锁、持久 `CreatorWindowsSessionId` + Windows Job 四态 fail-closed 命令组退出证明与租约后移结算），并已由 Root 打包至 `artifacts/mmc-dev-build/20261115-e0b87974`（未改动且仅作构建证据保留）。
- 基于 `e0b87974` 执行 Package A 补充实测与证据核对（日志见 `artifacts/mmc-postphase-20261115/`）：
  - `PCLLF.Mmc.Export.Tests.exe` 14/14、`PCLLF.Migration.Tests.exe` 隔离/恢复 3 组 12/12、`PCLLF.Native.Facade.Tests.exe` 11 组（含导出 UI、包更新/组件编辑中断与拒绝、生命周期/修复/删除准入及选择分组，保留 60 秒分组上限）11/11 全部通过。
  - 通过生产入口 `PCL.ModNet.CreateMmcPlatformOriginCatalog()` 完成 `Modrinth` `/v2/` 只读元数据及基于合成 `MmcPlatformOriginEvidence` 持有人的列表枚举在线预览实测（12 条候选通过编码器核验，**不**构成任意包作者身份绑定或在线更新提交证据）；`CurseForge` `/v1/` 实测请求在当前环境失败并抛出 `IOException: The official platform metadata endpoint is unavailable or redirected.`（根因尚待具备有效 API 凭据及网络条件的环境核查）。
  - 核实 `user-launch-67112.json` 保留的 `SDL3.dll 3.4.14.0`（`2,983,936` B，SHA256 `292406de...`）与 `lwjgl-sdl-natives-windows-3.4.3.jar`（`1,241,055` B）本地字节及 `jdk-26.0.2+10-55` 一致；崩溃根因未闭环。
- Package B 已在 `tests/PCLLF.Migration.Tests/MmcProfileTests.cs`（`mmc-profile`）接入对实际 `PCLLF.MMCC.Core.dll` 的 PE 元数据与 IL 纯度约束检查（阻断宿主/WPF 引用、磁盘/控制台/网络/进程/环境/注册表、`PinvokeImpl`/`DllImport`/`LibraryImport` 及非确定性时钟/`Stopwatch`/随机数；对 `System.AppContext` 仅放行源生成 `[GeneratedRegex]` `<RegexGenerator_g>...__Utilities::.cctor` 只读 `GetData`/`TryGetSwitch` 并全表核销，手工 `AppContext` 仍拒绝，附 7 组非执行负例与正例夹具）。修复初测中 `System.AppContext` 误报及 `CS9113` 后，私有 `.NET 10` SDK 构建 `PCLLF.Migration.Tests.csproj` **0 警告 / 0 错误**，执行 `PCLLF.Migration.Tests.exe --group mmc-profile` **90/90 通过**（`b-final-build-migration.log`、`b-final-mmc-profile.log`；保留原失败现场 `architecture-profile.log`）；`Check-Documentation.py` 通过；同步完成存储／缓存演化、禁共享库硬链接／禁祖先目录信任缓存及跨卷移动四阶段安全协议评估（均保持当前明确边界，未新增缓存 store 或跨卷移动实现）。
- Package C 完成收口：修复 `Select-MigrationChecks.py` / `MigrationSelection.golden.json` / `Test-MigrationSelection.py` 此前缺少第 45 个已注册分组 `mmc-runtime-environment` 的选择与全量清单不匹配问题，实测 `Test-MigrationSelection.py` **176/176 通过**（`selector-final-root.log`）且使用 `artifacts/mmc-goal/python-ci-deps` 运行 `Test-MainCiGates.py` **49/49 通过**；整合冻结真实 `e0b87974` 运行时的 12 点 GTNH 启动准备基线（Java 8 冷/热 `12,895.6` / `11,842.4 ms`、Java 21 冷/热 `15,583.7` / `15,069.0 ms`）、取消排空（`171.1` / `313.0 ms`）、三轮合成 2,003 文件私有载荷导入基准（总耗时中位数 `100,103 ms`、预检+放置中位数 `31,707 ms`，13 条日志变更 / 0 批量体复制）及冻结程序集 1,024 条损坏目录分页基准（首页中位数 `1,341.6 ms`、全量 32 页有序遍历中位数 `33,112.7 ms`），记录于[性能与架构决策记录](mmc-performance-20261115.md)。
- Package D（提交 `32106473`）完成单一可控启动性能优化并采纳：移除 `MmcInstanceBackend.LaunchFiles` 中返回值被丢弃的冗余并行 `ResolveRelative` 预扫并保持原词法验证边界，同时维持紧接其后 `ValidatePreparedLaunch` 的全量物理链接检查与 `SHA256`/长度核验，增补 `DeepTargetLaunchValidation`（124/128/131 段 Maven 路径）；经迁至 `E:` 的同夹具冻结双臂 24 点交错 A/B 实测，Java 8 冷/热总准备 `-10.86%` / `-7.72%`（`-1,541.1` / `-1,001.7 ms`）、Java 21 冷 `-13.93%`（`-2,098.4 ms`）、Java 21 热 `+0.45%`（`+55.3 ms`，基本持平，数据中存在单点较大波动，未超过预注册回归门槛，CPU `-6.02%`），全四组 `launch-bind` 下降 `-33.78%` 至 `-48.68%`、CPU 全面下降 `-6.02%` 至 `-17.45%` 且 `LaunchInputs` 与全部权威不变量 0 偏差；候选取消排空 `119.0` / `94.0 ms`，`--mmc-launch-validation-checks` **4/4**、隔离 `--group mmc-configuration` **36/36**、隔离目录缓存/Junction 安全探针 `Probe.exe` 0 违规、`Test-MigrationSelection.py` **176/176** 通过（`--private-payload-path-checks` 9 通过 / 2 项非特权 symlink 不可用；先前同存在外部观测进程期间的一轮宽泛扫描如实保留为 216/220，未对 `Unknown` 作固定根因归因）。`artifacts/mmc-dev-build/20261115-e0b87974/` 既有测试包保持未改动，后续新包重打包由 Root 统一执行。
- 详细五类（已实现／受控通过／公网只读已验／真实待验／设计不支持）台账与 Package B 架构／存储评估见[二期待办与缺陷台账](mmc-phase2-todo.md)。
