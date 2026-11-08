<div align="center">

<img src="Plain Craft Launcher 2/Images/Branding/LivingPCL-256.png" alt="PCL Living Fork" width="90" height="90">

# PCL Living Fork

源自 [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL)、基于 **C# / .NET 10** 独立演进的现代化 Minecraft 启动器

[![Latest Release](https://img.shields.io/github/v/release/DDZmumo/PCL-Living-Fork?style=flat-square&label=Release&color=3b82f6)](https://github.com/DDZmumo/PCL-Living-Fork/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/DDZmumo/PCL-Living-Fork/build.yml?branch=main&style=flat-square&label=Build)](https://github.com/DDZmumo/PCL-Living-Fork/actions/workflows/build.yml)
[![MPL-2.0 原创代码许可](https://img.shields.io/badge/License-MPL--2.0%20%28PCLLF%20original%20code%29-3b82f6?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64%20%7C%20.NET%2010-64748b?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Downloads](https://img.shields.io/github/downloads/DDZmumo/PCL-Living-Fork/total?style=flat-square&label=Downloads&color=3b82f6)](https://github.com/DDZmumo/PCL-Living-Fork/releases)
[![QQ 交流群](https://img.shields.io/badge/QQ%20%E4%BA%A4%E6%B5%81%E7%BE%A4-%E7%82%B9%E5%87%BB%E5%8A%A0%E5%85%A5-12B7F5?style=flat-square&logo=tencentqq&logoColor=white)](https://qm.qq.com/q/MX1RivlHeE)

[下载 1.4.2-pre-1 预发布版](https://github.com/DDZmumo/PCL-Living-Fork/releases/tag/v1.4.2-pre-1) ·
[CNB 国内镜像](https://cnb.cool/ddzmumo/PCL-Living-Fork/-/releases) ·
[更新说明](https://github.com/DDZmumo/PCL-Living-Fork/releases/tag/v1.4.2-pre-1) ·
[历史版本](https://github.com/DDZmumo/PCL-Living-Fork/releases) ·
[问题反馈](https://github.com/DDZmumo/PCL-Living-Fork/issues) ·
[加入 QQ 群](https://qm.qq.com/q/MX1RivlHeE)

</div>

---

## 项目简介

**PCL Living Fork（PCLLF，原 LivingPCL）** 是由 [DDZmumo](https://github.com/DDZmumo) 独立维护的 Minecraft 启动器。项目最初源自 [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL)，经过全量 **C# / .NET 10** 架构重写与持续迭代，现已在底层架构、界面主题与核心功能体系上走出了独立于 **PCL 原版** 与 **PCL-CE** 的演进路线。

在延续丝滑动效与原有目录兼容的同时，PCLLF 围绕**本地资源全生命周期管理、模组依赖图谱与启动前补全、整合包三方差异更新、环境还原点与存档自动备份、跨版本个人习惯同步**以及**原生深浅色昼夜外观**构建了完整的功能闭环，让日常游玩与折腾整合包都更省心 (｡•̀ᴗ-)✧

> **使用前请注意**
> - **独立项目定位**：PCLLF 与 **PCL 官方版**、**PCL-CE** 均为各自独立演进的不同项目，底层实现、功能设计与版本号互不相同；遇到问题或有功能建议请在[本仓库 Issues](https://github.com/DDZmumo/PCL-Living-Fork/issues) 反馈或[加入 QQ 交流群](https://qm.qq.com/q/MX1RivlHeE)，**请勿向 PCL 官方或 PCL-CE 仓库提交本项目的任何问题** (´-ω-｀)
> - **Minecraft API 与 CurseForge 第三方 API 均已获批**：正式发行版内置本项目自有 Client ID 与正式 API Key，开箱即用（接口可用性以后续平台授权及服务政策为准）。

## 功能亮点

### 游戏资源与模组管理
- **全局资源收藏**：顶栏收藏区沿用下载区的搜索、分类与列表界面，收藏整个在线项目；下载、版本资源管理与收藏页同步状态，同一平台项目的不同文件和版本只保留一条记录。已匹配在线项目的模组、资源包和光影包可直接从本地列表收藏。
- **游戏资源管理**：在启动器内直接打理存档、截图、模组、资源包、光影包和投影/原理图，保留 PCL 原生列表、多选与动效；资源包与光影包支持在线更新，CurseForge 地图下载后自动归档至 `saves\PCLLFWorldDownload` 并解压安装，避免覆盖同名存档。
- **资源回收站**：误删的本地资源会先移入回收站，支持随时还原或确认后彻底删除。
- **模组依赖检查**：以圆角图标关系图直观展示模组的前置、被依赖关系与冲突；网络超时或平台未提供精确声明时自动回退参考本地声明，并缓存依赖树以加速后续打开。
- **加载器匹配核查**：识别 Fabric、Forge、NeoForge 及旧版 Forge 的实际加载声明，减少旧整合包残留模板造成的误报；不匹配模组在列表中标红提醒，内置组件自动归组展示。
- **启动前依赖管理**：主页“上次游玩”上方可随时开关依赖检查与自动补全。启动前先列出必需前置，已禁用且兼容的模组可直接启用，缺失文件则通过原生下载任务一键补齐后再启动。

### 整合包维护与环境保护
- **整合包更新**：支持从本地文件或 CurseForge / Modrinth 更新已识别的整合包，按“新增、更新、移除、保留个人修改”清晰分类预览；半途取消时也会等待文件处理与必要回滚收尾后再解除版本占用。
- **还原点**：支持手动或在更新前保存 Minecraft、加载器、模组与配置快照，改坏环境时可整套恢复，且**不会回退当前存档的游玩进度**（手动还原点独立保留，不计入自动清理上限）( •̀ ω •́ )✧
- **存档备份**：游戏正常退出后及更新 Minecraft 前自动备份世界，可在存档卡片下展开查看、恢复和设置自动备份保留份数（手动备份同样独立保留）。
- **测试副本与崩溃对照**：想加模组试水时可一键克隆当前版本，在版本选择中按来源折叠收纳；若游戏发生崩溃，会自动对照上次正常运行后的环境变化，并可联动还原点快速恢复。
- **整合包导出与模式**：支持附带启动器导出、设置版本隔离策略，并提供仅保留必要入口的整合包界面模式。

### 游玩体验与个性化
- **继续上次的世界**：主页卡片与存档管理页可直接通过 Quick Play 进入上次游玩的 Java 版存档（主页卡片可在“个性化 → 主页”中关闭）。
- **个人游戏习惯**：全局页记录导入来源，左右对照当前版本与共用基础设置，编辑后点击保存，来源标记为自定义；版本页跟随全局时锁定编辑，选择不同步后可单独编辑并保存本版本设置。按键、开关、拖动条复用 PCL 原生控件；新版本首次启动前即可写入勾选的基础设置并保留整合包默认配置，已有完整配置只修改对应项目。支持备份、冲突处理，按键管理也会标注基础键位的全局设置 ( ´ ▽ ｀ )ﾉ
- **浅色 / 深色外观与昼夜背景**：可在“设置 → 个性化”中即时切换浅色与深色主题，内置蓝白水晶角色背景会随主题自动切换白天与夜晚姿态；窗口、面板和背景图片的不透明度各自独立调节，不淡化文字与图标。自定义主题色等解锁功能请[赞助原作者龙腾猫跃](https://meloong.com/afd/a/LTCat)获取官方快照版 ♪(^∇^*)

## 运行环境与更新说明

| 项目 | 说明 |
| --- | --- |
| **运行环境** | `1.4.0` 及后续正式版基于 **C# / .NET 10 (Windows x64)** 构建。公开发布仅提供精简版 `EXE`、`ZIP` 与 `SHA256SUMS.txt`（均不含运行时），运行或自动升级前请先安装 [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)。 |
| **历史版本** | 旧版 `1.3.4`、`1.3.5-pre-3` 基于 `.NET Framework 4.8` 运行，旧 VB 源码已退役归档；有需要可从 [历史 Releases](https://github.com/DDZmumo/PCL-Living-Fork/releases) 按对应说明下载。 |
| **更新源与通道** | 国内入口为 [CNB Releases](https://cnb.cool/ddzmumo/PCL-Living-Fork/-/releases)。启动器更新默认使用 CNB 源，可在设置或更新提示中切换为 GitHub 源；默认仅接收正式版，也可开启“正式版 + 预发布版”通道。 |
| **数据与账号兼容** | 升级时完整保留原有 `PCL`、`.minecraft`、游戏目录及原文件名（兼容旧 `LivingPCL2-Re` 更新入口）。微软登录使用带 `_LF` 后缀的独立注册表键，避免与其他分支互相覆盖令牌；同目录运行时共用 `PCL\Setup.ini`。 |

## 更新日历与 Commit 日历

<div align="center">

<a href="https://github.com/DDZmumo/PCL-Living-Fork/releases">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/DDZmumo/PCL-Living-Fork/refs/heads/codex/readme-calendar/docs/assets/calendar-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/DDZmumo/PCL-Living-Fork/refs/heads/codex/readme-calendar/docs/assets/calendar-light.svg">
    <img alt="PCL Living Fork 更新与提交日历" src="docs/assets/calendar-light.svg" width="100%">
  </picture>
</a>

</div>

## 自行编译

当前源码使用 **.NET 10 SDK** 与 Windows Desktop / WPF 工具链（构建旧版兼容交接 EXE 还需安装 Visual Studio / Build Tools 的 MSBuild 与 .NET Framework 4.8 编译工具）。在 Windows PowerShell 中运行：

```powershell
git submodule update --init --recursive
dotnet restore .\PCL-Living-Fork.Modern.slnx
.\scripts\Build-Release.ps1 -Slim
```

- 本机构建默认显示 `dev_edition`；公开发布构建需显式使用 `-Slim` 和 `-OfficialRelease`。
- 源码不包含实际 Client ID 或 API Key，本地可通过环境变量 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 提供配置，构建时会加密嵌入。
- 相关工程文档：
  - [现代构建与测试说明](docs/BUILDING_MODERN.md)
  - [公开发布约定](docs/PUBLISHING.md)
  - [外观与启动器更新通道](docs/theme-and-update-channels.md)

## 来源与许可

原作作者为 **龙腾猫跃** 及 PCL 原项目贡献者，原作版权归 **成都瓜皮龙科技有限公司** 所有。本项目早期以 PCL `2.14.0.0` 为起点独立演进并完成 C# / .NET 10 架构重构（部分兼容实现曾参考 [PCL-CE](https://github.com/PCL-Community/PCL-CE)），完整保留原作与第三方组件的署名和许可。

- [上游 PCL 仓库](https://github.com/Meloong-Git/PCL) · [来源与版权声明](NOTICE.md) · [许可与使用指南](LICENCE) · [爱发电支持原作者](https://meloong.com/afd/a/LTCat)

PCLLF 确认原创的独立源码采用 **[Mozilla Public License 2.0（MPL-2.0）](LICENSE)**，仓库根目录提供完整协议，范围见 [文件清单](licenses/MPL-COVERED-FILES.txt)。PCL 继承代码、混合来源及第三方组件保留各自许可，详见 [原 PCL 许可](LICENCE)、[第三方声明](THIRD-PARTY-NOTICES.md) 与 [许可审计](docs/licensing-audit.md)。
