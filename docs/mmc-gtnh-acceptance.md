# GTNH 显式实包导入验收

此入口需要本机原包、真实 Java 与外网，**不属于默认 facade 分组或 CI 门禁**。它测试真实 Main 的 managed MMC 导入、最终配置和制品布局；导入完成不能当作游戏启动、世界或修复验收通过。

在测试程序 `Main` 的默认分组解析之前显式接入：

```csharp
if (MmcGtnhAcceptance.TryRun(args, out int gtnhExit)) return gtnhExit;
```

构建完整真实 Main/facade 产物后手动执行：

```powershell
dotnet tests/PCLLF.Native.Facade.Tests/bin/Debug/net10.0-windows/PCLLF.Native.Facade.Tests.dll --mmc-gtnh-acceptance
```

可用 `--gtnh-case java8` 或 `--gtnh-case java21` 单独运行一份；默认分别运行两份。入口固定只读使用：

- `D:\User\Downloads\GT_New_Horizons_2.9.0_Java_8.zip`。
- `D:\User\Downloads\GT_New_Horizons_2.9.0_Java_17-26.zip`。

原包 SHA256 与 [P0 实测基线](mmc-samples.md)逐字节核对；不重新下载发行 ZIP，也不读取玩家实例、存档或账号。fixture 的 Java 列表只含真实安装的 Java 8 与 Java 21，仍由实际 Main Java 探测和包策略过滤选择；入口记录探测路径、版本与最终编译环境。

每次运行在仓库已忽略的 `artifacts/mmc-gtnh-acceptance/<时间-GUID>/` 下创建独立 case、游戏根、缓存和私有 profile。另一个 `PclNativeFacade-<GUID>` 临时根保存原隔离 fixture 身份与完整编译产物；两处均写所有权记录并永久保留。父进程核对 nonce/PID/根路径后只结束自己创建的子进程，不递归删除验收数据。后续实机启动可继续使用报告中的实际实例根。

后续重跑可显式传入 `--gtnh-reuse-cache-run <旧验收 run 根>`。仅接受本仓库验收目录的直接子 run，核对 run/case 的所有权标记并拒绝重解析点祖先；每个新实例和 profile 仍使用全新目录。它只复用旧 run 对应 case 的 metadata、libraries、assets 和 source-cache，继续执行原有来源/摘要校验，不把旧的半成品实例或事务目录直接发布。旧日志、原包和实例目录保留。

新版 runner 等待真实 receipt/子进程退出，不按总耗时判定失败；UI 泵使用有限等待片段，未达到 idle 只结束本片段、继续等待实际结果；父进程周期输出存活提示。相同 NetFile 计数不会重复宣称有新进展。

`stdout.log`、`stderr.log` 和 `import-result.json` 记录每份结果，`source-and-java.json` 保存原包/实际 Main 身份、Java 探测和摘要。BYTES 日志来自原 ZIP 实际读取、后端真实解包/物化回调与真实 NetFile 计数；总量未知时写 `unknown`，不据此编造完成百分比。最终核对已发布定义/兼容投影、Java 策略、入口、参数、库顺序、实际文件 SHA256、owned 运行库路径和 native/asset 派生记录。

初版为每个 case 使用独立 Minecraft 根；它证明逐包独立导入，不覆盖“同一游戏根混合普通实例与两类 MMC”的验收。该项需要随后在保留的目录上通过受支持的移动/克隆与实例管理入口补做，不通过手改定义或删除缓存来冒充混合回归。真实游戏、世界、修复和再次启动继续单独记录。


已发布实例可独立复核，不再解包或下载：

```powershell
dotnet <真实 facade 路径> --mmc-gtnh-post-publish-check --gtnh-case java8 --gtnh-owned-run <保留 run 根>
```

`java21` 对应现代包。此入口核对原真实 Main 身份、已发布 definition/projection、运行库和 native 的实际 SHA256，再运行真实版本列表 worker 并等到 dispatcher idle。只对两个必须读取的旧账号 schema key 提供空私有 store，其余 Registry/设置 guard 保持启用；不会读取用户账号、重新导入、修复或启动游戏。每次复核生成独立 `post-publish-<case>-<GUID>` 证据，不改原导入 receipt 或日志。新的导入 runner 同样等待列表 worker 和 UI 回调完成后才检查零 forbidden。

## 2026-11-08 真实 Java 8 游戏验收

继续使用已成功导入的独立 Java 8 实例，没有重新下载或导入发行 ZIP。
实际运行 Java 为本机 `jre1.8.0_491`，6144 MiB 最大堆、1024 MiB 初始堆，
通过真实 Main 执行器、owned classpath/native、会话与退出监视器启动。
账号边界使用验收专用离线资料，不读取玩家账号。

| 项目 | 已取得的证据 |
| --- | --- |
| 首轮主菜单、进入世界 | 用户明确确认“成功，进入主界面并且进入世界”；日志出现实际本地服务端和维度加载，建立 `New World` 存档 |
| 正常退出 | 两轮 Java 退出码均为 0；两次 `Exited.record` 已归档，mutable 临时目录清理、Java pin 释放均为 true |
| 修复 | 在实际退出及收尾后执行专用后端，`repair-2.json` 记录同 ID、generation 2 成功 |
| 再次启动与原世界 | 用户重开 `New World`；第二轮日志记录同名世界加载、本地玩家加入及保存，随后正常退出，Watcher 为 Ended |
| 现代 Java 包 | 已使用 Java 21 完成同样的实机闭环；见下节的失败修正与最终证据 |

当前证据根是集成工作树的
`artifacts/mmc-gtnh-launch-acceptance/20261108-114527-a1eae77059834dd2a49c48c63c816948/`，
保留两轮真实进程/脱敏计划、游戏日志、两轮退出、修复及用户确认记录，
`java8-actual-closed-loop.json` 汇总同一实例/存档、退出与清理事实，验收宿主随后也以 0 退出。
用户按 Esc 停止电脑操作后，明确选择自行操作游戏界面；后续代理只读进程与日志，不再控制界面。

此前另一次在子代理持有的验收链中，游戏与宿主同时突然退出，未出现正常退出收据或明确崩溃报告。
该轮保留为中断，不计成功，也没有把尾部非致命模组告警推定为退出原因。
已用真实进程身份检查和会话恢复流程确认退出并收尾，再由主代理持有本轮命令。
原中断证据位于子工作树 `mmc-core-parsing` 的
`artifacts/mmc-gtnh-launch-acceptance/20261108-113335-9cf8bf9390c541a18df05bb8cfc544a7/`。

## 现代 Java 21 的失败修正与闭环

第一轮在创建 Java 进程前被旧 Forge Java 上限拦截。只读对照确认包内 Minecraft 原始声明的 Guava 15
已由 Forge 的 Guava 17 正常替代，而完整启动链证明误要求被替代的旧 JAR 仍在最终 classpath。
已按编译器相同的 Maven identity、extension、role 与较高版本关系修复；原有最终输入锁、类观察、
入口归属和其他硬约束仍检查。最小回归由 30/31 变为 31/31，独立只读复核未发现新增 P1/P2。

第二轮真实 Java 21 和 RFB 已启动，但 LWJGL3 加载 SDL3 时退出：错误为 Windows `206`，
实际 session/extract 路径超过原生加载器可接受长度。该轮 Java 返回 0，但 Watcher 为 Crashed、
未进入主菜单，因此明确计为失败；退出后的会话、临时目录和 pin 已正常收尾，宿主也已收到 finish。
没有通过挪动样本目录或改变包内容把失败计为成功，随后实现了通用短 mutable/native 目录修复。
机制对照 [LWJGL 3.4.3 WindowsLibrary](https://github.com/LWJGL/lwjgl3/blob/3.4.3/modules/lwjgl/core/src/main/java/org/lwjgl/system/windows/WindowsLibrary.java)。

证据分别保留于集成工作树：

- `artifacts/mmc-gtnh-launch-acceptance/20261108-121713-e67c536f106f420ebb26c4b08bda10b9/`：旧约束拦截、无游戏进程。
- `artifacts/mmc-gtnh-launch-acceptance/20261108-122816-1a0c2367ad6b45d9a511f137e217e977/`：实际 RFB/native 加载失败、Watcher/退出与清理记录。

短目录按实例与会话隔离，收据只持有已知布局和 host authority 指纹，不接受自报删除根；
默认位置由当前用户的 LocalAppData 计算，验收采用已验证 GUID 私有 profile 的授权短目录。
旧 v1 inside 布局继续可读，启动前、失败放弃、已验证退出和宿主异常恢复使用同一精确 binding。
真实 Java21/LWJGL 探针核对原 JAR 与 SDL3 字节不变：308 字符路径报 206，生产 owned 短路径 119 字符加载成功。

最终实机证据在集成工作树：
`artifacts/mmc-gtnh-launch-acceptance/20261108-131033-5770e0aaaac3405b8229b14de1e737a1/`。
用户手动确认首轮主菜单和新世界，随后专用修复发布同 ID 的 generation 2，第二轮日志记录再次加载
`New World` 并由同一离线玩家加入。两轮正常退出码均为 0，Watcher 均为 Ended，
两次 receipt 归档、短 mutable 清理及 pin 释放均成功，最终验收宿主退出码也为 0。
`modern-java21-actual-closed-loop.json` 汇总上述事实与实际 mutable binding。

本次明确通过的实包 Java 组合是 Java8 发行包＋Java8，以及现代发行包＋Java21。
现代包声明的 Java17/25 尚未作实包游戏验收；Java26 的后续有限反馈见下节。
全平台、完整性能矩阵也未据此宣告通过。
固定 Prism 提交的实际 C++ [原生解析/计划子集对照](mmc-prism-differential.md)另行记录；
它不运行完整 Prism resolver/应用/游戏，不能替代本页的实机闭环或全应用兼容验收。

## 2026-11-09 用户开发包 Java26 反馈

用户在 `81796b2c` 开发包自行导入现代原包，实例位于独立的用户游戏根；代理只读日志与进程。
首次 Java26.0.2 进程 `67112` 的 Windows Application Error/WER 记录显示
`SDL3.dll 3.4.14.0` 在偏移 `0x15d921` 发生 `0xc0000005`，随后 `0xc000041d`，
启动器观察到退出码 `-1073740771`。仅凭这个故障模块不能确定 Java、驱动或模组中的具体诱因。
同版本 SDL native JAR 的 shared/owned 字节相同，不以下载文件缺失解释原生崩溃。

用户重新指定 Java26 后，真实进程 `66924` 于 16:15 完成声音／材质加载，用户确认进入主菜单，
16:16 正常退出，退出码 0。三份会话收据摘要有效，活动记录与本轮 mutable scope 均已清理。
这证明同一 Java26 能完成此轮主菜单与退出，没有据此认定首轮崩溃已修复，
也没有新增 Java26 的建世界／修复／重开世界结论。
脱敏诊断保留于 `artifacts/mmc-goal/diagnostics/user-launch-67112.json`。

本轮另确认设置页自动 Java 仍按普通 MC1.7.10 显示 Java8，而 MMC 启动按组件声明使用 Java26；
设置展示与实际选择的入口统一作为独立缺陷修复，不能通过硬编码 GTNH 或排除 Java26 掩盖。
