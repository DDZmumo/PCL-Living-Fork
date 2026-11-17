# MMC 隔离区永久清理候选

当前实现仍在验证，Main 没有永久清理入口，普通删除仍只移动到可恢复隔离区。尚不能把此候选报告为已完成的玩家数据清理功能。

逐条目 TxF 是为了解决 Windows 共享模式只保护单个数据流的问题。仅对默认流取得禁止写入的句柄，仍可能允许另一个程序新建 ADS；最后一次检查 ADS 后直接设置普通 delete-pending 也不能消除竞态。先设置普通 delete-pending、再检查并撤销的方案同样不成立：检查前被终止时，关闭句柄可能已经删除未确认的数据。

选定候选仅使用本地 NTFS 的短 KTM/TxF 事务：取得绑定事务的 writer 句柄后，在同一事务视图中核对文件身份、默认流摘要和完整有界数据流清单；目前只接受默认数据流。验证后暂存该条目的删除，关闭条目句柄，保留事务直至提交。提交前进程死亡应回滚本条目；提交后只允许删除已经验证的条目。外层 durable purge decision 仍负责记录剩余条目，并在恢复时继续前滚。目录和根目录也需要流清单和最终空目录检查。

原语的真实小样本验证见下文：外部 writer 明确使用 `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE` 时，ADS 新建/更改在 writer 存活期间及条目句柄关闭、事务尚未提交期间均被拒绝，提交前/后终止也分别证明回滚/已批准删除。

之后已静态接入 `PurgeForward`：文件、目录、根目录和未决准备记录的撤回均通过短 TxF，不存在普通 disposition 删除后备路径。预览和执行扫描均拒绝已有 ADS/EFS；不可逆外层决定前还会用主定义执行无修改的 transaction-writer 能力检查。每条目在 writer 上重验身份/默认流清单/摘要，暂存删除并关闭条目句柄后才提交；`EntryStaged` 断点保留事务，用于确认该间隙的排他和真实终止回滚。根目录最终删除同样使用该原语并检查为空。已适配的实例所有权许可可验证当前事务 marker writer，避免为了读取自身 marker 而放松其它 pending/session 守卫。

Main 入口已接入并通过下述四项隔离样本验证。原语与后端结果分别记录，均未操作真实玩家数据或用户桌面。

每个原生句柄均限制为本地 NTFS，并拒绝零或全一的 64 位文件 ID。ReFS、网络文件系统、TxF 缺失/禁用、EFS 或写入冲突均拒绝处理或保留剩余条目，禁止退回普通 disposition 删除。此可选功能不是导入、修复或启动的依赖，也不修改服务、ACL、卷设置或 TxRM 配置。

微软[说明 TxF 的数据流采用文件级锁，并让绑定事务的 disposition 参与事务](https://learn.microsoft.com/en-us/windows/win32/fileio/programming-considerations-for-transacted-fileio-)；[最后一个事务句柄在提交前关闭会回滚](https://learn.microsoft.com/en-us/windows/win32/api/ktmw32/nf-ktmw32-createtransaction)。微软也[不推荐新项目依赖 TxF，并提示未来 Windows 可能移除此 API](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf)。因此运行时能力不足时只能明确拒绝永久清理，保留普通隔离/还原路径。

2026-11-09 首轮只编译 Migration 项目并运行 `MmcPurgeTxFPrimitiveTests.SmokeCases()` 的一个文件原语用例：固定 SDK 10/c774，编译 0 警告/0 错误；已有 ADS 修改和新建请求在 writer 存活时以错误 32 被拒，关闭条目句柄、事务仍活动时的新 ADS 请求以 6800 被拒，随后提交删除已验证文件。此结果尚不覆盖目录、根目录、终止恢复或完整实例清理。原始日志位于工作树已忽略目录 `artifacts/mmc-purge-txf-proof/20261109-035916-e3a5b423f5cd47f095c61f60cc602b79/`。

随后单节点二编同样为 0 警告/0 错误，4 个 `BoundaryCases()` 分进程通过：文件/目录/根目录已有 ADS 被拒且原字节保留；目录/根目录的 stage 后关闭句柄间隙仍以 6800 拒绝 ADS，外部新子目录不能加入；已有 share-delete ADS writer 阻止事务 writer；3 个 parent 创建并核对 GUID/nonce/ready PID 的子进程分别在 writer、stage 后/commit 前、commit 后被终止，前两者回滚并保留主体和未批准流，后者只删除已验证主体。原始日志位于 `artifacts/mmc-purge-txf-proof/20261109-040105-boundaries-b56ff3d5f5df4644bc3440d2aa675619/`。这些是原语证据，尚不是完整实例永久清理验收。

完整后端首次运行在只读预览的深层原生打开处遇到 Win32 错误 3，未到永久清理决定，立即停止。失败日志保留在 `artifacts/mmc-purge-txf-proof/20261109-051012-full-8b823f9586424808a57d9b2b2aaf25d0/`。原异常没有记录具体失败路径，fixture 随后按既有清理逻辑移除，因此不能把推算路径当作该次实测路径；根据固定目录布局，私有来源对象路径在此 TEMP 根下为 277 字符。修复只在 native 参数加入扩展前缀，逻辑路径、所有权、文件 ID 与最终 handle-path 检查保持原样。

`9092ba28` 的后续运行固定 SDK 10/c774、单节点、关闭共享编译，Migration 编译为 0 警告/0 错误（10.22 秒）。新增长路径用例实际记录绝对逻辑路径长度 324，ordinary/TX 句柄保持同身份、同摘要并成功提交删除。随后 13 个主场景各自独立进程、独立日志全部通过：完整导入后隔离再清理；错误身份/过期预览；未决事务/会话/生产者；决定前取消；实际 catalog 撤准备；决定后恢复；外来文件/空目录/异身份主定义/链接；文件/目录/根目录的 15 种 ADS 时机；真实 writer/只读文件失败；同句柄及 late empty-dir；损坏计划关闭还原入口；四个自有子进程的 prepared、decided、entry-staged、primary-removed 终止恢复。最后一组保留原 20 秒 ready/5 秒 exit 限额，按 GUID、nonce、ready PID 核验后只终止 parent 创建的进程。

全部整合证据位于 `artifacts/mmc-purge-txf-proof/20261109-051352-full-longpath-eca8a3d24e0144ea8a5a69f45d711687/`：`build.log`、`long-path.log`、`00-*.log` 至 `12-*.log` 和 `case-summary.log`。没有重复运行已绿原语矩阵，没有修改 Windows/TxRM/ACL 设置，也没有使用真实玩家数据、JVM、网络或用户桌面。

## Main 清理入口

正常删除先移入隔离区；在“MMC 隔离区与恢复”选择完整隔离实例后，可选择恢复或永久清理。
清理任务先重新核对目录记录的指纹、实例 ID 和代数，再读取被固定的 intent 生成完整快照。
确认框显示精确隔离路径、文件数、体积与存档等数据范围；只有明确选择“永久清理”才提交。
确认期间不持有实例准入或配置静默化。最终完整库存复核结束后才进入不可取消的决定阶段，
后续失败保留剩余内容和恢复记录。未决准备先撤回；已经决定的清理只能继续原决定。

Root Main 构建为 78 个既有警告／0 错误，Native 为 0／0。四个原 60 秒子进程全部通过：
真实 Loader 成功清理、拒绝确认、最终决定边界取消、决定后取消继续完成。
测试核对确认的持久快照、shared busy／真实 worker 收尾、Purged 列表状态，
并保留旁实例、共享资源、外部缓存、模拟 Java 与重建原路径中的新世界。
GPT-6.1 Sol max 对新入口与后端接缝只读审查，未发现新增可证 P1/P2。
以上仍不是实际玩家隔离区或断电实测结论；TxF 不支持时不会退回普通删除。
