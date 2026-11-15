# 资源搜索中的中文映射与项目身份

搜索结果的项目 ID、平台、slug、下载详情入口和网站地址以对应平台返回的元数据为准。中文百科表负责补充名称与百科关联，不能覆盖真实项目身份。

## 通用核对规则

- 在当前平台的 slug 候选中核对原始英文名称；索引名称与平台原名矛盾时拒绝该关联。
- slug 的分隔符变化或不同平台字段可以辅助恢复旧映射，但必须同时有一致的英文名称。多个不同百科 ID 仍不能唯一确认时回退原始名称。
- 兼容不改变原名含义的加载器尾缀、可由全名验证的缩写，以及一侧增加的明确品牌前缀；保留有含义的名称差异。
- 空 slug 不与缺少该平台字段的条目相匹配。空描述、相同中文名或模糊相似性也不能单独建立跨平台关联，不能据此转移百科条目。
- 中文查询从匹配度最高的百科候选提取英文关键词；不以旧 slug 代替已经提供的英文名。缺少本平台 slug 时仍可用英文名称搜索，但不会编造直接获取地址。
- 通过索引 slug 直接获取的结果还要核对平台、返回 slug 和原名；不符的条目不会以目标模组的名义加入结果。平台正常关键词查询中的相关项目可保留，并显示各自正确名称。

条目没有可比较英文名称时，仅保留现有平台 slug 所提供的有限证据；更名或信息不足的项目可能显示平台原名，避免输出无法证实的中文标题。

## 核对实例与 PCLCE 对照

2026-11-13 查询平台，得到以下四个项目身份：

| 项目 | 平台 | ID | slug |
| --- | --- | --- | --- |
| 妖怪们的归家 | Modrinth | `uJT3ahxz` | `youkaishomecoming` |
| 妖怪们的归家 | CurseForge | `957437` | `youkais-homecoming` |
| 妖怪们的飨宴 | Modrinth | `Ohy0TgO2` | `gensokyo-delight-youkais-feasts` |
| 妖怪们的飨宴 | CurseForge | `1309991` | `gensokyo-delight-youkais-feasts` |

LF 内置百科第 21200 行把 `youkaishomecoming` 记在“飨宴”名下，与当前 Modrinth 项目不符。旧的中文关键词和直接获取会把这个不相符项目取回，并用该百科标题展示。

对照 PCLCE dev 提交 `b4949a92ba5ad3b490b139fece0172d8d5b2c846` 的 `mcmod.buf`，发现相同映射；其 `GetCompWikiEntryBySlug` 查询匹配 CurseForge 或 Modrinth 任一 slug 后取第一条，`TranslatedName` 直接使用数据库译名。代码与数据存在相同的误关联路径；这里只做源码／数据核对，没有运行 CE 客户端。

修复使用通用名称与平台证据校验，没有针对以上模组名称或 ID 编写生产特例，也没有手动改写这两行数据。LF 保留自身模型和搜索流程，未引入 CE 数据库或实现代码。

来源：[Modrinth 归家项目](https://api.modrinth.com/v2/project/uJT3ahxz)、[Modrinth 飨宴项目](https://api.modrinth.com/v2/project/Ohy0TgO2)、[CurseForge 归家](https://www.curseforge.com/minecraft/mc-mods/youkais-homecoming)、[CurseForge 飨宴](https://www.curseforge.com/minecraft/mc-mods/gensokyo-delight-youkais-feasts)、[CE 数据库查询](https://github.com/PCL-Community/PCL-CE/blob/b4949a92ba5ad3b490b139fece0172d8d5b2c846/PCL.Launcher.Core/Comp/Database.cs)、[CE 展示属性](https://github.com/PCL-Community/PCL-CE/blob/b4949a92ba5ad3b490b139fece0172d8d5b2c846/PCL.Launcher.Core/Comp/ProjectProps.cs)。

回归通过实际模型、内置百科和离线平台搜索响应验证名称、直接获取、排序与缓存还原，同时覆盖同 slug 不同平台、歧义条目、空身份、有效缩写与名称差异。不会执行游戏或发起测试用的真实搜索请求。
