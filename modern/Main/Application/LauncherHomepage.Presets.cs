#nullable enable
using MeloongCore;
namespace PCL;

// Exact LF preset texts, markup geometry, order and URLs; no CE live watcher/source replacement.
internal sealed partial class LauncherHomepage {
    private static (string? Content, string? Url) Preset(int kind) {
        switch (kind) {
            case 0: Logger.Info("主页预设：你知道吗"); return ("\n                            <local:MyCard Title=\"你知道吗？\" Margin=\"0,0,0,15\">\n                                <StackPanel Margin=\"25,38,23,15\">\n                                    <TextBlock FontSize=\"13.5\" IsHitTestVisible=\"False\" Text=\"{hint}\" TextWrapping=\"Wrap\" Foreground=\"{DynamicResource ColorBrush1}\" />\n                                    <WrapPanel Margin=\"0,14,0,0\">\n                                        <local:MyButton Text=\"获取官方 PCL\" Height=\"35\" Padding=\"16,0\" Margin=\"0,0,12,5\" EventType=\"打开网页\" EventData=\"https://meloong.com/PCL\" />\n                                        <local:MyButton Text=\"支持原作者\" Height=\"35\" Padding=\"16,0\" Margin=\"0,0,12,5\" EventType=\"打开网页\" EventData=\"https://afdian.com/a/LTCat\" />\n                                        <local:MyButton Text=\"购买正版 Minecraft\" Height=\"35\" Padding=\"16,0\" Margin=\"0,0,0,5\" EventType=\"打开网页\" EventData=\"https://www.minecraft.net/zh-hans/store/minecraft-java-bedrock-edition-pc\" />\n                                    </WrapPanel>\n                                </StackPanel>\n                                <local:MyIconButton Height=\"22\" Width=\"22\" Margin=\"9\" VerticalAlignment=\"Top\" HorizontalAlignment=\"Right\" \n                                    EventType=\"刷新主页\" EventData=\"/\"\n                                    Logo=\"M512.0 838.3c-80.2 0-153.4-29.3-210.2-77.4l75.5-75.5c11.5-11.5 25.8-22.0 25.8-37.0a27.2 27.2 0 0 0-27.1-27.1H104.0c-27.1 0-27.1 23.9-27.1 27.1v271.9a27.1 27.1 0 0 0 27.1 27.1c15.0 0 27.8-16.6 42.5-31.2l77.9-77.9c76.6 67.7 177.1 108.9 287.4 108.9 221.7 0 404.5-166.0 431.2-380.6h-109.8c-25.9 154.2-159.7 271.9-321.3 271.9zM919.9 76.6c-15.0 0-27.8 16.6-42.5 31.3L799.5 185.8c-76.5-67.7-177.1-108.9-287.4-108.9-221.8 0-404.5 166.1-431.3 380.6H190.6c25.9-154.2 159.7-271.9 321.4-271.9 80.2 0 153.4 29.3 210.1 77.4l-75.5 75.5c-11.6 11.5-25.8 22.0-25.8 37.1a27.2 27.2 0 0 0 27.1 27.1h271.9c27.1 0 27.1-23.9 27.1-27.1V103.8a27.1 27.1 0 0 0-27.1-27.1z\" />\n                            </local:MyCard>", null);
            case 1: Logger.Info("主页预设：回声洞"); return ("\n                            <local:MyCard Title=\"回声洞\" Margin=\"0,0,0,15\">\n                                <TextBlock Margin=\"25,38,23,15\" FontSize=\"13.5\" IsHitTestVisible=\"False\" Text=\"{cave}\" TextWrapping=\"Wrap\" Foreground=\"{DynamicResource ColorBrush1}\" />\n                                <local:MyIconButton Height=\"22\" Width=\"22\" Margin=\"9\" VerticalAlignment=\"Top\" HorizontalAlignment=\"Right\" \n                                    EventType=\"刷新主页\" EventData=\"/\"\n                                    Logo=\"M512.0 838.3c-80.2 0-153.4-29.3-210.2-77.4l75.5-75.5c11.5-11.5 25.8-22.0 25.8-37.0a27.2 27.2 0 0 0-27.1-27.1H104.0c-27.1 0-27.1 23.9-27.1 27.1v271.9a27.1 27.1 0 0 0 27.1 27.1c15.0 0 27.8-16.6 42.5-31.2l77.9-77.9c76.6 67.7 177.1 108.9 287.4 108.9 221.7 0 404.5-166.0 431.2-380.6h-109.8c-25.9 154.2-159.7 271.9-321.3 271.9zM919.9 76.6c-15.0 0-27.8 16.6-42.5 31.3L799.5 185.8c-76.5-67.7-177.1-108.9-287.4-108.9-221.8 0-404.5 166.1-431.3 380.6H190.6c25.9-154.2 159.7-271.9 321.4-271.9 80.2 0 153.4 29.3 210.1 77.4l-75.5 75.5c-11.6 11.5-25.8 22.0-25.8 37.1a27.2 27.2 0 0 0 27.1 27.1h271.9c27.1 0 27.1-23.9 27.1-27.1V103.8a27.1 27.1 0 0 0-27.1-27.1z\" />\n                            </local:MyCard>", null);
            case 2: Logger.Info("主页预设：Minecraft 新闻"); return (null, "https://mcnews.meloong.com");
            case 3: Logger.Info("主页预设：简单主页"); return (null, "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/MFn233/Custom.xaml");
            case 4: Logger.Info("主页预设：每日整合包推荐"); return (null, "https://pclsub.sodamc.com/");
            case 5: Logger.Info("主页预设：Minecraft 皮肤推荐"); return (null, "https://forgepixel.com/pcl_sub_file");
            case 6: Logger.Info("主页预设：OpenBMCLAPI 仪表盘 Lite"); return (null, "https://pcl-bmcl.milu.ink/");
            case 7: Logger.Info("主页预设：主页市场"); return (null, "https://pclhomeplazaoss.lingyunawa.top:26994/p/Homepages/Homepage.Market/Custom.xaml");
            case 8: Logger.Info("主页预设：更新日志"); return (null, "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/Joker2184/UpdateHomepage.xaml");
            case 9: Logger.Info("主页预设：PCL 新功能说明书"); return (null, "https://raw.gitcode.com/WForst-Breeze/WhatsNewPCL/raw/main/Custom.xaml");
            case 10: Logger.Info("主页预设：OpenMCIM Dashboard"); return (null, "https://files.mcimirror.top/PCL");
            case 11: Logger.Info("主页预设：杂志主页"); return (null, "https://gh-proxy.com/https://github.com/Neclyon/Magazine-Homepage-PCL/raw/main/output/Custom.xaml");
            case 12: Logger.Info("主页预设：PCL GitHub 仪表盘"); return (null, "https://ddf.pcl-community.top/Custom.xaml");
            case 13: Logger.Info("主页预设：PCL 更新摘要"); return (null, "https://raw.gitcode.com/ENC_Euphony/PCL-AI-Summary-HomePage/raw/master/Custom.xaml");
            case 14: Logger.Info("主页预设：今日新闻热点"); return (null, "https://pcl.wyc-w.top/index.xaml");
            case 15: Logger.Info("主页预设：Minecraft 芝士站"); return (null, "https://mkss-services.awaeric.xyz");
            case 16: Logger.Info("主页预设：整合包推荐引擎"); return (null, "https://qawsedrftgyhujiko.fun/pcl2/Custom.xaml");
            case 17: Logger.Info("主页预设：Bangumi 番剧主页"); return (null, "https://bangumi.p.kaphia.top");
            case 18: Logger.Info("主页预设：Bilibili 热门"); return (null, "https://bilibili.p.kaphia.top");
            case 19: Logger.Info("主页预设：Music 云音乐热门"); return (null, "https://cloudmusic.p.kaphia.top");
            case 20: Logger.Info("主页预设：模组推荐"); return (null, "https://g-fish.dpdns.org/download/ModPage.xaml");
            case 21: Logger.Info("主页预设：每日游戏推荐"); return (null, "https://pcl.xn--9iq209asnop94b.top/download/GamePage.xaml");
            case 22: Logger.Info("主页预设：Wallpaper 每日壁纸"); return (null, "https://wallpaper.p.kaphia.top");
            case 23: Logger.Info("主页预设：Weather 天气预报"); return (null, "https://weather-p.kaphia.top/{variable:PCLDailyWeatherPage:choose}.xaml");
            case 24: Logger.Info("主页预设：新一代狗屁王大杂烩主页"); return (null, "https://end.g-fish.dpdns.org/Custom.xaml");
            default: return (null, null);
        }
    }
}
