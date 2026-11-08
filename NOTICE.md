# PCL Living Fork 来源与版权声明

项目标识：PCL-Living-Fork。程序名称：PCL Living Fork（原 LivingPCL）。内部程序集及既有数据标识保留兼容。

本项目是基于 Plain Craft Launcher（PCL）原项目的第三方修改版本，不是龙腾猫跃、成都瓜皮龙科技有限公司或原 PCL 团队发布的官方版本，也不表示已获得其背书。

## 原作与来源

- 原作：Plain Craft Launcher（PCL）。
- 原作作者：龙腾猫跃及 PCL 原项目贡献者；具体贡献记录见原仓库 Git 历史与关于页面。
- 原作版权声明：Copyright © 成都瓜皮龙科技有限公司。
- 上游原仓库：https://github.com/Meloong-Git/PCL 。
- 本分支仓库：https://github.com/DDZmumo/PCL-Living-Fork ，由本次克隆来源 `DDZmumo/PCL` 经 `LivingPCL2-Re` 更名而来；旧仓库保留为兼容更新入口。
- 本分支代码基线：`5ef81e490ae46b1100d61e33d0183094c7a80ca8`，PCL 2.14.0.0 正式版。

## 源码基线与逆向参考

当前工程基于上述源码继续修改；部分功能结合对原版发行程序的分析实现。维护者补充的逆向参考来源包含赞助快照版。源码基线、参考发行版和本项目的最终构建属于不同层次，不将整个项目笼统称为官方开源版或官方赞助快照版。

对发行程序的分析不改变原作、源码与资产的版权归属；本项目保持独立第三方身份，不主张获得原作权利人背书。

工具百宝箱的今日人品算法及结果文案参考维护者提供的 PCL 2.99 发行程序，经只读静态分析恢复。该派生实现保留原 PCL 许可，不纳入 PCLLF 原创代码的 MPL 清单；准确文件身份、方法与实现边界见 [来源记录](modern/Main/SOURCE_PROVENANCE.md)。
- 原作者赞助链接：https://meloong.com/afd/a/LTCat 。

## 许可及第三方组件

本项目保留原仓库的《PCL 分发有限许可》链接与《PCL 存储库合理使用指南》，见 [LICENCE](LICENCE)。更名、重新编译或添加本地修改不改变原作与第三方资产的版权归属，也不授予原许可未授予的权利。本项目修改部分的权利归相应贡献者所有，不主张对原作拥有版权。

经维护者确认，PCLLF 原创独立源码采用 [Mozilla Public License 2.0](LICENSE-MPL-2.0.txt)，以 [逐文件清单](licenses/MPL-COVERED-FILES.txt) 与文件头通知确定范围。该授权不覆盖继承的 PCL 代码、未分离的混合来源文件或第三方图形／字体／商标等资产。已有外源署名与适用许可继续保留；这不是将整个仓库统一改为 MPL。

完整组件索引和离线协议见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、[许可清单](licenses/manifest.json) 与 [许可审计](docs/licensing-audit.md)。其中未确认的授权、历史二进制对应源码和资产出处仍明确列为待核实项，不以复制通用协议替代相应证据。

工具箱可按用户操作从 PCL Symbio Edition 的官方 GitHub Release 下载独立程序；LF 安装包不内嵌该程序，也不以 LF 的 MPL 声明覆盖它的许可。

MeloongCore、第三方库、图标、字体、帮助文件及其他资源分别遵循其自身的许可与版权声明；保留关于页的原作者、协力开发者、资源贡献者和第三方许可列表。MeloongCore 的声明见 [MeloongCore/LICENSE.txt](MeloongCore/LICENSE.txt)。

Minecraft、Mojang 和 Microsoft 的名称与相关资产归相应权利人所有。本项目不是 Minecraft 官方产品，未经 Mojang 或 Microsoft 批准，也不与其关联。

## 本地修改范围

本分支使用 Minecraft 下载限制引入前的源码基线，禁用官方启动器更新，使用 PCL Living Fork 名称，并补充独立第三方身份、来源与版权说明。原仓库附带的旧版发行 ZIP 和历史更新补丁已从工作树移除，原始记录仍可通过 Git 历史追溯。
