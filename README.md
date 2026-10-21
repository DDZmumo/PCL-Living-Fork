# LivingPCL2-Re

**LivingPCL** 是基于 Plain Craft Launcher（PCL）官方开源版的第三方 Minecraft 启动器，由 DDZmumo 维护，非 PCL 官方版本。

保留原版下载与启动功能，加入角色背景和图标、兼容配置共享，以及本仓库的检查更新与下载更新。

## 使用

需要 Windows 64 位系统和 .NET Framework 4.8。从 [Releases](https://github.com/DDZmumo/LivingPCL2-Re/releases) 下载 EXE 直接运行，或下载 ZIP 解压后运行 `LivingPCL.exe`。升级时保留原有 `PCL`、`.minecraft` 和游戏目录。

- 与官方 PCL 共用兼容的注册表设置和账号；同目录运行时也共用 `PCL\Setup.ini`。
- 在一边修改共享设置或账号，会影响另一边；切换启动器前请先退出当前实例。
- 内置背景与默认配色可在设置中调整，自定义图片放入 `PCL\Pictures`。
- 更多 → 原版百宝箱提供内存优化、清理游戏垃圾和自定义文件下载。
- 整合包模式隐藏下载、更多和部分管理选项，版本选择仅限本目录的整合包，概览显示已安装组件；附带启动器导出时默认开启，仅带本目录导出标记的整合包可切换此模式。
- 导出高级选项可设置“开启该版本的版本隔离”，默认勾选，导入后按此选择使用独立或共享游戏目录。

**CurseForge API Key 临时可用，不代表永久有效。正式 API Key 仍在申请。**

## 编译

需要 Visual Studio 2022 的“.NET 桌面开发”工作负载、.NET Framework 4.8 Targeting Pack 和 .NET 9 SDK。

```powershell
git submodule update --init --recursive
dotnet restore .\PCLCS\PCLCS.csproj
.\scripts\Build-Release.ps1
```

请在 VS Developer PowerShell 中构建。程序默认输出到 `Plain Craft Launcher 2\bin\LivingPCL.exe`。

源码不包含 Client ID 或 API Key。自行构建时，通过 `PCL_MS_CLIENT_ID` 和 `PCL_CURSEFORGE_API_KEY` 环境变量提供自己的配置；发布构建会加密保存这些值。

## 来源与许可

原作作者为龙腾猫跃及 PCL 原项目贡献者，原作版权归成都瓜皮龙科技有限公司。基线为上游 2.14.0.0，保留原作与第三方组件的署名和许可。

- [上游 PCL](https://github.com/Meloong-Git/PCL)
- [来源与版权声明](NOTICE.md)
- [许可与使用指南](LICENCE)
- [支持原作者](https://meloong.com/afd/a/LTCat)
