# PCL Living Fork

**PCL Living Fork**（原 LivingPCL）是基于 [Plain Craft Launcher（PCL）](https://github.com/Meloong-Git/PCL) 原项目的第三方 Minecraft 启动器，由 DDZmumo 维护，非 PCL 官方版本。

[下载 1.3.1](https://github.com/DDZmumo/PCL-Living-Fork/releases/tag/v1.3.1) · [更新说明](RELEASE_NOTES_v1.3.1.md) · [问题反馈](https://github.com/DDZmumo/PCL-Living-Fork/issues)

## 功能

- **游戏资源管理**：在启动器内管理存档、截图、模组、资源包、光影包和投影/原理图，使用 PCL 原生列表、多选与动效。资源包和光影包支持在线更新，下载页提供 CurseForge 地图下载。
- **个人游戏习惯**：按类别同步按键、鼠标、视野、字幕等设置，每个版本可单独例外；按键管理支持搜索、同键占用检查和重新绑定。
- **继续上次的世界**：首页和存档管理可直接进入支持 Quick Play 的 Java 版存档，首页卡片可在“个性化 → 主页”关闭。
- **整合包更新**：从本地文件或 CurseForge / Modrinth 更新已识别的整合包，先预览变化与冲突，保留个人修改并下载变化文件。取消后会等待文件处理和必要的回滚结束，再解除版本占用。
- **还原点**：手动或在更新前保存 Minecraft、加载器、模组与配置，支持整套恢复、删除和恢复前留档，保留当前存档进度。
- **存档备份**：正常退出后备份世界，更新 Minecraft 前额外备份；在存档下展开查看、恢复和删除备份，并设置保留份数。
- **模组依赖检查**：查看依赖与被依赖关系，删除、禁用前提示影响，启动前检查明确的依赖错误；缺失元数据时尝试联网补充。
- **测试副本与崩溃对照**：克隆当前版本进行测试，在版本选择中按来源折叠显示；崩溃时对照上次正常运行后的环境变化，并连接到还原点。
- **整合包导出与模式**：可附带启动器、选择该版本的版本隔离，并使用仅保留必要入口的整合包界面。

## 使用

需要 **Windows 64 位系统和 .NET Framework 4.8**。从 [Releases](https://github.com/DDZmumo/PCL-Living-Fork/releases) 下载 EXE，或解压 ZIP 后运行 `PCL Living Fork.exe`。

升级时保留原有 `PCL`、`.minecraft` 和游戏目录。旧 `LivingPCL2-Re` 仓库继续提供兼容更新入口。

可与官方 PCL 共用兼容的账号和设置；同目录运行时共用 `PCL\Setup.ini`，修改共享配置也会影响另一边。保留固定默认配色，背景图片可自行更换；自定义及其他颜色主题请[赞助原作者](https://meloong.com/afd/a/LTCat)，获取官方快照版。

**Minecraft API 与 CurseForge 第三方 API 均已获批，发行版使用本项目自有 Client ID 和正式 API Key。** 接口可用性以后续平台授权及服务政策为准。

## 自行编译

需要 Visual Studio 2022 的“.NET 桌面开发”工作负载、.NET Framework 4.8 Targeting Pack 和 .NET 9 SDK。在 VS Developer PowerShell 中运行：

```powershell
git submodule update --init --recursive
dotnet restore .\PCLCS\PCLCS.csproj
.\scripts\Build-Release.ps1
```

输出为 `Plain Craft Launcher 2\bin\PCL Living Fork.exe`。源码不包含实际 Client ID 或 API Key；请通过 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 环境变量提供自己的配置，构建时会加密嵌入。

## 来源与许可

原作作者为龙腾猫跃及 PCL 原项目贡献者，原作版权归成都瓜皮龙科技有限公司。基线为上游 2.14.0.0，保留原作与第三方组件的署名和许可。

- [上游 PCL](https://github.com/Meloong-Git/PCL)
- [来源与版权声明](NOTICE.md)
- [许可与使用指南](LICENCE)
- [支持原作者](https://meloong.com/afd/a/LTCat)
