// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.VisualBasic.CompilerServices;
using static PCLCS.ModDependencyVersions;
namespace PCLCS;
// Full LF declarations at 50b028da5fcad2226d74d901a41d8c43a249bfb2 (MPL-2.0 for PCLLF-original code).
// PCL/CE fixed-source provenance remains in Contracts; no CE policy imported.
#nullable enable
/// <summary>Only the original live instance reads cross this boundary; local declarations stay in the inspector.</summary>
public sealed class ModDependencyInstanceHost<TInstance> where TInstance : class {
    public Func<TInstance, bool> Reliable = null!, HasOptiFine = null!, OptiFineAsMod = null!;
    public Func<TInstance, string?> VanillaName = null!, OptiFine = null!;
    public Func<TInstance, IEnumerable<KeyValuePair<string, string?>>> ModLoaderVersions = null!;
    public Func<TInstance, JObject?> JsonObject = null!;
}

public sealed partial class ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile> {
    public static TContext ContextForInstance<TInstance>(TInstance instance, ModDependencyInstanceHost<TInstance> host, string? javaVersion = null) where TInstance : class {
        if (instance is null) throw new ArgumentNullException("Instance");
        TContext result = new() { MinecraftVersion = host.Reliable(instance) ? host.VanillaName(instance) : null, JavaVersion = javaVersion, LoaderKnown = true };
        foreach (KeyValuePair<string, string?> pair in host.ModLoaderVersions(instance)) result.LoaderVersions[CanonicalLoaderId(pair.Key)] = pair.Value;
        JArray? libraries = host.JsonObject(instance)?["libraries"] as JArray;
        if (libraries is not null) {
            foreach (JObject library in libraries.OfType<JObject>()) {
                string coordinate = Text(library["name"]);
                if (coordinate.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal)) {
                    result.LoaderVersions.Remove("fabricloader");
                    result.LoaderVersions["quilt_loader"] = coordinate.Substring("org.quiltmc:quilt-loader:".Length).Split(':')[0];
                    result.LoaderVersions["fabricloader"] = null;
                } else if (result.LoaderVersions.ContainsKey("neoforge") && coordinate.StartsWith("net.neoforged.fancymodloader:loader:", StringComparison.Ordinal)) {
                    result.AdditionalBuiltins["javafml"] = coordinate.Split(':')[2];
                }
            }
        }
        if (result.LoaderVersions.TryGetValue("forge", out _) && Regex.IsMatch(result.MinecraftVersion ?? "", @"^1\.(?:[0-9]|1[0-2])(?:\.|$)")) result.LegacyForgeVersionFolder = result.MinecraftVersion;
        if (result.LoaderVersions.ContainsKey("neoforge") && result.MinecraftVersion == "1.20.1") result.LoaderVersions["forge"] = result.LoaderVersions["neoforge"];
        if (host.HasOptiFine(instance) && !host.OptiFineAsMod(instance)) result.AdditionalBuiltins["optifine"] = host.OptiFine(instance);
        return result;
    }

        // 仅统一比较键；实际读取仍使用原始路径，保留 Windows 长路径支持。
        #nullable disable
public static global::System.String FilePathKey(global::System.String fullPath)
        {
            global::System.String Value = global::System.IO.Path.GetFullPath(fullPath.Replace(global::System.IO.Path.AltDirectorySeparatorChar, global::System.IO.Path.DirectorySeparatorChar));
            if (Value.StartsWith(@"\\?\UNC\", global::System.StringComparison.OrdinalIgnoreCase))
            {
                Value = (@"\\" + Value.Substring(8));
            }
            else if ((((((Value.StartsWith(@"\\?\", global::System.StringComparison.OrdinalIgnoreCase)) && ((Value.Length) >= (7))) && (global::System.Char.IsLetter(Value[4]))) && ((Value[5]) == (':'))) && ((Value[6]) == ('\\'))))
            {
                Value = Value.Substring(4);
            }
            return Value;
        }

        public static global::System.Boolean SameFilePath(global::System.String first, global::System.String second)
        {
            return global::System.String.Equals(FilePathKey(first), FilePathKey(second), global::System.StringComparison.OrdinalIgnoreCase);
        }

        private static global::System.String CanonicalId(global::System.String Id)
        {
            // Mod IDs are exact identifiers. In particular, "fabric" is the old Fabric API ID, not Fabric Loader.
            return (Id) ?? ("");
        }
        private static global::System.String CanonicalLoaderId(global::System.String Id)
        {
            switch ((((Id) ?? ("")).ToLowerInvariant() ?? ""))
            {
                case "fabric":
                case "fabric-loader":
                case "fabric_loader":
                case "fabricloader":
                    {
                        return "fabricloader";
                    }
                case "quilt":
                case "quilt-loader":
                case "quiltloader":
                case "quilt_loader":
                    {
                        return "quilt_loader";
                    }
                case "liteloader":
                    {
                        return "liteloader";
                    }
                case "neoforge":
                case "forge":
                case "minecraft":
                case "java":
                    {
                        return Id.ToLowerInvariant();
                    }

                default:
                    {
                        return (Id) ?? ("");
                    }
            }
        }
        private static global::System.Collections.Generic.List<TMod> CreateBuiltins(TContext Context)
        {
            var Values = new global::System.Collections.Generic.Dictionary<global::System.String, global::System.String>(global::System.StringComparer.Ordinal) { { "minecraft", Context.MinecraftVersion }, { "java", NormalizeJava(Context.JavaVersion) } };
            if (((!(Context.LoaderKnown)) && ((Context.LoaderVersions.Count) == (0))))
            {
                foreach (var Id in new[] { "forge", "neoforge", "fabricloader", "quilt_loader", "liteloader" })
                    Values[Id] = (global::System.String)null;
            }
            foreach (var Pair in Context.LoaderVersions)
                Values[CanonicalLoaderId(Pair.Key)] = Pair.Value;
            if (((Values.ContainsKey("forge")) || (Values.ContainsKey("neoforge"))))
            {
                Values["fml"] = (global::System.String)null;
                Values["javafml"] = (global::System.String)null;
                Values["lowcodefml"] = (global::System.String)null;
                global::System.String Forge = (global::System.String)null;
                if (((Values.TryGetValue("forge", out Forge)) && (global::System.Text.RegularExpressions.Regex.IsMatch((Forge) ?? (""), @"^\d+(?:\.\d+)*$"))))
                {
                    Values["javafml"] = Forge.Split('.')[0];
                    Values["lowcodefml"] = Forge.Split('.')[0];
                }
            }
            if (IsLegacyForge(Context))
                Values["FML"] = (global::System.String)null;
            foreach (var Pair in Context.AdditionalBuiltins)
                Values[CanonicalId(Pair.Key)] = Pair.Value;
            return Values.Select(Pair => new TMod() { Id = Pair.Key, Name = Pair.Key, Version = Pair.Value, IsBuiltin = true, Enabled = true }).ToList();
        }
        private static global::System.String NormalizeJava(global::System.String Value)
        {
            if (global::System.String.IsNullOrEmpty(Value))
                return Value;
            if (Value.StartsWith("1.", global::System.StringComparison.Ordinal))
                Value = Value.Substring(2);
            // Java 8 update syntax is numeric; preserve unknown suffixes instead of guessing EA/release ordering.
            return global::System.Text.RegularExpressions.Regex.Replace(Value, @"^(\d+(?:\.\d+)*)_(\d+)$", "$1.$2");
        }
        public static global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> Identities(TMod item)
        {
            var Result = new global::System.Collections.Generic.Dictionary<global::System.String, global::System.String>(item.Provides, global::System.StringComparer.Ordinal);
            if (((item.IsMetadataOnly) || (item.IncompatibleLoader)))
                return Result;
            Result[item.Id] = item.Version;
            if (!(global::System.String.IsNullOrEmpty(item.Group)))
                Result[(item.Group + ":") + item.Id] = item.Version;
            return Result;
        }
        private static global::System.Collections.Generic.List<TMod> ActiveMods(TReport Report)
        {
            return Report.Mods.Where(Item => ((((Item.Enabled) && (!(Item.IsMetadataOnly))) && (!(Item.IncompatibleLoader))) && (SideApplies(Item.Side, Report.Context)))).ToList();
        }
        // Bridge platform identity to a loader ID only through an exactly matched local file.
        // The solved loader edges then decide whether a selected nested/standalone provider satisfies it.
        public static global::System.Collections.Generic.List<TMod> RuntimeProvidersForPlatformDependency(TReport Report, global::System.String OwnerPath, TOnlineDependency Requirement)
        {
            var Paths = new global::System.Collections.Generic.HashSet<global::System.String>(Requirement.EnabledPaths.Concat(Requirement.MatchedPaths).Concat(Requirement.ProjectPaths).Select(FilePathKey), global::System.StringComparer.OrdinalIgnoreCase);
            var Ids = new global::System.Collections.Generic.HashSet<global::System.String>(Report.Mods.Where(Item => ((!(Item.IsMetadataOnly)) && (Paths.Contains(FilePathKey(Item.RootPath))))).SelectMany(Item => Identities(Item).Keys), global::System.StringComparer.Ordinal);
            Ids.UnionWith(Requirement.VerifiedLoaderIds);
            if (((Ids.Count) == (0)))
                return new global::System.Collections.Generic.List<TMod>();
            return Report.Edges.Where(Edge => (((((((((Edge.Owner.Enabled) && (SameFilePath(Edge.Owner.RootPath, OwnerPath))) && (Edge.Evaluated)) && (!(Edge.ConditionBypassed))) && (Edge.StateValue == ModDependencyMatchState.Satisfied)) && ((Edge.Requirement.Children.Count) == (0))) && (Edge.Requirement.KindValue != ModDependencyKind.Conflict)) && (Edge.Requirement.KindValue != ModDependencyKind.Incompatible)) && (Ids.Contains(Edge.Requirement.Id)))).SelectMany(Edge => Edge.Providers).Where(Item => (((((Item.Enabled) && (!(Item.IsBuiltin))) && (!(Item.IsMetadataOnly))) && (!(Item.IsCandidate))) && (!(Item.IncompatibleLoader)))).Distinct().ToList();
        }

        public static global::System.Boolean FileSatisfiesDependency(TReport report, global::System.String fullPath, TDependency requirement)
        {
            var Modules = report.Mods.Where(Item => ((SameFilePath(Item.RootPath, fullPath)) && (SideApplies(Item.Side, report.Context))));
            return (Resolve(requirement, ProviderMap(Modules), report.Context).State == ModDependencyMatchState.Satisfied);
        }

        public static TReport WithEnabledFiles(TReport report, global::System.Collections.Generic.IEnumerable<global::System.String> paths, global::System.Threading.CancellationToken token)
        {
            var Enabled = new global::System.Collections.Generic.HashSet<global::System.String>(paths.Select(FilePathKey), global::System.StringComparer.OrdinalIgnoreCase);
            var Result = new TReport() { Context = report.Context, Builtins = report.Builtins, MetadataIncomplete = report.MetadataIncomplete };
            Result.Notices.AddRange(report.Notices);
            Result.Mods.AddRange(report.Mods.Select(Item => Item.CopyWithEnabled((Item.Enabled) || (Enabled.Contains(FilePathKey(Item.RootPath))))));
            BuildReport(Result, token);
            return Result;
        }

        public static global::System.Boolean IsRequiredFileProvider(TReport report, TEdge edge, global::System.String fullPath)
        {
            return (FileProviderRelation(report, edge, fullPath) == ModDependencyFileProviderKind.Required);
        }

        public static ModDependencyFileProviderKind FileProviderRelation(TReport report, TEdge edge, global::System.String fullPath)
        {
            if ((((!(edge.Evaluated)) || (edge.ConditionBypassed)) || (SameFilePath(edge.Owner.RootPath, fullPath))))
                return ModDependencyFileProviderKind.None;
            var Providers = edge.Providers.Where(Item => !(Item.IsBuiltin)).ToList();
            if (((!(Providers.Any(Item => SameFilePath(Item.RootPath, fullPath)))) || (FileSatisfiesDependency(report, edge.Owner.RootPath, edge.Requirement))))
                return ModDependencyFileProviderKind.None;
            if (Providers.All(Item => SameFilePath(Item.RootPath, fullPath)))
                return ModDependencyFileProviderKind.Required;
            var WithoutFile = ActiveMods(report).Where(Item => !(SameFilePath(Item.RootPath, fullPath))).Concat((global::System.Collections.Generic.IEnumerable<TMod>)report.Builtins);
            if (((edge.StateValue == ModDependencyMatchState.Satisfied) && (Resolve(edge.Requirement, ProviderMap(WithoutFile), report.Context).State == ModDependencyMatchState.Unsatisfied)))
                return ModDependencyFileProviderKind.Required;
            if (((((edge.Requirement.Children.Count) == (0)) && (edge.StateValue == ModDependencyMatchState.Satisfied)) && (Resolve(edge.Requirement, ProviderMap(Providers.Where(Item => SameFilePath(Item.RootPath, fullPath))), report.Context).State == ModDependencyMatchState.Unsatisfied)))
                return ModDependencyFileProviderKind.None;
            return ModDependencyFileProviderKind.Possible;
        }

        private static global::System.Collections.Generic.Dictionary<global::System.String, global::System.Collections.Generic.List<TMod>> ProviderMap(global::System.Collections.Generic.IEnumerable<TMod> Mods)
        {
            var Result = new global::System.Collections.Generic.Dictionary<global::System.String, global::System.Collections.Generic.List<TMod>>(global::System.StringComparer.Ordinal);
            foreach (var Item in Mods)
            {
                foreach (var Id in Identities(Item).Keys)
                {
                    if (!(Result.ContainsKey(Id)))
                        Result[Id] = new global::System.Collections.Generic.List<TMod>();
                    Result[Id].Add(Item);
                }
            }
            // A top-level Fabric/Quilt mod fixes that ID's version; an embedded copy is only a selectable fallback.
            foreach (var Id in Result.Keys.ToArray())
            {
                var Direct = Result[Id].Where(Item => !(Item.IsNested)).ToList();
                if (((Direct.Count) > (0)))
                    Result[Id] = Direct;
            }
            return Result;
        }

        private class Resolution
        {
            public ModDependencyMatchState State;
            public global::System.Boolean Missing;
            public global::System.Collections.Generic.List<TMod> Providers = new global::System.Collections.Generic.List<TMod>();
        }
        private static Resolution Resolve(TDependency Requirement, global::System.Collections.Generic.Dictionary<global::System.String, global::System.Collections.Generic.List<TMod>> Providers, TContext Context, global::System.Int32 Depth = 0)
        {
            if ((((Depth) > (32)) || (Requirement.IsUnknown)))
                return new Resolution() { State = ModDependencyMatchState.Unknown };
            if (!(SideApplies(Requirement.Side, Context)))
                return new Resolution() { State = ModDependencyMatchState.Satisfied };
            var Result = new Resolution();
            if (((Requirement.Children.Count) > (0)))
            {
                var Children = Requirement.Children.Select(Child => Resolve(Child, Providers, Context, (Depth) + (1))).ToList();
                Result.State = Combine(Children.Select(Child => Child.State), Requirement.Join == "all");
                Result.Missing = Children.All(Child => Child.Missing);
                Result.Providers = Children.SelectMany(Child => Child.Providers).Distinct().ToList();
            }
            else
            {
                global::System.Collections.Generic.List<TMod> Candidates = (global::System.Collections.Generic.List<TMod>)null;
                if (((!(Providers.TryGetValue((Requirement.Id) ?? (""), out Candidates))) || ((Candidates.Count) == (0))))
                {
                    Result.State = ModDependencyMatchState.Unsatisfied;
                    Result.Missing = true;
                }
                else
                {
                    Result.Providers = Candidates.ToList();
                    Result.State = Combine(Candidates.Select(Item => (Item.IsRuntimeCandidate) ? (ModDependencyMatchState.Unknown) : (MatchVersions(Identities(Item)[Requirement.Id], Requirement.Versions, Requirement.Syntax))), false);
                }
            }
            if (Requirement.Unless is not null)
            {
                var UnlessResult = Resolve(Requirement.Unless, Providers, Context, (Depth) + (1));
                global::System.Boolean Negative = ((Requirement.KindValue == ModDependencyKind.Incompatible) || (Requirement.KindValue == ModDependencyKind.Conflict));
                if ((UnlessResult.State == ModDependencyMatchState.Satisfied))
                {
                    Result.State = (Negative) ? (ModDependencyMatchState.Unsatisfied) : (ModDependencyMatchState.Satisfied);
                    Result.Missing = false;
                    Result.Providers = Result.Providers.Concat((global::System.Collections.Generic.IEnumerable<TMod>)UnlessResult.Providers).Distinct().ToList();
                }
                else if (((UnlessResult.State == ModDependencyMatchState.Unknown) && (((((((Negative) && (Result.State == ModDependencyMatchState.Satisfied)))) || ((((!(Negative)) && (Result.State == ModDependencyMatchState.Unsatisfied)))))))))
                {
                    Result.State = ModDependencyMatchState.Unknown;
                }
            }
            if (((Requirement.KindValue == ModDependencyKind.Optional) && (Result.Missing)))
                Result.State = ModDependencyMatchState.Satisfied;
            return Result;
        }
        public static void BuildReport(TReport report, global::System.Threading.CancellationToken token)
        {
            foreach (var Group in report.Mods.Where(Item => ((((Item.Enabled) && (!(Item.IsNested))) && (!(Item.IsMetadataOnly))) && (Item.IncompatibleLoader))).GroupBy(Item => FilePathKey(Item.RootPath), global::System.StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var Item = Group.First();
                global::System.String Expected = LoaderDisplay(report.Context);
                report.Issues.Add(new TIssue()
                {
                    KindValue = ModDependencyIssueKind.LoaderMismatch,
                    Owner = Item,
                    RelatedMods = Group.ToList(),
                    IsError = true,
                    Message = ((((((((((((("文件：" + global::System.IO.Path.GetFileName(Item.RootPath)) + global::Microsoft.VisualBasic.Constants.vbCrLf) + "适用：") + Item.Format) + "；当前：") + Expected) + global::Microsoft.VisualBasic.Constants.vbCrLf) + ((global::System.String.IsNullOrEmpty(Item.LoaderMismatchDetail)) ? ("") : (Item.LoaderMismatchDetail + global::Microsoft.VisualBasic.Constants.vbCrLf))) + "处理：在 Mod 管理中禁用或移出此文件，换用支持 Minecraft ") + report.Context.MinecraftVersion) + " 与 ") + Expected) + " 的版本；若要使用此模组，请另选匹配加载器的游戏实例。")
                });
            }
            var Active = ActiveMods(report);
            var Map = ProviderMap(Active.Concat((global::System.Collections.Generic.IEnumerable<TMod>)report.Builtins));
            foreach (var Group in Active.Where(Item => ((!(Item.IsNested)) && (!(Item.IsRuntimeCandidate)))).GroupBy(Item => Item.Id, global::System.StringComparer.Ordinal).Where(Items => ((Items.Count()) > (1))))
                report.Issues.Add(new TIssue()
                {
                    KindValue = ModDependencyIssueKind.DuplicateId,
                    Owner = Group.First(),
                    RelatedMods = Group.ToList(),
                    IsError = true,
                    Message = ((("重复模组 ID " + Group.Key) + "：") + global::System.String.Join("、", Group.Select(Item => global::System.IO.Path.GetFileName(Item.RootPath))))
                });
            var RequiredKeys = new global::System.Collections.Generic.HashSet<global::System.String>(Active.Where(Item => ((!(Item.IsNested)) && (!(Item.IsRuntimeCandidate)))).Select(Item => Item.Key), global::System.StringComparer.Ordinal);
            // An embedded candidate is checked as required only when a required chain uniquely selects it.
            global::System.Boolean Changed = true;
            while (Changed)
            {
                Changed = false;
                foreach (var Item in Active.Where(Entry => RequiredKeys.Contains(Entry.Key)))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var Requirement in Item.Dependencies.Where(Entry => ((Entry.KindValue == ModDependencyKind.Required) && (SideApplies(Entry.Side, report.Context)))))
                    {
                        var Value = Resolve(Requirement, Map, report.Context);
                        var Candidates = Value.Providers.Where(Entry => Entry.IsNested).ToList();
                        if (((((Value.Providers.Count) == (1)) && ((Candidates.Count) == (1))) && (RequiredKeys.Add(Candidates[0].Key))))
                            Changed = true;
                    }
                }
            }
            foreach (var Item in Active)
                Item.IsCandidate = ((Item.IsRuntimeCandidate) || ((((Item.IsNested) && (!(RequiredKeys.Contains(Item.Key)))))));
            foreach (var Item in Active)
            {
                token.ThrowIfCancellationRequested();
                foreach (var Requirement in Item.Dependencies)
                {
                    var Value = Resolve(Requirement, Map, report.Context);
                    var Edge = new TEdge()
                    {
                        Owner = Item,
                        Requirement = Requirement,
                        StateValue = Value.State,
                        Providers = Value.Providers,
                        Evaluated = ((RequiredKeys.Contains(Item.Key)) && (SideApplies(Requirement.Side, report.Context))) && !(report.Context.IgnoreJavaRequirements && Requirement.Id == "java"),
                        ConditionBypassed = (Requirement.Unless is not null && (Resolve(Requirement.Unless, Map, report.Context).State == ModDependencyMatchState.Satisfied))
                    };
                    report.Edges.Add(Edge);
                    foreach (var Provider in Value.Providers)
                    {
                        if (!(report.ReverseDependencies.ContainsKey(Provider.Key)))
                            report.ReverseDependencies[Provider.Key] = new global::System.Collections.Generic.List<TEdge>();
                        report.ReverseDependencies[Provider.Key].Add(Edge);
                    }
                    if (Edge.Evaluated)
                        AddIssue(report, Edge, Value);
                }
            }
            if (Active.Any(Item => ((Item.IsCandidate) && (Item.IsNested))))
                report.Notices.Add("内嵌模组的候选版本由加载器选择；未把未选中的候选自身依赖判为启动错误。");
        }
        private static void AddIssue(TReport Report, TEdge Edge, Resolution Value)
        {
            var Requirement = Edge.Requirement;
            ModDependencyIssueKind Kind;
            global::System.Boolean IsError = false;
            global::System.String Detail = (global::System.String)null;
            switch (Requirement.KindValue)
            {
                case ModDependencyKind.Required:
                case ModDependencyKind.Optional:
                    {
                        if ((Value.State != ModDependencyMatchState.Unsatisfied))
                            return;
                        if (((Requirement.KindValue == ModDependencyKind.Optional) && (!(Requirement.EnforceWhenPresent))))
                            return;
                        Kind = (Value.Missing) ? (ModDependencyIssueKind.MissingRequired) : (ModDependencyIssueKind.VersionMismatch);
                        IsError = ((!(Report.MetadataIncomplete)) || ((((!(Value.Missing)) && (Value.Providers.All(Item => !(Item.IsNested)))))));
                        Detail = (((((Value.Missing) ? ("缺少必需依赖：") : ("版本不匹配：")) + Edge.Owner.Display) + " 需要 ") + RequirementDisplay(Requirement, Report.Context));
                        break;
                    }
                case ModDependencyKind.Incompatible:
                case ModDependencyKind.Conflict:
                    {
                        if ((((Value.State != ModDependencyMatchState.Satisfied) || (Value.Missing)) || (!(SideApplies(Requirement.Side, Report.Context)))))
                            return;
                        Kind = ModDependencyIssueKind.Incompatible;
                        IsError = ((Requirement.KindValue == ModDependencyKind.Incompatible) && (Value.Providers.All(Item => !(Item.IsCandidate))));
                        Detail = (((((Value.Providers.Any(Item => Item.IsCandidate)) ? ("内嵌候选可能不兼容：") : ("已声明不兼容：")) + Edge.Owner.Display) + " 与 ") + Requirement.Display);
                        break;
                    }
                case ModDependencyKind.Recommended:
                    {
                        if ((Value.State != ModDependencyMatchState.Unsatisfied))
                            return;
                        Kind = ModDependencyIssueKind.Recommendation;
                        Detail = ((("建议依赖未满足：" + Edge.Owner.Display) + " 建议安装 ") + Requirement.Display);
                        break;
                    }

                default:
                    {
                        return;
                    }
            }
            if ((((Value.Providers.Count) > (0)) && (!(IsInternalLoaderId(Requirement.Id)))))
                Detail += ("；本地：" + global::System.String.Join("、", Value.Providers.Select(Item => ((Item.Display + " ") + ((global::System.String.IsNullOrEmpty(Item.Version)) ? ("未知版本") : (Item.Version))))));
            var Disabled = Report.Mods.Where(Item => ((!(Item.Enabled)) && (DependencyMentions(Requirement, Identities(Item).Keys)))).ToList();
            if (((Value.Missing) && ((Disabled.Count) > (0))))
                Detail += ("；已禁用：" + global::System.String.Join("、", Disabled.Select(Item => global::System.IO.Path.GetFileName(Item.RootPath)).Distinct()));
            if (!(global::System.String.IsNullOrEmpty(Requirement.Reason)))
                Detail += (("（" + Requirement.Reason) + "）");
            Report.Issues.Add(new TIssue() { KindValue = Kind, Owner = Edge.Owner, Requirement = Requirement, RelatedMods = Value.Providers, IsError = IsError, Message = Detail });
        }

        public static global::System.Collections.Generic.List<TImpact> GetImpacts(TReport report, global::System.Collections.Generic.IEnumerable<global::System.String> removedPaths)
        {
            if (report is null)
                throw new global::System.ArgumentNullException(nameof(report));
            var Removed = new global::System.Collections.Generic.HashSet<global::System.String>(removedPaths.Select(Name => FilePathKey(Name)), global::System.StringComparer.OrdinalIgnoreCase);
            var Active = ActiveMods(report);
            var Original = ProviderMap(Active.Concat((global::System.Collections.Generic.IEnumerable<TMod>)report.Builtins));
            var Unavailable = new global::System.Collections.Generic.HashSet<global::System.String>(Active.Where(Item => Removed.Contains(FilePathKey(Item.RootPath))).Select(Item => Item.Key), global::System.StringComparer.Ordinal);
            var Result = new global::System.Collections.Generic.List<TImpact>();
            global::System.Boolean Changed = true;
            var Round = default(global::System.Int32);
            while (Changed)
            {
                Changed = false;
                var Map = ProviderMap(Active.Where(Item => !(Unavailable.Contains(Item.Key))).Concat((global::System.Collections.Generic.IEnumerable<TMod>)report.Builtins));
                var Additions = new global::System.Collections.Generic.List<TImpact>();
                foreach (var Item in Active.Where(Entry => (((!(Removed.Contains(FilePathKey(Entry.RootPath)))) && (!(Unavailable.Contains(Entry.Key)))) && (!(Entry.IsCandidate)))))
                {
                    var Failed = new global::System.Collections.Generic.List<TDependency>();
                    foreach (var Requirement in Item.Dependencies.Where(Entry => ((Entry.KindValue == ModDependencyKind.Required) && (SideApplies(Entry.Side, report.Context)))))
                    {
                        if ((Resolve(Requirement, Original, report.Context).State == ModDependencyMatchState.Unsatisfied))
                            continue;
                        if ((Resolve(Requirement, Map, report.Context).State == ModDependencyMatchState.Unsatisfied))
                            Failed.Add(Requirement);
                    }
                    if (((Failed.Count) == (0)))
                        continue;
                    Additions.Add(new TImpact()
                    {
                        Mod = Item,
                        Requirements = Failed,
                        IsDirect = ((Round) == (0)),
                        IsDefinite = ((!(report.MetadataIncomplete)) && (Failed.All(Requirement => (Resolve(Requirement, Original, report.Context).State == ModDependencyMatchState.Satisfied)))),
                        Reason = ((((Round) == (0)) ? ("将失去必需依赖：") : ("间接受影响，必需依赖将无法加载：")) + global::System.String.Join("；", Failed.Select(Requirement => Requirement.Display)))
                    });
                }
                foreach (var Impact in Additions)
                {
                    Result.Add(Impact);
                    Unavailable.Add(Impact.Mod.Key);
                    Changed = true;
                }
                Round += 1;
            }
            return Result;
        }
        public static global::System.String DescribeImpacts(TReport report, global::System.Collections.Generic.IEnumerable<global::System.String> removedPaths)
        {
            global::System.String[] Paths = removedPaths.ToArray();
            var Impacts = GetImpacts(report, Paths);
            global::System.String Online = report.DescribeOnlineImpacts(Paths);
            if (((Impacts.Count) == (0)))
                return Online;
            var Lines = new global::System.Collections.Generic.List<global::System.String>() { "以下模组依赖本次操作涉及的文件：" };
            foreach (var Impact in Impacts.Take(30))
                Lines.Add(((((((("• " + Impact.Mod.Display) + "（") + global::System.IO.Path.GetFileName(Impact.Mod.RootPath)) + "）") + ((Impact.IsDefinite) ? ("") : (" [待确认]"))) + global::Microsoft.VisualBasic.Constants.vbCrLf) + "  ") + Impact.Reason);
            if (((Impacts.Count) > (30)))
                Lines.Add(("另有 " + (((Impacts.Count) - (30)))) + " 个模组受影响。");
            if (!string.IsNullOrEmpty(Online))
                Lines.Add(global::Microsoft.VisualBasic.Constants.vbCrLf + Online);
            Lines.Add("仍可继续操作；如需一起禁用或删除，请先返回模组管理选择这些文件。");
            return global::System.String.Join(global::Microsoft.VisualBasic.Constants.vbCrLf, Lines);
        }
        public static global::System.Boolean DependencyMentions(TDependency requirement, global::System.Collections.Generic.IEnumerable<global::System.String> ids)
        {
            var Names = new global::System.Collections.Generic.HashSet<global::System.String>(ids, global::System.StringComparer.Ordinal);
            if (Names.Contains((requirement.Id) ?? ("")))
                return true;
            if ((requirement.Unless is not null && (DependencyMentions(requirement.Unless, Names))))
                return true;
            return requirement.Children.Any(Child => DependencyMentions(Child, Names));
        }
        private static global::System.String DescribeFile(TReport Report, global::System.String FullPath)
        {
            global::System.String Target = FilePathKey(FullPath);
            var Items = Report.Mods.Where(Item => SameFilePath(Item.RootPath, Target)).ToList();
            var Lines = new global::System.Collections.Generic.List<global::System.String>() { global::System.IO.Path.GetFileName(Target) };
            if (((Items.Count) == (0)))
            {
                Lines.Add("未识别到支持的本地模组元数据，无法确定依赖关系。");
                Lines.AddRange(Report.Notices.Where(Notice => Notice.Contains(global::System.IO.Path.GetFileName(Target))).Take(4));
                global::System.String Online = Report.DescribeOnlineFile(Target);
                if (!string.IsNullOrEmpty(Online))
                    Lines.Add(global::Microsoft.VisualBasic.Constants.vbCrLf + Online);
                return global::System.String.Join(global::Microsoft.VisualBasic.Constants.vbCrLf, Lines);
            }
            foreach (var Item in Items)
            {
                Lines.Add("");
                Lines.Add((((((Item.Display + " · ") + ((global::System.String.IsNullOrEmpty(Item.Version)) ? ("未知版本") : (Item.Version))) + " · ") + Item.Format) + ((Item.Enabled) ? ("") : (" · 已禁用"))) + ((Item.IsNested) ? (" · 内嵌") : ("")));
                if ((Item.Side != "*"))
                    Lines.Add("运行环境：" + Item.Side);
                Lines.Add("需要的模组：");
                if (((Item.Dependencies.Count) == (0)))
                    Lines.Add("  元数据未声明依赖。");
                foreach (var Requirement in Item.Dependencies.Take(60))
                {
                    var Edge = Report.Edges.FirstOrDefault(Entry => (Object.ReferenceEquals((Entry.Owner), Item) && Object.ReferenceEquals((Entry.Requirement), Requirement)));
                    global::System.String State = Edge is null ? ("未参与本次启动检查") : (((Edge.StateValue == ModDependencyMatchState.Satisfied) ? ("已满足") : (((Edge.StateValue == ModDependencyMatchState.Unsatisfied) ? ("未满足") : ("版本待确认")))));
                    var KindText = new global::System.Collections.Generic.Dictionary<ModDependencyKind, global::System.String>() { { ModDependencyKind.Required, "必需" }, { ModDependencyKind.Optional, "可选" }, { ModDependencyKind.Recommended, "建议" }, { ModDependencyKind.Suggested, "可选提示" }, { ModDependencyKind.Incompatible, "不兼容" }, { ModDependencyKind.Conflict, "冲突提示" } };
                    if (((Requirement.KindValue == ModDependencyKind.Incompatible) || (Requirement.KindValue == ModDependencyKind.Conflict)))
                        State = (Edge is not null && (Edge.StateValue == ModDependencyMatchState.Satisfied)) ? ("检测到匹配项") : ("未检测到确定匹配项");
                    Lines.Add((((((("  • [" + KindText[Requirement.KindValue]) + "] ") + RequirementDisplay(Requirement, Report.Context)) + "（") + State) + ((Requirement.Side == "*") ? ("") : ("；" + Requirement.Side))) + "）");
                }
                global::System.Collections.Generic.List<TEdge> Referrers = (global::System.Collections.Generic.List<TEdge>)null;
                Report.ReverseDependencies.TryGetValue(Item.Key, out Referrers);
                var Reverse = ((Referrers) ?? (new List<TEdge>())).Where(Edge => IsRequiredFileProvider(Report, Edge, Item.RootPath)).ToList();
                Lines.Add("被这些模组依赖：");
                if (((Reverse.Count) == (0)))
                    Lines.Add("  未发现其他已启用模组声明依赖此 ID。");
                foreach (var Edge in Reverse.Take(60))
                    Lines.Add((((("  • " + Edge.Owner.Display) + "（") + global::System.IO.Path.GetFileName(Edge.Owner.RootPath)) + "）") + ((Edge.Requirement.KindValue == ModDependencyKind.Required) ? (" · 必需") : (" · 可选或兼容声明")));
            }
            Lines.Add("");
            Lines.Add("本地依赖来自模组元数据；未声明的运行时依赖无法在此确认。");
            global::System.String Supplement = Report.DescribeOnlineFile(Target);
            if (!string.IsNullOrEmpty(Supplement))
                Lines.Add(global::Microsoft.VisualBasic.Constants.vbCrLf + Supplement);
            return global::System.String.Join(global::Microsoft.VisualBasic.Constants.vbCrLf, Lines);
        }

    public static ModDependencyMatchState MatchVersion(string actual,string requirement,string syntax="fabric") => ModDependencyVersions.MatchVersion(actual,requirement,syntax);
}
