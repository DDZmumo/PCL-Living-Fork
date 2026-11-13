# 启动前依赖检查规则

检查以当前实例、当前加载器实际读取的 JAR 声明为基础，再补充通过文件指纹确认的平台项目信息。版本不确定、声明未读完整或网络证明不足时保留核对信息，不据模组名称猜测替代关系。

## 替代前置与可选声明

Fabric 的 `provides` 声明提供模组 ID 别名，依赖范围仍按提供者的实际版本检查。已启用、版本满足且适合当前加载器的替代品可以满足该 ID；禁用、过旧和不相关的文件不能满足。推荐与建议依赖已有有效提供者时，也参与平台声明核对。

Quilt 的“任一满足”分支按完整表达式核对。若平台列出其中一个项目，而本地已满足另一个分支，平台补充不会再次要求安装前一个分支；“全部满足”要求及真正缺失的其他前置仍独立检查。

平台项目 ID 与加载器模组 ID 不等同。跨项目对应关系必须来自本地精确匹配或经过长度、SHA 和当前加载器校验的参考 JAR 元数据。元数据下载仍限制为最多两次、每份不超过 4 MiB，优先核对明确的替代关系，并在下载预算用尽后继续使用已有缓存证明。

Insecticide 的 `fabric.mod.json` 声明提供 `cicada`，是这种机制的一个实例；生产逻辑不包含这两个模组的名称特例。

资料：[Fabric 规范](https://docs.fabricmc.net/develop/loader/fabric-mod-json#provides)、[Insecticide 声明](https://github.com/Tigercrl/insecticide-lib/blob/master/src/main/resources/fabric.mod.json)。

## NeoForge 1.21.1 的热修兼容规则

在已确认的 NeoForge / Minecraft 1.21.1 环境中，内置依赖的普通 Maven 范围判断失败后，按 FML 的兼容矩阵再检查以下版本：

| 内置 ID | 兼容检查版本 |
| --- | --- |
| `minecraft` | `1.21` |
| `neoforge` | `21.0.166` |

因此 `[1.21]`、`[1.21,1.21.1)` 等声明在这个特定运行环境中可以满足。普通 Maven 比较、其他游戏版本、其他加载器和普通模组的版本约束保持各自的语义。

规则依据为 NeoForge 21.1.265 使用的 FML 4.0.45：[模块依赖](https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.265/neoforge-21.1.265.module)、[固定版本源码归档](https://maven.neoforged.net/releases/net/neoforged/fancymodloader/loader/4.0.45/loader-4.0.45-sources.jar)、[官方兼容矩阵](https://github.com/neoforged/FancyModLoader/blob/1.21.1/loader/src/main/java/net/neoforged/fml/loading/VersionSupportMatrix.java)。这里只记录互操作所需的版本事实，未引入该 Java 实现或分发 FML。

## 回归验证

```powershell
dotnet run --project tests/PCLLF.Migration.Tests/PCLLF.Migration.Tests.csproj -- --group mod-dependencies --group mod-dependency-online
```

回归覆盖正向替代、禁用及版本反例、错误项目证明、任一／全部分支、预算和缓存，以及 NeoForge 规则的运行环境边界。可选的 `PCLLF_ISSUE25_REAL_DIR` 用于读取固定哈希的真实 JAR 元数据回放；参考文件仅放在忽略的验证目录，不随启动器分发，不执行游戏或模组代码。
