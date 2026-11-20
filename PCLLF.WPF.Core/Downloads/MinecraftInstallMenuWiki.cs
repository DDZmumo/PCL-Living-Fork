using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class MinecraftInstallMenuController<TView, TVirtual, TClick, TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource> {
    public void McUpdateLogShow(JToken json) {
        string id = json["id"]!.ToString().Lower(), wiki;
        if (id == "3d shareware v1.34") wiki = "3D_Shareware_v1.34";
        else if (id == "2.0") wiki = "Java版2.0";
        else if (id == "1.rv-pre1") wiki = "Java版1.RV-Pre1";
        else if (id == "combat test 1" || id.Contains("combat-1") || id.Contains("combat-212796")) wiki = "Java版1.14.3_-_Combat_Test";
        else if (id == "combat test 2" || id.Contains("combat-2") || id.Contains("combat-0")) wiki = "Java版Combat_Test_2";
        else if (id == "combat test 3" || id == "1.14_combat-3") wiki = "Java版Combat_Test_3";
        else if (id == "combat test 4" || id == "1.15_combat-1") wiki = "Java版Combat_Test_4";
        else if (id == "combat test 5" || id == "1.15_combat-6") wiki = "Java版Combat_Test_5";
        else if (id == "combat test 6" || id == "1.16_combat-0") wiki = "Java版Combat_Test_6";
        else if (id == "combat test 7c" || id == "1.16_combat-3") wiki = "Java版Combat_Test_7c";
        else if (id == "combat test 8b" || id == "1.16_combat-5") wiki = "Java版Combat_Test_8b";
        else if (id == "combat test 8c" || id == "1.16_combat-6") wiki = "Java版Combat_Test_8c";
        else if (id == "1.0.0-rc2-2") wiki = "Java版RC2";
        else if (id.StartsWithF("1.19_deep_dark_experimental_snapshot-") || id.StartsWithF("1_19_deep_dark_experimental_snapshot-")) wiki = id.Replace("1_19", "1.19").Replace("1.19_deep_dark_experimental_snapshot-", "Java版Deep_Dark_Experimental_Snapshot_");
        else if (id == "b1.9-pre6") wiki = "Java版Beta_1.9_Prerelease_6";
        else if (id.Contains("b1.9")) wiki = "Java版Beta_1.9_Prerelease";
        else if ((string?)json["type"] == "release" || (string?)json["type"] == "snapshot" || (string?)json["type"] == "special") wiki = (id.Contains("w") ? "" : "Java版") + id.Replace(" Pre-Release ", "-pre");
        else if (id.StartsWithF("b")) wiki = "Java版" + id.TrimEnd('a', 'b', 'c', 'd', 'e').Replace("b", "Beta_");
        else if (id.StartsWithF("a")) wiki = "Java版" + id.TrimEnd('a', 'b', 'c', 'd', 'e').Replace("a", "Alpha_v");
        else if (id == "inf-20100618") wiki = "Java版Infdev_20100618";
        else if (id == "c0.30_01c" || id == "c0.30_survival" || id.Contains("生存测试")) wiki = "Java版Classic_0.30（生存模式）";
        else if (id.StartsWithF("c0.31")) wiki = "Java版Indev_0.31_20100130";
        else if (id.StartsWithF("c")) wiki = "Java版" + id.Replace("c", "Classic_");
        else if (id.StartsWithF("rd-")) wiki = "Java版Pre-classic_" + id;
        else { Logger.Error("未知的版本格式：" + id + "。"); return; }
        host.OpenWebsite("https://zh.minecraft.wiki/w/" + wiki.Replace("_experimental-snapshot-", "-exp"));
    }
}
