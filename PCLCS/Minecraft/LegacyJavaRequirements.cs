using System;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed partial class LegacyJavaService<TInstance> where TInstance : class {
    /// <summary>LF's exact existing vanilla/loader/account Java constraints, including conflict replacement.</summary>
    public (ValueRange<Version> Range, string? RecommendedComponent) GetJavaRequirement(TInstance instance) =>
        GetJavaRequirement(instance, managed: false, retireMinecraft: false, retireForge: false);

    /// <summary>
    /// Managed launch retains all unretired constraints and uses a strict intersection. Only
    /// independently proven Minecraft/Forge launch replacements may retire their named inference.
    /// </summary>
    public (ValueRange<Version> Range, string? RecommendedComponent) GetManagedJavaRequirement(TInstance instance,
        bool retireMinecraft, bool retireForge, bool? nideLogin = null) => GetJavaRequirement(instance, managed: true, retireMinecraft, retireForge, nideLogin);

    private (ValueRange<Version> Range, string? RecommendedComponent) GetJavaRequirement(TInstance instance,
        bool managed, bool retireMinecraft, bool retireForge, bool? nideLogin = null) {
        ValueRange<Version>? userRange = null;
        if (host.ReadInteger("VersionArgumentJavaV2", instance) == 1) {
            try {
                ValueRange<Version> range = ValueRange<Version>.FromString(
                    host.ReadString("VersionArgumentJavaRange", instance), s => StringUtils.ParseVersionWithDefaults(s));
                if (!managed) return (range, null);
                userRange = range;
            } catch (Exception) {
                Logger.Warn($"版本设置中指定的 Java 版本范围有误（{host.ReadString("VersionArgumentJavaRange", instance)}），这可能是因为首次修改此设置，尚未初始化");
            }
        }

        ValueRange<Version> javaRange = ValueRange<Version>.All();
        void AddConstraint(ValueRange<Version> constraint) {
            javaRange = javaRange.Intersect(constraint);
            if (javaRange.IsEmpty()) {
                if (managed) Logger.Warn($"MMC Java 版本要求的严格交集为空：当前追加的要求为 {constraint}，游戏版本为 {host.VersionDisplayName(instance)}");
                else {
                    Logger.Warn($"Java 版本要求冲突，若启动失败请在版本设置手动指定 Java：当前追加的要求为 {constraint}，游戏版本为 {host.VersionDisplayName(instance)}", LogBehavior.Toast);
                    javaRange = constraint;
                }
            }
        }
        McVersion VersionData() => host.Version(instance);

        if (userRange is not null) AddConstraint(userRange);

        if (!retireMinecraft) {
            if ((!VersionData().Valid && host.ReleaseTime(instance) >= new DateTime(2024, 4, 2)) ||
                (VersionData().Valid && VersionData().Vanilla >= new Version(20, 0, 5))) {
                AddConstraint(ValueRange<Version>.AtLeast(new Version(21, 0)));
            } else if ((!VersionData().Valid && host.ReleaseTime(instance) >= new DateTime(2021, 11, 16)) ||
                       (VersionData().Valid && VersionData().Vanilla.Major >= 18)) {
                AddConstraint(ValueRange<Version>.AtLeast(new Version(17, 0)));
            } else if ((!VersionData().Valid && host.ReleaseTime(instance) >= new DateTime(2021, 5, 11)) ||
                       (VersionData().Valid && VersionData().Vanilla.Major >= 17)) {
                AddConstraint(ValueRange<Version>.AtLeast(new Version(16, 0)));
            } else if (host.ReleaseTime(instance).Year >= 2017) {
                AddConstraint(ValueRange<Version>.AtLeast(new Version(8, 0)));
            } else if (host.ReleaseTime(instance) <= new DateTime(2013, 5, 1) && host.ReleaseTime(instance).Year >= 2001) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(9, 0)));
            }
        }

        string? recommendedComponent = null;
        int recommendedCode = retireMinecraft ? 0 : host.JsonObject(instance)?["javaVersion"]?["majorVersion"]?.ToObject<int>()
            ?? host.JsonVersion(instance)?["java_version"]?.ToObject<int>() ?? 0;
        if (!retireMinecraft && recommendedCode >= 22) {
            Logger.Info("Mojang 要求至少使用 Java " + recommendedCode);
            AddConstraint(ValueRange<Version>.AtLeast(new Version(recommendedCode, 0)));
            recommendedComponent = host.JsonObject(instance)?["javaVersion"]?["component"]?.ToString()
                ?? host.JsonVersion(instance)?["java_component"]?.ToString();
            if (recommendedComponent == "") recommendedComponent = null;
        }

        if (VersionData().HasOptiFine && VersionData().Valid) {
            if (VersionData().Vanilla.Major < 7) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(9, 0)));
            } else if (VersionData().Vanilla.Major >= 8 && VersionData().Vanilla.Major < 12) {
                AddConstraint(ValueRange<Version>.ClosedOpen(new Version(8, 0), new Version(9, 0)));
            } else if (VersionData().Vanilla.Major == 12) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(9, 0)));
            }
        }

        if (!retireForge && VersionData().ModLoaders.HasFlagF(ModLoaders.Forge)) {
            if (VersionData().Vanilla >= new Version(6, 0, 1) && VersionData().Vanilla <= new Version(7, 0, 2)) {
                AddConstraint(ValueRange<Version>.ClosedOpen(new Version(7, 0), new Version(8, 0)));
            } else if (VersionData().Vanilla.Major <= 12 || !VersionData().Valid) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(9, 0)));
            } else if (VersionData().Vanilla.Major <= 14) {
                AddConstraint(ValueRange<Version>.ClosedOpen(new Version(8, 0), new Version(11, 0)));
            } else if (VersionData().Vanilla.Major == 15) {
                AddConstraint(ValueRange<Version>.ClosedOpen(new Version(8, 0), new Version(16, 0)));
            } else if (McUtils.CompareVersionGE(VersionData().ModLoaderVersions[ModLoaders.Forge], "34.0.0") &&
                       McUtils.CompareVersionGE("36.2.25", VersionData().ModLoaderVersions[ModLoaders.Forge])) {
                AddConstraint(ValueRange<Version>.AtMost(new Version(8, 0, 320)));
            } else if (McUtils.CompareVersionGE(VersionData().ModLoaderVersions[ModLoaders.Forge], "36.2.26") &&
                       McUtils.CompareVersionGE("36.999999.999999", VersionData().ModLoaderVersions[ModLoaders.Forge])) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(24, 0)));
            } else if (McUtils.CompareVersionGE(VersionData().ModLoaderVersions[ModLoaders.Forge], "37.0.0") &&
                       McUtils.CompareVersionGE("37.0.79", VersionData().ModLoaderVersions[ModLoaders.Forge])) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(17, 0)));
            } else if (VersionData().Vanilla.Major == 18 && VersionData().HasOptiFine) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(19, 0)));
            } else if (McUtils.CompareVersionGE(VersionData().ModLoaderVersions[ModLoaders.Forge], "45.0.21") &&
                       McUtils.CompareVersionGE("45.0.65", VersionData().ModLoaderVersions[ModLoaders.Forge])) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(20, 0)));
            } else if (McUtils.CompareVersionGE(VersionData().ModLoaderVersions[ModLoaders.Forge], "45.0.66") &&
                       McUtils.CompareVersionGE("47.4.8", VersionData().ModLoaderVersions[ModLoaders.Forge])) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(22, 0)));
            }
        }

        if (VersionData().ModLoaders.HasFlagF(ModLoaders.NeoForge)) {
            if (VersionData().Vanilla == new Version(20, 0, 1) ||
                (McUtils.CompareVersionGE("20.2.62-beta", VersionData().ModLoaderVersions[ModLoaders.NeoForge]) &&
                 !VersionData().ModLoaderVersions[ModLoaders.NeoForge].Contains("25w14craftmine"))) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(22, 0)));
            }
        }

        if (VersionData().ModLoaders.HasFlagF(ModLoaders.Fabric) && VersionData().Valid) {
            if (VersionData().Vanilla.Major >= 15 && VersionData().Vanilla.Major <= 16) {
                AddConstraint(ValueRange<Version>.AtLeast(new Version(8, 0)));
            } else if (VersionData().Vanilla.Major >= 18) {
                AddConstraint(ValueRange<Version>.AtLeast(new Version(17, 0)));
            }
            if (VersionData().ModLoaderVersions[ModLoaders.Fabric] != "未知版本" &&
                McUtils.CompareVersion(VersionData().ModLoaderVersions[ModLoaders.Fabric], "0.17.0") < 0) {
                AddConstraint(ValueRange<Version>.LessThan(new Version(25, 0)));
            }
        }

        if (VersionData().ModLoaders.HasFlagF(ModLoaders.LiteLoader) && VersionData().Valid) {
            AddConstraint(ValueRange<Version>.LessThan(new Version(9, 0)));
        }
        if (managed && nideLogin.HasValue ? nideLogin.Value : host.IsNideLogin()) {
            // Preserve LF's actual 8u141 value (the old comment said 8u101).
            AddConstraint(ValueRange<Version>.AtLeast(new Version(8, 0, 141)));
        }
        return (javaRange, recommendedComponent);
    }
}
