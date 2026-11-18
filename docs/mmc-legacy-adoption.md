# 旧 MMC 实例的主动管理副本

这是用户主动选择旧实例和对应 MMC ZIP 后创建的新副本。旧实例保留原路径和普通 PCL 管理方式，不写入新实例 ID，不批量接管普通版本。

## 来源意图

`StrictVerified` 核对旧目录保留的 MMC 清单、包内补丁和配置与所选原包的内容关联。它不认证发行者签名，也不替代真实游戏验证。没有这些记录时，普通 Forge JSON、MC 版本和文件名不能证明历史 MMC 来源。

`CreateCopyWithExplicitSourceRebinding` 是另一个明确的用户动作：按这次选择的合法 MMC ZIP 作为新来源创建副本，不声称验证旧历史来源或运行兼容。预览显示旧/新组件、逐个游戏文件差异和来源问题。UI 提供单独的重绑定按钮和再次确认；后端确认指纹同时绑定预览、意图、目标路径及名称，不能把严格确认悄悄换成重绑定。

两种意图的新 `Origin.PackageSha256` 都来自实际读取的原 ZIP。当前旧文件只属于个人快照；它们不会反推或改写作者基线、组件元数据或制品锁。

## 目录角色

| 旧内容 | 新副本处理 |
| --- | --- |
| 明确隔离的旧平铺游戏目录，或唯一的 `minecraft/.minecraft` 子目录 | 按原包声明的独立游戏子目录放置，不合并游戏树 |
| 模组、个人配置、存档、截图、服务器及游戏目录内其它文件 | 按完整个人快照复制，包括用户删除作者游戏文件的状态 |
| 旧启动 JSON/JAR、旧 `PCL` 私有记录、日志、账号文件 | 保存在私有恢复备份，不作为新包来源；实例偏好通过独立协调器迁移 |
| 包内声明、补丁、运行库、CAS、派生运行环境 | 通过现有 MMC 导入、解析、绑定和物化流程从所选原包建立 |
| 旧嵌套实例的本地库 | 对已属于原包的库核对原始字节，修改冲突拒绝；额外个人文件不自动成为执行类路径 |
| 原包派生的受锁游戏资源 | 保留受锁结果；与旧个人快照同路径且字节不同则拒绝，不静默替换 |

共享全局游戏目录、双树、平铺目录中与作者游戏数据重叠的 PCL/启动文件角色、重解析点、未知清单版本和无法完整读取的数据均拒绝。全局账号存储不读取、不复制。旧私有数据的备份不属于可分享的整合包。

## 提交边界

1. 后台取得旧实例实际路径锁，检查活动/未知会话。在配置 quiescence 和路径 admission 内固定全文件快照、旧 JSON/config/INI 摘要，释放门后进行其它准备。
2. 在 `<Minecraft>/PCL/MmcLegacyAdoption/<GUID>` 写入逐文件 SHA/CRC 库存与一份封存并刷新到磁盘的 `backup.zip`。备份前后复验旧源；不为每个备份小文件执行一次 fsync。
3. 在同一私有操作目录的 `private-minecraft/versions/<新名称>` 内，复用 `ImportPreparedAsync` 建立完整新 ID、G1、作者基线、source/CAS 和运行环境。这个目录不属于用户版本列表扫描位置。
4. 只有验证过的新私有目标能持有 `PCL/MmcLegacyAdoptionPending.json` 许可。检查模板现有字节后替换个人游戏载荷；保留删除状态，迁移普通偏好、模式和显式空 JVM 值。旧源提供器始终写旧路径。
5. 再次取得旧源/private/final 的排空状态与排序后的 admission，核对活动状态、全源 SHA、个人载荷、配置和原锁。`CommitDecided` 之前允许取消，之后只完成发布或保留恢复冲突。
6. 用不覆盖的同卷目录移动发布。协调器将新副本的 private 配置上下文迁移到 final；完成记录落盘后才清除精确自有 pending 标记。

生产者全过程持有归属绑定的跨进程 `use.lock`。恢复必须取得相同文件的独占写打开，不能把 marker 存在或 PID 信息当作生产者已经退出的证明。恢复仍重新检查目录、原件、定义、配置及个人快照；同名目标和第三方字节不覆盖。未作发布决定的半成品保留备份并拒绝自动采用，旧实例可继续保留使用。

备份 ZIP 内含固定 `.pcllf-legacy-private-backup.json` 格式标记；MMC 来源读取器统一拒绝它作为作者原包，即使它被改名或移动。恢复只接受原操作绑定的封存记录与库存。旧目录清理入口还需调用 `MmcLegacyAdoptionCapabilities.RequireLegacyGameFolderSafe`，保护这些私有恢复数据。

这里使用文件内容刷新、封存恢复源、当前字节复验和目录移动边界，不宣称文件系统映像或任意断电下的多文件原子性。个人载荷尚未发布时可以从私有封存备份诊断；无法确认的现有字节保留，不损坏重采用。

## 实现接口与验证范围

- `MmcLegacyAdoptionService<TNetFile>.PreviewAsync/PreviewPreparedAsync`：只读预览和单个原 ZIP 句柄。
- `CreateManagedCopyAsync`：备份、私有导入、个人快照及无覆盖发布。
- `RecoverAsync`、`MmcLegacyAdoptionCatalog`：重放已有发布决定和有界只读恢复目录。
- `MmcLegacyAdoptionUi.PromptCreateCopy/PromptRecovery`：真实 Loader、取消 fence、活动检查、Java 检查及现有下载批处理。

首轮真实 PCLCS 构建已通过。`MmcLegacyAdoptionTests.Cases()` 提供自有小包的严格/显式重绑定、个人数据与作者分离、取消、源变化、Use 锁、冲突及离线恢复用例；这些测试的运行结果需单独记录。Main 接线、实际游戏兼容与大包转换尚未由本文宣称通过。
