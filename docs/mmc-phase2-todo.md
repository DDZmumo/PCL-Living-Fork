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
- [ ] 补外部平台真实在线更新、大包组件修改及完整真实游戏后的导出重导／生命周期 UI 实测（线上只读预览／本地受控导出重导、更新／组件／生命周期中断与准入 UI 自动化已在 2026-11-15 Package A 通过，见下节台账；CurseForge `/v1/` 实测请求在当前环境失败并抛出 `IOException: The official platform metadata endpoint is unavailable or redirected.`，原因未定，与 `Modrinth` 真实 MMC ZIP 下载提交、大实包组件修改后真机运行一并保留待测）。
  在线更新、组件编辑、克隆、改名、同卷移动、还原、删除／恢复、导出及主动原 ZIP 转换已有实现，
  此项是补实测，避免重复安排为“尚未开发”。
- [ ] 更完整的 Prism 原生对照，包括完整 resolver、应用启动和实际提取器；现有 C++ 解析／计划子集差分保留其明确边界。
- [ ] 持续检查库补全／修复、普通实例和 MMC 实例同根使用时的互不覆盖，以及活动会话下的实例间资源隔离。

## 当前状态与补充验收台账（2026-11-15 Package A/B/C/D，运行时 `32106473` 与基线 `e0b87974` 检查收口）

说明：与[完成清单](mmc-completion-ledger.md)及本地归档回执 `artifacts/mmc-review-20261115/review-findings.md` 一致；仅登记真实观测结果，不将受控夹具、只读 API 预览或静态评估外推为实机／真实下载通过。

| 类别 | 当前结论与已核实证据 |
| --- | --- |
| **已实现（Implemented）** | 一期 MMC 核心／宿主管理、生命周期／隔离清理／导出与二期四项（包阶段命令、`OverrideEnv/Env`、`WrapperCommand`、console/PermGen 差异通知）均已接入主线；2026-11-15 经 `bf4176c2`（移除不安全祖先目录校验缓存并恢复物理路径/Junction 检查）、`e0b87974`（`GameRoot` 命令上下文同步、活动 `PostExit` 跨进程文件租约、持久 `CreatorWindowsSessionId` + Windows Job 四态 fail-closed 命令组退出证明及租约后移释放）与 `32106473`（移除 `MmcInstanceBackend.LaunchFiles` 返回值被丢弃的冗余 `ResolveRelative` 预扫并保持原词法边界，保留紧接其后 `ValidatePreparedLaunch` 的全量物理链接检查与 `SHA256`/长度核验及 `DeepTargetLaunchValidation` 深层 Maven 目标路径覆盖）完成收口；Package B 在 `MmcProfileTests`（`mmc-profile`）接入基于 `System.Reflection.Metadata` + `PEReader` 的实际 `PCLLF.MMCC.Core.dll` 编译后纯度与 IL 门禁（对 `System.AppContext` 按调用方与成员分级：仅放行 `[GeneratedRegex]` 源生成 `<RegexGenerator_g>...__Utilities::.cctor` 的只读 `GetData`/`TryGetSwitch` 调用并在全表延后审计中核销，继续阻断普通 `AppContext` 读写及未核销引用，并补充 `Stopwatch`/`Console`/手工 `AppContext` 负例与纯正则正例）。 |
| **受控夹具已通过（Controlled Fixture Passed）** | 基于 `e0b87974` 与 `artifacts/mmc-postphase-20261115/` 串行实测：`PCLLF.Mmc.Export.Tests.exe` **14/14**（导出／重导／偏好／CAS 与目标卷保护）；`PCLLF.Migration.Tests.exe` 共存隔离与恢复组（`mmc-import-recovery`/`mmc-legacy-adoption`/`mmc-standard-managed`）**12/12**；`PCLLF.Native.Facade.Tests.exe` 原 60 秒预算下 **11/11** 选中组通过（`mmc-export-ui`、`mmc-package-update-cancel-before-decision`、`mmc-package-update-cancel-after-decision`、`mmc-package-update-refuse-unknown`、`mmc-component-cancel-before-decision`、`mmc-component-cancel-final-validation`、`mmc-component-session`、`mmc-lifecycle`、`mmc-repair-admission`、`mmc-delete-admission`、`mmc-selection-groups`）。另前序同提交已验证 Migration 配置/会话事务 71/71、配置/制品/运行环境/变体 128/128、生命周期/包更新/定义/元数据 65/65、清理 15/15、启动校验 3/3、Runtime JVM 边界 13/13、Native `mmc-phase2-launch`/`mmc-phase2-dotmc-active-root`/`mmc-phase2-postexit-lease`/`mmc-launch-replay`/`mmc-variant-launch`。Package B/C 收口实测：`PCLLF.Migration.Tests.csproj` 私有 `.NET 10` SDK 构建 **0 警告 / 0 错误**，执行 `PCLLF.Migration.Tests.exe --group mmc-profile` **90/90 通过**（见 `b-final-build-migration.log`、`b-final-mmc-profile.log`；前序误报失败现场保留于 `architecture-profile.log`）；`Check-Documentation.py` 通过；已定位并修复 `Select-MigrationChecks.py` / `MigrationSelection.golden.json` / `Test-MigrationSelection.py` 此前 `GROUPS` 与 golden 全量清单缺第 45 个已注册分组 `mmc-runtime-environment` 及缺少 `MmcRuntimeEnvironment` 显式映射的缺陷，`Test-MigrationSelection.py` 全量 **176/176 通过**（`selector-final-root.log`），使用隔离依赖 `artifacts/mmc-goal/python-ci-deps` 运行 `Test-MainCiGates.py` **49/49 通过**。冻结真实 `e0b87974` 程序集 C 盘 12 点 GTNH 启动准备基线（Java 8 冷/热 `12,895.6` / `11,842.4 ms`、Java 21 冷/热 `15,583.7` / `15,069.0 ms`）、取消排空（`171.1` / `313.0 ms`）、三轮合成 2,003 文件私有载荷导入（总耗时中位数 `100,103 ms`、预检+放置中位数 `31,707 ms`，日志 13 条变更 / 0 批量体复制）与冻结程序集 1,024 条损坏恢复目录分页基准（首页中位数 `1,341.6 ms`、全量 32 页有序遍历中位数 `33,112.7 ms`，分配 `4.481` / `142.756 MiB`）均通过。Package D（`32106473`）在工作树迁至 `E:` 盘并清零旧目录后完成冻结双臂 24 点交错 A/B 实测：Java 8 冷/热总准备 `-10.86%` / `-7.72%`（`-1,541.1` / `-1,001.7 ms`）、Java 21 冷 `-13.93%`（`-2,098.4 ms`）、Java 21 热 `+0.45%`（`+55.3 ms`，受单点异常峰值影响未超回归门禁，CPU 仍降 `-6.02%`），全四组 `launch-bind` 下降 `-33.78%` 至 `-48.68%`、CPU 全面下降 `-6.02%` 至 `-17.45%` 且 `LaunchInputs` 一致、0 不变量违规；候选取消排空 `119.0` / `94.0 ms`、`--mmc-launch-validation-checks` **4/4**、隔离 `--group mmc-configuration` **36/36**、隔离 `Probe.exe` 0 违规与 `Test-MigrationSelection.py` **176/176** 通过，详见 [性能与架构决策记录](mmc-performance-20261115.md)。环境变量与并发边界：`--private-payload-path-checks` 9 通过 / 2 项文件 symlink 用例因当前 Windows 非管理员账户缺创建符号链接特权报 `PayloadLinkFixtureUnavailableException` 未行使（不提升系统权限）；与外部并发观测进程重叠期间的宽泛探索扫描另保留实情 `216/220`（2 项 symlink 不可用 + 2 项 `MmcCommandExecutionTests` 在并发环境中于 `ReconcileAll` 保守 `TokenScan` 判定为 `Unknown`）；跨 Windows 登录会话与 Job 拒绝用 `ObserveNamedJobOverride` 注入。 |
| **真实公网只读已验证（Real Public Network Verified）** | 通过真实生产桥接 `PCL.ModNet.CreateMmcPlatformOriginCatalog()`（严格系统 TLS、禁重定向、无私密凭据）实测 `https://api.modrinth.com/v2/` 只读元数据：`GetReleaseAsync("l9m9tuPN", "M8j2mfGj")` 成功校验并解码 1 条发布文件；基于合成 `MmcPlatformOriginEvidence` 持有人调用内部列表路径 `ListAsync`（`GetAvailableReleasesAsync` 底层枚举与逐版本解码）返回并核验 12 条发布文件（证明公开元数据读取及内部候选列表枚举/解码，**不**构成任意本地包作者身份绑定证明）；对只读 `GT_New_Horizons_2.9.0_Java_8.zip` 调用 `FindModrinthCandidatesAsync` 发出真实 SHA512 查找并由端返回非 2xx（该包非 Modrinth 托管，映射为 `IOException("The official platform metadata endpoint is unavailable or redirected.")`）。以上均不构成外部 ZIP 下载、在线更新提交或真实游戏证据。 |
| **真实游戏／账号／凭据待验（Pending）** | CurseForge `/v1/` 现网元数据与在线更新验收（当前环境真实调用 `GetReleaseAsync` 失败报 `IOException: The official platform metadata endpoint is unavailable or redirected.`，具体网络/端点或凭据根因尚待具备有效 API 凭据及目标网络条件的环境定位并实测）；真实 Modrinth MMC ZIP 在线下载更新提交；GTNH 现代包 Java 17/25 全闭环及 Java 26 世界/修复闭环与 `SDL3.dll 3.4.14.0` 首次崩溃根因；非 GTNH 作者实包真机启动；Authlib/Nide 真实账号；Windows 特权文件 symlink 与真实双登录会话内核 Job 跨会话测试；`e0b87974` 既有自包含测试包冷启动验收（基于 `32106473` 的后续新包重打包由 Root 统筹安排）；GTNH 大压缩包完整导入、真实 Main 普通实例／版本列表扫描及真实大容量有效隔离区分页实测（当前仅有冻结准备矩阵及合成 2,003 文件导入探针）。 |
| **设计明确不支持（Unsupported by Design）** | 普通 `.mrpack` / CurseForge `.zip` 当作 MMC 容器导入；未知关键语义/`features`/`@Variant`/主动包内命令与主动 `OverrideEnv` 的可移植导出；非 NTFS/TxF 下的永久隔离清理；跨卷实例移动、实验 Relauncher 及不经显式确认的普通旧实例自动迁移；跨 Windows 登录会话/不可观测命名 Job 时猜测进程已退出。 |

## Package B 架构硬化与存储／缓存／跨卷移动评估（2026-11-15）

> [!NOTE]
> 本节评估基于当前代码架构及已知诊断样本完成，用于明确边界并避免过早优化；其中跨卷移动与持久化编译计划缓存处于**已完成评估、当前明确不实现／不支持**状态，不宣称为已实现功能。

### 1. 三层职责边界与自动化门禁（已落实并通过 `mmc-profile` 90/90 实测）

| 层 | 当前代码落点与许可 | 职责与强制不变量 |
| --- | --- | --- |
| **Pure Core** | `PCLLF.MMCC.Core`（`net10.0`、`MPL-2.0`，零外部引用） | 仅作确定性输入→输出：读取 `mmc-pack.json`/`instance.cfg`/补丁、解析组件依赖与冲突、编译运行计划及指纹、校验/序列化 `MmcDefinitionCodec` 持久化结构与诊断。不包含任何主机路径、进程、网络、注册表、P/Invoke 或非确定性时钟/随机数。 |
| **Host** | `PCLCS`（保留原仓库许可） | 拥有物理文件系统访问与 `MmcInstancePaths`（严格分离 `InstanceRoot = VersionRoot`、`GameRoot = InstanceRoot/Layout.GameRootLeaf` 且仅允许已验证 `minecraft`/`.minecraft`、`LocalLibraryRoot = InstanceRoot/libraries`、`RuntimeRoot = InstanceRoot/PCL/MmcRuntime`）；维护稳定 `InstanceId` 与单调递增 `Generation`（改名保留 ID 并升代、克隆生新 ID 并记 `ParentInstanceId`）；协调跨进程 `MmcAdmissionGate`、`MmcConfigurationCoordinator` 键级重基与结构事务、`MmcArtifactPrefetchAdapter`/`MmcArtifactMaterializer`、`CreatorWindowsSessionId` + Windows Job 会话控制。 |
| **Main UI** | `modern/Main`（保留原仓库许可） | 负责版本展示、更新/组件/克隆/删除差异预览、D13 确认与任务阶段提示；不在 UI 层重算依赖图或绕过宿主准入门禁。普通 PCL 实例继续沿用传统目录、隔离与分派逻辑，不自动写入 MMC 身份，无需第二套通用实例框架。 |

- **真实二进制纯度门禁（`tests/PCLLF.Migration.Tests/MmcProfileTests.cs` / `mmc-profile`，已实测 90/90 通过）**：替代原本仅检查 `GetReferencedAssemblies()` 的浅校验，改用 `System.Reflection.Metadata` + `System.Reflection.PortableExecutable` 对实际编译产物 `PCLLF.MMCC.Core.dll` 执行全表及 IL/签名扫描（覆盖 `AssemblyReference`、`TypeReference`、`TypeSpecification`、`MemberReference`、`MethodSpecification`、`MethodDefinition` 的 `PinvokeImpl`/`MethodImport`/`DllImport`/`LibraryImport` 与方法体/局部/异常处理 `IL` token，含编译器生成的 lambda 与状态机嵌套类型）。允许 `MemoryStream`、`SHA256`、JSON、不可变集合、UTF 编码与纯 `Path` 字符串操作；对 `System.AppContext` 仅放行 `[GeneratedRegex]` 源生成 `<RegexGenerator_g>...__Utilities::.cctor` 的只读 `GetData`/`TryGetSwitch` 开关读取（且表级延后条目须在方法体扫描中全部核销），继续拒绝手工/常规 `AppContext` 读写及 `typeof(AppContext)`；其余拒绝宿主/WPF 程序集、磁盘/控制台/网络/进程/环境/注册表/P/Invoke 及 `DateTime.Now`/`UtcNow`、`Stopwatch`、`Guid.NewGuid`、`Random` 等非确定性来源。按 `scripts/Select-MigrationChecks.py` 规则，`PCLLF.MMCC.Core/**` 与 `MmcProfileTests.cs` 变更均路由至 `mmc-profile`。

### 2. 存储、缓存演进与安全不变量评估

1. **所有权、共享缓存与不可信共享执行防护**：
   - 全局 `<游戏根>/libraries/`、`<游戏根>/assets/` 及 `MmcArtifactCacheRoots` 仅作**共享下载来源缓存**；进入启动前均经 `MmcArtifactIo` 核验字节摘要与大小，并经 `MmcArtifactMaterializer`（`StageTargetAsync` / `Io.CopyAsync`）复制至 `.staging/<guid>` 后再以事务发布到实例独占运行视图（`PCL/MmcRuntime/<ViewKey>/`）与不可重取原件库（`PCL/MmcArtifacts/sha256/<hash>`），实例本地库保留在 `libraries/`。
   - **禁止向可变共享库创建硬链接**：必须维持复制+摘要校验模型，防止普通实例或其他工具覆盖全局同坐标库时静默篡改正在运行或待修复的 MMC 实例字节。
   - **禁止祖先目录链接缓存**：`MmcInstancePaths.EnsureNoLinks` 对路径及其逐级祖先保留物理 `Attributes`/`GetFileAttributesW` 检查，不在内存中缓存“已校验目录路径”（`bf4176c2` 教训已固化；未来若再考虑优化，仅凭非可变目录句柄语义或完整内核重验证才可重新论证）。
2. **现行缓存键、失效原则与热点实证判断**：
   - 视图与规划锚点：`ViewKey`（`PCLLF.MmcRuntimeView/v3`）由 `LogicalContext` 与全部 `PreparedInput`（Key、`SourceRevision`、Role、Scope、`RelativePath`、`NativeClassifier`、`ExtractExcludes`、`AdoptedSha256`、`SizeBytes`）计算 SHA256；`MmcNativeViewCache` 额外绑定 `NativeSourceSetFingerprint` 并逐个重算解压后文件 SHA256；`ProfileFingerprint` / `RuntimePlanFingerprint` / `BoundFingerprint` 与 `Generation` 共同阻断跨代数、跨偏好、跨 Java 误复用，任何缓存命中仍必须通过 `ValidatePreparedLaunch` 的最终门禁。
   - **暂不新增持久编译计划缓存或新存储库**：在非正式冻结基准的单项诊断样本 `artifacts/mmc-performance/java8-e0-diagnostic.json` 中，`Backend.PrepareLaunch` 总耗时 `14052.5 ms`，其中 `launch-compile` 仅 **`43.2 ms`**（`Diagnostic.Core.Compile` `12.1 ms`），`launch-sources` 为 `805.9 ms`，主要耗时集中在 `launch-prefetch`（`3083.5 ms`）、`launch-bind`（`2910.2 ms`）、`launch-final-validation`（`1728.3 ms`）及包含物化/验证在内的 `launch-artifact-scope`（`9393.8 ms`，外层含内层不可直接叠加）。Core 编译并非主导热点；过早增加持久化编译缓存或第二套实例表只会扩大失效漏洞、升级迁移负担并增加绕过实时定义/签名校验的风险。是否优化待正式冻结性能矩阵取得实证后再评估。

### 3. 跨卷移动与更广实例布局评估（当前不支持，维持显式边界）

- **现状保持**：`MmcLifecycleAccess.SameVolume` 通过 Windows `GetVolumePathNameW` + `GetVolumeNameForVolumeMountPointW` 显式要求同一本地卷，拒绝跨卷移动、隔离与恢复；普通 PCL 实例继续沿用既有目录与隔离规则，绝不自动写入 MMC ID 或自动迁移旧目录。
- **未来若支持跨卷移动，最低安全协议要求**：
  1. **Copy & Verify（独占 Staging 复制与完整摘要校验）**：双路径排序取得 `Quiesce` + `MmcAdmissionGate` 并确认无活动/待确认会话；在目标卷专用 Staging/预留边界内完整复制实例三根、非活动游戏树、`MmcSource`、`MmcArtifacts` 与玩家 `saves/`，全量核对受管清单与原件 SHA256/大小，双向执行物理 `EnsureNoLinks`。
  2. **Durably Decide & Publish Destination（目标侧先持久化提交决定与路径重基）**：在触发任何源删除前，必须在目标卷完成配置路径重写、代数 `N+1` 的 `InstanceDefinition`、兼容投影与目标侧 `CommitDecided` 的原子落盘；`CommitDecided` 前任意取消或崩溃只清理目标 Staging，**源实例与玩家存档零改动保留**。
  3. **Retire Source & Duplicate-ID Conflict Fencing（源树受控退役与双卷同 ID 防冲突）**：仅在目标发布决定持久化后才退役源目录。若在退役完成前崩溃或重启导致两卷短暂并存同一 `InstanceId`，版本扫描与写入协调必须双向阻断共享索引写入及普通修复，只允许依据唯一带签名/摘要的跨卷迁移日志对已 `CommitDecided` 目标前滚收尾；无有效迁移日志的第三方手动复制同 ID 目录按既有重复 ID 规则保持只读冲突提示（需分配新 ID 另导），严禁凭目录名称猜删源目录。
  4. **Saves & Cancellation Protection（取消排空与存档兜底）**：跨卷大目录复制必须接入 `CancellationToken` 真实异步 I/O 排空；目标空间不足、文件占用、权限拒绝或退役失败时一律保留双方 `saves/` 与个人内容并转为 `NeedsRecovery` 显式诊断状态，禁止静默删档。
- **更广实例布局评估**：当前仅支持唯一规范化的 `minecraft` 或 `.minecraft` 活动游戏根叶名并保留共存非活动树；历史旧 MMC 实例继续严格采用显式选择原 ZIP 创建新管理副本、保留旧实例与玩家存档的转换路径，不扩写普通实例布局。

## 后期性能与架构

- [x] 建立基于冻结真实 `e0b87974` 运行时的 12 点 GTNH 启动准备冷／热正式基线与取消排空实测、三轮合成 2,003 文件私有导入与 1,024 条损坏目录分页基准、Core 编译后 PE 元数据与 IL 纯度门禁（`mmc-profile` 90/90）及存储／缓存／跨卷移动四阶段协议评估；已明确暂不引入持久编译计划缓存或削弱物理路径／硬链接隔离约束（见[性能与架构决策记录](mmc-performance-20261115.md)）。
- [x] 完成 Package D 首轮受控 A/B 性能优化（提交 `32106473`，移除 `LaunchFiles` 丢弃返回值的冗余 `ResolveRelative` 预扫并保持原词法与后续全量 `ValidatePreparedLaunch` 物理/SHA256 核验）：经迁至 `E:` 的冻结双臂 24 点交错 A/B 矩阵实测并按预注册门禁采纳（全四组 `launch-bind` 下降 `-33.78%` 至 `-48.68%`、CPU 下降 `-6.02%` 至 `-17.45%`、总准备 Java 8 冷/热 `-10.86%`/`-7.72%`、Java 21 冷 `-13.93%`、Java 21 热 `+0.45%`，0 不变量违规，候选取消排空 `119.0` / `94.0 ms`）。
- [ ] 基于本轮正式基线定位的预取、`ValidatePreparedLaunch` 及后续完整 GTNH 导入／真实 UI 列表扫描主导成本，按需继续开展后续受控 A/B 优化对比与真实热点下增量边界验证，继续保留来源摘要、修复权威、逐级链接检查与禁硬链接共享库约束。
- [ ] 持续保持普通 PCL 实例沿用原目录与管理方式（不自动写入 MMC ID）；跨卷移动维持显式“不支持”并在未来立项时严格按上节四阶段恢复协议落地。
- [ ] 扩大平台与 Java 实测范围。未来 Java 主版本继续由包声明和实际探测决定，不新增固定的“最高支持版本”判断。

Core 继续采用独立 C#／MPL-2.0，宿主保留原许可；仅实际复用代码、资源、工具或依赖时补齐对应许可与署名。
