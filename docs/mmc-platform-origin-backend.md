# MMC 平台来源后台

本批实现 Core 与 PCLCS 后台；Main 的入口、选择器和展示已在集成分支接线，完整任务链回归仍在补齐。
普通资源目录及非 MMC 整合包流程不变。这里的受控 HTTP 记录不等于 Modrinth／CurseForge
生产接口或真实游戏验收。

## 信任边界与存储

`MmcInstanceOrigin.PlatformEvidenceReference` 是可空、摘要绑定的来源对象引用。
对象存放于 `PCL/MmcSource/platform-origin/<sha256>.json`，包含精确平台／项目／版本、
选定发布文件、原 ZIP 的 SHA256／SHA1／SHA512／大小、两个有界元数据原文和官方来源地址。
原 ZIP 的实测 SHA256 必须等于当前 `Origin.PackageSha256`。

`MmcPlatformOriginCodec` 独立核对原文中项目类型、稳定 ID、版本归属、文件摘要与大小。
Modrinth 匹配全部 `files`；相同摘要的多个结果保留为候选，需要用户选择完整发布文件。
CurseForge 必须为 Minecraft 整合包，且文件可用、`modId`／文件 ID／`gameId` 正确并含 SHA1。
fingerprint 和仅 MD5 均不能授予身份。文件改名不影响验证，已解压文件不能用于重建原包身份。

Core 不接触网络、文件系统、账号或 UI，也不声称 JSON 能自行证明 TLS 来源。
生产入口是 `LegacyNetworkRuntime.CreateMmcPlatformOriginCatalog()`：复用现有鉴权签名与
HTTP 缓存基础设施，采用专用缓存路径和持久客户端，使用系统 TLS 校验、禁自动重定向及
cookies；3xx 被拒绝，签名前后请求必须保持精确官方 URI。单响应上限 2 MiB，单目录操作
累计上限 64 MiB／512 次请求，单请求 30 秒，并传递取消令牌。普通 `DlModRequest`
保留其既有镜像行为。测试必须显式选用只能连接 HTTP loopback 的 fixture policy。

只有经过该服务入口重新核对且实际匹配 ZIP 的后台预览能够提交来源修订。
证据不是 `verified=true` 标志，也不从可变 `PCL/ModpackOrigin.json` 镜像授予身份。
旧定义的三个平台字符串仍可读取，但没有证据时是未核实状态；空字段不新增 JSON 成员，
原有绑定指纹协议保持不变，非空证据才追加独立指纹域。

## 提交与在线更新

`BindOriginRevision` 保留 ID、原包、组件源、覆盖、库锁与玩家文件，只生成 N+1 来源修订。
Host 先完成原 ZIP 和服务核对，再进入配置静默及实例准入，捕获撤销点，并通过一次现有
文件事务提交证据对象、定义和代数提示。保存来源、修复、克隆及历史恢复均验证或复制该
不可变对象；离线修复无需平台在线。导出包声明不会重新授予 Host 平台证据。

`MmcInstancePlatformOriginBackend` 提供 `PreviewAsync`、`CommitAsync`、
`ReadVerified`、`GetAvailableReleasesAsync`。`MmcPlatformOriginCatalog` 提供精确发布文件
描述符及 Modrinth SHA512 候选查询。缓存 key 使用现有 `ResourceProjectKey`，平台使用完整
`ResourcePlatforms` 名称。

`MmcInstancePlatformOnlineBackend<TNetFile>` 使用现有 `MmcArtifactDownloadSpec`、
下载缓存及 NetFile 批次，并等待真实批次取消／收尾；下载后重新实测全部摘要／大小、
重新核对选定元数据并调用真实 MMC Reader。普通 mrpack 或 CurseForge ZIP 不会被假称
MMC。通过检查的包交给已有 ZIP 更新 `Prepare/Select/Commit`，沿用组件冲突、三方文件
选择、世界备份、完整 undo 和单文件事务。

在线预览固定当前 ID／代数／主定义摘要、完整发布文件描述符、元数据指纹及实测 ZIP。
内部已核实来源同时固定原目标选区，复制公开 request 并修改目标不能转移来源权。
提交再次核对来源、实际 ZIP 及候选；全部网络和下载都发生在结构准入之前。
新包保存新版本证据；改用未核实本地 ZIP 则清除平台身份及证据引用。

## 验证与许可

Core、生产 Modern PCLCS 和注入真实 Catalog 的 Migration 图已经以 SDK 10、单节点、
禁共享编译、`--warnaserror` 首编通过。2026-11-09 最终源码的新增 8 组真实小 ZIP／loopback
HTTP 与原有定义及 LaunchAuthority 共 33 例全部通过。覆盖 CF HTTP、跨项目摘要候选、
同摘要完整文件选择、普通修复无法替换证据、旧 null／冻结指纹、完整身份连续性及实际批次
取消收尾。Migration 本次用外置临时 targets 注入真实 Catalog 与 StartupObject，未改共享
csproj／Program／selector；集成分支需正式注册该 Catalog 源文件和 `MmcPlatformOriginTests.Cases()`。

新增 Core 文件是独立原创 C#，采用 MPL-2.0，文件含 SPDX 声明；Host 与测试属于仓库
现有 LICENCE。没有复制第三方平台或启动器实现，没有新增外部依赖，MeloongCore 保持
固定 `c774ae4c85fd94e0eaa171db21e78a65870df19b`。

根集成追加：正式注册 Catalog 源文件、`mmc-platform-origin` 分组、变更选择器与 Main 官方服务桥。
选择器 166 项通过，Migration 构建为 0 警告／0 错误，平台九项全部通过。
独立审查发现初始加载后平台证据仍可能被外部改写，最终修复／启动共享来源复核与组件提交前
现已重新读取并验证该证据。新增一项包含修复／组件两路径各自晚删除、晚替换共四个负例，
全部拒绝发布，原代数、主定义、玩家文件与外部变化现场保留。
测试 HTTP 服务改为持续绑定的 loopback TCP socket，避免随机 HTTP.sys URL 权限与关闭重绑窗口；
没有修改系统 URL ACL、证书或生产网络设置。以上仍没有外部平台在线或游戏结论。

## Main 入口

受管理实例设置左侧“MMC 实例工具 → 平台来源与更新”打开 MMC 专用页面
（2026-11-15 从概览页迁入）。没有证据时，明确选择最初导入的原 ZIP、
平台、稳定项目 ID 和精确发布 ID，再从官方元数据列出的完整发布文件中选择；即使只有一个文件，
也不会自动代选。确认后保存来源修订和环境记录。已有核实来源时，可选择同一项目的发布文件，
下载后先读取真实 MMC 容器，再根据其声明选择 Java，接着复用原 ZIP 更新的差异、选择、
备份和提交界面。普通 mrpack／CurseForge 格式继续明确拒绝。

Main 首编为 78 个既有警告／0 错误，Native 为 0／0；控件选择与未启动取消两组通过。
GPT-6.1 Sol max 对冻结 UI 与共享入口进行只读审查，未发现新增可证 P1/P2。
追加的实际 Main 七组全部通过：绑定成功／确认取消／元数据变化拒绝，以及在线更新成功／
预览取消／元数据变化拒绝／真实下载期间取消。在线组使用真实 NetFile loopback HTTP、
原 ZIP 差异与提交任务，并核对实际生产者排空；前三个在线组探测本机 Java 8，但没有启动游戏。
确认期间检查实例和候选准入均释放，绑定同摘要候选仍明确选择完整文件。
最终根 Migration 包含真实 Reader 后环境选择与取消等待的新增例，十项全部通过。
这些是受控元数据与真实本机下载链证据，外部平台接口仍未实测。

接口形状来自 LF 当前 Modrinth `/v2`／CurseForge `/v1` 资源实现及官方资料：

- [Modrinth 精确版本与全部 files](https://docs.modrinth.com/api/operations/getversion/)
- [Modrinth 摘要查询 multiple 参数](https://docs.modrinth.com/api/operations/versionfromhash/)
- [CurseForge Get Mod File 与 File schema](https://docs.curseforge.com/rest-api/#get-mod-file)

Context7 查询因配额不可用后改查以上官方主资料；没有进行外网大包下载或真实游戏测试。
