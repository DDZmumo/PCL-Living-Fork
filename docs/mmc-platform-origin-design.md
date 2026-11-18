# MMC 平台来源与在线更新接入约束

本文件记录开发约束。证据绑定、官方元数据适配与在线 ZIP 后端已实现，根集成九项测试通过；
产品入口仍在接线，不等于生产接口或真实游戏验收。当前状态见
`mmc-completion-ledger.md` 与 `mmc-platform-origin-backend.md`；普通 CurseForge／Modrinth 整合包继续保留原有流程。

## 可靠身份

MMC 组件 UID、实例名称、模组组合、压缩包文件名均不能证明平台项目身份。
平台身份必须同时绑定平台、稳定项目 ID、精确版本／文件 ID、所选发布文件和已测量原包 SHA256。
现有 `MmcPlatformProjectIdentity` 是身份数据形状，单独存在不等于来源已经核实。

核实流程接入既有资源目录及下载适配，不再实现第二个通用下载器：

1. 用户明确选择平台项目／版本，或从文件摘要查询得到候选。多个匹配不自动任选一个。
2. 重新取得精确项目和版本元数据，核对项目类型、项目 ID、版本归属和实际发布文件。
3. 从用户提供的原包或已校验下载中同时测量平台摘要、大小和 SHA256。
   原包 SHA256 必须等于当前实例的 `Origin.PackageSha256`；只有包内已解压文件时不得倒推原 ZIP。
   文件改名不影响摘要匹配，名称只用于展示和安全下载命名。
4. 保存有界元数据证据、选定发布文件及来源地址，把证据摘要绑定到新定义。
   明确来源修订保留实例 ID，以 N+1 提交；组件、库和玩家文件不随附加身份隐式改变。
5. 后续读取、克隆、历史恢复及导出分别认识证据的角色；不能从可变的
   `PCL/ModpackOrigin.json` 镜像重新授予平台身份。离线原件修复不依赖平台接口存活。

Modrinth 应匹配返回版本的 `project_id`、`id` 与**实际匹配的文件**，
不能只取 `files[0]` 或主文件。使用平台提供的内容摘要和大小；摘要查询的多结果须明确处理。
CurseForge 应重新核对文件的 `modId`、`id`、`gameId`、`isAvailable`、大小和 SHA1；
fingerprint 或仅 MD5 的结果不能独自建立本功能的可靠来源。

## 在线更新

版本列表只来自已核实的同一项目；用户选择具体发布文件。下载完成后再次核对精确来源元数据、
摘要和大小，并用真实 MMC Reader 检查实际容器。普通 mrpack／CurseForge ZIP 不被标成 MMC 导入成功。
通过检查后，在线包只作为现有 MMC ZIP 更新后端的候选输入，复用预览、组件冲突、三方文件选择、
世界备份、完整 undo、单一文件事务和恢复协议。

新版本的身份必须重新绑定到新原包和新证据；不能把旧 VersionId 直接带到新版。
用户改用没有核实来源的本地 ZIP 时，沿用当前 ZIP 后端清除旧平台归属的规则。
读取网络／用户选择期间不持有实例准入和配置静默化；提交时固定确认的身份、代数、主定义摘要、
发布文件、候选摘要和选择指纹，过期则停止。活动／未知会话、待恢复事务和未结束生产者均拒绝结构提交。

## 最小验证

受控 HTTP 与真实小 ZIP 覆盖正确归属、错误项目／版本／文件、同摘要多项目、主文件之外的匹配、
改名原包、摘要／大小不符、仅 MD5、不可用发布文件及实际非 MMC 容器。
贯通附加身份→克隆／历史恢复→在线新版本→本地未核实版本，检查身份不泄漏或沿用；
同时检查取消后的下载真实收尾、确认后元数据变化和更新前完整 undo。外部服务实测单独记录。

参考接口及现有接点：

- [Modrinth 文件摘要查询](https://docs.modrinth.com/api/operations/versionfromhash/)
- [CurseForge 文件接口](https://docs.curseforge.com/rest-api/#get-mod-file)
- `PCLCS/Resources/ResourceCatalogPipeline.cs` 与 `ResourceVersionRules.cs`
- `modern/Main/Modules/Minecraft/ModpackUpdateFlow.cs`
- `PCLCS/Minecraft/MmcInstances/MmcInstancePackageUpdateBackend.cs`

实施时核对 LF 当前服务版本和真实响应，保留有界契约样本；机制参考独立实现，不把上述说明当作已经完成的 API 适配。
