# 现代 C# 产品开发入口

主产品已在 `main` 上使用 C#、.NET 10 和 WPF。打开 [PCL-Living-Fork.sln](../PCL-Living-Fork.sln) 或 [PCL-Living-Fork.Modern.slnx](../PCL-Living-Fork.Modern.slnx)，将 [Main 项目](../modern/Main/PCL-Living-Fork.Modern.csproj)设为启动项目；两个 solution 都使用同一套产品代码。旧 VB 产品工程已退役，独立的历史测试 oracle 继续保留。

来源与许可见 [NOTICE.md](../NOTICE.md) 和 [SOURCE_PROVENANCE.md](../modern/Main/SOURCE_PROVENANCE.md)。迁移期任务账本与审计报告保存在 Git 历史中，已不作为当前开发计划。

业务 Core 与 Windows/WPF 适配层已分为独立程序集，依赖方向、现存宿主约束和新 UI 接入要求见 [架构边界](ARCHITECTURE.md)。

当前默认工作台与经典对照的运行、内部壁纸材质和定向验证见 [WPF 工作台界面](GLASS_UI.md)。

## Windows 工具链与依赖

使用 Windows x64、.NET 10 SDK，以及支持 .NET 10/WPF 的 IDE。初始化固定 Shared 子模块：

```powershell
git submodule update --init MeloongCore
git -C MeloongCore rev-parse HEAD
```

Shared 固定为 `c774ae4c85fd94e0eaa171db21e78a65870df19b`，保留 `netstandard2.0`。普通精简包依赖已安装的 .NET 10 Desktop Runtime x64；构建内部完整包或旧版兼容桥时，另需 Visual Studio/Build Tools 的 .NET Framework 编译工具。

## 构建、启动和定向验收

构建与发布在同一工作树顺序执行，避免并发覆盖共享输出：

```powershell
dotnet restore PCL-Living-Fork.Modern.slnx
dotnet build PCL-Living-Fork.Modern.slnx --configuration Debug --no-restore -nr:false
```

只修改产品时，可直接构建 Main：

```powershell
dotnet build modern/Main/PCL-Living-Fork.Modern.csproj --configuration Debug --no-restore -nr:false
dotnet run --project modern/Main/PCL-Living-Fork.Modern.csproj --configuration Debug --no-build
```

普通启动使用正常用户数据路径。需要隔离验证时，使用仓库现有测试宿主，不伪造 CI 守卫环境。可选 LegacyLink 编译模式使用 `-p:EnableLegacyLink=true`；编译通过与服务实际可用是不同的验证范围。

测试按受影响链选择，例如：

```powershell
dotnet run --project tests/PCLLF.Migration.Tests/PCLLF.Migration.Tests.csproj --configuration Debug --no-build -- --group world-backups
```

真实 WPF 组件与 Native facade 验收在 Windows 执行，入口分别见 [组件测试工程](../tests/PCLLF.Modern.Wpf.Tests/PCLLF.Modern.Wpf.Tests.csproj)和 [Native facade 说明](../tests/PCLLF.Native.Facade.Tests/README.md)。源码、组件测试、冷启动、真实账号和 Minecraft 启动结果分别报告。

只构建业务库时不需要 WPF 工具链：

```powershell
dotnet build modern/Core/PCLLF.Core.csproj --configuration Debug -nr:false
dotnet run --project tests/PCLLF.Migration.Tests/PCLLF.Migration.Tests.csproj --configuration Debug -- --group foundation
```

纯业务测试通过 `ProjectReference` 引用产品使用的 Core DLL，不再链接生产源码另编一份。`foundation` 组同时核对程序集边界、资源归属和 ZIP 流读取契约。Core 的框架目标为 `net10.0`；其中仍有 Windows 注册表和路径兼容接口，无 WPF 依赖不表示所有 API 都可跨平台运行。

应用服务的状态归属和接入边界见 [架构说明](ARCHITECTURE.md#应用服务与状态归属)。定向验证使用 `foundation` 与 `validation-loaders` 业务组，以及 Windows Main 的 `application-services` 组；账号链另行选择 `account-ui`。本地 facade 运行前设置 `$env:PCL_NATIVE_REPOSITORY_ROOT = $PWD.Path`，不要伪造 CI 环境。

## Release 发布包

普通 Debug、Release 和精简构建默认显示 `dev_edition`。`ModBase.ReleaseBaselineVersion` 用于更新比较，开发构建不会被同基线或更旧发布覆盖；更新通道仍决定是否接收预发布版。只有显式传入 `-OfficialRelease`／`LFOfficialRelease=true` 才使用正式发布身份。

本地精简测试包：

```powershell
$releaseOutput = Join-Path $PWD ('artifacts/slim-test/' + [Guid]::NewGuid().ToString('N'))
pwsh -NoProfile -File scripts/Build-Release.ps1 -Slim -Configuration Release -OutputPath $releaseOutput
```

正式版或预发布版先更新源码中的发布基线和数字程序集版本，再构建：

```powershell
$releaseOutput = Join-Path $PWD ('artifacts/public-release/' + [Guid]::NewGuid().ToString('N'))
pwsh -NoProfile -File scripts/Build-Release.ps1 -Slim -OfficialRelease -Configuration Release -OutputPath $releaseOutput
```

精简包不含 .NET 运行时。公开 EXE 自动包含旧版更新兼容桥，ZIP 内包含可直接运行的精简原生主程序，二者使用相同的公开文件名；兼容桥内嵌负载与 ZIP 内程序必须逐字节一致。`SlimRelease.Artifacts.json` 记录实际版本、角色、文件名、大小、SHA256、兼容桥标记和原生负载摘要。附件必须符合 [公开发布约定](PUBLISHING.md)。

源码不包含实际 Client ID 或 API Key。本地通过 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 提供构建输入；CI 仅在对应步骤映射这两个值，生成器使用独立临时目录并在结束后清理。凭据不得进入命令行、日志或提交。

不带 `-Slim` 的 Native 自包含包保留给内部 CI、诊断和旧桥资格验证。其 `NativeRelease.Artifacts.json` 中的 `native-exe`、`native-zip`、`legacy-bridge`、`checksums` 是内部角色，不能直接当作公开附件清单上传。构建脚本只生成文件，不发布 Release。

## 主分支 CI 门禁

`main` 的代码、项目、构建脚本、工作流或未知路径变更执行产品合并检查、迁移及 WPF 验证。产品检查编译 Debug 和 Release 主程序。Release 还验证资源、凭据、更新桥、真实启动与正常关闭后重开，在独立 Windows runner 实测旧版升级，并运行启动、账号、工作台、更新、实例和资源的核心回归。纯文档变更按选择器的明确白名单执行轻量校验，并返回必需检查状态。

文档与代码混合修改仍走合并检查。`docs/` 下的脚本和 JSON 数据、产品图片、运行时资源及代理指令文件不属于文档白名单。重命名检查同时考虑旧路径和新路径。事件、比较基线或路径无法可靠取得时，选择器启用产品、迁移及 WPF 验证。

`Native product gate`、`Migration validation gate`、`Modern WPF gate` 核对真实选择结果：选中的验证必须成功，失败、取消和意外跳过不能通过。CNB 检查只处理离线合成数据，不触发公开发布。

需要全量 Windows 回归时，在 Actions 手动运行 **Build**。手动构建还检查 Debug 打包和 facade、Release 连续界面操作、历史构建工具、可选 LegacyLink 编译及精简打包。针对单项功能，也可以在 Windows 本地运行对应分组。 [Native facade 命令与环境要求](../tests/PCLLF.Native.Facade.Tests/README.md)

本地验证选择器和门禁（Python 需有 PyYAML 6.0.2）：

```text
python scripts/Check-Documentation.py
python scripts/tests/Test-MainCiGates.py
python scripts/tests/Test-MigrationSelection.py
```

CI 规则本身的修改不享受纯文档快速路径。实际产品、旧版升级与发布资格仍以对应提交的干净 CI 和隔离 Windows 证据为准。

旧版升级验证使用仓库中长期保存的 [固定历史归档](../tests/fixtures/legacy-upgrade/README.md)，继续核对原始 ZIP 与 EXE 的长度、SHA256 和包内结构，不依赖会过期的 Actions 附件，也不改用漂移的最新版本。历史 VB 测试的编译器依次通过显式 `-VbcPath`、`PATH`、`vswhere` 定位，支持不同 Visual Studio 版本及自定义安装路径；可运行 `scripts/tests/Test-VbCompilerDiscovery.ps1` 验证定位逻辑。

## 源码职责与发布体积

| 路径 | 职责 |
| --- | --- |
| `modern/Main` | WPF 入口、页面、原生控件适配与模块调用面 |
| `modern/Core/PCLLF.Core.csproj` | 编译 `PCLCS` 中的业务源码，目标 `net10.0`，兼容程序集名 `PCLCS` |
| `PCLLF.WPF.Core/PCLLF.WPF.Core.csproj` | 编译 `PCLLF.WPF.Core` 中的控制器、契约与服务，目标 `net10.0-windows`，程序集名 `PCLLF.WPF.Core` |
| `presentation` | WPF 控件、页面、对话框与窗口表现层 |
| `modern/BuildEvents` | 原 WithEvents 事件绑定的生成与校验 |
| `modern/BuildResources` | 必要数据/原生资源及 WPF pack 资源清单 |

托管依赖通过正常依赖图或单文件 bundle 提供，不在主程序集资源中重复嵌入。Native 资源保留 `Custom`、`ForgeInstaller`、`Help`、`libwebp64`、`Transformer` 五个必要数据/原生负载。框架依赖的单文件包保留完整资源和身份校验，不使用会破坏 WPF 的裁剪来缩小体积。

Core 的四个原嵌入资源继续保存在 `PCLCS.dll`，大小写、反斜线资源名和读取行为保持一致。`PCLLF.WPF.Core.dll` 由项目引用自动进入构建及发布输出；维护按 DLL 加载的诊断工具时需要同时提供它。历史 `PCLCS/PCLCS.csproj` 是旧 net48 工程，不属于当前产品 solution。

版本说明使用原生 WPF Markdown 视图。Markdig 和其他第三方组件的许可保留在关于页与仓库第三方目录。
