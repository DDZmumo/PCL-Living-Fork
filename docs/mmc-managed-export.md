# 受管理 MMC 导出后端

`MmcInstanceExportBackend` 是 managed 实例专用后端；普通导出门禁和 UI 路由由宿主接线，不让 MMC 悄悄降级为普通 Forge 导出。源码范围仍是宿主现有许可；使用既有独立 Core，不复制上游实现或新增纯参考项目许可证。

## 调用与发布协议

构造 `MmcInstanceExportRequest` 时提供逻辑 SourceRoot、完整 OutputZipPath、DisplayName，以及当前 inspector 的实例 ID、generation、primary SHA256。先 `PreviewAsync` 可取得目录角色、entry/byte 数、转换记录和指纹；确认后把该值传入 `ExpectedPreviewFingerprint` 调用 `ExportAsync`，源/偏好/选项变化会拒绝。

整个操作在一个后台 worker 上依次保留 configuration quiescence、admission lease。active/unknown session、pending 操作/事务、损坏/变化的定义及兼容投影均拒绝。来源读取只使用已发布 SavedSource/CAS/locks，导出不读取全局 Maven/Java/cache 来补缺。

输出父目录必须已存在、本地、无链接。目标必须是新的 ZIP，不能在源实例内，也不能通过物理路径别名写回源；已有 ZIP、目录、原包均不覆盖。GUID partial 与最终文件在同一输出目录，因此发布不跨卷；源内容可以从别的卷只读复制。ZIP entry/writer 真实退出、flush 后，通过生产 bounded reader 重读所有 entry CRC/SHA/size 和离线重新编译，再重捕获源文件清单、source/CAS、偏好和 ID/gen/primary，最后无覆盖原子 Move。

取消不把线程状态当作已停止：返回前已退出 entry、ZipArchive 和输出 FileStream。失败/取消保留本次 owned partial，位置由结果返回；它不会发布为成品，也不删除源或现有目标。owner sidecar 只有随机 operation、目标文件名和 preview 指纹，不记录实例 ID、源绝对路径或账号。

## 内容与作用域

| 类别 | 行为 |
| --- | --- |
| 组件与 patch | 从当前 adopted resolution 冻结每个 effective patch（含用户 patch）为 `patches/<uid>.json`，固定 UID/version、顺序、enabled、important/dependencyOnly、requires/conflicts，不拉最新 metadata |
| shared 声明 | 不打包 runtime cache；从已采用 immutable 原件复算并固定当前 download SHA1/size，保留平台/native 声明；源 CAS 缺失/变化则拒绝 |
| 私有库 | 区分实例 `libraries` 与 game 子目录的 `libraries`；已采用 current/private 原件与 live bytes 均核验，未采用的私有文件拒绝，不赋予新执行 authority |
| 游戏环境 | 当前已声明 active `minecraft` 或 `.minecraft` 原目录名，mods/config/scripts 等 current 内容和作者已声明路径；未知个人文件默认拒绝，显式 `IncludeUnrecognizedGameFiles` 才纳入 |
| 另一游戏树 | 非空默认拒绝；明确 `InactiveGameTree=Exclude` 才排除，不合并为 active，也不生成无法重导的双 game root |
| 玩家数据 | saves、截图、servers 默认不带；各自 bool 选项明确包含；已知账号/令牌 store 和 LF PCL/事务/会话/历史/运行缓存始终不入 ZIP |
| JVM 偏好 | InheritGlobal 不复制全局值；UsePackage/Custom 写合法 override，Custom 空字符串保留 `OverrideJavaArgs=true` 与空 `JvmArgs`；新实例以包内 override 接收 |
| Java/内存 | 不导出本机绝对 Java 身份。默认拒绝无法移植的机器 Java/range/硬件内存；可显式选 DiscardMachineJavaSelection、给定 portable min/max MiB，preview 记录转换 |
| 窗口/额外 game | 普通窗口、包内/手动内存可表达。额外 game/fullscreen 转最后一个合法组件。无偏好尾部时原 structured tweakers 不变；有尾部时将原 --tweakClass 按原位置保留在完整 game argv，再追加偏好，清除独立+tweakers避免重复，preview 记录表示转换，共享类证明同时验证 raw tweaks |

组件有效启动定义经离线 Parse→Resolve→Compile，比对 main、JVM/game argv、tweakers、classpath/mainJar 顺序、native/download-only/aux、traits 与 Java policy。来源 hash、artifact key 和新实例身份不会冒充相同。native/source ZIP 提取规则未修改。

固定 Prism [MinecraftInstance.cpp](https://github.com/PrismLauncher/PrismLauncher/blob/899d2f3957215a09fe398a8a8cbb8a8a9c619968/launcher/minecraft/MinecraftInstance.cpp#L704-L712) 的 legacy minecraftArguments 按 ASCII 空格 split，随后追加 structured tweakers；它不执行引号解析。因此 literal 空/带空白单 token 不能假装通过加引号等价导出。若 source 的当前解释与该原生契约不一致，需先修正/重验源，不固化旧解析错误。依赖 Core `b492fabe` 的共享 raw/host 类要求和 `26dab1b5` 的仅 legacy 字段原生 split 修复；JVM/tokenizer 的语义不随之改变。

## 独立回归

```powershell
C:\Users\16892\.codex\tools\dotnet10\dotnet.exe run --project tests/PCLLF.Mmc.Export.Tests/PCLLF.Mmc.Export.Tests.csproj
```

该测试工程 ProjectReference 真正 modern/PCLCS，不链接第二份生产代码。自有小包通过真实 backend 导入→导出 ZIP→重导，覆盖 Java8/21 声明、fixed remote→local patch、已发布用户补丁、同名两根 library 字节、current config、new identity、空 JVM override、player 选项、inactive tree/未知数据、已有/冲突/原包/inside/非法输出、source/prefs stale，以及 held 真 ZIP writer 取消后的 drain。还覆盖有/无 structured tweaks 的 extra/fullscreen 精确 argv 和 raw class requirement、目标硬链接、不改原包、源 D:→目标 C: 但 staging/final 同目录、CAS 最后时刻篡改，以及 atomic publish 后观察者抛取消仍返回 Completed。所有网络响应和制品是自有 tiny metadata/JAR fixture；不访问公网，不下载 GTNH，不启动 Minecraft/JVM。

测试 roots 带 `PCLLF-MmcExportTests-<GUID>` 所有权文件并保留。曾在旧 import publication 遇到 transient reader-lock 导致 private-root rename AccessDenied，发生在 Export 之前；相关 roots 保留，集成前置 `3989d589` 提供重新验证后的 publication 修复。该依赖未混入导出提交，也不把测试前置失败写成导出成功。

最新验证：串行真图 build 0 warning / 0 error；十二个聚合组全 run 通过。有效自有 tiny JAR 包含 Java8 class 字节，但测试不启动 JVM/游戏。测试 fixture 与失败/取消 partial 按所有权保留，不删除玩家路径。known account 文件和 launcher/account flag 常量值明确拒绝；opaque mod 配置按字节复制，不将通用导出器描述为任意第三方模组凭据格式的解析/清洗器。

Astra medium 的两项 P2 实现修复已完成，仍待独立复核关闭：账号参数策略集中用于声明、extra 与 overlay/重编译后的最终参数；extra 还禁止替换 live account/root 绑定，合法声明占位符保持。默认排除服务器也检查 bare/equals flags。服务器 cfg 先按 INI 的存在性取值（空值同样优先），只有 INI 缺失且包内 JoinServerOnLaunch 真启用才回退地址，关闭状态的残留地址不会重新启用。
