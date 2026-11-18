# MMC 基本可用验收

本轮先验收导入、启动、专用补全及失败恢复；完整路线仍由
[全任务清单](mmc-completion-ledger.md)跟踪。性能工程后置，非 GTNH 实包游戏验收按用户决定交由用户在新构建上自行尝试。

| 项目 | 当前证据 | 状态 |
| --- | --- | --- |
| GTNH 2.9.0 Java 8／Java 21 | 两份独立实例完成主菜单、创建世界、正常退出、修复、重开世界、正常退出 | 已有实机证据，见 `mmc-gtnh-acceptance.md` |
| 普通 Forge／Fabric | 真实后端导入、依赖与库、离线修复、再次准备、世界／偏好保留 | 受控后端通过，不能称为实包游戏已测 |
| Main 补全入口 | 实际 Controller／Loader／RepairAsync、缺 JSON 与损坏 owned JAR、身份竞争、取消 | 已通过 |
| Main 导入恢复／重试 | 实际恢复按钮、目录、原记录校验、同 SHA 不同名称、取消排空、篡改拒绝 | 已通过 |
| 真实进程崩溃恢复 | 自有 child 在 Prepared／CommitDecided 持久信号后核验 PID／创建时间／镜像／双 nonce，再实际 Kill(false) 并调用真实恢复 | 三个场景实测通过，未用异常模拟代替终止 |
| 自包含程序结构 | 官方构建包装器、真实 bundle 身份、六个主要程序集与 publish 输出逐字节 SHA256 一致、ZIP 内容与 EXE 摘要 | 已通过；未执行程序 |
| 程序冷启动 | 用户在本构建路径问题中明确回复“能进入主界面” | 用户实测通过；未代替账号或游戏验收 |

## 2026-11-09 开发构建

源码：`10543184`，`codex/mmc-compatibility`；Windows x64，`Snapshot`／`dev_edition`。
使用 `scripts/Build-ModernRelease.ps1 -NativePayload` 的原凭据生成、构建租约与 finally 清理，
输出通过真实 `ModernBundleIdentity` 只读检查。

保留目录：`artifacts/mmc-dev-build/20261109-10543184/`。

- 用户 EXE：`package/PCL-Living-Fork-dev_edition.exe`，188,798,148 字节。
- EXE SHA256：`61d7dff1afd1b9f4d156547cd867f68f694608765546bc80c70791e16f281cdb`。
- ZIP：`PCL-Living-Fork-dev_edition-win-x64.zip`，80,809,591 字节。
- ZIP SHA256：`3b7a98ba8578151fae2fb031f8144747b21d1d77fae8d87997ad84b75434130f`。
- 包含 Core 对应源代码 ZIP、完整 MPL-2.0 文本、宿主原许可及使用说明。
- `bundle-verification.json` 与 `package-verification.json` 记录结构检查；`ExecutedProduct=false`。

构建和结构检查不替代冷启动、账号登录、真实游戏或全部 MMC 特性的验收。
本次没有推送、合并 main 或上传 Release。

2026-11-09 用户确认：新构建可进入主界面。代理没有操作桌面或代替用户进行点击；
只读 bundle/package 报告中的 `ExecutedProduct=false` 仍只描述那些结构检查自身，
冷启动证据为独立的用户确认，不改写既有检查记录。

## 2026-11-09 组件与维护入口开发构建

源码 `e059eee4848b9fc1635112a9da13248d3e30b389`，Windows x64 自包含 Snapshot。
路径：`artifacts/mmc-dev-build/20261109-e059eee4/package/PCL-Living-Fork-dev_edition.exe`。
本批包含组件／补丁／Java 编辑、可靠平台来源与在线 MMC ZIP 更新、隔离实例永久清理。
实际 Main 组件编辑十九组、绑定／在线任务七组、清理四组以及旧删除／列表回归分批通过；
平台后端十项、清理后端十三项与长路径专项通过。新增主体及入口由 GPT-6.1 Sol max 定向复核。

| 文件 | 字节数 | SHA256 |
| --- | ---: | --- |
| `PCL-Living-Fork-dev_edition.exe` | 189052100 | `9efd5dd4789468eb16fdfebc013c91bb695e19f1a0cae18f132a2c08cc367a49` |
| `PCL-Living-Fork-dev_edition-win-x64.zip` | 78329930 | `678cb8a63e9e12907459948d1626382338657d13559d63368ce2e41c24355b68` |

`bundle-verification.json` 对真实发布输出的六个主要模块逐字节核对；`package-verification.json`
核对 ZIP 的五个条目及所有字节。Core 源码 ZIP 中 37 个文件逐一匹配固定提交的 Git blob，
保留原 LF 字节并检查 CRC；源码、完整 MPL-2.0 文本及宿主原许可均随包提供。
这两份结构报告的 `ExecutedProduct=false` 保持原义；新包尚无用户冷启动、外部平台或新游戏结论。
之前“能进入主界面”的用户确认仅对应 `10543184`，不沿用为本包证明。

## 2026-11-09 用户 Java 选择故障修复

用户在 `e059eee4` 的任务管理／Java 列表截图证明程序已进入主界面，但两份 GTNH 原包均在
`MmcInstances.SelectJavaAsync` 报无兼容 Java。只读日志显示旧 Java 24 被检查，当前列表已有
Java 8／17／21／26；旧全局 `LaunchArgumentJavaSelect` 仍保留历史路径。
现代 PCL 已将这个旧值仅用于一次迁移并转成列表优先级，MMC 却又把它读回为硬性路径限制。

`6af1b6fed10d7ce6ba747bb5993b63427f20dff1` 修复导入与启动继承两处读取，按当前列表与包声明筛选；
实例显式强制、范围和目录模式继续保留。真实 Main 负例先复现原错误，修后候选组与偏好组均通过。
只读核对两份用户原 ZIP 完整 SHA256、固定元数据后，再通过真实 Main 方法分别选到 Java 8／21；
没有重新下载包、发布实例或运行游戏。另有 Java 27／31 声明与硬约束的纯策略回归，`mmc-profile` 81/81；
不把该策略证明写成 Java 27 实机启动。

修复包：`artifacts/mmc-dev-build/20261109-6af1b6fe/package/PCL-Living-Fork-dev_edition.exe`。
EXE 189052100 字节，SHA256 `f3551a1a43acb449135fe4712d6c392db66eaaf73ce4b3d615595679f7e21b74`。
ZIP 78331087 字节，SHA256 `6101a05a03d9d3d1fe92d49ac6f1a552af97d99fad5478d7528b4b1a05293c2a`。
六个发布模块、五个 ZIP 条目及 37 个 Core 源码 Git blob 均已核对；没有复制用户运行目录里的日志或设置。
Sol max 定向审查已关闭此修复；新包完整导入／冷启动结果仍须用户单独反馈。

## 真实进程终止后的恢复

只结束测试父进程自己 `Process.Start` 的 child；不枚举或终止用户程序、Java 或开发构建。
持久 checkpoint receipt 和实际 journal／预期文件字节在终止前已核对；child 返回 Windows 强制终止码 -1，
没有经过托管正常返回/finally，随后确认原 producer.use 摘要不变且锁由 OS 释放。

| 场景 | 实际结果 | 保留证据根 |
| --- | --- | --- |
| Prepared，child 59972 | 活动租约拒抢；实际终止后 Recover 回滚并要求新导入，同名／错 SHA 拒绝，同 SHA 新名称发布新 ID/G1；原 parked、旁目录和世界不变 | `D:/Temp/PCLLF-MmcRealCrash-7035ffdf169146d5bceab3adeb966265` |
| CommitDecided，child 6792 | 原 ZIP 移到自有 offline-evidence 后恢复成功，发布原 ID/G1，9 个 staged target 共 9301 字节逐字节匹配；标记清理和旁目录保持正确 | `D:/Temp/PCLLF-MmcRealCrash-f1d92db35a834766b7b4f782e1673208` |
| CommitDecided 后外部变化，child 5724 | unknown-only、同长度作者字节改变分别触发实际拒绝；重复恢复仍拒绝，世界／未知文件／改动后文件保留，没有 final 发布 | `D:/Temp/PCLLF-MmcRealCrash-e8790f3323134a229e9b3eedac2d966a` |

每根包含 `summary.json`、`acceptance.jsonl`、父子身份与 checkpoint receipts、实际预期字节及 stdout/stderr。
首轮测试观测 helper 的文件共享模式错误导致的失败现场单独保留，不计为产品失败或通过；
修正的是只读观测方式，生产者的独占写租约没有放宽。最终测试项目构建为 0 警告／0 错误。
上述测试无外网、JVM、Minecraft 或桌面操作，不等同于全部 MMC 后续功能验收。
