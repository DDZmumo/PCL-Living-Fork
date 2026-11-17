# 固定 Prism 原生解析与计划子集对照

2026-11-09，LF 产品基线 `d5c7e9fd`。这份证据使用固定 Prism
`899d2f3957215a09fe398a8a8cbb8a8a9c619968` 的实际 C++ 源码构建独立
命令行 harness，不使用手写移植规则生成预期，也不把本机已有 Prism 12
便携包当作该提交的输出。产品 Core 和宿主源码未因本对照修改。

本次测量范围是原生组件 JSON 解析、按给定顺序应用字段、库版本合成、
运行环境规则、mainJar/classpath/native 文件计划，以及绑定账号/变量前的
game/JVM/tweaker 有序内容。完整 Prism 应用、`PackProfile/ComponentUpdateTask`
依赖解析器、下载和物化、自动选择 Java、账号/宏/Wrapper 绑定、GUI 与游戏进程
没有执行。因此它是**真实上游原生解析/计划子集**证据，不能扩称完整 Prism
启动、跨平台实机或所有 MMC 语义等价。

## 输入与实测环境

两份原包只读使用 `D:\User\Downloads` 原件，没有再次下载、修改或解压游戏
payload。完整 SHA256 和长度逐字节复核，身份见 [P0 原包基线](mmc-samples.md)。
只提取原始 mmc-pack/cfg/patch JSON 和 ZIP 条目事实；LF 对照侧调用实际
`MmcPackageReader`、`MmcComponentResolver`、`MmcProfileCompiler`。

Java 8 包的三份远端 JSON 采用先前已保存的 `mmc-goal/metadata` 内容；本次
没有重新访问元数据服务，也没有使用新的 lwjgl3ify 模板。来源和锁定值：

| 组件 | 固定来源 | 已采纳内容 SHA256 |
| --- | --- | --- |
| LWJGL | [2.9.4-nightly-20150209](https://meta.prismlauncher.org/v1/org.lwjgl/2.9.4-nightly-20150209.json) | `29a245e8def0c2eaa53d1373f70af0f8b43e677a88a529989e8de0f6a3e93d4c` |
| Minecraft | [1.7.10](https://meta.prismlauncher.org/v1/net.minecraft/1.7.10.json) | `5da6b22bde2571e1e9602f390cf5ba6535f85414fb82dd0d63782f44f33a38d4` |
| Forge | [10.13.4.1614](https://meta.prismlauncher.org/v1/net.minecraftforge/10.13.4.1614.json) | `8805569218d16a3d66bf519961d9dbb96da82398c42056345450639b568dd199` |

这些 URL 标识历史来源，当前响应可能改变；复现必须通过上表内容摘要，不得
用当前响应悄悄替换。现代包使用原 ZIP 的全部五份本地补丁。

原生程序使用 Windows x64 上的便携 GCC 16.2.0、Qt 6.9.2。Qt/ICU 77 的
动态 C++ 依赖固定为 GCC 15.1.0 runtime；GCC 16 runtime 缺少 ICU77 需要的
两个 emutls 导出，首跑曾以 `0xC0000139` 失败，随后仅固定兼容运行库，没有
修改上游规则来绕过。编译17个翻译单元，串行进行；工具包及依赖约113MB，
解包和保留压缩包合计约1.1GB，全部位于独立 artifact，未安装到系统。

运行环境由 harness 显式提供 Windows、Java `amd64/64` 和包对应的主版本
8/21；小样例另提供 `i386/32`、`aarch64/64` 以验证计划筛选。这是显式输入
的原生计划计算，没有执行这些 Java 或加载 native。GTNH 游戏实测仍以
[已有 Java8/Java21 闭环](mmc-gtnh-acceptance.md)为准。

## 原生实取结果

| 项目 | Java 8 原包 | Java 17–26 原包，计划环境 Java21 |
| --- | --- | --- |
| C++ 返回码 | 0 | 0 |
| 最终 mainClass | `net.minecraft.launchwrapper.Launch` | `com.gtnewhorizons.retrofuturabootstrap.MainStartOnFirstThread` |
| mainJar | `com.mojang:minecraft:1.7.10:client` | 同左 |
| mainJar 逻辑位置 | 全局 Maven `com/mojang/minecraft/1.7.10/minecraft-1.7.10-client.jar` | 同左 |
| 原始 Java majors | `[8]` | `[17,21,25,26]` |
| 推荐 runtime | `jre-legacy` | `java-runtime-epsilon` |
| 源额外 JVM token 数 | 0 | 72 |
| 绑定前 game token 数，含 tweaker | 20 | 20 |
| tweaker | `cpw.mods.fml.common.launcher.FMLTweaker` | 同左 |
| classpath 项数，含末尾 mainJar | 45 | 92 |
| native 输入 JAR 项数 | 4 | 3 |
| 实例平面 local 库 | 无 | `libraries/lwjgl3ify-3.0.42-forgePatches.jar` |

这些路径是 Prism 逻辑计划的来源角色；LF 实际执行会绑定已验证的 owned
运行视图。两启动器不要求宿主绝对路径相同，不能因此把运行视图直接改为
共享可变库。差分仅移除显式全局/实例库根前缀，保留相对路径和有序位置。

## LF 真实 Core 的逐字段结果

最终原生17个翻译单元编译/链接成功；Core-only adapter 使用私有 .NET10 SDK
构建，0警告/0错误。两个程序分别读取同一固定输入，最终比较16项：入口、
游戏版本、JVM/game/tweaker、推荐运行时、traits、组件顺序/内容摘要、mainJar
坐标/路径、classpath/native 有序路径、native 排除项、mavenFiles 和离散 Java
集合。[可审阅摘要](mmc-prism-differential-results.json)保留差异原值和所有身份；
完整有序数组/原始 stdout/stderr 保留在本轮独立工作树
`C:\Users\16892\.codex\worktrees\mmc-prism-differential\PCL2-MMC-Compatibility\artifacts\prism-native`。
最终完整报告是 `comparison-final-20261109.json`，SHA256
`ad7b33573221811a9d7b32f82c64dac9c951eeafea0654655665471669748099`。

| 输入 | 实际两侧返回码 | 逐字段结果 |
| --- | --- | --- |
| GTNH Java8 | 0 / 0 | 15/16 相同；LF 多一条 logging JVM 参数 |
| GTNH 现代包，Java21计划环境 | 0 / 0 | 15/16 相同；native 输入顺序不同 |
| 有序字段/版本替换/local/mavenFiles/Java 集合 | 0 / 0 | 16/16 相同；保留重复 JVM、空 token 与 `[B,A,A]` tweaker |
| Windows32 rules/native | 0 / 0 | 15/16 原值相同；仅无推荐名称的空字符串/null 表示不同 |
| ARM64 rules/native | 0 / 0 | 同上；平台/native 筛选结果一致 |
| 显式 mainJar:null | 9 / 0 | 原生拒绝、LF接受；不生成字段等价分数 |

比较器返回1，明确表示有已保留的差异，没有把它包装为整体等价通过：

1. **Java8 logging JVM 追加。** 已锁定的 Minecraft 元数据含
   `logging.argument=-Dlog4j.configurationFile=${path}`。固定原生 parser/LaunchProfile
   的组件额外 JVM 列表为空，LF 编译器将其作为有独立配置制品的
   `-Dlog4j.configurationFile=${mmc_logging_config}` 追加到自己的 JVM 列表。
   这不是丢失或重排 `+jvmArgs`；它是 LF 额外支持/绑定 logging 的输出差异。
   静态继续核对固定 `MinecraftInstance::extraArguments/javaArguments` 和
   [BaseInstance::extraArguments](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/BaseInstance.cpp)：
   后者从用户 JvmArgs 设置拆分参数，前者绑定组件参数、agent、平台/内存等，
   这些路径没有发现独立 logging 注入。本次没有执行完整宿主设置和最终命令，
   因此保留为组件/编译层级的输出差异，不推断最终启动缺陷。
2. **现代 native 文件顺序。** 固定原生 `getLibraryFiles` 先收集无 `${arch}`
   的 native，再追加按32/64位选中的列表，得到 `jinput → twitch → twitch-external`。
   LF 的 `NativeArtifacts` 保留组件贡献槽位，为 `twitch → twitch-external → jinput`。
   三个相对文件、classifier 和排除项一致，但有序 native 输入确实不相同。
   补充只读实查保留验收实例的这3个 JAR：合计10个 DLL 条目，无重名、无
   META-INF；JAR 摘要匹配已采纳收据和固定元数据，LF 已物化的10个文件逐项
   SHA256 与输入 payload 相同。故这组实际输入不因排列改变文件/字节集合。
   这是实际字节清单加顺序无关性证明，没有执行 Prism extractor。固定
   [ExtractNatives](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/launch/ExtractNatives.cpp)
   的全文件写出/`.jnilib` 更名路径只作源码核查；这组 Windows DLL 不触及
   后者。通用情况下不能据此称 native 顺序无关；LF 的
   [物化器](../PCLCS/Minecraft/MmcArtifacts/MmcNativeArchive.cs)对同目标异字节
   明确拒绝，这也是与可覆盖提取器不同的保护策略。本次未改 Core。
3. **null mainJar 的语法接受范围。** 固定 parser 要求对象，LF 的既定设计
   允许 null 不清除早前 JAR。保留为差异，两个实包均未触及。
4. **无推荐 runtime 的表示。** 原生 getter 返回空 QString，LF 模型使用 null；
   原值报告仍列出，两侧均未产生额外推荐或扩大候选。没有为了使报告全绿而
   静默折叠这一差异。

## 字段机制、差异与未覆盖边界

[OneSixVersionFormat](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/OneSixVersionFormat.cpp)
解析组件字段，[VersionFile::applyTo](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/VersionFile.cpp)
只允许 Minecraft 贡献游戏版本/type/assets，再将入口、JAR、参数、库等应用至
[LaunchProfile](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/LaunchProfile.cpp)。
本 harness 直接编译并执行这些函数。`PackProfile::getProfile` 按 components
顺序调用 component apply 的机制已[固定查看](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/PackProfile.cpp)，
但完整 resolver 没有运行，清单顺序和 effective-enabled 由 adapter 明确供给。

| 字段/规则 | 本次对照口径 |
| --- | --- |
| mainClass/mainJar | 非空入口覆盖；mainJar 独立定位并置于普通库后。显式 JSON `mainJar:null` 的语法差异单列，不能用 apply 的空指针行为冒充 parser 接受 null |
| JVM/game/tweaker | JVM 数组元素不二次拆分，重复和空 token 保留；game 采用原 `processMinecraftArgs` 绑定前语句；重复 tweaker 移至后部，保留新序列中的重复 |
| Java majors | Prism 原始列表按组件追加；LF 按设计稳定去重为有限候选集合。只对这一表示差异归一化，不做区间扩展，也不把 Java 17–26 当连续范围 |
| 库/classpath/mainJar | 比较有序坐标及路径角色；较高 Maven 版本替换原槽位；local 使用实例库平面文件名；mavenFiles 保留重复且不进入 classpath |
| native/rules/extract | 直接调用固定 [Library](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/Library.cpp)、[Rule](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/Rule.cpp) 和 [RuntimeContext](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/RuntimeContext.h)；比较计划选中的文件与排除项，不声称实际 native 已加载 |
| 全启动命令 | 不比较账号、动态宏值、PCL Wrapper 与 Prism Java launcher 的整条字符串；这些不在原生子集范围 |

原生微型样例已经观察到：有序字段样例保留6个 JVM token、`[B,A,A]` tweaker
和原始 `[8,17,21,17]` Java 列表；Windows32 的 rules/native 样例得到
classpath2/native1，ARM64得到classpath1/native0。显式 `mainJar:null` 样例
被固定原生 parser 拒绝，返回9，诊断为 `'mainJar' is not an object`；LF 已有
测试和设计允许 null 不清除，故这是需明确记录的语法接受范围差异。两份
GTNH 原包均没有这个 null 字段。

LF 的重复 UID、路径安全、预算、未知关键能力、启用运行图保护，以及
禁用状态/依赖解析的全部差异仍以 [设计矩阵](mmc-instance-design.md)和自身
测试为准。本次没有执行完整 Prism resolver，不能将上述安全策略统称为
原生差分全部通过。对应 MMC-03/04/05/14 的计划字段和 MMC-22 的来源边界；
不覆盖 MMC-06..21 的事务、UI、跨进程或完整生命周期门槛。

## 可复现接线与许可

工具、命令和完整范围说明见 [独立 reference 工具](../scripts/mmc/prism-reference/README.md)。
`source-lock.json` 固定43份原始文件摘要；`toolchain-lock.json` 固定依赖包。
`transformations.json` 记录逐文件剪裁/adapter 与生成文件摘要，`build/identity.json`
记录编译器、Qt和二进制身份；每次差分保留两个程序的 stdout/stderr、退出码、
字段数组、输入/输出/实际 DLL 摘要。报告文件独占创建，不覆盖既有证据。
关键解析、字段应用、库筛选/路径方法还按原始文本区间逐段核对摘要；比较器
拒绝与当前源码/变换清单/adapter/Qt 身份不匹配的旧二进制。
后续字节核对由 `VerifyNativePayload.py` 从实际两侧计划、保留导入收据和
owned 文件直接复核；`native-payload-verified.json` 保存3个输入和10个输出摘要。
四份补充静态源码另有 `static-reference/source-record.json` 固定摘要，不混入
已编译43文件清单或虚增原生执行覆盖。

被编译的上游原件和生成摘录保留原作者、GPL-3.0/Apache 声明及原许可文本；
独立链接 adapter 采用 GPL-3.0-only。Python 编排和 C# Core adapter 是 LF 原创
开发工具，未移植上游规则。上游源码、工具链和 reference EXE 仅在 ignored
artifact；Core、宿主、产品工程引用与发布 EXE 不包含此对照工具。该依赖边界
与行为证据不能替代整套产品的权属或法律审计。
