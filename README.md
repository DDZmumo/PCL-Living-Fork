# LivingPCL2-Re

**LivingPCL**，完整名称为 **PCL LivingPCL（第三方修改版）**，是第三方基于 Plain Craft Launcher（PCL）独立修改的 Minecraft 启动器，不是原作者或 PCL 团队发布的官方版本。

当前发布版本：**1.0.1**。Windows 64 位系统需要 .NET Framework 4.8；下载发布包后解压运行 `LivingPCL.exe`。

**CurseForge API Key 临时可用，不代表永久有效，后续可能失效或更换。正式 API Key 仍在申请。** 本次发布按维护者确认包含当前临时 Key；请勿将其可用性视为长期保证。

- 原作作者：龙腾猫跃及 PCL 原项目贡献者。
- 原作版权：Copyright © 成都瓜皮龙科技有限公司。
- 上游原仓库：[Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)。
- 本分支仓库：[DDZmumo/LivingPCL2-Re](https://github.com/DDZmumo/LivingPCL2-Re)，由本次克隆来源 `DDZmumo/PCL` 更名而来。
- 原作者赞助链接：[龙腾猫跃 / LTCat](https://meloong.com/afd/a/LTCat)。

原作和第三方组件的版权、署名与许可继续保留，更名不代表原作版权转移。详见 [来源与版权声明](NOTICE.md)、[原仓库许可与使用指南](LICENCE) 及关于页面。

[![Stars](https://img.shields.io/github/stars/Meloong-Git/PCL?style=flat&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZlcnNpb249IjEiIHdpZHRoPSIxNiIgaGVpZ2h0PSIxNiI+PHBhdGggZD0iTTggLjI1YS43NS43NSAwIDAgMSAuNjczLjQxOGwxLjg4MiAzLjgxNSA0LjIxLjYxMmEuNzUuNzUgMCAwIDEgLjQxNiAxLjI3OWwtMy4wNDYgMi45Ny43MTkgNC4xOTJhLjc1MS43NTEgMCAwIDEtMS4wODguNzkxTDggMTIuMzQ3bC0zLjc2NiAxLjk4YS43NS43NSAwIDAgMS0xLjA4OC0uNzlsLjcyLTQuMTk0TC44MTggNi4zNzRhLjc1Ljc1IDAgMCAxIC40MTYtMS4yOGw0LjIxLS42MTFMNy4zMjcuNjY4QS43NS43NSAwIDAgMSA4IC4yNVoiIGZpbGw9IiNlYWM1NGYiLz48L3N2Zz4=&logoSize=auto&label=Stars&labelColor=444444&color=eac54f)](https://github.com/Meloong-Git/PCL/)
[![Issues](https://img.shields.io/github/issues/Meloong-Git/PCL?style=flat&label=Issues&labelColor=444444&color=1F883D)](https://github.com/Meloong-Git/PCL/issues)
[![哔哩哔哩](https://img.shields.io/badge/动态-BiliBili-00A4DB?style=flat&labelColor=444444&logoSize=auto)](https://space.bilibili.com/11343203/dynamic)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946ce6?style=flat&labelColor=444444&logoSize=auto)](https://meloong.com/afd/a/LTCat)

上方徽章与赞助链接指向原 PCL 项目。本分支包含原作的 UI 库、动画模块、下载模块、Minecraft 启动模块等代码及 LivingPCL 的本地修改。

### 本地固定版本

本分支基于 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`（2.14.0.0 正式版），即新增 Minecraft 下载限制之前的版本，包含对应的 MeloongCore 子模块。

本地编译的启动器已禁用上游 PCL 官方更新，不加载其更新配置或比较其版本缓存，并拒绝 `--update` 文件替换命令。启动时和检查更新按钮均查询本仓库 `DDZmumo/LivingPCL2-Re` 的 GitHub Releases。新正式版按版本提醒一次，可选择“下载并更新”；校验 SHA256 后运行新 EXE 完成替换和重启，替换失败保留或恢复旧 EXE，并提供发布页入口。无新版或启动检查遇到网络错误时保持安静。Minecraft 下载、整合包安装和游戏资源更新保持此基线的实现。

更新检查使用 GitHub 的最新正式 Release 接口，草稿和预发布不作为正式更新。发布标签建议使用 `v1.0.2` 这样的数字版本，并在发布新构建时同步更新 `VersionBaseName`；若仓库没有正式 Release、标签不能解析为数字版本或更新失败，手动检查会提供发布页供核对。CurseForge 下载临时可用，正在等待正式 API Key 下发；临时 Key 不代表永久有效。

编译需要 Windows、Visual Studio 2022 的“.NET 桌面开发”工作负载、.NET Framework 4.8 SDK/Targeting Pack，以及支持 C# 13 的 .NET SDK（本机使用 9.0.302）。在 VS 2022 Developer PowerShell 中执行：

```powershell
git submodule update --init --recursive
dotnet restore .\PCLCS\PCLCS.csproj
msbuild ".\Plain Craft Launcher 2\LivingPCL2-Re.vbproj" -p:Configuration=Release
```

解决方案为 `LivingPCL2-Re.sln`，输出位于 `Plain Craft Launcher 2\bin\LivingPCL.exe`。源码目录、内部 `PCL` 命名空间、现有配置/数据目录和环境变量名保留兼容；窗口标题、默认标题栏、程序属性、导出文件名及网络 User-Agent 使用 LivingPCL。仓库根目录的 `最新正式版.zip` 是上游原发布文件，不包含本地修改，不是 LivingPCL 构建产物。发布程序构建时注入 Client ID 和临时 CurseForge Key；Git 源码不包含这些值。自行构建可设置 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 后运行 `scripts/Build-Release.ps1`，或在运行程序前设置这两个环境变量。

微软应用注册与 Minecraft 游戏服务 API 审核是两个步骤。若网页登录和 Xbox 验证已成功，但 `authentication/login_with_xbox` 返回 `403` 且包含 `Invalid app registration`，请检查 Client ID 与 Mojang 允许名单审核状态；刚提交审核不代表已获批。启动器会将这一错误单独提示为应用注册未获认可。

当前本机复用龙腾猫跃 Client ID 后，已由用户实测完成正版登录；版本说明弹窗已同步这一状态。源码通过 `PCL_MS_CLIENT_ID` 环境变量配置 Client ID。

将程序放到目标文件夹即可使用：没有 `PCL` 文件夹时自动创建；已有时复用 `PCL\Setup.ini`、自定义素材及游戏版本配置，并只读继承官方 PCL 注册表中兼容的启动、Java、下载和界面偏好。LivingPCL 已保存的全局设置优先，游戏文件夹列表按路径合并去重，重启后读取 PCL 的新增设置。账号、令牌、第三方登录凭据及玩家资料统一使用官方 PCL 的注册表位置和加密规则（硬件标识、本地种子、稳定哈希及 DES）。首次启动会将旧 LivingPCL 账号和历史硬件密文转换为当前官方格式，校验后删除 `PCLDebug` 中已转换的账号项，不保留旧密文备份。微软和离线账号列表合并，同名账号优先保留 LivingPCL；登录缓存按整组转换，避免混用账号。转换后两个启动器共用账号存储，在一边修改或移除账号会影响另一边。无法验证的数据原样保留，避免丢失。主题解锁、使用次数、历史版本及彩蛋状态不迁移。

旧 PCL 配置和版本列表缓存中的内嵌图标地址会在读取时兼容到当前程序集，帮助包及自定义主页也支持旧程序集名、URL 编码和大小写变体；无需删除缓存或重设自定义图片。

“更多”保留帮助与关于，移除了百宝箱、反馈和新功能投票入口及对应的功能隐藏选项；旧页面编号会回到关于页。“关于”新增维护者 DDZmumo，头像取自其 GitHub 公开头像并随程序打包。

默认视觉采用用户提供的人设：蓝白水晶风格角色背景加入草方块；当前图标选用第二版六边形角色头像，右眼下方为蓝色实心小倒三角。背景、窗口图标、程序 ICO、可选启动画面和关于页图标均随程序内嵌，原 PCL 图标资源保留。“打开启动器时显示 PCL 图标”默认关闭，用户已有设置保留。`PCL\Pictures` 中存在自定义图片时仍优先使用自定义背景，清空后恢复内置背景；可在设置中调整默认背景的透明度、模糊和适应方式。设计素材与生成说明见 `Plain Craft Launcher 2/Images/Branding/DESIGN.md`。

<br/>

### 相关资源
- [PCL 下载](https://meloong.com/pcl)：下载可免费使用的正式版 PCL！
- [PCL 功能投票](https://github.com/Meloong-Git/PCL/discussions/2)：来参加投票吧，开发者会优先处理票数较高的项目！
- [帮助文档库](https://github.com/LTCatt/PCL2Help)：PCL 内置帮助文档的存储库

### 发布验证

构建后可运行 `powershell -NoProfile -STA -File scripts/Test-Release.ps1 -ExecutablePath "Plain Craft Launcher 2\bin\LivingPCL.exe"`。检查仅使用虚构账号和临时测试目录，验证账号合并、重加密、旧项清理、失败回滚、版本比较和 EXE 替换；不读取真实账号，也不打开启动器界面。
