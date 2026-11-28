// SPDX-License-Identifier: MPL-2.0
#nullable enable
namespace PCL;

internal static class AboutContent
{
    internal sealed record Link(string Caption, string? Address);
    internal sealed record Credit(string Name, string Info, string? Image, Link? Action);
    internal sealed record License(string Name, string Copyright, Link[] Actions);

    internal static readonly Credit[] Authors = [
        new("龙腾猫跃", "PCL 原作者", "/LivingPCL;component/Images/Heads/LTCat.jpg", new("支持原作者", "https://meloong.com/afd/a/LTCat")),
        new("HerobrineXia", "PCL 协力开发者", "/LivingPCL;component/Images/Heads/HerobrineXia.png", null),
        new("DDZmumo", "PCL Living Fork 维护者 · 功能与体验改进", "/LivingPCL;component/Images/Heads/DDZmumo.png", new("GitHub 主页", "https://github.com/DDZmumo")),
        new("PCL Living Fork", "当前版本：%VERSION%", "/LivingPCL;component/Images/Branding/LivingPCL-256.png", new("检查更新", null)),
    ];

    internal static readonly Credit[] Credits = [
        new("bangbang93", "提供 BMCLAPI 镜像源和 Forge 安装工具，详见 https://bmclapi.bangbang93.com", "/LivingPCL;component/Images/Heads/bangbang93.png", new("赞助镜像源", "https://meloong.com/afd/a/bangbang93")),
        new("MC 百科", "提供了 Mod 名称的中文翻译和更多相关信息！", "/LivingPCL;component/Images/Heads/wiki.png", new("打开百科", "https://www.mcmod.cn")),
        new("z0z0r4", "提供了 MCIM 社区资源镜像源和帮助库图床！", "/LivingPCL;component/Images/Heads/z0z0r4.png", new("MCIM 主页", "https://github.com/mcmod-info-mirror")),
        new("EasyTier", "提供了联机模块！", "/LivingPCL;component/Images/Heads/EasyTier.png", new("GitHub 页面", "https://github.com/EasyTier/EasyTier")),
        new("重庆大学开源软件镜像站", "提供了 PCL 启动器及 Minecraft 部分开源项目的镜像分发！", "/LivingPCL;component/Images/Heads/ChongQing.png", new("打开镜像站", "https://mirrors.cqu.edu.cn/")),
        new("00ll00", "提供了 Java Launch Wrapper 和一些重要服务支持！", "/LivingPCL;component/Images/Heads/00ll00.png", null),
        new("Patrick", "设计并制作了 PCL 图标，让我从做图标的水深火热中得到了解脱……", "/LivingPCL;component/Images/Heads/Patrick.png", null),
        new("Hao_Tian", "在内测中找出了一大堆没人想得到的诡异 Bug，有非同寻常的 Bug 体质", "/LivingPCL;component/Images/Heads/Hao_Tian.jpg", null),
        new("Minecraft 中文论坛", "虽然已经关站了，但感谢此前提供了 MCBBS 镜像源……", "/LivingPCL;component/Images/Heads/MCBBS.png", null),
        new("PCL 内群的各位", "感谢内群的沙雕网友们这么久以来对我和 PCL 的支持与鼓励！", "/LivingPCL;component/Images/Heads/PCL2.png", null),
    ];

    internal static readonly string[] ProjectText = [
        "PCL Living Fork 是基于 PCL 原项目的第三方修改版本，由 DDZmumo 维护。源码基线与逆向参考说明见项目 NOTICE。",
        "PCLLF 确认原创的独立源码采用 MPL-2.0；继承的 PCL 代码、混合来源及第三方组件保留各自许可。具体范围和完整协议可在本页下方离线查看。",
        "CurseForge 第三方 API 申请已获批。\n本版已接入正式 API Key。",
    ];
    internal static readonly Link[] ProjectLinks = [
        new("项目仓库", "https://github.com/DDZmumo/PCL-Living-Fork"),
        new("PCL 原作仓库", "https://github.com/Meloong-Git/PCL"),
    ];

    internal static readonly string[] LegalText = [
        "隐私声明与个人信息保护政策",
        "PCL Living Fork 已移除匿名遥测上报功能，不向 PCL 作者或本项目发送此类统计与错误报告。\n微软登录、资源下载和 GitHub 更新检查仍会按相应功能发起网络请求。",
        "其他信息",
        "Copyright © 成都瓜皮龙科技有限公司\r计算机软件著作权登记号：2020SR0875133\r违法违规行为举报投诉邮箱：admin@meloong.com\r非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。",
    ];
    internal static readonly Link[] LegalLinks = [
        new("用户协议与免责声明", "https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-RnU9"),
        new("PCL 分发有限许可", "https://shimo.im/docs/rGrd8pY8xWkt6ryW#anchor-X0bo"),
        new("原作源代码", "https://github.com/Meloong-Git/PCL"),
    ];

    internal static readonly string[] OfflineText = [
        "包含 PCLLF 原创代码的 MPL-2.0 范围、PCL／第三方来源与完整许可，以及仍待核实的授权项目。对应源码可从项目仓库按发行标签或提交取得。",
    ];
    internal static readonly Link[] OfflineLinks = [
    ];

    internal static readonly License[] Licenses = [
        new("Java Launch Wrapper 1.4.4", "Copyright © 2022 00ll00\nLicensed under the MIT License.", [new("查看来源网站", "https://github.com/00ll00/java_launch_wrapper/releases/tag/v1.4.4"), new("查看许可文档", "https://github.com/00ll00/java_launch_wrapper/blob/da6121dcf3c5d0f1f02be402b1fbbfdd9e07e095/LICENCE")]),
        new("LWJGL Unsafe Agent 2.0", "Copyright © 2026 Glavo\nLicensed under the Apache License, Version 2.0.", [new("查看来源网站", "https://github.com/HMCL-dev/lwjgl-unsafe-agent/releases/tag/2.0"), new("查看许可文档", "https://www.apache.org/licenses/LICENSE-2.0")]),
        new("Newtonsoft.Json", "Copyright © 2007 James Newton-King\nLicensed under the MIT License.", [new("查看来源网站", "https://www.newtonsoft.com/json"), new("查看许可文档", "https://opensource.org/licenses/MIT")]),
        new("NAudio", "Copyright © 2015 Mark Heath (mark.heath@gmail.com)\nLicensed under the Microsoft Public License (Ms-PL).", [new("查看来源网站", "https://github.com/naudio/NAudio"), new("查看许可文档", "https://github.com/naudio/NAudio/blob/f98664ba3d5966a736dd5cf45329e542be5fece8/license.txt")]),
        new("Ookii.Dialogs", "Copyright © 2009 Sven Groot (Ookii.org)\nLicensed under the BSD 3-Clause License.", [new("查看来源网站", "https://www.ookii.org/Software/Dialogs/"), new("查看许可文档", "https://github.com/caioproiete/ookii-dialogs-wpf/blob/master/LICENSE")]),
        new("Iconmonstr", "Copyright © 2019 iconmonstr (Alexander Kahlkopf)", [new("查看来源网站", "https://iconmonstr.com/"), new("查看许可文档", "https://iconmonstr.com/license/")]),
        new("libwebp wrapper for .NET", "Copyright © 2012 Imazen\nLicensed under the MIT License.", [new("查看来源网站", "https://github.com/imazen/libwebp-net"), new("查看许可文档", "https://github.com/imazen/libwebp-net/blob/master/LICENSE.txt")]),
        new("Iconfont", "Copyright © ALIMAMA MUX, powered by alimama THX.", [new("查看来源网站", "https://www.iconfont.cn/")]),
        new("ThrottleDebounce", "Copyright © 2026 Ben Hutchison\nLicensed under the Apache License, Version 2.0.", [new("查看来源网站", "https://github.com/Aldaviva/ThrottleDebounce"), new("查看许可文档", "https://licenses.nuget.org/Apache-2.0")]),
        new("CacheCow", "Copyright © Ali Kheyrollahi\nLicensed under the MIT License.", [new("查看来源网站", "https://github.com/aliostad/CacheCow"), new("查看许可文档", "https://licenses.nuget.org/MIT")]),
        new("Transformer Discovery Service 1.0", "Copyright © 2020 huangyuhui <huanghongxun2008@126.com> and contributors\nLicensed under the GNU General Public License, Version 3 or later, with additional terms.\n本组件不提供任何担保，可依 GPL 复制、修改及再分发；完整许可与对应源码随程序提供。", [new("查看来源网站", "https://github.com/HMCL-dev/HMCL/tree/7c510f9fb8655598a26b1b5937bcb0046d0960d4/HMCLTransformerDiscoveryService"), new("查看许可文档", "https://github.com/HMCL-dev/HMCL/blob/7c510f9fb8655598a26b1b5937bcb0046d0960d4/LICENSE"), new("保存对应源码", null)]),
        new("Markdig", "Copyright © 2016–2026 Alexandre Mutel\nLicensed under the BSD 2-Clause License.", []),
        new("MeloongCore", "MeloongCore contributors\nLicensed under the Apache License, Version 2.0.", []),
        new(".NET runtime / CoreCLR", "Copyright © .NET Foundation and Contributors\nLicensed under the MIT License.", []),
    ];
}
