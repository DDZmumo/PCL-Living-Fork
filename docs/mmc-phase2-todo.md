# MMC 二期待办

2026-11-09 用户确认一期按“基本可用的 MMC 兼容”收口，明确将高级兼容放到二期。
本表区分未接通的能力、已有功能的后续实测和长期工程，不将这些项目继续作为一期 goal 的完成条件。
一期记录见[完成清单](mmc-completion-ledger.md)，格式边界见[当前兼容范围](mmc-compatibility-scope.md)。

## 高级兼容：已接入主线实现与自动化验证

- [x] 包内命令：已接通 `PreLoadCommand`、`PreLaunchCommand`、`PostExitCommand` 的真实执行阶段（详见 [阶段契约](mmc-phase2-command-contract.md)），
  完成按生效脱敏模板摘要 `MmcPackageCommandConfirmed` 的 D13 用户确认、取消、非零/输入变更拒绝及执行后文件与输入指纹重验。玩家已手动设置的 PCL 启动前命令沿用现有功能；主动包内命令在可移植导出中明确拒绝。
- [x] 自定义环境变量：已接通有效 `OverrideEnv / Env`，核对 QSettings 列表／旧标量／JSON 数组格式、字段字面值、覆盖顺序与 D15 阶段分化（`PreLaunch`／`PostExit`／游戏继承并叠加会话 `TEMP`/`TMP`，`PreLoad` 保持宿主基础环境）；`@Variant` 未知编码、重声明 `TEMP`/`TMP`/执行输入通道等无法兑现要求明确拒绝，主动映射在可移植导出中拒绝。
- [x] `WrapperCommand`：已通过受管 Windows Job Object 进程组（无 `KILL_ON_JOB_CLOSE`）、`CREATE_SUSPENDED` 入口核验、匿名管道注入、nonce 进程发现及双身份登记接通包装程序及其 Java 子进程，日志经 `ModWatcher` / `LegacyWatcher` 退出前流式脱敏，窗口监控与退出/取消覆盖整个受管进程组，并采用 `WrapperManagedExit` 退出证明区分包装先退出与游戏存活；活动会话与失败清空回收已接通 Main 启动链。
- [x] 补全 console／PermGen 的保留原值与用户可见差异说明（`MmcDisplayDifference`）：导入时按严重级别通用路由到 `ModMain.Hint`（`Warning` 及以上）或 `Logger.Info`（`Information`），可移植导出以显式调整说明披露；不把关闭独立控制台映射为关闭整个 PCL，不将 Java 8 的 PermGen 参数替换为 Metaspace 参数。

以上四项已在当前集成分支 `codex/mmc-compatibility` 完成 `PCLLF.MMCC.Core`、`PCLCS`、`modern/Main` 与 `Migration / Mmc.Runtime / Mmc.Export / Native.Facade` 自动化回归接入；真实作者整合包实机启动、外部认证服务与下文其余待办仍保留跟踪。

## 已有功能：补充实测与缺陷跟踪

- [ ] 非 GTNH 作者包的真实启动，由用户拿新构建自行尝试；现有普通 Forge／Fabric 后端用例不等同于真实游戏验收。
- [ ] GTNH 现代包补 Java17／25 的游戏验证；Java26 目前只有用户确认的主菜单及正常退出，尚无这一 Java 的完整世界／修复闭环。
- [ ] 跟踪 Java26 首次启动的 SDL3 原生崩溃。相同 Java 重试成功并不证明已修复；
  Windows 故障模块、退出码及 DLL 字节证据见 `artifacts/mmc-goal/diagnostics/user-launch-67112.json`（2026-11-15 Package A 复核：本机 `jdk-26.0.2+10-55`、原包 `patches/org.lwjgl3.json` 声明 `3.4.3`、共享 `lwjgl-sdl-natives-windows-3.4.3.jar` `1,241,055` B / SHA256 `0c76c67fb12c8ab1c88bc89bf9f87fabb313d2b4d735b274f1267f71df3613ad` 与内嵌 `SDL3.dll 3.4.14.0` `2,983,936` B / SHA256 `292406def58d7771919eeae8d55a79d1b42657c8e0176b0602eb97ee796786c9` 一致，PID `67112` 在 `0x15d921` 报 `0xc0000005`→`0xc000041d`、退出 `-1073740771` 仍未定位根因；重试 PID `66924` 退出码 0 不计作已修复；不新增 GTNH 特定 Java 禁用，不修改玩家 Java 选择）。
- [ ] Authlib／Nide 用真实账号和服务验收。自编 agent 的 JVM premain、Main 传参、取消排空和清理证据已存在，不能替代外部账号验收。
- [ ] 补外部平台真实在线更新、大包组件修改及完整真实游戏后的导出重导／生命周期 UI 实测（线上预览／本地受控导出重导、更新／组件／生命周期中断与准入 UI 自动化已在 2026-11-15 Package A 通过，见下节台账；CurseForge `/v1/` 因构建环境缺少有效 API 凭据被阻塞，`Modrinth` 真实 MMC ZIP 下载提交与大实包组件修改后真机运行仍待外部条件补齐）。
  在线更新、组件编辑、克隆、改名、同卷移动、还原、删除／恢复、导出及主动原 ZIP 转换已有实现，
  此项是补实测，避免重复安排为“尚未开发”。
- [ ] 更完整的 Prism 原生对照，包括完整 resolver、应用启动和实际提取器；现有 C++ 解析／计划子集差分保留其明确边界。
- [ ] 持续检查库补全／修复、普通实例和 MMC 实例同根使用时的互不覆盖，以及活动会话下的实例间资源隔离。

## 当前状态与补充验收台账（2026-11-15 Package A，基线 `e0b87974`）

说明：与 [代码审查回执](../artifacts/mmc-review-20261115/review-findings.md) 一致；仅登记真实观测结果，不将受控夹具或只读 API 预览外推为实机/真实下载通过。

| 类别 | 当前结论与已核实证据 |
| --- | --- |
| **已实现（Implemented）** | 一期 MMC 核心／宿主管理、生命周期／隔离清理／导出与二期四项（包阶段命令、`OverrideEnv/Env`、`WrapperCommand`、console/PermGen 差异通知）均已接入主线；2026-11-15 经 `bf4176c2`（移除不安全祖先目录校验缓存并恢复物理路径/Junction 检查）与 `e0b87974`（`GameRoot` 命令上下文同步、活动 `PostExit` 跨进程文件租约、持久 `CreatorWindowsSessionId` + Windows Job 四态 fail-closed 命令组退出证明及租约后移释放）完成收口。 |
| **受控夹具已通过（Controlled Fixture Passed）** | 基于 `e0b87974` 与 `artifacts/mmc-postphase-20261115/` 串行实测：`PCLLF.Mmc.Export.Tests.exe` **14/14**（导出／重导／偏好／CAS 与目标卷保护）；`PCLLF.Migration.Tests.exe` 共存隔离与恢复组（`mmc-import-recovery`/`mmc-legacy-adoption`/`mmc-standard-managed`）**12/12**；`PCLLF.Native.Facade.Tests.exe` 原 60 秒预算下 **11/11** 选中组通过（`mmc-export-ui`、`mmc-package-update-cancel-before-decision`、`mmc-package-update-cancel-after-decision`、`mmc-package-update-refuse-unknown`、`mmc-component-cancel-before-decision`、`mmc-component-cancel-final-validation`、`mmc-component-session`、`mmc-lifecycle`、`mmc-repair-admission`、`mmc-delete-admission`、`mmc-selection-groups`）。另前序同提交已验证 Migration 配置/会话事务 71/71、配置/制品/运行环境/变体 128/128、生命周期/包更新/定义/元数据 65/65、清理 15/15、启动校验 3/3、Runtime JVM 边界 13/13、Native `mmc-phase2-launch`/`mmc-phase2-dotmc-active-root`/`mmc-phase2-postexit-lease`/`mmc-launch-replay`/`mmc-variant-launch`。环境变量边界：`mmc-backend` 2 项文件 symlink 用例因当前 Windows 账户缺创建符号链接特权报 `PayloadLinkFixtureUnavailableException` 未行使（不提升系统权限）；跨 Windows 登录会话与 Job 拒绝用 `ObserveNamedJobOverride` 注入。 |
| **真实公网只读已验证（Real Public Network Verified）** | 通过真实生产桥接 `PCL.ModNet.CreateMmcPlatformOriginCatalog()`（严格系统 TLS、禁重定向、无私密凭据）实测 `https://api.modrinth.com/v2/` 只读元数据：`GetReleaseAsync("l9m9tuPN", "M8j2mfGj")` 成功校验并解码 1 条发布文件；内部同源列表路径 `ListAsync`（`GetAvailableReleasesAsync` 底层枚举与逐版本复核）返回并核验 12 条发布文件；对只读 `GT_New_Horizons_2.9.0_Java_8.zip` 调用 `FindModrinthCandidatesAsync` 发出真实 SHA512 查找并由端返回非 2xx（该包非 Modrinth 托管，映射为 `IOException("The official platform metadata endpoint is unavailable or redirected.")`）。不构成外部 ZIP 下载、在线更新提交或真实游戏证据。 |
| **真实游戏／账号／凭据待验（Pending）** | CurseForge `/v1/` 真实现网调用（无可用 API 凭据，`GetReleaseAsync` 被阻断报 `IOException: The official platform metadata endpoint is unavailable or redirected.`）；真实 Modrinth MMC ZIP 在线下载更新提交；GTNH 现代包 Java 17/25 全闭环及 Java 26 世界/修复闭环与 `SDL3.dll 3.4.14.0` 首次崩溃根因；非 GTNH 作者实包真机启动；Authlib/Nide 真实账号；旧 `net48` 工程构建；Windows 特权文件 symlink 与真实双登录会话内核 Job 跨会话测试；新开发包（`artifacts/mmc-dev-build/20261115-e0b87974`）冷启动验收。 |
| **设计明确不支持（Unsupported by Design）** | 普通 `.mrpack` / CurseForge `.zip` 当作 MMC 容器导入；未知关键语义/`features`/`@Variant`/主动包内命令与主动 `OverrideEnv` 的可移植导出；非 NTFS/TxF 下的永久隔离清理；跨卷实例移动、实验 Relauncher 及不经显式确认的普通旧实例自动迁移；跨 Windows 登录会话/不可观测命名 Job 时猜测进程已退出。 |

## 后期性能与架构

- [ ] 用大包基线定位导入、校验、物化和列表扫描的主要成本，再决定是否优化；
  当前私有载荷路径检查消重只有单次自造包 A/B 证据，不外推成所有 GTNH 场景的收益。
- [ ] 再评估元数据／制品缓存、物化复用和磁盘成本，继续保留来源摘要、修复权威与取消后真实排空。
- [ ] 评估更广的实例布局、跨卷移动和显式角色迁移；普通 PCL 实例继续保持原目录与管理方式，
  不因这份待办自动写入 MMC ID 或自动搬动旧实例。
- [ ] 扩大平台与 Java 实测范围。未来 Java 主版本继续由包声明和实际探测决定，不新增固定的“最高支持版本”判断。

Core 继续采用独立 C#／MPL-2.0，宿主保留原许可；仅实际复用代码、资源、工具或依赖时补齐对应许可与署名。
