# 模组下载替换、重复检测与简介翻译

在资源详情页选择某个 Mod 文件并保存时，以本次选择的版本为准，支持升级与降级。先下载至 LF 独立缓存并完成原下载器的大小／摘要校验，再检查目标文件夹；不会在下载开始或失败时删除旧文件。

## 下载前的必需前置确认

“设置 → 其他 → 社区资源 → 下载 Mod 时检查并提示必需前置”默认开启。它与启动前依赖检查及自动补全分别保存；关闭时，下载仅处理用户选中的 Mod，也不查询前置版本。

开启后，使用下载目标实例的 Minecraft 和加载器匹配 CurseForge／Modrinth 的必需前置；保存到其他文件夹时，仅在所选文件能唯一确定游戏版本和加载器时自动匹配。弹窗列出前置名称、将安装的版本、已安装状态和无法匹配的原因，提供“一起安装”“仅下载此模组”“取消”。没有缺失项时直接下载主文件；无法确定兼容版本或存在固定版本冲突时，只提供手动核对或仅下载的选择。弹窗里的“以后下载时检查必需前置”开关同步保存全局偏好。

- 只处理平台的 `required` 声明，保留 Modrinth 的精确 `version_id` 及只有版本 ID 的引用；不把 optional、embedded 或 incompatible 项作为自动下载项。Fabric API 等必需库也纳入检查。
- 递归展开、去重并截断环，限制最多 128 个项目及 32 层；按前置顺序安装。项目级引用优先复用已安装且字节摘要匹配的兼容版本，否则优先兼容正式版；精确版本无法兼容时不改用其他版本。
- 已安装判断使用实际启用 JAR 的摘要，不以文件名或项目显示名称判断。禁用的前置在用户明确选择补全后安装并启用；普通手动下载同一模组仍保留原来的禁用状态。
- 确认前只读取元数据和本地文件；取消不会创建下载暂存或写入模组目录。一起安装时先完成所有文件的下载和校验，再复核已安装前置并逐项提交，主模组最后安装。单文件失败或取消沿用现有事务回滚，已经成功安装的其他前置保留。
- 目标文件名冲突、不同固定版本要求、未知来源或无法兑现的要求明确显示，用户可以取消或仅下载主文件。

交互流程参考 [BlockHelm 的前置确认及三项选择](https://github.com/zqq-699/BlockHelm-Launcher/blob/f858f0d5f9e841c96e5ffd214045fb1ba4a451b5/Launcher.App/Views/Shell/MainWindow.xaml#L778)、[Modrinth 的内容安装计划](https://github.com/modrinth/code/blob/94c7bda2b32b5a2832850b88a7e60577ae76deb1/apps/app-frontend/src/providers/content-install.ts)及[下载对话框依赖分组](https://github.com/modrinth/code/blob/94c7bda2b32b5a2832850b88a7e60577ae76deb1/apps/frontend/src/components/ui/ProjectDownloadModal/DownloadDependencies.vue)。实现使用 LF 原生弹窗、下载器和安装事务，独立编写，未引入上述项目的代码或资产。字段语义依据 [Modrinth 版本 API](https://docs.modrinth.com/api/operations/getversion/)。

## 替换依据

- 使用完整的顶层加载器 Mod ID 集合匹配同一模组，不使用名称、中文译名、文件名前缀、文件创建时间或版本号大小。内嵌组件和 `provides` 别名不能作为自动删除依据。
- 已有平台匹配只有在平台与项目 ID 相同、当前文件内容仍匹配缓存中指定版本的摘要时才可使用，支持同项目改过内部 ID 的版本；不把两个平台的同值项目 ID 混为一谈。
- 多模组包的 ID 集合仅有部分重叠、未能完整识别的文件、其他项目或链接文件不会因名称相似而被清理。同名目标无法确认身份时停止安装并保留原文件。
- 匹配的旧文件全部禁用时保留禁用状态；混合启用／禁用副本替换为本次选择的一个版本。
- 文件检查与提交在目标目录的独占范围内执行。提交前复核旧文件内容；写入失败或取消时恢复已移动的原文件。成功后直接清理事务副本，不建立资源回收站；恢复遇到外部文件冲突时保留备份位置，不覆盖外部修改。
- 实例文件任务的占用持续到事务与必要回滚结束。下载阶段只写暂存目录，多个下载不需要共同占用实例；进入安装阶段后协调实例写入。

## 模组管理

保留全部、启用、禁用、可更新筛选，新增“重复”筛选。通过已有依赖索引复用顶层 Mod ID，以及精确的“平台＋项目 ID”识别重复；不会额外发起平台搜索。

列表行和选择状态以完整路径为标识，同名启用／禁用副本分别展示和操作。搜索只改变显示结果，重复关系仍按完整已安装列表计算。这里仅提示重复，不按时间批量删除，也不将内嵌库的正常共享视作重复顶层模组。

删除操作冻结明确选中的路径，依赖影响预检和实际删除共用这一份计划。选择禁用副本不会连带删除启用文件，选择启用文件也不会自动补入 `.disabled`／`.old` 副本；普通删除与 Shift 永久删除使用同一范围。

## 简介翻译

资源详情页提供 PCL 原生“翻译简介”按钮，按需读取 [MCIM](https://mod.mcimirror.top/) 的公开项目译文，只发送平台与公开项目 ID，不提交本地文件或账号信息。保留当前原文，译文来源原文不一致时明确说明；服务不可用或没有译文不影响原文与下载。

缓存区分平台、项目 ID 和原文摘要，位于 `%LOCALAPPDATA%\PCLLivingFork\Cache\DescriptionTranslations`。响应限制为 64 KiB，网络超时为 8 秒，支持离开页面时取消，过期或损坏缓存重新请求。下载暂存位于同一 LF 缓存根目录的 `ModDownloads` 子目录。

## CE 参考范围

核对 [PCL-CE b4949a92](https://github.com/PCL-Community/PCL-CE/tree/b4949a92ba5ad3b490b139fece0172d8d5b2c846)：

- [重复项目筛选](https://github.com/PCL-Community/PCL-CE/blob/b4949a92ba5ad3b490b139fece0172d8d5b2c846/Plain%20Craft%20Launcher%202/Pages/PageInstance/PageInstanceCompResource.xaml.cs) 提供按已识别项目分组的参考；LF 保留原有页面与加载器，补充平台命名空间、顶层 ID、独立行键和事务替换策略。
- [中文简介请求](https://github.com/PCL-Community/PCL-CE/blob/b4949a92ba5ad3b490b139fece0172d8d5b2c846/PCL.Launcher.Core/Comp/ProjectProps.cs) 与[按需翻译交互](https://github.com/PCL-Community/PCL-CE/blob/b4949a92ba5ad3b490b139fece0172d8d5b2c846/Plain%20Craft%20Launcher%202/Pages/PageDownload/Comp/PageDownloadCompDetail.xaml.cs) 提供服务和交互参考；LF 使用自己的缓存、传输限制、错误与页面生命周期处理。

没有整体迁入 CE 的框架、账号、下载器或配置策略。CE 的 Core 与应用目录许可不同，保持已有 [许可清单](../licenses/manifest.json) 及来源记录；新增的 LF 独立实现按 [MPL 文件清单](../licenses/MPL-COVERED-FILES.txt) 授权。
