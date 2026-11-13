# 现代 C# 产品开发入口

主产品已在 `main` 上使用 C#、.NET 10 和 WPF。打开 [PCL-Living-Fork.sln](../PCL-Living-Fork.sln) 或 [PCL-Living-Fork.Modern.slnx](../PCL-Living-Fork.Modern.slnx)，将 [Main 项目](../modern/Main/PCL-Living-Fork.Modern.csproj)设为启动项目；两个 solution 都使用同一套产品代码。旧 VB 产品工程已退役，独立的历史测试 oracle 继续保留。

来源与许可见 [NOTICE.md](../NOTICE.md) 和 [SOURCE_PROVENANCE.md](../modern/Main/SOURCE_PROVENANCE.md)。迁移期任务账本与审计报告保存在 Git 历史中，已不作为当前开发计划。

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

精简包不含 .NET 运行时，也不自动生成旧版兼容桥。`SlimRelease.Artifacts.json` 记录实际版本、角色、文件名、大小与 SHA256。精简构建直接生成公开约定的 EXE、ZIP 及校验清单，ZIP 内主程序使用相同的最终 EXE 名。附件必须符合 [公开发布约定](PUBLISHING.md)。如提供旧版交接层，其负载必须与同次精简主程序一致。

源码不包含实际 Client ID 或 API Key。本地通过 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 提供构建输入；CI 仅在对应步骤映射这两个值，生成器使用独立临时目录并在结束后清理。凭据不得进入命令行、日志或提交。

不带 `-Slim` 的 Native 自包含包保留给内部 CI、诊断和旧桥资格验证。其 `NativeRelease.Artifacts.json` 中的 `native-exe`、`native-zip`、`legacy-bridge`、`checksums` 是内部角色，不能直接当作公开附件清单上传。构建脚本只生成文件，不发布 Release。

## 主分支 CI 门禁

`main` 的代码、项目、构建脚本、工作流或未知路径变更执行完整产品、迁移及 WPF 验证。纯文档变更按选择器的明确白名单识别，跳过产品构建、游戏/窗口运行、迁移测试及 WPF 矩阵，只执行轻量文档校验并返回必需检查状态；不使用 workflow 的 `paths-ignore`，避免 PR 卡在等待检查。

文档与代码混合修改仍走完整验证。`docs/` 下的脚本和 JSON 数据、产品图片、运行时资源及代理指令文件不属于文档白名单。重命名检查同时考虑旧路径和新路径。手动运行和 `--full` 保持完整检查；事件、比较基线或路径无法可靠取得时回退完整验证。

`Native product gate`、`Migration validation gate`、`Modern WPF gate` 核对真实选择结果：选中的验证必须成功，失败、取消和意外跳过不能通过。CNB 检查只处理离线合成数据，不触发公开发布。

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
| `PCLCS` | 业务规则、文件事务与平台实现 |
| `PCLCS/Platform/Windows` | 按业务域划分的 Windows 控制器、契约与服务 |
| `presentation` | WPF 控件、页面、对话框与窗口表现层 |
| `modern/BuildEvents` | 原 WithEvents 事件绑定的生成与校验 |
| `modern/BuildResources` | 必要数据/原生资源及 WPF pack 资源清单 |

托管依赖通过正常依赖图或单文件 bundle 提供，不在主程序集资源中重复嵌入。Native 资源保留 `Custom`、`ForgeInstaller`、`Help`、`libwebp64`、`Transformer` 五个必要数据/原生负载。框架依赖的单文件包保留完整资源和身份校验，不使用会破坏 WPF 的裁剪来缩小体积。

版本说明使用原生 WPF Markdown 视图。Markdig 和其他第三方组件的许可保留在关于页与仓库第三方目录。
