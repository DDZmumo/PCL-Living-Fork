# 模组加载器识别与依赖检查

启动器先使用当前游戏实例的加载器与 Minecraft 版本，再读取 JAR 内的声明。文件名和下载平台分类只作参考，不能代替实际声明。

| 加载器 | 读取依据 | 匹配规则 |
| --- | --- | --- |
| Fabric | 根目录的 `fabric.mod.json` | 分别解析 schema 0 与 1；检查运行环境、必需依赖、版本范围、别名和声明的内嵌 JAR。 |
| Forge 1.12.2 等旧版 | 类上的 `@Mod`、对应的 `mcmod.info`、核心插件及 `ContainedDeps` | 以实际加载声明确定 ID 和版本；描述文件中的模板、残留现代 TOML、Scala 伴生类不作为独立模组。 |
| 现代 Forge | `META-INF/mods.toml` | 解析 Forge、Minecraft、Java、FML 版本及客户端依赖；处理文件版本属性和 Jar-in-Jar 声明。 |
| NeoForge | 对应游戏版本的 `mods.toml` 或 `neoforge.mods.toml`，以及平台依赖 | 区分 1.20.4 及以前和 1.20.5 及以后的声明格式；FML 版本来自实际 FML 库，不套用 NeoForge 平台版本。 |

Fabric 的规则依据[当前声明规范](https://docs.fabricmc.net/develop/loader/fabric-mod-json)及[早期 schema 解析器](https://github.com/FabricMC/fabric-loader/blob/master/src/main/java/net/fabricmc/loader/impl/metadata/V0ModMetadataParser.java)。

Fabric 的 `^` 保持主版本相同，`~` 保持主、次版本相同，包括主版本为零或省略次版本的写法。例如 `^0.2.1` 可匹配 `0.9.0`，`~1` 仅匹配符合下限的 `1.0.x`。以[加载器实际比较运算](https://github.com/FabricMC/fabric-loader/blob/master/src/main/java/net/fabricmc/loader/api/metadata/version/VersionComparisonOperator.java)为准。

Forge 的规则依据[旧版加载器](https://github.com/MinecraftForge/MinecraftForge/blob/1.12.x/src/main/java/net/minecraftforge/fml/common/FMLModContainer.java)、[现代声明规范](https://docs.minecraftforge.net/en/1.21.x/gettingstarted/modfiles/)和[内嵌依赖规范](https://docs.minecraftforge.net/en/1.12.x/gettingstarted/dependencymanagement/)。

NeoForge 的规则依据[旧声明格式](https://docs.neoforged.net/docs/1.20.4/gettingstarted/modfiles/)、[新声明格式](https://docs.neoforged.net/docs/1.20.6/gettingstarted/modfiles/)和[FML 语言加载器版本实现](https://github.com/neoforged/FancyModLoader/blob/4.0/loader/src/main/java/net/neoforged/fml/loading/BuiltInLanguageLoader.java)。1.20.1 的 Forge 兼容关系单独处理，不推广到之后的 NeoForge 版本。

## 展示与处理

- 与当前加载器不匹配的启用文件，在检查弹窗单独分组，并在 Mod 列表中显示红框；悬停显示原因与处理方法。
- `javafml`、`lowcodefml` 等加载器内部组件合并到所属加载器。提示更新或调整加载器，不将它们作为独立 Mod 展示或下载。
- 已识别的 Fabric API 等外层 JAR 所提供的内嵌模块，合并到外层文件；启用和补全按外层文件处理。
- 精确平台项目通过其匹配文件中的实际 Mod ID，与本地求解结果关联。兼容的内嵌前置可满足其他模组，关系图说明提供它的外层文件；当前文件选中的独立内嵌前置，显示在当前节点下方的“已内嵌模组”区域，用向下箭头连接，并列出名称和版本。不再把已禁用的外部副本误标为缺失。内嵌版本不满足约束或没有可用提供方时仍提示启动问题，不因外部副本被禁用额外询问可选启用。
- 内部组件按声明归组，不维护模组名称或 ID 特例表：Fabric/Quilt 读取 Mod Menu 的父级声明（支持字符串、对象及父级别名），也将明确声明与其包内祖先具有相同源码项目地址的模块归入该项目；不以同作者、相同主页或名称前缀推断。Loom 为普通 Java 库生成的占位模组不单列。Forge/NeoForge 根据 Jar-in-Jar 声明读取实际模组元数据，同一声明中的多个内部 ID 仍归属外层文件。信息不足时不猜测组件身份，未选中的内嵌候选不标作已提供的独立前置。归组只影响展示，组件的提供关系、版本检查和启动错误保留。
- 启动提示优先显示模组名称和明确操作；已列入启用计划的文件不再以平台项目 ID 重复出现在说明中。
- 禁用文件不报启用错误。文件或加载器变化后，旧标记失效；刷新或检查后重新生成。

展示归组参考 [Mod Menu 的父子模块规范](https://github.com/TerraformersMC/ModMenu/blob/26.4/README.md#parents-parent-mod_id-or-)、[Fabric 嵌套 JAR 与 Loom 生成元数据](https://docs.fabricmc.net/develop/loom/)、[Forge Jar-in-Jar](https://docs.minecraftforge.net/en/fg-6.x/dependencies/jarinjar/) 和 [NeoForge Jar-in-Jar](https://docs.neoforged.net/toolchain/docs/dependencies/jarinjar/)。源码项目地址相同是本启动器的展示归组依据，不是加载器定义的独立模组判定，也不会改变加载选择。

## 依赖树索引

首次打开 Mod 管理或检查依赖时生成索引，保存在 `%LOCALAPPDATA%\PCLLivingFork\Cache\ModDependencyIndex`。索引包含本地依赖、反向依赖、内嵌归属和已有的平台映射，可在重启后恢复。

未变化的模组只核对文件清单、大小、修改时间、创建时间和链接状态，直接读取已有树，不重新解析 JAR。文件清单变化后按文件更新：新增或状态变化的文件重新读取，删除的文件移出树，其余声明复用。`.jar` 与 `.jar.disabled`／`.jar.old` 互相改名时，只有同目录基础路径唯一、文件状态一致且声明完整，才复用声明并更新启用状态；不推测任意改名的文件身份。内嵌模组随外层文件更新，再重算依赖与反向依赖。加载器类型或 Minecraft 版本变化时重新选择声明。Java 或同类加载器版本变化时直接利用已有元数据重算检查结果。

单个和批量禁用、删除前，只检查本地依赖并参考当前清单对应的已有平台缓存，不联网刷新，不计算整目录的平台文件指纹，也不等待依赖索引的写锁。首次没有缓存时仍须读取本地声明，结果保存为进程内独立快照，供后续操作增量复用；不会与后台写索引争用同一个写锁。清单变化后旧的平台路径映射失效，本地警告始终保留。确认后仍核对当前实例、写入锁与文件清单，变化时停止本次修改。普通自建实例同样保留必要的依赖影响提示。

旧索引兼容读取，没有逐文件快照的旧索引在下一次清单变化时完成一次完整读取。文件状态校验不是内容哈希；外部程序保留所有大小和时间信息却改写内容时，可手动更新索引强制重读。

在 Mod 管理中点击“更新依赖索引”会强制重新读取本地声明（包括旧版 Forge 的类声明），并分批从 Modrinth 和 CurseForge 获取全部模组的精确文件依赖声明，包括禁用文件。一并保存后，查看单个模组直接读取索引。按钮显示文件读取进度；网络不可用、超时或达到读取限制时保留本地树，并提示平台声明尚未获取完整。启动检查继续使用较小的联网预算，不自动执行整目录平台刷新。

平台项目身份与平台指定版本分别记录：同一文件经内容哈希识别到的多个平台声明合并为一个节点；平台指定其他版本时仍显示已安装文件，并注明版本待核对，不画成第二个缺失模组。不用同名、文件名或平台分类推断项目身份。未映射的项目只显示待确认状态，禁用文件和确定冲突保持相应提示。Modrinth 的具体版本声明依照[版本接口](https://docs.modrinth.com/api/operations/getversion/)，CurseForge 的项目依赖类型依照[官方接口](https://docs.curseforge.com/rest-api/#filedependency)；平台声明不覆盖本地加载器的版本范围检查，也不会据此自动降级已安装模组。

同一前置来自不同平台、或平台固定引用旧文件时，会先核对本地加载器实际声明的模组 ID 与版本范围。无法通过文件哈希直接建立平台关系时，只在已有本地依赖提供者的情况下，读取并校验平台参考包的元数据来确认 ID；每轮最多读取两个、每个不超过 4 MiB，复用已有请求与时间预算，结果随依赖索引缓存。参考包不安装到游戏目录。不会按名称、文件名或项目标题猜测关系，禁用、版本不匹配和无法验证的情况仍保留提示。

索引损坏或格式过期时自动恢复；取消或文件在处理过程中变化时，不发布不完整索引。修改解析规则或快照结构时应同步递增 `ModDependencyIndex.Schema`。

## 检查边界

当前模组管理、启动前模组依赖与整合包更新检查忽略 Java 要求项；解析器保留相关声明，游戏启动的 Java 选择仍由版本运行环境处理。

版本范围存在确定满足、确定不满足、无法确认三种结果。Maven 限定符顺序、未解析的模板和动态核心插件保持保守处理。未确认的旧版运行时依赖不会触发猜测性补全。

模组文件保持只读，检查结果写入本机索引缓存。检查不执行 JAR 中的代码，不自动切换加载器或删除模组。本地扫描与回归通过不能代替实际游戏启动验证。

## 验证

在仓库根目录运行 `powershell -NoProfile -File scripts/Test-ModDependencies.ps1`，检查三种加载器的声明与交叉匹配。构建后运行 `Test-LaunchDependencies.ps1` 和 `Test-ModDependencyGraph.ps1`，通过 `-ExecutablePath` 指定 EXE，验证红框、分组、内嵌归属和自动补全边界。
