// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using PCLCS;
using SubPage = PCL.FormMain.PageSubType;

namespace PCL;

internal sealed record ResourceBrowserCategory(SubPage Route, ResourceTypes Type, string Title, string Icon);
internal sealed record ResourceBrowserTag(string Label, string Value, bool Heading = false, int Indent = 0);

internal static class ResourceBrowserCatalog
{
    internal const string InstallIcon = "m351.9,801.7c-8.6,0-17-3-23.7-8.4L18.9,614.7c-12.5-7.2-19.7-20.5-18.8-34.9V221c-.7-15,6.6-28.8,19.2-36L325.2,8.3c6.6-5.3,15.1-8.3,24-8.3s17.3,2.9,23.9,8.2l308.3,178c.2.1.5.3.8.5h0c12,6.7,19.4,19.8,19.4,34.1,0,13.9-7.1,26-18.7,32.5l-290.1,167.5v276l234-135.1v-168.9c0-20.8,17-37.8,37.8-37.8s37.8,17,37.8,37.8v191.7c0,16.4-10.9,31.2-26.5,36.1l-299.4,172.8c-6.7,5.4-15.1,8.3-23.7,8.3h-.9Zm-34.8-101.9v-276l-241.4-139.4v276l241.4,139.4Zm35.3-342.8l236.8-136.7-240-138.6L112.4,218.3l240,138.6Z";
    internal const string AnyVersion = "全部 (也可自行输入)";
    internal static readonly string[] GameVersions = [AnyVersion, "26.2", "26.1", "1.21.11", "1.21.8", "1.21.4", "1.21.1",
        "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2", "1.7.10"];
    internal static IReadOnlyList<ResourceBrowserCategory> Categories { get; } = new ResourceBrowserCategory[] {
        new(SubPage.DownloadMod, ResourceTypes.Mod, "Mod", "M789.504 912.896h-195.072a35.328 35.328 0 0 1-34.816-35.328v-77.312a39.936 39.936 0 0 0-40.448-39.936H481.28a40.448 40.448 0 0 0-40.448 39.936v77.312a34.816 34.816 0 0 1-34.816 35.328H204.8a41.984 41.984 0 0 1-40.448-42.496v-200.192a35.328 35.328 0 0 1 35.328-35.328h72.704a39.936 39.936 0 0 0 39.936-39.936v-37.888a39.936 39.936 0 0 0-39.936-40.448H199.68a35.328 35.328 0 0 1-35.328-35.328V287.744A41.984 41.984 0 0 1 204.8 245.76h176.64v-32.768a102.4 102.4 0 0 1 102.4-102.4h33.792a102.4 102.4 0 0 1 102.4 102.4v32.768h170.496a41.984 41.984 0 0 1 41.984 41.984V460.8h28.672a102.4 102.4 0 0 1 102.4 102.4v33.792a102.4 102.4 0 0 1-102.4 102.4h-28.672V870.4a41.984 41.984 0 0 1-43.008 42.496z m-159.744-70.144h131.584V665.6a34.304 34.304 0 0 1 34.816-34.816h64a31.744 31.744 0 0 0 31.744-31.744V563.2a31.744 31.744 0 0 0-31.744-31.744h-64a34.816 34.816 0 0 1-34.816-35.328V316.416h-177.152a35.328 35.328 0 0 1-35.328-35.328V212.992a31.744 31.744 0 0 0-31.744-31.744h-33.792a31.744 31.744 0 0 0-31.744 31.744v68.096a35.328 35.328 0 0 1-34.816 35.328H234.496v130.048h37.888a110.592 110.592 0 0 1 110.08 110.592v37.888a110.592 110.592 0 0 1-110.08 110.592h-37.888v137.216h136.192v-42.496a110.592 110.592 0 0 1 110.592-110.08h37.888a110.592 110.592 0 0 1 110.592 110.08z"),
        new(SubPage.DownloadPack, ResourceTypes.ModPack, "整合包", "M511 995a128 128 0 0 1-57-13L70 791a126 126 0 0 1-70-113V311a126 126 0 0 1 15-60V248c1-2 3-5 5-8a127 127 0 0 1 49-42L454 13a128 128 0 0 1 112 0l383 190a126 126 0 0 1 72 113v360a126 126 0 0 1-70 115L568 984c-17 7-37 11-57 11z m42-470v370l360-178c14-7 23-21 23-38v-335L554 524zM85 330v347a42 42 0 0 0 23 38l360 178V523L85 330zM135 260l375 189 137-65L286 188 135 260z m245-118l363 197 150-71-365-180a42 42 0 0 0-37 0l-111 53z"),
        new(SubPage.DownloadMap, ResourceTypes.Map, "地图", "F0 M12 0A12 12 0 1 0 12 24A12 12 0 1 0 12 0 M12 2A10 10 0 1 1 12 22A10 10 0 1 1 12 2 M0 11H24V13H0Z M11 0H13V24H11Z M4 5H20V7H4Z M4 17H20V19H4Z"),
        new(SubPage.DownloadDataPack, ResourceTypes.DataPack, "数据包", "M445 545c18 0 33 14 33 33v149a182 182 0 1 1-182-182z m282 0a182 182 0 1 1-182 182v-149c0-18 14-33 33-33zM412 611H296a116 116 0 1 0 116 116V611z m315 0H611v116a116 116 0 1 0 116-116zM296 113a182 182 0 0 1 182 182v149a33 33 0 0 1-33 33H296a182 182 0 0 1 0-365z m431 0a182 182 0 0 1 0 365h-149a33 33 0 0 1-33-33V296A182 182 0 0 1 727 113zM296 180a116 116 0 0 0 0 232H412V296c0-62-48-112-109-115z m431 0a116 116 0 0 0-116 116V412h116c62 0 112-48 115-109l0-6a116 116 0 0 0-116-116z"),
        new(SubPage.DownloadResourcePack, ResourceTypes.ResourcePack, "资源包", "M884.4 130.6H140.6c-41.9 0-76 34.1-76 76v613.3c0 41.9 34.1 76 76 76h743.8c41.9 0 76-34.1 76-76V206.6c0-41.9-34.1-76-76-76z m-743.8 72h743.8c2.2 0 4 1.8 4 4v371.3L796 461.7c-21.7-27.3-54.1-43.7-88.8-45-34.7-1.3-68.1 12.7-91.7 38.3L454 630c-2.8 3-7.2 3.5-10.5 1.3l-103-70.3c-31.5-21.5-72.8-22.5-105.3-2.7l-98.6 60.1V206.6c0-2.2 1.8-4 4-4z m743.8 621.3H140.6c-2.2 0-4-1.8-4-4V702.7l136.1-83c8.4-5.1 19.1-4.8 27.2 0.7l103 70.3c15.9 10.8 35.1 15.6 54.2 13.4 19-2.2 36.7-11.2 49.8-25.3l161.6-175c9.5-10.3 22.3-15.7 36.2-15.1 13.9 0.5 26.3 6.8 35.1 17.8l148.7 187v126.3c-0.1 2.3-1.9 4.1-4.1 4.1z M231.8 400.6a69.6 69.6 0 1 0 98.4-98.4 69.6 69.6 0 1 0-98.4 98.4Z"),
        new(SubPage.DownloadShader, ResourceTypes.Shader, "光影包", "M512 0c25 0 42 17 42 42v85c0 25-17 42-42 42s-42-17-42-42V42c0-25 17-42 42-42zM512 853c25 0 42 17 42 42v85c0 25-17 42-42 42s-42-17-42-42v-85c0-25 17-42 42-42zM209 149c-17-17-42-17-59 0-17 17-17 42 0 59l59 59c17 17 42 17 59 0 17-17 17-42 0-59L209 149zM755 755c17-17 42-17 59 0l59 59c17 17 17 42 0 59s-42 17-59 0l-59-59c-17-17-17-46 0-59zM42 469c-25 0-42 17-42 42s17 42 42 42h85c25 0 42-17 42-42s-17-42-42-42H42zM853 512c0-25 17-42 42-42h85c25 0 42 17 42 42s-17 42-42 42h-85c-25 0-42-17-42-42zM149 814c-17 17-17 42 0 59 17 17 42 17 59 0l59-59c17-17 17-42 0-59-17-17-42-17-59 0l-59 59zM755 268c-17-17-17-42 0-59l59-59c17-17 42-17 59 0s17 42 0 59l-59 59c-17 17-46 17-59 0z M512 213c-166 0-298 132-298 298s132 298 298 298 298-132 298-298-132-298-298-298z m0 512c-119 0-213-93-213-213s93-213 213-213 213 93 213 213-93 213-213 213z"),
    };

    internal static ResourceBrowserCategory? Find(SubPage route) => Categories.FirstOrDefault(category => category.Route == route);

    internal static IReadOnlyList<ResourceBrowserTag> Tags(ResourceTypes type) => type switch {
        ResourceTypes.Mod => [
            new("全部", ""),
            new("世界元素", "406/worldgen"),
            new("生物群系", "407/", Indent: 15),
            new("维度", "410/", Indent: 15),
            new("矿物与资源", "408/", Indent: 15),
            new("天然结构", "409/", Indent: 15),
            new("科技", "412/technology"),
            new("管道与物流", "415/", Indent: 15),
            new("自动化", "4843/", Indent: 15),
            new("能源", "417/", Indent: 15),
            new("红石", "4558/", Indent: 15),
            new("食物与烹饪", "436/food"),
            new("农业", "416/", Indent: 15),
            new("游戏机制", "/game-mechanics"),
            new("运输", "414/transportation"),
            new("仓储", "420/storage"),
            new("魔法", "419/magic"),
            new("冒险", "422/adventure"),
            new("装饰", "424/decoration"),
            new("生物", "411/mobs"),
            new("实用", "5191/utility"),
            new("装备与工具", "434/equipment"),
            new("创造模式", "9026/"),
            new("性能优化", "6814/optimization"),
            new("信息显示", "423/"),
            new("服务器", "435/social"),
            new("支持库", "421/library"),
        ],
        ResourceTypes.ModPack => [
            new("全部", ""),
            new("多人", "4484/"),
            new("性能优化", "/optimization"),
            new("硬核", "4479/challenging"),
            new("战斗", "4483/combat"),
            new("任务", "4478/quests"),
            new("科技", "4472/technology"),
            new("魔法", "4473/magic"),
            new("冒险", "4475/adventure"),
            new("水槽包", "/kitchen-sink"),
            new("探索", "4476/"),
            new("小游戏", "4477/"),
            new("科幻", "4474/"),
            new("空岛", "4736/"),
            new("原版改良", "5128/"),
            new("FTB", "4487/"),
            new("基于地图", "4480/"),
            new("轻量整合", "4481/lightweight"),
            new("大型整合", "4482/"),
        ],
        ResourceTypes.Map => [
            new("全部", ""),
        ],
        ResourceTypes.DataPack => [
            new("全部", ""),
            new("世界元素", "/worldgen"),
            new("科技", "6951/technology"),
            new("游戏机制", "/game-mechanics"),
            new("运输", "/transportation"),
            new("仓储", "/storage"),
            new("魔法", "6952/magic"),
            new("冒险", "6948/adventure"),
            new("幻想", "6949/"),
            new("装饰", "/decoration"),
            new("生物", "/mobs"),
            new("实用", "6953/utility"),
            new("装备与工具", "/equipment"),
            new("性能优化", "/optimization"),
            new("服务器", "/social"),
            new("支持库", "6950/library"),
            new("Mod 相关", "6946/"),
        ],
        ResourceTypes.ResourcePack => [
            new("全部", ""),
            new("风格", "", Heading: true),
            new("原版风", "403/vanilla-like", Indent: 15),
            new("写实风", "400/realistic", Indent: 15),
            new("现代风", "401/", Indent: 15),
            new("中世纪", "402/", Indent: 15),
            new("蒸汽朋克", "399/", Indent: 15),
            new("主题化", "/themed", Indent: 15),
            new("简洁", "/simplistic", Indent: 15),
            new("装饰", "/decoration", Indent: 15),
            new("战斗", "/combat", Indent: 15),
            new("实用", "/utility", Indent: 15),
            new("改良", "/tweaks", Indent: 15),
            new("鬼畜", "/cursed", Indent: 15),
            new("特性", "", Heading: true),
            new("含实体", "/entities", Indent: 15),
            new("含声音", "/audio", Indent: 15),
            new("含字体", "5244/fonts", Indent: 15),
            new("含模型", "/models", Indent: 15),
            new("含语言", "/locale", Indent: 15),
            new("含 UI", "/gui", Indent: 15),
            new("核心着色器", "/core-shaders", Indent: 15),
            new("动态效果", "404/", Indent: 15),
            new("兼容 Mod", "4465/modded", Indent: 15),
            new("分辨率", "", Heading: true),
            new("8x 或更低", "/8x-", Indent: 15),
            new("16x", "393/16x", Indent: 15),
            new("32x", "394/32x", Indent: 15),
            new("48x", "/48x", Indent: 15),
            new("64x", "395/64x", Indent: 15),
            new("128x", "396/128x", Indent: 15),
            new("256x", "397/256x", Indent: 15),
            new("512x 或更高", "398/512x+", Indent: 15),
        ],
        ResourceTypes.Shader => [
            new("全部", ""),
            new("风格", "", Heading: true),
            new("原版风", "6555/vanilla-like", Indent: 15),
            new("幻想风", "6554/fantasy", Indent: 15),
            new("写实风", "6553/realistic", Indent: 15),
            new("半写实风", "/semi-realistic", Indent: 15),
            new("卡通风", "/cartoon", Indent: 15),
            new("特性", "", Heading: true),
            new("彩色光照", "/colored-lighting", Indent: 15),
            new("路径追踪", "/path-tracing", Indent: 15),
            new("PBR", "/pbr", Indent: 15),
            new("反射", "/reflections", Indent: 15),
            new("性能负荷", "", Heading: true),
            new("极低", "/potato", Indent: 15),
            new("低", "/low", Indent: 15),
            new("中", "/medium", Indent: 15),
            new("高", "/high", Indent: 15),
            new("加载器", "", Heading: true),
            new("原版可用", "/vanilla", Indent: 15),
            new("Iris", "/iris", Indent: 15),
            new("OptiFine", "/optifine", Indent: 15),
        ],
        _ => [new("全部", "")]
    };
}
