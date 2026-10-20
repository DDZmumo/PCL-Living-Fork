# LivingPCL2-Re 1.0.0

基于 PCL 2.14.0.0 的首个 LivingPCL2-Re 正式发布版本，为第三方独立修改版。

## 主要变化

- 保留 Minecraft 原版下载与整合包安装功能。
- 默认复用旧 Client ID，支持微软正版登录；本机已由用户实测登录成功。
- 新增蓝白水晶角色默认背景和第二版六边形角色头像图标；启动时显示图标默认关闭。
- 更新源接入 `DDZmumo/LivingPCL2-Re` 的 GitHub Releases，手动检查后前往发布页下载，不再接受上游官方更新。
- 移除百宝箱、反馈、新功能投票入口；关于页新增 DDZmumo 与 GitHub 头像，保留原作署名和版权说明。
- Minecraft API 返回 `Invalid app registration` 时，明确提示 AppID 审核问题。

## CurseForge 临时 API Key

**CurseForge API Key 临时可用，不代表永久有效。后续可能失效、受到限制或更换，正式 API Key 仍在申请。** 发布程序包含构建时注入的临时 Key，不保证长期可用；Git 源码不包含 Client ID 或 API Key。

## 使用与配置

- 需要 Windows 64 位系统与 .NET Framework 4.8。
- 解压 ZIP 后运行 `LivingPCL.exe`；请保留同包的配置及许可说明。
- 已有 `PCL` 配置和自定义背景继续使用；存在自定义图片时，不强制替换为内置背景。
- 可通过 `PCL_MS_CLIENT_ID` 覆盖默认 Client ID。新申请的 AppID 需完成 Mojang 允许名单审核。
- 包内不包含维护者的账号、登录令牌、游戏实例、日志或用户配置。

原作作者为龙腾猫跃及 PCL 原项目贡献者，原作版权归成都瓜皮龙科技有限公司。详见同包 `NOTICE.md` 与 `LICENCE`。

## 构建验证

已通过 Release 构建、版本号核验及独立目录启动参数检查。微软登录已由维护者此前实测，当前发布未重复登录。
