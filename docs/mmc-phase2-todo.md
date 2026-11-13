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
  Windows 故障模块、退出码及 DLL 字节证据见 `artifacts/mmc-goal/diagnostics/user-launch-67112.json`。
- [ ] Authlib／Nide 用真实账号和服务验收。自编 agent 的 JVM premain、Main 传参、取消排空和清理证据已存在，不能替代外部账号验收。
- [ ] 补外部平台真实在线更新、大包组件修改、导出重导和生命周期 UI 中断测试。
  在线更新、组件编辑、克隆、改名、同卷移动、还原、删除／恢复、导出及主动原 ZIP 转换已有实现，
  此项是补实测，避免重复安排为“尚未开发”。
- [ ] 更完整的 Prism 原生对照，包括完整 resolver、应用启动和实际提取器；现有 C++ 解析／计划子集差分保留其明确边界。
- [ ] 持续检查库补全／修复、普通实例和 MMC 实例同根使用时的互不覆盖，以及活动会话下的实例间资源隔离。

## 后期性能与架构

- [ ] 用大包基线定位导入、校验、物化和列表扫描的主要成本，再决定是否优化；
  当前私有载荷路径检查消重只有单次自造包 A/B 证据，不外推成所有 GTNH 场景的收益。
- [ ] 再评估元数据／制品缓存、物化复用和磁盘成本，继续保留来源摘要、修复权威与取消后真实排空。
- [ ] 评估更广的实例布局、跨卷移动和显式角色迁移；普通 PCL 实例继续保持原目录与管理方式，
  不因这份待办自动写入 MMC ID 或自动搬动旧实例。
- [ ] 扩大平台与 Java 实测范围。未来 Java 主版本继续由包声明和实际探测决定，不新增固定的“最高支持版本”判断。

Core 继续采用独立 C#／MPL-2.0，宿主保留原许可；仅实际复用代码、资源、工具或依赖时补齐对应许可与署名。
