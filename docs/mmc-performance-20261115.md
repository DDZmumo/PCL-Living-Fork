# MMC 启动准备性能与架构决策记录（2026-11-15）

本轮按用户的新授权继续后期测量，使用已交付测试程序的运行时代码 `e0b8797414877ecfc065e899d544161ba9cc45af`。此前暂停的候选对比仍按其[历史记录](mmc-performance-probe.md)保留，本轮没有把旧的未完成实验改为通过。

## 样本、输入冻结与测量方法

- 样本是先前保留且有完整归属记录的 GTNH 2.9.0 Java 8／现代 Java 21 两套独立导入实例。主定义仍为 generation 2，原始 ZIP 不重新下载，不修改玩家目录，不运行 Main、Java 或 Minecraft。
- 使用现有 `PCLLF.Mmc.Performance.Probe/Program.cs` 的原始源码，针对从本轮已交付自包含 EXE 提取的真实 PCLCS／Core／Shared 等依赖编译独立冻结探针。源码、实际生产程序集、EXE 与每份结果均记录摘要，没有替代宿主实现。探针及数据位于 Git 忽略的 `artifacts/mmc-performance/`。
- 归属清单 SHA256 为 `9d228b522d80b63b915ee38b6121a2d1cc30a2b5eaeab684c3b4b5e10a91abd0`；同时固定 owner／import／source／primary、实例 ID、generation、包摘要及所选环境。每轮前后重新检查定义、偏好和保留树。
- 调用 `scripts/Measure-MmcPerformance.ps1 -Rounds 3`：每种包三个新进程，每个进程先测第一次准备，再测同进程第二次准备，共 12 点。OS 文件缓存不清理、不受控；“冷”仅指新进程，不代表首次磁盘读取。
- 正式矩阵期间停止其他编译和受控测试。另一次诊断样本与补充测试有重叠，只用于定位阶段，不进入正式中位数。

## 三轮中位数

下表测量 `Backend.PrepareLaunch`，包含读取／编译、预取观察、物化检查、绑定、最终验证和 staging 收尾。CPU 为进程累计值，有界并行工作使其可以大于墙钟时间。

| 包／环境 | 状态 | 样本数 | 准备中位数 ms | 最小—最大 ms | CPU 中位数 ms | 托管分配中位数 MiB | 逻辑读取中位数 MiB |
| --- | --- | ---: | ---: | --- | ---: | ---: | ---: |
| Java 8 | 新进程 | 3 | 12,895.6 | 12,613.2—13,190.1 | 77,140.6 | 1,117.4 | 891.4 |
| Java 8 | 同进程第二次 | 3 | 11,842.4 | 11,749.8—12,149.2 | 73,906.3 | 1,144.1 | 891.4 |
| Java 21 | 新进程 | 3 | 15,583.7 | 12,999.8—16,136.0 | 93,000.0 | 1,136.7 | 1,042.4 |
| Java 21 | 同进程第二次 | 3 | 15,069.0 | 14,392.8—15,350.3 | 92,500.0 | 1,171.9 | 1,042.4 |

分配量是阶段内累计分配，**不是同时存活内存**。整个测量进程（含夹具检查）观察到的最大工作集为 Java 8 的 274.5 MiB、Java 21 的 299.2 MiB。保留树与 staging 的抽样逻辑磁盘峰值分别为 556.6／676.7 MiB；范围仅含已列出的来源、原件、运行视图与共享来源缓存，不包含原 ZIP、模组 payload、存档等整个游戏树。逻辑读取量不是物理磁盘读取量，1 秒抽样也不保证捕获所有短暂磁盘峰值。

12 点全部成功：无修复／代数变更，无 primary／兼容投影改变，无偏好改变，无保留树变化，无元数据请求和下载请求，staging 均已排空。

## 取消与排空

分别对两套实例在准备开始约 1 秒时请求取消，结果未计入成功准备中位数。

| 环境 | 实际请求时刻 ms | 请求后排空 ms | 原定义／偏好／保留树 | staging |
| --- | ---: | ---: | --- | --- |
| Java 8 | 1,013.5 | 171.1 | 保持 | 已排空 |
| Java 21 | 1,015.0 | 313.0 | 保持 | 已排空 |

## 阶段判断与当前决策

单次独立诊断中，Java 8 准备约 14,052.5 ms；来源解析约 805.9 ms、Core 编译约 43.2 ms、预取观察约 3,083.5 ms、绑定约 2,910.2 ms、最终验证约 1,728.3 ms。物化／校验及其收尾的外层作用域包含部分上述子阶段，不能相加。独立的编译诊断在已预热状态约 12.1 ms，同样不能当作首次编译耗时。

1. **暂不新增持久编译计划缓存。** 当前样本中 Core 编译占比很低，增加跨代数、Java、平台和路径失效协议的成本缺乏数据支持。现有已保存来源、制品内容摘要、私有视图与 native 复用继续保留。
2. **后续优化优先观察文件检查、绑定和累计分配。** 对比实验需要同输入、同测量口径的成功基线和边界回归；当前只有新的基线，没有新候选的受控 A/B，因此不宣布提速百分比或新性能验收通过。
3. **不恢复目录信任缓存或共享可变执行文件。** 每次实际 I/O 保留逐级链接检查、大小／摘要核验、取消与排空。原件、可再生视图和全局下载来源继续分开，修复仍以实例采用的原件与定义为权威。
4. **架构保持现有三层边界。** Core 输出规则与计划，宿主负责 I/O、账号／Java、事务与进程，Main 负责 UI。二进制架构约束的实现和状态见[二期与架构台账](mmc-phase2-todo.md)。普通 PCL 实例保留原管理方式。
5. **跨卷移动的评估已形成恢复协议要求。** 当前同卷移动继续沿用；跨卷功能保持不支持，未来需复制并校验、持久化发布决定、目标发布和源退役，以及重复 ID／存档保护验证，详见上述台账。

## 合成 2,003 文件私有载荷导入（独立 Migration 探针）

以下三轮串行测试调用现有源码链接探针 `PCLLF.Migration.Tests.exe --private-payload-import-probe`（每次构造 2,003 个属于实例的自有微小载荷文件、共 `26,947` 字节），**仅用于核实大文件数私有载荷无体复制导入行为与基准耗时，不代表 GTNH 大压缩包完整导入，也不替代真实打包 Main 的导入基准测试**。日志保留在 `artifacts/mmc-performance/private-import-round-{1,2,3}.log`。

现有 `INFO` 行计时起点覆盖 `BulkPayloadImportAsync` 内 `Backend.ImportAsync` 实际执行（含私有预检、放置、发布与收尾清理），不含前置夹具 ZIP 生成及事后逐文件读取断言；`private preflight+placement` 为 `installing-package` 到 `publishing-instance` 阶段差值：

| 轮次 | `ImportAsync` 总耗时 ms | 私有预检+放置 ms | 事务日志变更数 / 新建对象数 | 载荷体复制 (`new-*.bin` 命中) |
| --- | ---: | ---: | ---: | ---: |
| Round 1 | 98,166 | 30,620 | 13 / 13 (`< 64`) | 0 |
| Round 2 | 102,848 | 31,707 | 13 / 13 (`< 64`) | 0 |
| Round 3 | 100,103 | 35,371 | 13 / 13 (`< 64`) | 0 |
| **三样本中位数** | **100,103** | **31,707** | **13 / 13** | **0** |

全部三轮均 `PASS`：2,003 个私有载荷文件总计 `26,947` 字节全部就位且完成发布后校验，事务历史 `prepared.record` 仅记录 13 条宿主/元数据级变更与 13 个 `new-*.bin` 对象，不存在将 2,003 个载荷正文复制到事务日志的“批量体复制”。

## 合成 1,024 条损坏恢复目录分页遍历（冻结程序集探针）

另建 Git 忽略的独立基准 `artifacts/mmc-performance/catalog-probe/`，直接引用从 `e0b87974` 测试包提取的冻结程序集 `PCLCS.dll`（SHA256 `e671a284e49a8a3868cd1d7045527888efaf0be1a8bce82754cbf2342c4cad64`）与 `PCLLF.MMCC.Core.dll`（SHA256 `a81583fc49512bf335d1d136f7c80874781816d65dd29e619eb13c81430713ba`），**不替代宿主源码、不修改产品代码**。在新建标记 GUID 夹具中逆序生成 `1,024` 条仅含 `intent.record = "broken record"` 的目录且保留空 `versions` 目录（构建耗时 `2,440.984 ms`，**不计入测量**），同进程连续观测 3 次首页与全量 32 页有序遍历（默认 `PageSize = 32`）。

| 观测阶段 | 样本 1 | 样本 2 | 样本 3 | 三样本中位数 | CPU 中位数 ms | 托管分配中位数 MiB | 进程工作集／峰值中位数 MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 首页（第 1 页，32 条） | 1,397.1 ms | 1,341.6 ms | 970.0 ms | **1,341.6 ms** | 1,312.5 | 4.481 (`4,698,872` B) | 60.297 / 60.297 |
| 全量有序遍历（32 页 / 1,024 条） | 41,160.0 ms | 31,793.4 ms | 33,112.7 ms | **33,112.7 ms** | 32,406.3 | 142.756 (`149,690,472` B) | 60.297 / 60.297 |

3 次观测全部断言通过：每页 `1 <= Entries.Length <= 32`、`NeedsRefresh == false`、全部条目状态为 `Unknown`，首页 `Previous == null`，全量严格按字典序覆盖全部 `1,024` 个操作叶节点，无遗漏、重复或过期刷新。该结果**仅测得损坏 `intent.record` 夹具下的目录枚举、链接校验、XOR 清单指纹与有界 JSON 失败处理成本，不代表普通 Main 实例／版本列表扫描耗时，也不代表有效大型隔离区正文读取性能**；报告保留于 `artifacts/mmc-performance/catalog-benchmark-e0b87974.json`（临时 GUID 夹具已在退出时清理）。

## 复现证据与范围

本地原始矩阵：`artifacts/mmc-performance/20261115-065109-6df8e7ab0e5f41a9bb2830358ba9e286/`；六份 round JSON、`summary.json`、`formal-e0-receipt.json`、两份 `*-e0-cancellation.json`、三份 `private-import-round-{1,2,3}.log` 及 `catalog-benchmark-e0b87974.json` 保留。冻结程序集及源码摘要见 `baseline-e0b87974/frozen-inputs.json`，测试包身份见 `artifacts/mmc-dev-build/20261115-e0b87974/`。

使用实际 host 工程重建探针、注册自己的独立已导入实例、运行三轮矩阵的通用命令见[探针说明](mmc-performance-probe.md)。本轮不代表 GTNH 大包完整导入、真实 Main 普通实例／版本列表扫描、真实账号登录、JavaWrapper／游戏启动时间，也不覆盖其他作者包、其他 OS 或全部 Java；这些范围继续分开跟踪。

---

## Package D 受控性能优化候选（迁移至 E: 后交错 A/B 实测，已采纳）

> [!NOTE]
> 以下实测基于 Root 将 C 盘 26 棵 MMC 工作树迁至 `E:` 并完成旧目录清零（`cleanup-observer-complete.flag` = `success`）后的同机环境重新冻结夹具完成；此前在 C 盘中断于 `4/12` 的运行记录于 `artifacts/mmc-worktree-relocation-20261115/performance-stopped.json`（不完整未采纳）。**严禁将上文 `e0b87974` 的 C 盘 12 点旧数直接当作基线对比 `E:` 盘候选数据**；本节所有提速与回退判定均只基于本次 `E:` 盘同夹具、同机交错 A/B 矩阵。

### 1. 优化内容与安全不变量保持（提交 `32106473`）

- **运行时改动**（仅 [MmcInstanceBackend.Launch.cs](../PCLCS/Minecraft/MmcInstances/MmcInstanceBackend.Launch.cs)）：移除 `MmcInstanceBackend.LaunchFiles` 中返回值被直接丢弃的并行路径预扫（`MmcInstancePaths.ForEachBounded` 调用 `paths.ResolveRelative` 约 `28.6k–30.4k` 次逐级祖先链接重检），保持 `Add(...)` 原有词法验证语义不变（未对全量宿主机 `TargetRelativePath` 强加额外的 `128` 级限制，防止合法 `128` 级远端工件在拼接 `PCL/MmcRuntime/<view>/libraries` 后被误拒）。
- **物理与摘要安全门禁未削弱**：`LaunchFiles` 返回 6 行后，`PrepareLaunchCoreAsync` 立即在独占 `MmcAdmissionGate` 租约下进入 `ValidatePreparedLaunch`，继续对全部最终运行文件执行逐级物理 `ResolveRelative`（`EnsureNoLinks`）、只读 `FileStream` 打开与完整 `SHA256`/长度核验；清理逻辑亦保留物理重检。
- **专项回归新增**：在 `tests/PCLLF.Migration.Tests/MmcInstanceBackendTests.cs` 与 `tests/PCLLF.Migration.Tests/MmcPrivatePayloadPathTests.cs` 增补 `DeepTargetLaunchValidation`（覆盖 121 段 `'a'` 组路径共 124 段原始相对路径、对应 128 段宿主机目标路径与展开后 131 段 Maven 库路径的验证与字节篡改拒绝），并接入 `--mmc-launch-validation-checks`。

### 2. 迁移夹具绑定与双臂冻结输入

- **迁移夹具清单**：`artifacts/mmc-performance/fixture-e0b87974-relocated.json`（SHA256 `e1be201d74d7eb2bbd8016f1285eb9bb3414e80da7222d2b705999c37f93bf21`），锁定原清单 `fixture-e0b87974.json`（`9d228b522d80b63b915ee38b6121a2d1cc30a2b5eaeab684c3b4b5e10a91abd0`）、清零最终回执 `final-receipt.json`（`6141ff4687afb86c31db4dcdb486bafa771557dd6274c09bd5b1855d213f69d0`）及迁移索引 `moved-worktrees.json`（`9db408b9b221444af4705d565de30f3fbf08ebf3eee490cf05e3da8f66f5d579`）；核对重定位后的 `java8`/`java21` 实例 `InstanceDefinition.json`、`Setup.ini`、`config.json` 中已无 `worktrees` / `C:\Users` 残留引用，原 `owner`/`import`/`source`/`primary` 摘要保持不变。
- **双臂冻结目录**：`artifacts/mmc-performance/candidate-d-relocated/{baseline,candidate}/bin/` 使用同一份重定位探针（`RelocatedBaselineProbe.dll` SHA256 `886fa56e39e1f834bb3f73102a16b6deff3e09dc1a02d8a473e11d9b048d9845`）与逐文件字节一致的 `PCLLF.MMCC.Core.dll`（`a81583fc49512bf335d1d136f7c80874781816d65dd29e619eb13c81430713ba`）及全部其余依赖，唯一字节差异为 `PCLCS.dll`：
  - 基线臂（`e0b87974`）：`e671a284e49a8a3868cd1d7045527888efaf0be1a8bce82754cbf2342c4cad64`
  - 候选臂（`32106473`）：`016b01acae19953e413efb81835bf09a73756a439e9ee9030ad10394f416d167`

### 3. 四组阶段诊断拆分（先于正式矩阵，不计入中位数）

在 24 点矩阵前分别执行 `--repetitions 1 --diagnostic-split` 四次受控诊断（全部退出码 `0`、不变量完整通过）：

| 场景 | 基线 Prepare ms（CPU / 分配 MiB） | 候选 Prepare ms（CPU / 分配 MiB） | 基线 `launch-bind` ms（CPU） | 候选 `launch-bind` ms（CPU） | 基线→候选 `launch-final-validation` ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Java 8 | 16,512.5 (`99,515.6` / `1,115.1`) | 15,711.1 (`92,484.4` / `1,099.6`) | 3,599.6 (`23,140.6`) | 2,066.2 (`12,375.0`) | 2,003.9 → 2,142.8 |
| Java 21 | 15,284.2 (`91,500.0` / `1,141.9`) | 13,114.8 (`76,687.5` / `1,131.0`) | 3,253.4 (`20,843.8`) | 1,665.2 (`9,937.5`) | 1,832.2 → 1,953.2 |

### 4. 24 点交错 A/B 三轮中位数（预注册门禁判定：**采纳 Accepted**）

调用 `Run-InterleavedMatrix.ps1 -Rounds 3`（奇数轮基线→候选、偶数轮候选→基线，每个进程 `--repetitions 2`，共 24 点；证据位于 `artifacts/mmc-performance/candidate-d-relocated/matrix-20261115-094241-30d78b27385e41fe9614e1afeecdaa8c/summary.json`）。

| 包／环境 | 状态 | 臂（每组 3 点） | 准备中位数 ms（最小—最大） | 总准备变化 | CPU 中位数 ms（变化） | `launch-bind` 中位数 ms（CPU 变化） | 分配 MiB / 最大峰值工作集 MiB |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| Java 8 | 新进程冷 | 基线 / 候选 | 14,184.4 (`11,781.4—14,380.0`) → **12,643.3** (`10,493.3—12,804.2`) | **`-1,541.1 ms` (`-10.86%`)** | 81,359.4 → **70,859.4** (`-12.91%`) | 3,063.3 → **1,673.4** (`-45.37%` / CPU `-48.92%`) | 1,110.0→1,100.7 / 204.8→204.6 |
| Java 8 | 同进程热 | 基线 / 候选 | 12,975.0 (`12,163.9—13,842.0`) → **11,973.4** (`9,580.7—12,100.8`) | **`-1,001.7 ms` (`-7.72%`)** | 77,265.6 → **71,234.4** (`-7.81%`) | 3,125.6 → **1,604.0** (`-48.68%` / CPU `-53.36%`) | 1,140.2→1,128.8 / 276.9→276.9 |
| Java 21 | 新进程冷 | 基线 / 候选 | 15,068.1 (`11,847.5—15,245.1`) → **12,969.6** (`10,674.4—13,247.7`) | **`-2,098.4 ms` (`-13.93%`)** | 88,890.6 → **73,375.0** (`-17.45%`) | 2,920.5 → **1,684.7** (`-42.31%` / CPU `-48.26%`) | 1,138.2→1,125.2 / 203.6→207.4 |
| Java 21 | 同进程热 | 基线 / 候选 | 12,263.9 (`11,477.7—14,501.9`) → 12,319.2 (`10,107.6—16,346.2`) | `+55.3 ms` (`+0.45%`) | 76,296.9 → **71,703.1** (`-6.02%`) | 2,549.8 → **1,688.4** (`-33.78%` / CPU `-44.56%`) | 1,168.9→1,157.8 / 299.3→289.3 |

- **门禁核对与异常解释**：
  - 全 24 点均满足：`DownloadRequests = 0`、`MetadataNetworkAttempts = 0`、`ProviderPreferencesChanged = false`、`StructuralAuthorityUnchanged = true`、`ChangedRetainedTrees = []`、`StagingDrained = true`；保留树/staging 抽样逻辑峰值与逻辑读取同为 Java 8 `556.6` / `891.4 MiB`（`934,697,935` B）、Java 21 `676.7` / `1,042.4 MiB`（`1,093,000,777` B）；基线与候选产出完全一致的 `LaunchInputs`（Java 8：`1464` 项、`367,466,426` B、清单 `cc7b5f87fb740c7fb1e3cd7151225495d839a2ebcdad03f11e891f7bb0d0fe7e` / 绑定 `fd54194e8efea99f0ad7cfb8512f7319d932b6dfe7137f06d059476ffb09b284`；Java 21：`1550` 项、`433,440,216` B、清单 `a66cdda8186edc6fabb455133874395c3585a55755bb408c12de938dd0823fcf` / 绑定 `130ebb82f756813b7b49bd6f524b79cd48d7210434ed1d3ccd92489338686592`）。
  - `Java 21` 同进程热状态受第 2 轮单点 `16,346.2 ms`（该点 OS 未清缓存/调度波动下整体 CPU 升至 `99,125 ms`，`launch-final-validation` 升至 `2,590.3 ms`）拉动中位数为 `+55.3 ms`（`+0.45%`），远未触发回退门槛（`> 10% 且 > 200 ms`），且第 1、3 轮同口径样本均为候选更快（`12,319.2` / `10,107.6 ms` 对比基线 `14,501.9` / `12,263.9 ms`），四组全场景 CPU 中位数全部稳定下降 `-6.02%` 至 `-17.45%`，`launch-bind` 墙钟中位数全面下降 `-33.78%` 至 `-48.68%`（CPU 下降 `-44.56%` 至 `-53.36%`）。依预注册规则判定 **ACCEPT**。

### 5. 候选取消／排空与安全性实测收口

- **候选取消排空**（`--cancel-after-ms 1000`，`cancel-candidate-java8.json` / `cancel-candidate-java21.json`）：

| 环境 | 实际请求时刻 ms | 请求后排空 ms | `PrepareLaunch` ms | 原定义／偏好／保留树 | staging / 网络 |
| --- | ---: | ---: | ---: | --- | --- |
| Java 8 | 1,002.4 | 119.0 | 1,085.6 | 保持（`StructuralAuthorityUnchanged=true`、`ProviderPreferencesChanged=false`、`[]`） | 已排空 / 0 |
| Java 21 | 1,007.6 | 94.0 | 1,074.2 | 保持（`StructuralAuthorityUnchanged=true`、`ProviderPreferencesChanged=false`、`[]`） | 已排空 / 0 |

- **功能、安全探针与 CI 选择器检查**：
  - 私有 `.NET 10` SDK 编译 `modern\PCLCS\PCLCS.Modern.csproj` Release **0 警告 / 0 错误**。
  - `PCLLF.Migration.Tests.exe --mmc-launch-validation-checks` **4/4 通过**（含 `DeepTargetLaunchValidation`）。
  - `PCLLF.Migration.Tests.exe --private-payload-path-checks` **9 通过 / 2 不可用 / 共 11 项**（`source-link` 与 `existing-source-link` 因当前 Windows 非管理员账户缺少创建文件符号链接特权抛出 `PayloadLinkFixtureUnavailableException`，如实计为不可用、未计作通过）。
  - 在隔离目录执行的冻结安全探针 `Probe.exe` 退出码 `0`（`artifacts/mmc-performance/candidate-d-launchfiles/directory-cache-probe.json`：`FreshCheckRejected=true`、`CachedCheckAccepted=false`、`HashReaderRejectedAncestorReplacement=true`、`PublicationRejectedAncestorReplacement=true`、`PublicationAppearedOutsideApprovedInstance=false`）。
  - 隔离重跑 `PCLLF.Migration.Tests.exe --group mmc-configuration` **36/36 通过**；与外部并发进程重叠期间的宽泛探索跑录保持实情 `216/220`（2 项非特权 symlink 不可用 + 2 项 `MmcCommandExecutionTests` 在并发条件下于 `MmcCommandOperations.ReconcileAll` 保守 `TokenScan` 判定为 `Unknown`）。
  - `Test-MigrationSelection.py` **176/176 通过**。
  - `artifacts/mmc-dev-build/20261115-e0b87974/` 测试包仍绑定 `e0b87974` 未改动；是否基于提交 `32106473` 重新打包由 Root 统筹安排。
