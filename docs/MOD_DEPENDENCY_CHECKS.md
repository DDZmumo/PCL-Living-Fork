# 启动前依赖检查规则

检查以当前实例、当前加载器实际读取的 JAR 声明为基础，再补充通过文件指纹确认的平台项目信息。版本不确定、声明未读完整或网络证明不足时保留核对信息，不据模组名称猜测替代关系。

## 替代前置与可选声明

Fabric 的 `provides` 声明提供模组 ID 别名，依赖范围仍按提供者的实际版本检查。已启用、版本满足且适合当前加载器的替代品可以满足该 ID；禁用、过旧和不相关的文件不能满足。推荐与建议依赖已有有效提供者时，也参与平台声明核对。

Quilt 的“任一满足”分支按完整表达式核对。若平台列出其中一个项目，而本地已满足另一个分支，平台补充不会再次要求安装前一个分支；“全部满足”要求及真正缺失的其他前置仍独立检查。

平台项目 ID 与加载器模组 ID 不等同。跨项目对应关系必须来自本地精确匹配或经过长度、SHA 和当前加载器校验的参考 JAR 元数据。元数据下载仍限制为最多两次、每份不超过 4 MiB，优先核对明确的替代关系，并在下载预算用尽后继续使用已有缓存证明。

Insecticide 的 `fabric.mod.json` 声明提供 `cicada`，是这种机制的一个实例；生产逻辑不包含这两个模组的名称特例。

资料：[Fabric 规范](https://docs.fabricmc.net/develop/loader/fabric-mod-json#provides)、[Insecticide 声明](https://github.com/Tigercrl/insecticide-lib/blob/master/src/main/resources/fabric.mod.json)。

## Quilt 和 Maven 的版本语义

Quilt 的裸语义版本号按兼容到下一主版本解释，例如 `1.0.0` 可以接受 `1.5.0`；`=1.0.0` 才是精确版本。`^` 保持主版本，`~` 保持主次版本，包括主版本为零和省略次版本的情况。Fabric 的裸版本仍按精确版本处理，不能把两种加载器的规则混用。

Maven 范围支持数字版本，以及与数字版本有明确顺序的常见 `alpha`、`beta`、`milestone`、`rc`／`cr`、`snapshot` 预发布编号。例如 `2.0-rc1` 可以落在 `[1,2)` 中，`21.7.26-beta` 不满足 `[21.7.26,)`。不同限定词之间尚未确认的排序、任意自定义限定词仍保留为未知，避免错误放行或阻断。

这些边界通过 Quilt Loader 0.28.0 的实际版本解析器和 Maven Artifact 3.9.9 对照。资料：[Quilt 规范](https://github.com/QuiltMC/rfcs/blob/main/specification/0002-quilt.mod.json.md#version-specifier)、[Quilt 实现](https://github.com/QuiltMC/quilt-loader/blob/3492f6601e0c0bc86d53eb5cd988160db3b39703/src/main/java/org/quiltmc/loader/impl/metadata/qmj/VersionConstraintImpl.java)、[Maven 版本规则](https://maven.apache.org/pom.html#dependency-version-requirement-specification)。验证工具仅放在忽略目录，不随启动器分发。

## Java 功能要求

Forge 的 `features.<modid>.java_version` 与 NeoForge 的 `features.<modid>.javaVersion` 按实际加载器分别解释，并与启动器选中的 Java 版本比较。缺失的另一种拼写不会被擅自当作当前加载器的要求；语言加载器版本与 Java 运行时版本仍分开检查。

资料：[Forge 功能要求](https://docs.minecraftforge.net/en/1.20.1/gettingstarted/modfiles/#features)、[NeoForge 功能要求](https://docs.neoforged.net/docs/1.21.1/gettingstarted/modfiles/#features)。

## NeoForge 的热修兼容规则

在已确认的对应 NeoForge 运行环境中，内置依赖的普通 Maven 范围判断失败后，按 FML 的兼容矩阵再检查以下版本：

| Minecraft 运行环境 | 内置 ID | 兼容检查版本 |
| --- | --- | --- |
| `1.21.1` | `minecraft` | `1.21` |
| `1.21.1` | `neoforge` | `21.0.166` |
| `1.21.8` | `minecraft` | `1.21.7` |
| `1.21.8` | `neoforge` | `21.7.26-beta` |

因此 `[1.21]`、`[1.21,1.21.1)` 等声明在这个特定运行环境中可以满足。普通 Maven 比较、其他游戏版本、其他加载器和普通模组的版本约束保持各自的语义。

规则依据为 NeoForge 21.1.265 使用的 FML 4.0.45：[模块依赖](https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.265/neoforge-21.1.265.module)、[固定版本源码归档](https://maven.neoforged.net/releases/net/neoforged/fancymodloader/loader/4.0.45/loader-4.0.45-sources.jar)、[1.21.1 官方矩阵](https://github.com/neoforged/FancyModLoader/blob/1.21.1/loader/src/main/java/net/neoforged/fml/loading/VersionSupportMatrix.java)，以及 [1.21.8 官方矩阵](https://github.com/neoforged/FancyModLoader/blob/1.21.8/loader/src/main/java/net/neoforged/fml/loading/VersionSupportMatrix.java)。这里只记录互操作所需的版本事实，未引入该 Java 实现或分发 FML。

声明解析缓存会按新规则自动重建，避免旧缓存中 Quilt 的语法或遗漏的 Java 要求继续影响判断。

## 回归验证

```powershell
dotnet run --project tests/PCLLF.Migration.Tests/PCLLF.Migration.Tests.csproj -- --group mod-dependencies --group mod-dependency-online
```

回归覆盖正向替代、禁用及版本反例、错误项目证明、任一／全部分支、预算和缓存，以及 NeoForge 规则的运行环境边界。可选的 `PCLLF_ISSUE25_REAL_DIR` 用于读取固定哈希的真实 JAR 元数据回放；参考文件仅放在忽略的验证目录，不随启动器分发，不执行游戏或模组代码。
