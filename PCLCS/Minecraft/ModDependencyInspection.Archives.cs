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
public sealed partial class ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile> {

        private const global::System.Int32 MaxMetadataBytes = ((1024) * (1024));
        private const global::System.Int64 MaxNestedBytes = (((64L) * 1024L) * 1024L);
        private const global::System.Int32 MaxNestedDepth = 4;
        private const global::System.Int32 MaxNestedCount = 256;

        public static TReport Inspect(global::System.String modsFolder, TContext context = null, global::System.Threading.CancellationToken token = default(global::System.Threading.CancellationToken))
        {
            var Result = Index.Inspect(modsFolder, (context) ?? (new TContext()), token, false);
            RememberLoaderWarnings(modsFolder, Result);
            return Result;
        }
        public static TReport RebuildIndex(global::System.String ModsFolder, TContext Context = null, global::System.Threading.CancellationToken Token = default(global::System.Threading.CancellationToken))
        {
            var Result = Index.Inspect(ModsFolder, (Context) ?? (new TContext()), Token, true);
            RememberLoaderWarnings(ModsFolder, Result);
            return Result;
        }
        public static TReport InspectForImpact(string modsFolder, TContext context, CancellationToken token = default)
        {
            var result = Index.InspectForImpact(modsFolder, context ?? new TContext(), token);
            RememberLoaderWarnings(modsFolder, result);
            return result;
        }
        /// <summary>Read one archive with the shared loader parser, without folder scans, disk indexes or legacy caches.</summary>
        public static TReport ReadSingleArchive(string fileName, TContext context, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = new TReport { Context = context ?? new TContext() };
            result.Builtins = CreateBuiltins(result.Context);
            try
            {
                if ((File.GetAttributes(fileName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("跳过链接文件");
                using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                    ReadArchive(archive, Path.GetFullPath(fileName), "", true, false, 0, result,
                        new ReadBudget { ForceFresh = true, NoCache = true }, token);
                BuildReport(result, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is JsonException || error is FormatException || error is DecoderFallbackException ||
                error is ArgumentException || error is InvalidOperationException || error is OverflowException)
            {
                Incomplete(result, Path.GetFileName(fileName) + "：无法完整读取元数据（" + error.Message + "）。");
            }
            return result;
        }
        internal static TReport ReadArchiveBytes(byte[] bytes, TContext context, CancellationToken token) {
            var result = new TReport { Context = context };
            result.Builtins = CreateBuiltins(context);
            using var stream = new MemoryStream(bytes, false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            ReadArchive(archive, Path.Combine(Path.GetTempPath(), "PCL-platform-metadata.jar"), "", true, false, 0, result,
                new ReadBudget { ForceFresh = true, NoCache = true }, token);
            BuildReport(result, token);
            return result;
        }
        public static TReport ScanRaw(global::System.String ModsFolder, TContext Context, global::System.Threading.CancellationToken Token, global::System.Boolean ForceFresh = false)
            => ScanRawCore(ModsFolder, Context, Token, ForceFresh, null);

        private static TReport ScanRawCore(string ModsFolder, TContext Context, CancellationToken Token, bool ForceFresh,
            Func<string, TReport> ReadMetadata)
        {
            var Result = new TReport() { Context = (Context) ?? (new TContext()) };
            Result.Builtins = CreateBuiltins(Result.Context);
            if (!(global::System.IO.Directory.Exists(ModsFolder)))
                return Result;
            var Roots = new global::System.Collections.Generic.List<global::System.String>() { global::System.IO.Path.GetFullPath(ModsFolder) };
            if (((!(global::System.String.IsNullOrEmpty(Result.Context.LegacyForgeVersionFolder))) && (global::System.Text.RegularExpressions.Regex.IsMatch(Result.Context.LegacyForgeVersionFolder, "^[0-9.]+$"))))
            {
                global::System.String Legacy = global::System.IO.Path.Combine(ModsFolder, Result.Context.LegacyForgeVersionFolder);
                if (global::System.IO.Directory.Exists(Legacy))
                    Roots.Add(global::System.IO.Path.GetFullPath(Legacy));
            }
            foreach (var Folder in Roots)
            {
                Token.ThrowIfCancellationRequested();
                global::System.String[] Files;
                try
                {
                    if (((global::System.Int32)(((global::System.IO.File.GetAttributes(Folder) & global::System.IO.FileAttributes.ReparsePoint))) != (0)))
                    {
                        Incomplete(Result, "跳过链接目录：" + global::System.IO.Path.GetFileName(Folder));
                        continue;
                    }
                    Files = global::System.IO.Directory.EnumerateFiles(Folder, "*", global::System.IO.SearchOption.TopDirectoryOnly).Where(Item => IsModFile(Item)).OrderBy(Item => Item, global::System.StringComparer.OrdinalIgnoreCase).ToArray();
                }
                catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
                {
                    Result.IndexReadFailed = true;
                    Incomplete(Result, "无法枚举模组目录：" + ex.Message);
                    continue;
                }
                foreach (var FullPath in Files)
                {
                    Token.ThrowIfCancellationRequested();
                    var Metadata = ReadMetadata is null ? ReadFileMetadata(FullPath, Result.Context, Token, ForceFresh) : ReadMetadata(FullPath);
                    Result.Mods.AddRange(Metadata.Mods);
                    Result.Notices.AddRange(Metadata.Notices);
                    Result.MetadataIncomplete |= Metadata.MetadataIncomplete;
                    Result.IndexReadFailed |= Metadata.IndexReadFailed;
                }
            }
            BuildReport(Result, Token);
            return Result;
        }

        private static bool ArchiveEnabled(string Path) => !Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !Path.EndsWith(".old", StringComparison.OrdinalIgnoreCase);

        // Keep per-file parse diagnostics separate from graph evaluation so a changed inventory
        // can reuse the declarations of unchanged files without carrying stale graph warnings.
        private static TReport ReadFileMetadata(string FullPath, TContext Context, CancellationToken Token, bool ForceFresh)
        {
            var Result = new TReport { Context = Context };
            bool Enabled = ArchiveEnabled(FullPath);
            try
            {
                if (((global::System.Int32)(((global::System.IO.File.GetAttributes(FullPath) & global::System.IO.FileAttributes.ReparsePoint))) != (0)))
                    throw new global::System.IO.IOException("跳过链接文件");
                using (var Stream = new global::System.IO.FileStream(FullPath, global::System.IO.FileMode.Open, global::System.IO.FileAccess.Read, global::System.IO.FileShare.ReadWrite | global::System.IO.FileShare.Delete))
                using (var Archive = new global::System.IO.Compression.ZipArchive(Stream, global::System.IO.Compression.ZipArchiveMode.Read, false))
                {
                    var Budget = new ReadBudget() { ForceFresh = ForceFresh };
                    ReadArchive(Archive, global::System.IO.Path.GetFullPath(FullPath), "", Enabled, false, 0, Result, Budget, Token);
                }
            }
            catch (global::System.OperationCanceledException )
            {
                throw;
            }
            catch (global::System.Exception ex) when (((((((ex is global::System.IO.IOException) || (ex is global::System.IO.InvalidDataException)) || (ex is global::System.UnauthorizedAccessException)) || (ex is global::Newtonsoft.Json.JsonException)) || (ex is global::System.FormatException)) || (ex is global::System.Text.DecoderFallbackException)))
            {
                if ((((((ex is global::System.IO.IOException) && (!(ex is global::System.IO.InvalidDataException))))) || (ex is global::System.UnauthorizedAccessException)))
                    Result.IndexReadFailed = true;
                if (Enabled)
                {
                    Incomplete(Result, ((global::System.IO.Path.GetFileName(FullPath) + "：无法完整读取元数据（") + ex.Message) + "）。");
                }
                else
                {
                    Result.Notices.Add(global::System.IO.Path.GetFileName(FullPath) + "：已禁用，无法读取元数据。");
                }
            }
            return Result;
        }
        public static void Reevaluate(TReport Report, TContext Context, global::System.Threading.CancellationToken Token)
        {
            Report.Context = Context;
            Report.Builtins = CreateBuiltins(Context);
            Report.Edges.Clear();
            Report.Issues.Clear();
            Report.ReverseDependencies.Clear();
            BuildReport(Report, Token);
            Report.Notices = Report.Notices.Distinct().ToList();
        }

        private class LoaderWarningMark
        {
            public global::System.Int64 Length;
            public global::System.Int64 LastWrite;
            public global::System.String Message;
        }
        private class LoaderWarningSnapshot
        {
            public global::System.String ContextKey;
            public global::System.Collections.Generic.Dictionary<global::System.String, LoaderWarningMark> Files = new global::System.Collections.Generic.Dictionary<global::System.String, LoaderWarningMark>(global::System.StringComparer.OrdinalIgnoreCase);
        }
        private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, LoaderWarningSnapshot> LoaderWarnings = new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, LoaderWarningSnapshot>(global::System.StringComparer.OrdinalIgnoreCase);
        private static global::System.String LoaderContextKey(TContext Context)
        {
            return ((((Context.LoaderKnown + "|") + Context.MinecraftVersion) + "|") + global::System.String.Join(";", Context.LoaderVersions.OrderBy(Pair => Pair.Key).Select(Pair => ((Pair.Key + "=") + Pair.Value))));
        }
        private static void RememberLoaderWarnings(global::System.String Folder, TReport Report)
        {
            var Snapshot = new LoaderWarningSnapshot() { ContextKey = LoaderContextKey(Report.Context) };
            foreach (var Issue in Report.Issues.Where(Value => (Value.KindValue == ModDependencyIssueKind.LoaderMismatch)))
            {
                try
                {
                    var Info = new global::System.IO.FileInfo(Issue.Owner.RootPath);
                    if (Info.Exists)
                        Snapshot.Files[FilePathKey(Info.FullName)] = new LoaderWarningMark()
                        {
                            Length = Info.Length,
                            LastWrite = Info.LastWriteTimeUtc.Ticks,
                            Message = (("加载器不匹配" + global::Microsoft.VisualBasic.Constants.vbCrLf) + Issue.Message)
                        };
                }
                catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
                {
                    // A changed or removed file will be checked again on refresh.
                }
            }
            if (((LoaderWarnings.Count) > (32)))
                LoaderWarnings.Clear();
            LoaderWarnings[FilePathKey(Folder).TrimEnd(global::System.IO.Path.DirectorySeparatorChar, global::System.IO.Path.AltDirectorySeparatorChar)] = Snapshot;
        }
        public static global::System.String LoaderWarningFor(global::System.String Folder, global::System.String FullPath, TContext Context)
        {
            if (Context is null)
                return "";
            LoaderWarningSnapshot Snapshot = (LoaderWarningSnapshot)null;
            LoaderWarningMark Mark = (LoaderWarningMark)null;
            if ((((!(LoaderWarnings.TryGetValue(FilePathKey(Folder).TrimEnd(global::System.IO.Path.DirectorySeparatorChar, global::System.IO.Path.AltDirectorySeparatorChar), out Snapshot))) || ((Snapshot.ContextKey ?? "") != (LoaderContextKey(Context) ?? ""))) || (!(Snapshot.Files.TryGetValue(FilePathKey(FullPath), out Mark)))))
                return "";
            try
            {
                var Info = new global::System.IO.FileInfo(FullPath);
                if (((((Info.Exists) && ((global::System.Int32)(((Info.Attributes & global::System.IO.FileAttributes.ReparsePoint))) == (0))) && ((Info.Length) == (Mark.Length))) && ((Info.LastWriteTimeUtc.Ticks) == (Mark.LastWrite))))
                    return Mark.Message;
            }
            catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
            {
                // Never keep a red marker for a file that no longer matches the inspected content.
            }
            return "";
        }

        private class ReadBudget
        {
            public global::System.Boolean ForceFresh, NoCache;
            public global::System.Int64 Bytes;
            public global::System.Int32 Count;
        }

        public static global::System.Boolean IsModFile(global::System.String FullPath)
        {
            global::System.String Name = FullPath;
            if (((Name.EndsWith(".disabled", global::System.StringComparison.OrdinalIgnoreCase)) || (Name.EndsWith(".old", global::System.StringComparison.OrdinalIgnoreCase))))
                Name = global::System.IO.Path.GetFileNameWithoutExtension(Name);
            return new[] { ".jar", ".zip", ".litemod" }.Contains(global::System.IO.Path.GetExtension(Name).ToLowerInvariant());
        }

        private static void Incomplete(TReport Report, global::System.String Message)
        {
            Report.MetadataIncomplete = true;
            if (!(Report.Notices.Contains(Message)))
                Report.Notices.Add(Message);
        }

        private static global::System.String ReadText(global::System.IO.Compression.ZipArchiveEntry Entry)
        {
            if (((Entry.Length) > (global::System.Int64)(MaxMetadataBytes)))
                throw new global::System.IO.InvalidDataException("元数据超过 1 MiB");
            using (var Stream = Entry.Open())
            using (var Reader = new global::System.IO.StreamReader(Stream, new global::System.Text.UTF8Encoding(false, true), true))
            {
                var Buffer = new global::System.Char[4096];
                var Content = new global::System.Text.StringBuilder();
                global::System.Int32 Count;
                do
                {
                    Count = Reader.Read(Buffer, 0, Buffer.Length);
                    Content.Append(Buffer, 0, Count);
                    if (((Content.Length) > (MaxMetadataBytes)))
                        throw new global::System.IO.InvalidDataException("元数据超过 1 MiB");
                }
                while (((Count) > (0)));
                return Content.ToString();
            }
        }

        private static void ReadArchive(global::System.IO.Compression.ZipArchive Archive, global::System.String RootPath, global::System.String NestedPath, global::System.Boolean Enabled, global::System.Boolean Nested, global::System.Int32 Depth, TReport Report, ReadBudget Budget, global::System.Threading.CancellationToken Token)
        {
            Token.ThrowIfCancellationRequested();
            if (IsLegacyForge(Report.Context))
            {
                global::System.Int32 Before = Report.Mods.Count;
                ReadLegacyArchive(Archive, RootPath, NestedPath, Enabled, Nested, Depth, Report, Budget, Token);
                if (((Report.Mods.Skip(Before).Any(Value => !(Value.IsMetadataOnly))) || (!(new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml" }.Any(Name => Archive.GetEntry(Name) is not null)))))
                    return;
                // A modern-only mod in a legacy instance still needs a loader mismatch warning.
                Report.Mods.RemoveRange(Before, (Report.Mods.Count) - (Before));
            }
            var Choices = new global::System.Collections.Generic.List<global::System.String>();
            var Loaders = Report.Context.LoaderVersions;
            if (Loaders.ContainsKey("quilt_loader"))
                Choices.Add("quilt.mod.json");
            if (((Loaders.ContainsKey("fabricloader")) || (Loaders.ContainsKey("quilt_loader"))))
                Choices.Add("fabric.mod.json");
            if (((Loaders.ContainsKey("neoforge")) && (NeoModernMetadata(Report.Context).GetValueOrDefault(true))))
                Choices.Add("META-INF/neoforge.mods.toml");
            if (((Loaders.ContainsKey("forge")) || (Loaders.ContainsKey("neoforge"))))
                Choices.Add("META-INF/mods.toml");
            Choices.AddRange(new[] { "quilt.mod.json", "fabric.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json" });
            var Metadata = Choices.Distinct(global::System.StringComparer.Ordinal).Select(Name => Archive.GetEntry(Name)).FirstOrDefault(Entry => Entry is not null);
            if (Metadata is null)
            {
                global::System.Int32 Before = Report.Mods.Count;
                ReadLegacyArchive(Archive, RootPath, NestedPath, Enabled, Nested, Depth, Report, Budget, Token);
                foreach (var Item in Report.Mods.Skip(Before))
                    Item.IncompatibleLoader = !(LoaderAccepts("legacy", Report.Context));
                return;
            }
            var Items = new global::System.Collections.Generic.List<TMod>();
            var NestedJars = new global::System.Collections.Generic.List<global::System.String>();
            global::System.String Content = ReadText(Metadata);
            global::System.String DeclaredPlatform = "";
            switch ((Metadata.FullName ?? ""))
            {
                case "fabric.mod.json":
                    {
                        var Json = global::Newtonsoft.Json.Linq.JObject.Parse(Content);
                        global::System.String Schema = Json["schemaVersion"] is null ? ("0") : (Text(Json["schemaVersion"]));
                        global::System.Boolean Supported = ((Schema == "0") || (Schema == "1"));
                        if (((!(Supported)) && (Enabled)))
                            Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：Fabric schemaVersion 未受支持，未推断其中的依赖声明。");
                        foreach (var Name in (Schema == "0") ? (new[] { "requires", "recommends", "conflicts" }) : (new[] { "depends", "recommends", "suggests", "breaks", "conflicts" }))
                        {
                            if (((Json[Name] is not null && (!(Json[Name] is global::Newtonsoft.Json.Linq.JObject))) && (Enabled)))
                                Incomplete(Report, ((global::System.IO.Path.GetFileName(RootPath) + "：无法完整读取 ") + Name) + " 声明。");
                        }
                        var Item = NewMod(Json["id"], Json["name"], Json["version"], RootPath, NestedPath + Metadata.FullName, "Fabric", Enabled, Nested);
                        Item.Side = ReadSide(Json[(Schema == "0") ? ("side") : ("environment")]);
                        if (((Schema == "0") && (Item.Side == "universal")))
                            Item.Side = "*";
                        Item.IconPath = ReadIconPath(Json["icon"]);
                        ReadDisplayOwnership(Item, Json["custom"] as global::Newtonsoft.Json.Linq.JObject, Json["contact"] as global::Newtonsoft.Json.Linq.JObject);
                        foreach (var AliasId in (Schema == "1") ? (ArrayValues(Json["provides"])) : (global::System.Linq.Enumerable.Empty<JToken>()))
                        {
                            if ((AliasId.Type == global::Newtonsoft.Json.Linq.JTokenType.String))
                                Item.Provides[Text(AliasId)] = Item.Version;
                        }
                        if (Supported)
                            ReadFabricDependencies(Item, Json, Schema == "0");
                        Items.Add(Item);
                        foreach (var Jar in (Schema == "1") ? (ArrayValues(Json["jars"])) : (global::System.Linq.Enumerable.Empty<JToken>()))
                        {
                            global::System.String EntryName = (Jar.Type == global::Newtonsoft.Json.Linq.JTokenType.Object) ? (Text(Jar["file"])) : ("");
                            if (!string.IsNullOrEmpty(EntryName))
                                NestedJars.Add(EntryName);
                        }

                        break;
                    }
                case "quilt.mod.json":
                    {
                        var Json = global::Newtonsoft.Json.Linq.JObject.Parse(Content);
                        global::Newtonsoft.Json.Linq.JObject Loader = Json["quilt_loader"] as global::Newtonsoft.Json.Linq.JObject;
                        if (Loader is null)
                            throw new global::System.FormatException("缺少 quilt_loader");
                        if (((Text(Json["schema_version"]) != "1") && (Enabled)))
                            Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：Quilt 元数据并非受支持的 schema_version 1。");
                        foreach (var Name in new[] { "depends", "breaks", "provides", "jars" })
                        {
                            if (((Loader[Name] is not null && (!(Loader[Name] is global::Newtonsoft.Json.Linq.JArray))) && (Enabled)))
                                Incomplete(Report, ((global::System.IO.Path.GetFileName(RootPath) + "：无法完整读取 ") + Name) + " 声明。");
                        }
                        var Item = NewMod(Loader["id"], Loader["metadata"]?["name"], Loader["version"], RootPath, NestedPath + Metadata.FullName, "Quilt", Enabled, Nested);
                        Item.Group = Text(Loader["group"]);
                        Item.IconPath = ReadIconPath(Loader["metadata"]?["icon"]);
                        ReadDisplayOwnership(Item, Json, Loader["metadata"]?["contact"] as global::Newtonsoft.Json.Linq.JObject);
                        Item.Side = ReadSide(Json["minecraft"]?["environment"]);
                        foreach (var AliasValue in ArrayValues(Loader["provides"]))
                        {
                            global::System.String AliasId = (AliasValue.Type == global::Newtonsoft.Json.Linq.JTokenType.String) ? (Text(AliasValue)) : (Text(AliasValue["id"]));
                            if (string.IsNullOrEmpty(AliasId))
                                continue;
                            global::System.String AliasVersion = ((AliasValue.Type == global::Newtonsoft.Json.Linq.JTokenType.Object) && AliasValue["version"] is not null) ? (Text(AliasValue["version"])) : (Item.Version);
                            Item.Provides[AliasId] = AliasVersion;
                            if (((!(AliasId.Contains(":"))) && !string.IsNullOrEmpty(Item.Group)))
                                Item.Provides[(Item.Group + ":") + AliasId] = AliasVersion;
                            if (AliasId.Contains(":"))
                                Item.Provides[AliasId.Substring((AliasId.LastIndexOf(':')) + (1))] = AliasVersion;
                        }
                        foreach (var Value in ArrayValues(Loader["depends"]))
                            Item.Dependencies.Add(ReadQuiltDependency(Value, ModDependencyKind.Required, true));
                        foreach (var Value in ArrayValues(Loader["breaks"]))
                            Item.Dependencies.Add(ReadQuiltDependency(Value, ModDependencyKind.Incompatible, false));
                        Items.Add(Item);
                        foreach (var Jar in ArrayValues(Loader["jars"]))
                        {
                            if ((Jar.Type == global::Newtonsoft.Json.Linq.JTokenType.String))
                                NestedJars.Add(Text(Jar));
                        }

                        break;
                    }
                case "META-INF/mods.toml":
                case "META-INF/neoforge.mods.toml":
                    {
                        // NeoForge 1.20.4 also uses mods.toml; distinguish it using actual platform dependencies.
                        var Platforms = ReadToml(Content).Where(Section => Section.IsArray && (((Section.Name.StartsWith("dependencies.", global::System.StringComparison.Ordinal)) && (Scalar(Section, "mandatory") != "false")) && (new[] { "", "required" }.Contains(Scalar(Section, "type"))))).Select(Section => Scalar(Section, "modId")).ToList();
                        if (((Metadata.FullName.Contains("neoforge")) || (Platforms.Contains("neoforge"))))
                        {
                            DeclaredPlatform = "neoforge";
                        }
                        else if (Platforms.Contains("forge"))
                        {
                            DeclaredPlatform = "forge";
                        }
                        global::System.Boolean IsNeo = ((DeclaredPlatform == "neoforge") || (((string.IsNullOrEmpty(DeclaredPlatform) && (Loaders.ContainsKey("neoforge"))))));
                        ReadForge(Content, Archive, RootPath, NestedPath + Metadata.FullName, Enabled, Nested, IsNeo, Items, Report);
                        var JarJar = Archive.GetEntry("META-INF/jarjar/metadata.json");
                        if (JarJar is not null)
                        {
                            foreach (var Jar in ArrayValues(global::Newtonsoft.Json.Linq.JObject.Parse(ReadText(JarJar))["jars"]))
                            {
                                global::System.String EntryName = Text(Jar["path"]);
                                if (!string.IsNullOrEmpty(EntryName))
                                    NestedJars.Add(EntryName);
                            }
                        }

                        break;
                    }
                case "mcmod.info":
                    {
                        global::System.Int32 Before = Report.Mods.Count;
                        ReadLegacyArchive(Archive, RootPath, NestedPath, Enabled, Nested, Depth, Report, Budget, Token);
                        foreach (var Item in Report.Mods.Skip(Before))
                            Item.IncompatibleLoader = !(LoaderAccepts("legacy", Report.Context));
                        return;
                    }
                case "litemod.json":
                    {
                        var Json = global::Newtonsoft.Json.Linq.JObject.Parse(Content);
                        var Item = NewMod(Json["name"], Json["displayName"], Json["version"], RootPath, NestedPath + Metadata.FullName, "LiteLoader", Enabled, Nested);
                        Item.IconPath = ReadIconPath(Json["icon"]);
                        Items.Add(Item);
                        if (Enabled)
                            Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：LiteLoader 依赖可能在运行时代码中声明。");
                        break;
                    }
            }
            if (((Items.Count) == (0)))
                throw new global::System.FormatException("声明中未找到模组 ID");
            foreach (var Item in Items)
            {
                Item.IncompatibleLoader = !(LoaderAccepts(Metadata.FullName, Report.Context, DeclaredPlatform));
                var NeoGeneration = NeoModernMetadata(Report.Context);
                if (((((Item.IncompatibleLoader) && (Loaders.ContainsKey("neoforge"))) && (NeoGeneration.HasValue)) && (new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" }.Contains(Metadata.FullName))))
                {
                    Item.LoaderMismatchDetail = (("此 NeoForge 版本读取 " + ((NeoGeneration.Value) ? ("META-INF/neoforge.mods.toml") : ("META-INF/mods.toml"))) + "，当前文件的声明格式不适用。");
                }
            }
            Report.Mods.AddRange((global::System.Collections.Generic.IEnumerable<TMod>)Items);
            ReadNestedArchives(Archive, NestedJars, RootPath, NestedPath, Enabled, Depth, Report, Budget, Token);
        }

        private static void ReadNestedArchives(global::System.IO.Compression.ZipArchive Archive, global::System.Collections.Generic.IEnumerable<global::System.String> NestedJars, global::System.String RootPath, global::System.String NestedPath, global::System.Boolean Enabled, global::System.Int32 Depth, TReport Report, ReadBudget Budget, global::System.Threading.CancellationToken Token)
        {
            foreach (var EntryName in NestedJars.Distinct(global::System.StringComparer.Ordinal))
            {
                Token.ThrowIfCancellationRequested();
                global::System.String[] Parts = EntryName.Replace('\\', '/').Split('/');
                if (((EntryName.StartsWith("/", global::System.StringComparison.Ordinal)) || (Parts.Any(Part => ((Part == "..") || string.IsNullOrEmpty(Part))))))
                {
                    if (Enabled)
                        Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：跳过不安全的内嵌路径。");
                    continue;
                }
                var Jar = Archive.GetEntry(EntryName);
                if (Jar is null)
                {
                    if (Enabled)
                        Incomplete(Report, (global::System.IO.Path.GetFileName(RootPath) + "：声明的内嵌文件不存在：") + EntryName);
                    continue;
                }
                if (((((Depth) >= (MaxNestedDepth)) || ((Budget.Count) >= (MaxNestedCount))) || ((Jar.Length) > ((MaxNestedBytes) - (Budget.Bytes)))))
                {
                    if (Enabled)
                        Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：内嵌模组达到读取上限。");
                    continue;
                }
                Budget.Bytes += Jar.Length;
                Budget.Count += 1;
                try
                {
                    using (var Buffer = new global::System.IO.MemoryStream())
                    {
                        using (var Input = Jar.Open())
                        {
                            var Bytes = new global::System.Byte[8192];
                            global::System.Int32 Count;
                            global::System.Int64 Remaining = Jar.Length;
                            do
                            {
                                Count = Input.Read(Bytes, 0, (global::System.Int32)global::System.Math.Min((global::System.Int64)Bytes.Length, (Remaining) + 1L));
                                if (((global::System.Int64)(Count) > (Remaining)))
                                    throw new global::System.IO.InvalidDataException("内嵌文件长度与声明不符");
                                Buffer.Write(Bytes, 0, Count);
                                Remaining -= (global::System.Int64)Count;
                            }
                            while (((Count) > (0)));
                        }
                        Buffer.Position = 0L;
                        using (var Inner = new global::System.IO.Compression.ZipArchive(Buffer, global::System.IO.Compression.ZipArchiveMode.Read, true))
                        {
                            ReadArchive(Inner, RootPath, (NestedPath + EntryName) + "!", Enabled, true, (Depth) + (1), Report, Budget, Token);
                        }
                    }
                }
                catch (global::System.OperationCanceledException )
                {
                    throw;
                }
                catch (global::System.Exception ex) when ((((((ex is global::System.IO.IOException) || (ex is global::System.IO.InvalidDataException)) || (ex is global::Newtonsoft.Json.JsonException)) || (ex is global::System.FormatException)) || (ex is global::System.Text.DecoderFallbackException)))
                {
                    if (Enabled)
                        Incomplete(Report, ((global::System.IO.Path.GetFileName(RootPath) + "!") + EntryName) + "：无法完整读取内嵌模组。");
                }
            }
        }

        private static TMod NewMod(global::Newtonsoft.Json.Linq.JToken Id, global::Newtonsoft.Json.Linq.JToken Name, global::Newtonsoft.Json.Linq.JToken Version, global::System.String RootPath, global::System.String MetadataPath, global::System.String Format, global::System.Boolean Enabled, global::System.Boolean Nested)
        {
            global::System.String ModId = Text(Id);
            if (!(global::System.Text.RegularExpressions.Regex.IsMatch(ModId, "^[A-Za-z0-9_.-]+$")))
                throw new global::System.FormatException("模组 ID 无效");
            return new TMod()
            {
                Id = ModId,
                Name = string.IsNullOrEmpty(Text(Name)) ? (ModId) : (Text(Name)),
                Version = Text(Version),
                RootPath = RootPath,
                MetadataPath = MetadataPath,
                Format = Format,
                Enabled = Enabled,
                IsNested = Nested
            };
        }

        private static void ReadDisplayOwnership(TMod Item, global::Newtonsoft.Json.Linq.JObject Custom, global::Newtonsoft.Json.Linq.JObject Contact)
        {
            var Parent = (Custom?["modmenu"] as global::Newtonsoft.Json.Linq.JObject)?["parent"];
            if (Parent is global::Newtonsoft.Json.Linq.JObject)
                Parent = Parent["id"];
            if ((global::System.Boolean)(((Parent?.Type) is { } arg1) && ((arg1) == (global::Newtonsoft.Json.Linq.JTokenType.String))))
                Item.DisplayParentId = Text(Parent);
            var Generated = Custom?["fabric-loom:generated"];
            Item.IsGeneratedLibrary = ((Generated is not null && (Generated.Type == global::Newtonsoft.Json.Linq.JTokenType.Boolean)) && (Generated.Value<bool>()));
            var Source = Contact?["sources"];
            global::System.Uri Address = (global::System.Uri)null;
            if (((((Source is null || (Source.Type != global::Newtonsoft.Json.Linq.JTokenType.String)) || (!(global::System.Uri.TryCreate(Text(Source), global::System.UriKind.Absolute, out Address)))) || (!(new[] { "http", "https" }.Contains(Address.Scheme)))) || (Address.AbsolutePath == "/")))
                return;
            // Compare the declared project path, never a shared homepage, author or mod-name prefix.
            Item.SourceProject = ((Address.IdnHost.ToLowerInvariant() + ((Address.IsDefaultPort) ? ("") : (":" + Address.Port))) + Address.AbsolutePath.TrimEnd('/'));
            if (Item.SourceProject.EndsWith(".git", global::System.StringComparison.Ordinal))
                Item.SourceProject = Item.SourceProject.Substring(0, (Item.SourceProject.Length) - (4));
        }

        public static global::System.Collections.Generic.List<TMod> EmbeddedModsForDisplay(TReport Report, global::System.String FullPath)
        {
            var FileMods = Report.Mods.Where(Item => ((SameFilePath(Item.RootPath, FullPath)) && (!(Item.IsMetadataOnly)))).ToList();
            return FileMods.Where(Item => (((((((Item.IsNested) && (Item.Enabled)) && (!(Item.IsBuiltin))) && (!(Item.IsCandidate))) && (!(Item.IncompatibleLoader))) && (SideApplies(Item.Side, Report.Context))) && (!(IsInternalDisplayModule(Item, FileMods))))).ToList();
        }

        private static global::System.Boolean IsInternalDisplayModule(TMod Item, global::System.Collections.Generic.List<TMod> FileMods)
        {
            if (((Item.IsGeneratedLibrary) || (IsInternalLoaderId(Item.Id))))
                return true;
            global::System.String Prefix = ArchivePrefix(Item);
            var Ancestors = FileMods.Where(Parent => (!Object.ReferenceEquals((Parent), Item) && ((((!(Parent.IsNested)) || (((((ArchivePrefix(Parent).Length) < (Prefix.Length)) && (Prefix.StartsWith(ArchivePrefix(Parent), global::System.StringComparison.Ordinal))))))))));
            return Ancestors.Any(Parent => (((((!(global::System.String.IsNullOrEmpty(Item.DisplayParentId))) && (Identities(Parent).ContainsKey(Item.DisplayParentId))))) || ((((!(global::System.String.IsNullOrEmpty(Item.SourceProject))) && (global::System.String.Equals(Item.SourceProject, Parent.SourceProject, global::System.StringComparison.Ordinal)))))));
        }

        private static global::System.String ArchivePrefix(TMod Item)
        {
            global::System.String Metadata = (Item.MetadataPath) ?? ("");
            return Metadata.Substring(0, (Metadata.LastIndexOf('!')) + (1));
        }

        private static global::System.String Text(global::Newtonsoft.Json.Linq.JToken Value)
        {
            return (Value is null || (Value.Type == global::Newtonsoft.Json.Linq.JTokenType.Null)) ? ("") : (Value.ToString());
        }
        private static global::System.Collections.Generic.IEnumerable<global::Newtonsoft.Json.Linq.JToken> ArrayValues(global::Newtonsoft.Json.Linq.JToken Value)
        {
            return (Value is global::Newtonsoft.Json.Linq.JArray) ? (((global::Newtonsoft.Json.Linq.JArray)(Value)).AsEnumerable()) : (global::System.Linq.Enumerable.Empty<JToken>());
        }
        private static global::System.String ReadIconPath(global::Newtonsoft.Json.Linq.JToken Value)
        {
            if (Value is global::Newtonsoft.Json.Linq.JObject)
            {
                Value = ((global::Newtonsoft.Json.Linq.JObject)(Value)).Properties().OrderBy(Item =>
                    {
                        global::System.Int32 Size;
                        return ((global::System.Int32.TryParse(Item.Name, out Size)) && ((Size) >= (32))) ? (Size) : (global::System.Int32.MaxValue);
                    }).Select(Item => Item.Value).FirstOrDefault();
            }
            if ((Value is null || (Value.Type != global::Newtonsoft.Json.Linq.JTokenType.String)))
                return "";
            global::System.String Name = Text(Value).Replace('\\', '/');
            if ((((Name.StartsWith("/", global::System.StringComparison.Ordinal)) || (Name.Contains(":"))) || (Name.Split('/').Any(Part => (((Part == "..") || string.IsNullOrEmpty(Part)) || (Part == "."))))))
                return "";
            return Name;
        }

        private static global::System.String ReadSide(global::Newtonsoft.Json.Linq.JToken Value)
        {
            if (Value is global::Newtonsoft.Json.Linq.JArray)
            {
                var Values = ((global::Newtonsoft.Json.Linq.JArray)(Value)).Select(Item => Text(Item)).ToList();
                if (((Values.Contains("*")) || ((((Values.Contains("client")) && (Values.Contains("server")))))))
                    return "*";
                return global::System.String.Join(",", Values);
            }
            return (string.IsNullOrEmpty(Text(Value)) ? ("*") : (Text(Value))).ToLowerInvariant().Replace("dedicated_server", "server");
        }
        private static global::System.Boolean SideApplies(global::System.String Side, TContext Context)
        {
            return ((((Side == "*") || string.IsNullOrEmpty(Side)) || (Side == "both")) || (Side.Split(',').Contains(((Context.Side) ?? ("client")).ToLowerInvariant())));
        }
        private static void ReadFabricDependencies(TMod Item, global::Newtonsoft.Json.Linq.JObject Json, global::System.Boolean LegacySchema = false)
        {
            var Kinds = (LegacySchema) ? (new global::System.Collections.Generic.Dictionary<global::System.String, ModDependencyKind>() { { "requires", ModDependencyKind.Required }, { "recommends", ModDependencyKind.Suggested }, { "conflicts", ModDependencyKind.Incompatible } }) : (new global::System.Collections.Generic.Dictionary<global::System.String, ModDependencyKind>() { { "depends", ModDependencyKind.Required }, { "recommends", ModDependencyKind.Recommended }, { "suggests", ModDependencyKind.Suggested }, { "breaks", ModDependencyKind.Incompatible }, { "conflicts", ModDependencyKind.Conflict } });
            foreach (var Pair in Kinds)
            {
                global::Newtonsoft.Json.Linq.JObject Values = Json[Pair.Key] as global::Newtonsoft.Json.Linq.JObject;
                if (Values is null)
                    continue;
                foreach (var Entry in Values.Properties())
                    Item.Dependencies.Add(new TDependency() { Id = CanonicalId(Entry.Name), Versions = Entry.Value.DeepClone(), KindValue = Pair.Value });
            }
        }
        private static TDependency ReadQuiltDependency(global::Newtonsoft.Json.Linq.JToken Value, ModDependencyKind Kind, global::System.Boolean Any)
        {
            var Result = new TDependency() { KindValue = Kind, Join = (Any) ? ("any") : ("all") };
            if (Value is global::Newtonsoft.Json.Linq.JArray)
            {
                foreach (var Child in ((global::Newtonsoft.Json.Linq.JArray)(Value)))
                    Result.Children.Add(ReadQuiltDependency(Child, Kind, Any));
                Result.IsUnknown = ((Result.Children.Count) == (0));
            }
            else if ((Value.Type == global::Newtonsoft.Json.Linq.JTokenType.String))
            {
                Result.Id = CanonicalId(Text(Value));
            }
            else if (Value is global::Newtonsoft.Json.Linq.JObject)
            {
                Result.Id = CanonicalId(Text(Value["id"]));
                Result.Versions = (Value["versions"]?.DeepClone()) ?? (new global::Newtonsoft.Json.Linq.JValue("*"));
                Result.Reason = Text(Value["reason"]);
                if (Text(Value["optional"]).Equals("true", global::System.StringComparison.OrdinalIgnoreCase))
                {
                    Result.KindValue = ModDependencyKind.Optional;
                    Result.EnforceWhenPresent = true;
                }
                if (Value["unless"] is not null)
                    Result.Unless = ReadQuiltDependency(Value["unless"], ModDependencyKind.Required, true);
            }
            else
            {
                Result.IsUnknown = true;
            }
            if ((((Result.Children.Count) == (0)) && (global::System.String.IsNullOrEmpty(Result.Id))))
                Result.IsUnknown = true;
            return Result;
        }

        private class TomlSection
        {
            public global::System.String Name;
            public bool IsArray, Incomplete;
            public global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> Values = new global::System.Collections.Generic.Dictionary<global::System.String, global::System.String>(global::System.StringComparer.Ordinal);
        }

        // A bounded TOML subset reader, with quoted/comment/multiline state. Descriptions must never become dependency declarations.
        // Only complete scalar assignments in recognised sections are used; unsupported values remain unknown.
        private static global::System.Collections.Generic.List<TomlSection> ReadToml(global::System.String Content)
        {
            var Sections = new global::System.Collections.Generic.List<TomlSection>();
            var Current = new TomlSection() { Name = "" };
            var Statement = new global::System.Text.StringBuilder();
            Sections.Add(Current);
            global::System.Char Quote = '\0';
            var Triple = default(global::System.Boolean);
            var Escape = default(global::System.Boolean);
            var Square = default(global::System.Int32);
            var Curly = default(global::System.Int32);
            var Comment = default(global::System.Boolean);
            var Index = default(global::System.Int32);
            while (((Index) <= (Content.Length)))
            {
                global::System.Char Ch = ((Index) == (Content.Length)) ? ('\n') : (Content[Index]);
                if (Comment)
                {
                    if (((Ch) != ('\n')))
                    {
                        Index += 1;
                        continue;
                    }
                    Comment = false;
                }
                if (((Quote) != ('\0')))
                {
                    if (Ch == '\n' && !Triple)
                        throw new FormatException("TOML 单行字符串未结束");
                    Statement.Append(Ch);
                    if (Escape)
                    {
                        Escape = false;
                    }
                    else if ((((Ch) == ('\\')) && ((Quote) == ('"'))))
                    {
                        Escape = true;
                    }
                    else if (((Ch) == (Quote)))
                    {
                        if (Triple)
                        {
                            if ((((((Index) + (2)) < (Content.Length)) && ((Content[(Index) + (1)]) == (Quote))) && ((Content[(Index) + (2)]) == (Quote))))
                            {
                                Statement.Append(Quote, 2);
                                Index += 2;
                                Quote = '\0';
                                Triple = false;
                            }
                        }
                        else
                        {
                            Quote = '\0';
                        }
                    }
                }
                else if (((Ch) == ('#')))
                {
                    Comment = true;
                }
                else if ((((Ch) == ('"')) || ((Ch) == ('\''))))
                {
                    Quote = Ch;
                    Triple = (((((Index) + (2)) < (Content.Length)) && ((Content[(Index) + (1)]) == (Ch))) && ((Content[(Index) + (2)]) == (Ch)));
                    Statement.Append(Ch);
                    if (Triple)
                    {
                        Statement.Append(Ch, 2);
                        Index += 2;
                    }
                }
                else
                {
                    if (((Ch) == ('[')))
                        Square += 1;
                    if (((Ch) == (']')))
                        Square -= 1;
                    if (((Ch) == ('{')))
                        Curly += 1;
                    if (((Ch) == ('}')))
                        Curly -= 1;
                    if (Square < 0 || Curly < 0)
                        throw new FormatException("TOML 容器括号不匹配");
                    if (((((Ch) == ('\n')) && ((Square) <= (0))) && ((Curly) <= (0))))
                    {
                        global::System.String Line = Statement.ToString().Trim();
                        Statement.Clear();
                        if (Line.StartsWith("[", global::System.StringComparison.Ordinal))
                        {
                            var Header = Regex.Match(Line, @"^(?<open>\[\[|\[)\s*(?<name>[A-Za-z0-9_-]+(?:\s*\.\s*[A-Za-z0-9_-]+)*)\s*(?<close>\]\]|\])$");
                            bool validHeader = Header.Success && Header.Groups["open"].Length == Header.Groups["close"].Length;
                            Current = new TomlSection() {
                                Name = validHeader ? Regex.Replace(Header.Groups["name"].Value, @"\s+", "") : "?",
                                IsArray = validHeader && Header.Groups["open"].Length == 2,
                                Incomplete = !validHeader
                            };
                            Sections.Add(Current);
                        }
                        else if (!string.IsNullOrEmpty(Line))
                        {
                            var Assignment = global::System.Text.RegularExpressions.Regex.Match(Line, @"^([A-Za-z0-9_.-]+)\s*=\s*(.+)$", global::System.Text.RegularExpressions.RegexOptions.Singleline);
                            if (Assignment.Success) {
                                string key = Assignment.Groups[1].Value;
                                if (Current.Values.ContainsKey(key)) Current.Incomplete = true;
                                Current.Values[key] = Assignment.Groups[2].Value.Trim();
                            } else {
                                // A valid quoted/dotted key outside this subset is not silently ignored.
                                Current.Incomplete = true;
                            }
                        }
                        Square = 0;
                        Curly = 0;
                    }
                    else
                    {
                        Statement.Append(Ch);
                    }
                }
                Index += 1;
            }
            if (((((Quote) != ('\0')) || ((Square) != (0))) || ((Curly) != (0))))
                throw new global::System.FormatException("TOML 字符串或容器未结束");
            return Sections;
        }
        private static global::System.String Scalar(TomlSection Section, global::System.String Key, global::System.String DefaultValue = "")
        {
            global::System.String Raw = (global::System.String)null;
            if (!(Section.Values.TryGetValue(Key, out Raw)))
                return DefaultValue;
            if (((Raw.StartsWith(("\"" + "\"") + "\"", global::System.StringComparison.Ordinal)) || (Raw.StartsWith("'''", global::System.StringComparison.Ordinal))))
                return DefaultValue;
            if (((((Raw.Length) >= (2)) && ((Raw[0]) == ('\''))) && ((Raw.Last()) == ('\''))))
                return Raw.Substring(1, (Raw.Length) - (2));
            if (((((Raw.Length) >= (2)) && ((Raw[0]) == ('"'))) && ((Raw.Last()) == ('"'))))
            {
                try
                {
                    return global::Newtonsoft.Json.JsonConvert.DeserializeObject<string>(Raw);
                }
                catch (global::Newtonsoft.Json.JsonException )
                {
                    return DefaultValue;
                }
            }
            if (((Raw == "true") || (Raw == "false")))
                return Raw;
            return DefaultValue;
        }
        private static void ReadForge(global::System.String Content, global::System.IO.Compression.ZipArchive Archive, global::System.String RootPath, global::System.String MetadataPath, global::System.Boolean Enabled, global::System.Boolean Nested, global::System.Boolean Neo, global::System.Collections.Generic.List<TMod> Items, TReport Report)
        {
            var Sections = ReadToml(Content);
            var GlobalSection = Sections[0];
            var FileProperties = Sections.FirstOrDefault(Entry => !Entry.IsArray && Entry.Name == "properties");
            if (Enabled && Sections.Any(section => section.Incomplete || section.Name == "?" ||
                (section.Name == "mods" && !section.IsArray) ||
                (section.Name.StartsWith("dependencies.", StringComparison.Ordinal) && !section.IsArray) ||
                (section.Name == "properties" && section.IsArray)))
                Incomplete(Report, Path.GetFileName(RootPath) + "：TOML 表类型、表名或赋值不受支持，部分声明未解析。");
            global::System.String ManifestVersion = "";
            var Manifest = Archive.GetEntry("META-INF/MANIFEST.MF");
            if (Manifest is not null)
            {
                global::System.String ManifestText = global::System.Text.RegularExpressions.Regex.Replace(ReadText(Manifest), @"\r?\n ", "");
                var Match = global::System.Text.RegularExpressions.Regex.Match(ManifestText, @"(?m)^Implementation-Version:\s*([^\r\n]+)");
                if (Match.Success)
                    ManifestVersion = Match.Groups[1].Value.Trim();
            }
            foreach (var Section in Sections.Where(Entry => Entry.IsArray && Entry.Name == "mods"))
            {
                global::System.String Version = global::System.Text.RegularExpressions.Regex.Replace(Scalar(Section, "version"), @"\$\{file\.([A-Za-z0-9_.-]+)\}", Match =>
                    {
                        global::System.String Value = (Match.Groups[1].Value == "jarVersion") ? (ManifestVersion) : ((FileProperties is null ? ("") : (Scalar(FileProperties, Match.Groups[1].Value))));
                        return string.IsNullOrEmpty(Value) ? (Match.Value) : (Value);
                    });
                var Item = NewMod(new global::Newtonsoft.Json.Linq.JValue(Scalar(Section, "modId")), new global::Newtonsoft.Json.Linq.JValue(Scalar(Section, "displayName")), new global::Newtonsoft.Json.Linq.JValue(Version), RootPath, MetadataPath, (Neo) ? ("NeoForge") : ("Forge"), Enabled, Nested);
                Item.IconPath = ReadIconPath(new global::Newtonsoft.Json.Linq.JValue(Scalar(Section, "logoFile", Scalar(GlobalSection, "logoFile"))));
                if ((Scalar(GlobalSection, "clientSideOnly") == "true"))
                    Item.Side = "client";
                Items.Add(Item);
            }
            foreach (var Section in Sections.Where(Entry => Entry.IsArray && Entry.Name.StartsWith("dependencies.", global::System.StringComparison.Ordinal)))
            {
                global::System.String OwnerId = Section.Name.Substring("dependencies.".Length);
                var Item = Items.FirstOrDefault(Entry => ((Entry.Id ?? "") == (OwnerId ?? "")));
                if (Item is null)
                {
                    if (Enabled)
                        Incomplete(Report, (global::System.IO.Path.GetFileName(RootPath) + "：依赖声明所属 ID 不存在：") + OwnerId);
                    continue;
                }
                global::System.String Mandatory = Scalar(Section, "mandatory");
                global::System.String KindText = Scalar(Section, "type", ((Neo) && string.IsNullOrEmpty(Mandatory)) ? ("required") : (""));
                ModDependencyKind Kind;
                global::System.Boolean Unknown = false;
                switch ((KindText ?? ""))
                {
                    case "required":
                        {
                            Kind = ModDependencyKind.Required;
                            break;
                        }
                    case "optional":
                        {
                            Kind = ModDependencyKind.Optional;
                            break;
                        }
                    case "incompatible":
                        {
                            Kind = ModDependencyKind.Incompatible;
                            break;
                        }
                    case "discouraged":
                        {
                            Kind = ModDependencyKind.Conflict;
                            break;
                        }

                    default:
                        {
                            if ((Mandatory == "true"))
                            {
                                Kind = ModDependencyKind.Required;
                            }
                            else if ((Mandatory == "false"))
                            {
                                Kind = ModDependencyKind.Optional;
                            }
                            else
                            {
                                Kind = ModDependencyKind.Required;
                                Unknown = true;
                            }

                            break;
                        }
                }
                global::System.String Id = Scalar(Section, "modId");
                Item.Dependencies.Add(new TDependency()
                {
                    Id = CanonicalId(Id),
                    Versions = new global::Newtonsoft.Json.Linq.JValue(Scalar(Section, "versionRange", "*")),
                    Syntax = "maven",
                    KindValue = Kind,
                    Side = ReadSide(new global::Newtonsoft.Json.Linq.JValue(Scalar(Section, "side", "*"))),
                    Reason = Scalar(Section, "reason"),
                    EnforceWhenPresent = (Kind == ModDependencyKind.Optional),
                    IsUnknown = ((Unknown) || string.IsNullOrEmpty(Id))
                });
            }
            foreach (var Item in Items)
            {
                var Features = Sections.FirstOrDefault(Entry => ((Entry.Name ?? "") == (("features." + Item.Id) ?? "")));
                if ((Features is not null && (Features.Values.ContainsKey("java_version"))))
                {
                    Item.Dependencies.Add(new TDependency() { Id = "java", Versions = new global::Newtonsoft.Json.Linq.JValue(Scalar(Features, "java_version")), Syntax = "maven", KindValue = ModDependencyKind.Required });
                }
                // Forge FML uses the Forge major version; NeoForge's language loader version is a separate numbering scheme.
                global::System.String Language = Scalar(GlobalSection, "modLoader", (Neo) ? ("javafml") : (""));
                global::System.String Range = Scalar(GlobalSection, "loaderVersion");
                if (((new[] { "javafml", "lowcodefml" }.Contains(Language)) && !string.IsNullOrEmpty(Range)))
                {
                    Item.Dependencies.Add(new TDependency() { Id = Language, Versions = new global::Newtonsoft.Json.Linq.JValue(Range), Syntax = "maven", KindValue = ModDependencyKind.Required, Reason = "FML 语言加载器版本" });
                }
            }
        }
        public static global::System.Boolean IsLegacyForge(TContext Context)
        {
            if ((Context is null || (!(Context.LoaderVersions.ContainsKey("forge")))))
                return false;
            return ((global::System.Text.RegularExpressions.Regex.IsMatch((Context.MinecraftVersion) ?? (""), @"^1\.(?:[0-9]|1[0-2])(?:\.|$)")) || (global::System.Text.RegularExpressions.Regex.IsMatch((Context.LoaderVersions["forge"]) ?? (""), @"^(?:[1-9]|1[0-4])\.")));
        }

        private static global::System.Boolean? NeoModernMetadata(TContext Context)
        {
            global::System.Version Version = (global::System.Version)null;
            if (((!(Context.LoaderVersions.ContainsKey("neoforge"))) || (!(global::System.Version.TryParse(Context.MinecraftVersion, out Version)))))
                return (global::System.Boolean?)(default(global::System.Boolean?));
            return ((Version) >= (new global::System.Version(1, 20, 5)));
        }
        private static global::System.Boolean LoaderAccepts(global::System.String Metadata, TContext Context, global::System.String DeclaredPlatform = "")
        {
            if (((!(Context.LoaderKnown)) || ((Context.LoaderVersions.Count) == (0))))
                return true;
            var Loaders = Context.LoaderVersions;
            switch ((Metadata ?? ""))
            {
                case "fabric.mod.json":
                    {
                        return ((Loaders.ContainsKey("fabricloader")) || (Loaders.ContainsKey("quilt_loader")));
                    }
                case "quilt.mod.json":
                    {
                        return Loaders.ContainsKey("quilt_loader");
                    }
                case "META-INF/neoforge.mods.toml":
                    {
                        return ((Loaders.ContainsKey("neoforge")) && (NeoModernMetadata(Context).GetValueOrDefault(true)));
                    }
                case "META-INF/mods.toml":
                    {
                        if ((DeclaredPlatform == "neoforge"))
                            return ((Loaders.ContainsKey("neoforge")) && (!(NeoModernMetadata(Context).GetValueOrDefault(false))));
                        if ((DeclaredPlatform == "forge"))
                            return (((((Loaders.ContainsKey("forge")) && (!(IsLegacyForge(Context)))))) || ((((Loaders.ContainsKey("neoforge")) && (Context.MinecraftVersion == "1.20.1")))));
                        return (((((Loaders.ContainsKey("forge")) && (!(IsLegacyForge(Context)))))) || ((((Loaders.ContainsKey("neoforge")) && (!(NeoModernMetadata(Context).GetValueOrDefault(false)))))));
                    }
                case "legacy":
                    {
                        return IsLegacyForge(Context);
                    }
                case "litemod.json":
                    {
                        return Loaders.ContainsKey("liteloader");
                    }

                default:
                    {
                        return true;
                    }
            }
        }

        private static global::System.String LoaderDisplay(TContext Context)
        {
            if (Context.LoaderVersions.ContainsKey("quilt_loader"))
                return "Quilt";
            if (Context.LoaderVersions.ContainsKey("fabricloader"))
                return "Fabric";
            if (Context.LoaderVersions.ContainsKey("neoforge"))
                return "NeoForge";
            if (Context.LoaderVersions.ContainsKey("forge"))
                return (IsLegacyForge(Context)) ? ("Forge（旧版）") : ("Forge");
            if (Context.LoaderVersions.ContainsKey("liteloader"))
                return "LiteLoader";
            return "当前加载器";
        }
        public static global::System.String RuntimeOwnerId(global::System.String Id, TContext Context)
        {
            switch (((Id) ?? ("") ?? ""))
            {
                case "javafml":
                case "lowcodefml":
                case "fml":
                case "FML":
                    {
                        return (Context.LoaderVersions.ContainsKey("neoforge")) ? ("neoforge") : ("forge");
                    }
                case "forge":
                case "neoforge":
                case "fabricloader":
                case "quilt_loader":
                case "liteloader":
                case "minecraft":
                case "java":
                    {
                        return Id;
                    }

                default:
                    {
                        return "";
                    }
            }
        }
        public static global::System.Boolean IsInternalLoaderId(global::System.String Id)
        {
            return new[] { "javafml", "lowcodefml", "fml", "FML" }.Contains((Id) ?? (""));
        }
        public static global::System.String RuntimeDisplayName(global::System.String Id)
        {
            switch ((Id ?? ""))
            {
                case "forge":
                    {
                        return "Forge";
                    }
                case "neoforge":
                    {
                        return "NeoForge";
                    }
                case "fabricloader":
                    {
                        return "Fabric Loader";
                    }
                case "quilt_loader":
                    {
                        return "Quilt Loader";
                    }
                case "liteloader":
                    {
                        return "LiteLoader";
                    }
                case "minecraft":
                    {
                        return "Minecraft";
                    }
                case "java":
                    {
                        return "Java";
                    }

                default:
                    {
                        return Id;
                    }
            }
        }
        public static global::System.String RequirementDisplay(TDependency Requirement, TContext Context)
        {
            if (((Requirement.Children.Count) > (0)))
                return (("(" + global::System.String.Join((Requirement.Join == "all") ? (" 且 ") : (" 或 "), Requirement.Children.Select(Child => RequirementDisplay(Child, Context)))) + ")");
            global::System.String Owner = RuntimeOwnerId(Requirement.Id, Context);
            if (string.IsNullOrEmpty(Owner))
                return Requirement.Display;
            if (IsInternalLoaderId(Requirement.Id))
                return (RuntimeDisplayName(Owner) + " 内置支持");
            return (RuntimeDisplayName(Owner) + Requirement.Display.Substring(((Requirement.Id) ?? ("")).Length));
        }

        private class LegacyCacheEntry
        {
            public global::System.Collections.Generic.List<TMod> Mods;
            public global::System.Collections.Generic.List<global::System.String> Notices;
        }
        private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, LegacyCacheEntry> LegacyCache = new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, LegacyCacheEntry>(global::System.StringComparer.OrdinalIgnoreCase);

        private static void ReadLegacyArchive(global::System.IO.Compression.ZipArchive Archive, global::System.String RootPath, global::System.String NestedPath, global::System.Boolean Enabled, global::System.Boolean Nested, global::System.Int32 Depth, TReport Report, ReadBudget Budget, global::System.Threading.CancellationToken Token)
        {
            // Forge 1.12 discovers class-level @Mod annotations, not modern TOML or every mcmod.info entry.
            // Rules: https://github.com/MinecraftForge/MinecraftForge/blob/1.12.x/src/main/java/net/minecraftforge/fml/common/FMLModContainer.java
            // Embedded dependencies: https://docs.minecraftforge.net/en/1.12.x/gettingstarted/dependencymanagement/
            global::System.String CacheKey = (global::System.String)null;
            global::System.Int32 BeforeMods = Report.Mods.Count;
            global::System.Int32 BeforeNotices = Report.Notices.Count;
            if (!Nested && !Budget.NoCache)
            {
                var SourceInfo = new global::System.IO.FileInfo(RootPath);
                CacheKey = ((((((((((((FilePathKey(RootPath) + "|") + SourceInfo.Length) + "|") + SourceInfo.LastWriteTimeUtc.Ticks) + "|") + SourceInfo.CreationTimeUtc.Ticks) + "|") + Enabled) + "|") + Report.Context.MinecraftVersion) + "|") + global::System.String.Join(";", Report.Context.LoaderVersions.OrderBy(Pair => Pair.Key).Select(Pair => ((Pair.Key + "=") + Pair.Value))));
                LegacyCacheEntry Saved = (LegacyCacheEntry)null;
                if (((!(Budget.ForceFresh)) && (LegacyCache.TryGetValue(CacheKey, out Saved))))
                {
                    Report.Mods.AddRange(Saved.Mods.Select(Item => Item.CopyWithEnabled(Enabled)));
                    foreach (var Notice in Saved.Notices)
                        Incomplete(Report, Notice);
                    return;
                }
            }
            var Descriptions = new global::System.Collections.Generic.List<global::Newtonsoft.Json.Linq.JObject>();
            var Info = Archive.GetEntry("mcmod.info");
            if (Info is not null)
            {
                try
                {
                    var Json = global::Newtonsoft.Json.Linq.JToken.Parse(ReadText(Info));
                    Descriptions.AddRange(ArrayValues((Json is JArray) ? (Json) : (Json["modList"])).OfType<JObject>());
                }
                catch (global::System.Exception ex) when (((ex is global::Newtonsoft.Json.JsonException) || (ex is global::System.IO.InvalidDataException)))
                {
                    if (Enabled)
                        Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：无法完整读取 mcmod.info，继续检查 @Mod 声明。");
                }
            }
            var Items = new global::System.Collections.Generic.List<TMod>();
            var Total = default(global::System.Int64);
            var Count = default(global::System.Int32);
            var Manifest = Archive.GetEntry("META-INF/MANIFEST.MF");
            var Headers = new global::System.Collections.Generic.Dictionary<global::System.String, global::System.String>(global::System.StringComparer.OrdinalIgnoreCase);
            if (Manifest is not null)
            {
                foreach (var Line in global::System.Text.RegularExpressions.Regex.Replace(ReadText(Manifest), @"\r*\n ", "").Split('\n'))
                {
                    if (global::System.String.IsNullOrWhiteSpace(Line))
                        break;
                    global::System.Int32 Colon = Line.IndexOf(':');
                    if (((Colon) > (0)))
                        Headers[Line.Substring(0, Colon)] = Line.Substring((Colon) + (1)).Trim();
                }
            }
            global::System.String PluginName = (global::System.String)null;
            if (Headers.TryGetValue("FMLCorePlugin", out PluginName))
            {
                try
                {
                    var Plugin = Archive.GetEntry(PluginName.Replace('.', '/') + ".class");
                    global::Newtonsoft.Json.Linq.JObject Core = (global::Newtonsoft.Json.Linq.JObject)null;
                    if ((Plugin is not null && ((Plugin.Length) <= (global::System.Int64)(MaxMetadataBytes))))
                    {
                        using (var Stream = Plugin.Open())
                        using (var ClassReader = new LegacyClassReader(Stream, Plugin.Length))
                        {
                            Core = ClassReader.ReadMod(1);
                        }
                    }
                    var PluginCore = Core;
                    global::System.String ContainerName = Text(Core?["container"]);
                    if (!string.IsNullOrEmpty(ContainerName))
                    {
                        var Container = Archive.GetEntry(ContainerName.Replace('.', '/') + ".class");
                        if ((Container is not null && ((Container.Length) <= (global::System.Int64)(MaxMetadataBytes))))
                        {
                            using (var Stream = Container.Open())
                            using (var ClassReader = new LegacyClassReader(Stream, Container.Length))
                            {
                                Core = ClassReader.ReadMod(2);
                            }
                            if (!string.IsNullOrEmpty(Text(Core?["modId"])))
                            {
                                var Item = NewMod(Core["modId"], Core["name"], Core["version"], RootPath, NestedPath + Container.FullName, "Forge (旧版核心插件)", Enabled, Nested);
                                Item.IsRuntimeCandidate = (Text(PluginCore?["uncertain"]) == "True");
                                Items.Add(Item);
                                if (((Item.IsRuntimeCandidate) && (Enabled)))
                                    Incomplete(Report, ((global::System.IO.Path.GetFileName(RootPath) + "：核心插件按运行环境决定是否注册 ") + Item.Id) + "，未将其作为可自动安装的缺失依赖。");
                            }
                        }
                    }
                }
                catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.FormatException)))
                {
                    if (Enabled)
                        Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：核心插件的运行时模组信息无法静态确认。");
                }
            }
            // Exact Forge ITypeDiscoverer.classFile rule: do not discover Scala Foo$.class, whitespace copies or __MACOSX files.
            foreach (var Entry in Archive.Entries.Where(Value => ((!(Value.FullName.StartsWith("__MACOSX", global::System.StringComparison.Ordinal))) && (global::System.Text.RegularExpressions.Regex.IsMatch(Value.FullName, @"\A[^\s\$]+(\$[^\s]+)?\.class\z")))))
            {
                Token.ThrowIfCancellationRequested();
                Count += 1;
                if (((((Count) > (50000)) || ((Entry.Length) > (((4L) * 1024L) * 1024L))) || ((Entry.Length) > ((((128L) * 1024L) * 1024L) - (Total)))))
                {
                    if (Enabled)
                        Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：旧版类声明扫描达到读取上限。");
                    break;
                }
                Total += Entry.Length;
                try
                {
                    global::Newtonsoft.Json.Linq.JObject Annotation;
                    using (var Stream = Entry.Open())
                    using (var ClassReader = new LegacyClassReader(Stream, Entry.Length))
                    {
                        Annotation = ClassReader.ReadMod();
                    }
                    if (Annotation is null)
                        continue;
                    global::System.String Id = Text(Annotation["modid"]);
                    var Description = Descriptions.FirstOrDefault(Value => ((Text(Value["modid"]) ?? "") == (Id ?? "")));
                    global::System.Boolean MetadataOverrides = (Annotation["useMetadata"] is null || (Text(Annotation["useMetadata"]).Equals("true", global::System.StringComparison.OrdinalIgnoreCase)));
                    global::System.String Name = Text(Annotation["name"]);
                    global::System.String Version = Text(Annotation["version"]);
                    if ((Description is not null && ((((MetadataOverrides) || string.IsNullOrEmpty(Name))))))
                        Name = Text(Description["name"]);
                    if (string.IsNullOrEmpty(Version))
                    {
                        var Properties = Archive.GetEntry("version.properties");
                        if (Properties is not null)
                        {
                            var Match = global::System.Text.RegularExpressions.Regex.Match(ReadText(Properties), (@"(?m)^\s*" + global::System.Text.RegularExpressions.Regex.Escape(Id)) + @"\.version\s*[=:]\s*([^\r\n\\]+)\s*$");
                            if (Match.Success)
                                Version = Match.Groups[1].Value.Trim();
                        }
                        if ((string.IsNullOrEmpty(Version) && Description is not null))
                            Version = Text(Description["version"]);
                    }
                    if (Version.Contains("${"))
                        Version = "";
                    var Item = NewMod(new global::Newtonsoft.Json.Linq.JValue(Id), new global::Newtonsoft.Json.Linq.JValue(Name), new global::Newtonsoft.Json.Linq.JValue(Version), RootPath, NestedPath + Entry.FullName, "Forge (旧版)", Enabled, Nested);
                    if (Description is not null)
                        Item.IconPath = ReadIconPath(Description["logoFile"]);
                    if (Text(Annotation["clientSideOnly"]).Equals("true", global::System.StringComparison.OrdinalIgnoreCase))
                        Item.Side = "client";
                    if (Text(Annotation["serverSideOnly"]).Equals("true", global::System.StringComparison.OrdinalIgnoreCase))
                        Item.Side = "server";
                    if (((Description is not null && (MetadataOverrides)) && (Text(Description["useDependencyInformation"]).Equals("true", global::System.StringComparison.OrdinalIgnoreCase))))
                    {
                        foreach (var Required in ArrayValues(Description["requiredMods"]))
                            AddLegacyDependency(Item, Text(Required), ModDependencyKind.Required);
                        foreach (var OptionalValue in ArrayValues(Description["dependencies"]).Concat(ArrayValues(Description["dependants"])))
                            AddLegacyDependency(Item, Text(OptionalValue), ModDependencyKind.Optional);
                    }
                    else
                    {
                        foreach (var Raw in Text(Annotation["dependencies"]).Split(';').Where(Value => !(global::System.String.IsNullOrWhiteSpace(Value))))
                        {
                            global::System.String[] Parts = Raw.Split(':');
                            global::System.String[] Instructions = Parts[0].Trim().Split('-');
                            if ((((Parts.Length) != (2)) || (Instructions.Any(Value => !(new[] { "required", "before", "after", "client", "server" }.Contains(Value))))))
                            {
                                if (Enabled)
                                    Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：无法完整解析旧版依赖声明。");
                                continue;
                            }
                            global::System.String Side = (Instructions.Contains("client")) ? ("client") : (((Instructions.Contains("server")) ? ("server") : ("*")));
                            AddLegacyDependency(Item, Parts[1], (Instructions.Contains("required")) ? (ModDependencyKind.Required) : (ModDependencyKind.Optional), Side);
                        }
                    }
                    global::System.String MinecraftRange = Text(Annotation["acceptedMinecraftVersions"]);
                    if (new[] { "[1.12]", "[1.12.1]", "[1.12,1.12.1]" }.Contains(MinecraftRange))
                        MinecraftRange = "[1.12,1.12.2]";
                    if (!string.IsNullOrEmpty(MinecraftRange))
                        AddLegacyDependency(Item, "minecraft@" + MinecraftRange, ModDependencyKind.Required);
                    Items.Add(Item);
                }
                catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.FormatException)))
                {
                    if (Enabled)
                        Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：部分旧版类声明无法读取。");
                }
            }
            if (((Items.Count) == (0)))
            {
                // Core plugins may construct containers at runtime. Keep descriptions for detail UI only, never as duplicate IDs.
                foreach (var Description in Descriptions)
                {
                    try
                    {
                        var Item = NewMod(Description["modid"], Description["name"], Description["version"], RootPath, NestedPath + "mcmod.info", "Forge (旧版)", Enabled, Nested);
                        Item.IsMetadataOnly = true;
                        Item.IconPath = ReadIconPath(Description["logoFile"]);
                        Items.Add(Item);
                    }
                    catch (global::System.FormatException )
                    {
                        // Unexpanded templates do not establish a loader ID.
                    }
                }
                if (Enabled)
                    Incomplete(Report, global::System.IO.Path.GetFileName(RootPath) + "：未找到静态 @Mod 声明，可能通过核心插件在运行时注册。");
            }
            Report.Mods.AddRange((global::System.Collections.Generic.IEnumerable<TMod>)Items);
            global::System.String Contained = (global::System.String)null;
            if (Headers.TryGetValue("ContainedDeps", out Contained))
            {
                var Paths = Contained.Split(new[] { ' ' }, global::System.StringSplitOptions.RemoveEmptyEntries).Select(Name => Archive.GetEntry("META-INF/libraries/" + Name) is not null ? ("META-INF/libraries/" + Name) : (Name));
                ReadNestedArchives(Archive, Paths, RootPath, NestedPath, Enabled, Depth, Report, Budget, Token);
            }
            if (CacheKey is not null)
            {
                if (((LegacyCache.Count) >= (1024)))
                    LegacyCache.Clear();
                LegacyCache[CacheKey] = new LegacyCacheEntry()
                {
                    Mods = Report.Mods.Skip(BeforeMods).Select(Item => Item.CopyWithEnabled(Enabled)).ToList(),
                    Notices = Report.Notices.Skip(BeforeNotices).ToList()
                };
            }
        }

        private static void AddLegacyDependency(TMod Item, global::System.String Raw, ModDependencyKind Kind, global::System.String Side = "*")
        {
            Raw = Raw.Trim();
            global::System.Int32 At = Raw.IndexOf('@');
            global::System.String Id = (((At) < (0)) ? (Raw) : (Raw.Substring(0, At))).Trim();
            if ((Id == "*"))
                return; // A load-order rule, not a dependency on a mod called *.
            Item.Dependencies.Add(new TDependency()
            {
                Id = Id,
                Versions = new global::Newtonsoft.Json.Linq.JValue(((At) < (0)) ? ("*") : (Raw.Substring((At) + (1)).Trim())),
                KindValue = Kind,
                Syntax = "maven",
                Side = Side,
                EnforceWhenPresent = (Kind == ModDependencyKind.Optional),
                IsUnknown = !(global::System.Text.RegularExpressions.Regex.IsMatch(Id, "^[A-Za-z0-9_.-]+$"))
            });
        }

        // Reads only class metadata; never loads, invokes or rewrites mod code. Limits also apply to malformed class attributes.
        private sealed class LegacyClassReader : global::System.IDisposable
        {
            private readonly global::System.IO.BinaryReader Reader;
            private global::System.Int64 Remaining;
            private global::System.Object[] Pool;
            private static readonly global::System.Text.UTF8Encoding StrictUtf8 = new global::System.Text.UTF8Encoding(false, true);
            private class PoolReference
            {
                public global::System.Int32 Kind;
                public global::System.Int32 First;
                public global::System.Int32 Second;
                public PoolReference(global::System.Int32 Kind, global::System.Int32 First, global::System.Int32 Second = 0)
                {
                    this.Kind = Kind;
                    this.First = First;
                    this.Second = Second;
                }
            }
            public LegacyClassReader(global::System.IO.Stream Stream, global::System.Int64 Length)
            {
                this.Reader = new global::System.IO.BinaryReader(new global::System.IO.BufferedStream(Stream, 8192), global::System.Text.Encoding.UTF8, false);
                this.Remaining = Length;
            }
            public void Dispose()
            {
                this.Reader.Dispose();
            }
            private global::System.Byte[] Bytes(global::System.Int32 Count)
            {
                if ((((Count) < (0)) || ((global::System.Int64)(Count) > (this.Remaining))))
                    throw new global::System.IO.InvalidDataException("无效的类声明长度");
                global::System.Byte[] Value = this.Reader.ReadBytes(Count);
                if (((Value.Length) != (Count)))
                    throw new global::System.IO.EndOfStreamException();
                this.Remaining -= (global::System.Int64)Count;
                return Value;
            }
            private global::System.Int32 U1()
            {
                if (((this.Remaining) < 1L))
                    throw new global::System.IO.EndOfStreamException();
                this.Remaining -= 1L;
                return (global::System.Int32)this.Reader.ReadByte();
            }
            private global::System.Int32 U2()
            {
                return (((this.U1()) * (256)) + (this.U1()));
            }
            private global::System.Int64 U4()
            {
                return (((((global::System.Int64)this.U2())) * 65536L) + (global::System.Int64)(this.U2()));
            }
            private void Skip(global::System.Int64 Count)
            {
                if ((((Count) < 0L) || ((Count) > (this.Remaining))))
                    throw new global::System.IO.InvalidDataException("无效的类声明长度");
                while (((Count) > 0L))
                {
                    global::System.Int32 Part = (global::System.Int32)global::System.Math.Min(8192L, Count);
                    this.Bytes(Part);
                    Count -= (global::System.Int64)Part;
                }
            }
            private global::System.Object Constant(global::System.Int32 Index)
            {
                if ((((Index) <= (0)) || ((Index) >= (this.Pool.Length))))
                    throw new global::System.IO.InvalidDataException("无效的常量索引");
                return this.Pool[Index];
            }
            private global::System.String Utf(global::System.Int32 Index)
            {
                global::System.String Value = this.Constant(Index) as string;
                if (Value is null)
                    throw new global::System.IO.InvalidDataException("无效的文本常量");
                return Value;
            }
            private static global::System.String ModifiedUtf8(global::System.Byte[] Value)
            {
                try
                {
                    return LegacyClassReader.StrictUtf8.GetString(Value);
                }
                catch (global::System.Text.DecoderFallbackException )
                {
                    // Java encodes NUL and UTF-16 surrogates differently from standard UTF-8.
                }
                var Result = new global::System.Text.StringBuilder();
                var Index = default(global::System.Int32);
                while (((Index) < (Value.Length)))
                {
                    global::System.Int32 First = (global::System.Int32)Value[Index];
                    global::System.Int32 Code;
                    global::System.Int32 Count;
                    if (((First) < (128)))
                    {
                        Code = First;
                        Count = 1;
                    }
                    else if ((((((First) & (0xE0)))) == (0xC0)))
                    {
                        Code = ((First) & (0x1F));
                        Count = 2;
                    }
                    else if ((((((First) & (0xF0)))) == (0xE0)))
                    {
                        Code = ((First) & (0xF));
                        Count = 3;
                    }
                    else
                    {
                        throw new global::System.IO.InvalidDataException("无效的 Java UTF-8 常量");
                    }
                    if ((((Index) + (Count)) > (Value.Length)))
                        throw new global::System.IO.InvalidDataException("截断的 Java UTF-8 常量");
                    for (global::System.Int32 Offset = 1, loopTo = (Count) - (1); Offset <= loopTo; Offset++)
                    {
                        global::System.Byte NextByte = Value[(Index) + (Offset)];
                        if ((((((global::System.Int32)(NextByte) & (0xC0)))) != (0x80)))
                            throw new global::System.IO.InvalidDataException("无效的 Java UTF-8 常量");
                        Code = (((Code) * (64)) + ((((global::System.Int32)(NextByte) & (0x3F)))));
                    }
                    Result.Append(global::Microsoft.VisualBasic.Strings.ChrW(Code));
                    Index += Count;
                }
                return Result.ToString();
            }
            private global::System.String ReferencedName(global::System.Int32 Index)
            {
                LegacyClassReader.PoolReference Value = this.Constant(Index) as LegacyClassReader.PoolReference;
                if ((Value is null || ((Value.Kind) != (7))))
                    return "";
                return this.Utf(Value.First);
            }
            public global::Newtonsoft.Json.Linq.JObject ReadMod(global::System.Int32 CoreMode = 0)
            {
                if (((this.U4()) != (0xCAFEBABEL)))
                    throw new global::System.IO.InvalidDataException("无效的 Java 类文件");
                this.Skip(4L);
                this.Pool = new global::System.Object[(this.U2())];
                global::System.Int32 Index = 1;
                while (((Index) < (this.Pool.Length)))
                {
                    global::System.Int32 Kind = this.U1();
                    switch (Kind)
                    {
                        case 1:
                            {
                                this.Pool[Index] = LegacyClassReader.ModifiedUtf8(this.Bytes(this.U2()));
                                break;
                            }
                        case 3:
                            {
                                this.Pool[Index] = this.U4();
                                break;
                            }
                        case 4:
                            {
                                this.Skip(4L);
                                break;
                            }
                        case 5:
                        case 6:
                            {
                                this.Skip(8L);
                                Index += 1;
                                break;
                            }
                        case 7:
                        case 8:
                        case 16:
                        case 19:
                        case 20:
                            {
                                this.Pool[Index] = new LegacyClassReader.PoolReference(Kind, this.U2());
                                break;
                            }
                        case 9:
                        case 10:
                        case 11:
                        case 12:
                        case 17:
                        case 18:
                            {
                                this.Pool[Index] = new LegacyClassReader.PoolReference(Kind, this.U2(), this.U2());
                                break;
                            }
                        case 15:
                            {
                                this.Skip(3L);
                                break;
                            }

                        default:
                            {
                                throw new global::System.IO.InvalidDataException("未知的 Java 常量类型");
                            }
                    }
                    Index += 1;
                }
                if ((((CoreMode) == (0)) && (!(Pool.Any(Value => ((global::System.Object.Equals(Value, "Lnet/minecraftforge/fml/common/Mod;")) || (global::System.Object.Equals(Value, "Lcpw/mods/fml/common/Mod;"))))))))
                    return (global::Newtonsoft.Json.Linq.JObject)null;
                this.Skip(4L);
                global::System.String Superclass = this.ReferencedName(this.U2());
                var Core = new global::Newtonsoft.Json.Linq.JObject();
                if ((((CoreMode) == (2)) && (!(new[] { "net/minecraftforge/fml/common/DummyModContainer", "cpw/mods/fml/common/DummyModContainer" }.Contains(Superclass)))))
                    return (global::Newtonsoft.Json.Linq.JObject)null;
                this.Skip((((global::System.Int64)this.U2())) * 2L);
                for (global::System.Int32 MemberKind = 1; MemberKind <= 2; MemberKind++)
                {
                    global::System.Int32 Members = this.U2();
                    for (global::System.Int32 Member = 1, loopTo = Members; Member <= loopTo; Member++)
                    {
                        this.Skip(2L);
                        global::System.String MemberName = this.Utf(this.U2());
                        global::System.String Descriptor = this.Utf(this.U2());
                        global::System.Int32 Attributes = this.U2();
                        for (global::System.Int32 Attribute = 1, loopTo1 = Attributes; Attribute <= loopTo1; Attribute++)
                        {
                            global::System.String Name = this.Utf(this.U2());
                            global::System.Int64 Length = this.U4();
                            global::System.Int64 Before = this.Remaining;
                            if (((((MemberKind) == (2)) && (Name == "Code")) && (((((((((CoreMode) == (1)) && (MemberName == "getModContainerClass")) && (Descriptor == "()Ljava/lang/String;")))) || ((((((CoreMode) == (2)) && (MemberName == "<init>")) && (Descriptor == "()V")))))))))
                            {
                                this.Skip(4L);
                                global::System.Int64 CodeLength = this.U4();
                                if (((CodeLength) > 65535L))
                                    throw new global::System.IO.InvalidDataException("类方法超过读取上限");
                                Core = this.ReadCoreCode(this.Bytes((global::System.Int32)CodeLength), CoreMode);
                            }
                            this.Skip((Length) - ((((Before) - (this.Remaining)))));
                        }
                    }
                }
                if (((CoreMode) != (0)))
                    return Core;
                global::System.Int32 ClassAttributes = this.U2();
                global::Newtonsoft.Json.Linq.JObject Result = (global::Newtonsoft.Json.Linq.JObject)null;
                for (global::System.Int32 Attribute = 1, loopTo2 = ClassAttributes; Attribute <= loopTo2; Attribute++)
                {
                    global::System.String Name = this.Utf(this.U2());
                    global::System.Int64 Length = this.U4();
                    global::System.Int64 Before = this.Remaining;
                    if ((Name == "RuntimeVisibleAnnotations"))
                    {
                        global::System.Int32 Count = this.U2();
                        for (global::System.Int32 Annotation = 1, loopTo3 = Count; Annotation <= loopTo3; Annotation++)
                        {
                            global::System.String Kind = (global::System.String)null;
                            var Value = this.ReadAnnotation(0, ref Kind);
                            if (((Kind == "Lnet/minecraftforge/fml/common/Mod;") || (Kind == "Lcpw/mods/fml/common/Mod;")))
                                Result = Value;
                        }
                    }
                    this.Skip((Length) - ((((Before) - (this.Remaining)))));
                }
                return Result;
            }
            private global::Newtonsoft.Json.Linq.JObject ReadCoreCode(global::System.Byte[] Code, global::System.Int32 Mode)
            {
                var Result = new global::Newtonsoft.Json.Linq.JObject();
                var Index = default(global::System.Int32);
                global::System.String PreviousString = (global::System.String)null;
                global::System.String PreviousClass = (global::System.String)null;
                global::System.Boolean Conditional = false;
                while (((Index) < (Code.Length)))
                {
                    global::System.Int32 Op = (global::System.Int32)Code[Index];
                    global::System.Int32 Operands;
                    switch (Op)
                    {
                        case var @case when 0 <= @case && @case <= 15:
                        case var case1 when 26 <= case1 && case1 <= 53:
                        case var case2 when 59 <= case2 && case2 <= 131:
                        case var case3 when 133 <= case3 && case3 <= 152:
                        case var case4 when 172 <= case4 && case4 <= 177:
                        case 190:
                        case 191:
                        case 194:
                        case 195:
                            {
                                Operands = 0;
                                break;
                            }
                        case 16:
                        case 18:
                        case var case5 when 21 <= case5 && case5 <= 25:
                        case var case6 when 54 <= case6 && case6 <= 58:
                        case 188:
                            {
                                Operands = 1;
                                break;
                            }
                        case 17:
                        case 19:
                        case 20:
                        case 132:
                        case var case7 when 178 <= case7 && case7 <= 184:
                        case 187:
                        case 189:
                        case 192:
                        case 193:
                            {
                                Operands = 2;
                                break;
                            }
                        case 185:
                        case 186:
                            {
                                Operands = 4;
                                break;
                            }
                        case 197:
                            {
                                Operands = 3;
                                break;
                            }
                        case var case8 when 153 <= case8 && case8 <= 168:
                        case 198:
                        case 199:
                            {
                                if (((Mode) != (1)))
                                    return new global::Newtonsoft.Json.Linq.JObject();
                                Conditional = true;
                                Operands = 2;
                                break;
                            }
                        case 200:
                        case 201:
                            {
                                if (((Mode) != (1)))
                                    return new global::Newtonsoft.Json.Linq.JObject();
                                Conditional = true;
                                Operands = 4;
                                break;
                            }

                        default:
                            {
                                return new global::Newtonsoft.Json.Linq.JObject(); // Branches, switches and dynamic/wide code are deliberately not inferred.
                            }
                    }
                    if ((((Index) + (Operands)) >= (Code.Length)))
                        throw new global::System.IO.InvalidDataException("截断的类方法");
                    global::System.Int32 Argument = ((Operands) == (0)) ? (0) : ((((Operands) == (1)) ? (((global::System.Int32)Code[(Index) + (1)])) : (((((global::System.Int32)Code[(Index) + (1)])) * (256)) + (global::System.Int32)(Code[(Index) + (2)]))));
                    global::System.String Literal = (global::System.String)null;
                    global::System.String ClassLiteral = (global::System.String)null;
                    if ((((Op) == (18)) || ((Op) == (19))))
                    {
                        LegacyClassReader.PoolReference Reference = this.Constant(Argument) as LegacyClassReader.PoolReference;
                        if ((Reference is not null && ((Reference.Kind) == (8))))
                            Literal = this.Utf(Reference.First);
                        if ((Reference is not null && ((Reference.Kind) == (7))))
                            ClassLiteral = this.Utf(Reference.First).Replace('/', '.');
                    }
                    if (((((Mode) == (1)) && ((Op) == (182))) && PreviousClass is not null))
                    {
                        LegacyClassReader.PoolReference Method = this.Constant(Argument) as LegacyClassReader.PoolReference;
                        if (((Method is not null && ((Method.Kind) == (10))) && (this.ReferencedName(Method.First) == "java/lang/Class")))
                        {
                            LegacyClassReader.PoolReference NameType = this.Constant(Method.Second) as LegacyClassReader.PoolReference;
                            if ((((NameType is not null && ((NameType.Kind) == (12))) && (this.Utf(NameType.First) == "getName")) && (this.Utf(NameType.Second) == "()Ljava/lang/String;")))
                                Literal = PreviousClass;
                        }
                    }
                    if (((((Mode) == (1)) && ((Op) == (176))) && PreviousString is not null))
                    {
                        Result["container"] = PreviousString;
                        Result["uncertain"] = Conditional;
                        return Result;
                    }
                    if ((((Mode) == (2)) && ((Op) == (181))))
                    {
                        LegacyClassReader.PoolReference Field = this.Constant(Argument) as LegacyClassReader.PoolReference;
                        if (((Field is not null && ((Field.Kind) == (9))) && (new[] { "net/minecraftforge/fml/common/ModMetadata", "cpw/mods/fml/common/ModMetadata" }.Contains(this.ReferencedName(Field.First)))))
                        {
                            LegacyClassReader.PoolReference NameType = this.Constant(Field.Second) as LegacyClassReader.PoolReference;
                            if (((NameType is not null && ((NameType.Kind) == (12))) && (this.Utf(NameType.Second) == "Ljava/lang/String;")))
                            {
                                global::System.String Name = this.Utf(NameType.First);
                                if (new[] { "modId", "name", "version" }.Contains(Name))
                                    Result[Name] = PreviousString is null ? (global::Newtonsoft.Json.Linq.JValue.CreateNull()) : (new global::Newtonsoft.Json.Linq.JValue(PreviousString));
                            }
                        }
                    }
                    PreviousString = Literal;
                    PreviousClass = ClassLiteral;
                    Index += ((1) + (Operands));
                }
                return ((Mode) == (2)) ? (Result) : (new global::Newtonsoft.Json.Linq.JObject());
            }
            private global::Newtonsoft.Json.Linq.JObject ReadAnnotation(global::System.Int32 Depth, ref global::System.String Kind)
            {
                if (((Depth) > (16)))
                    throw new global::System.IO.InvalidDataException("Java 注解嵌套过深");
                Kind = this.Utf(this.U2());
                var Result = new global::Newtonsoft.Json.Linq.JObject();
                global::System.Int32 Count = this.U2();
                for (global::System.Int32 Pair = 1, loopTo = Count; Pair <= loopTo; Pair++)
                {
                    global::System.String Name = this.Utf(this.U2());
                    Result[Name] = this.ReadElement((Depth) + (1));
                }
                return Result;
            }
            private global::Newtonsoft.Json.Linq.JToken ReadElement(global::System.Int32 Depth)
            {
                if (((Depth) > (16)))
                    throw new global::System.IO.InvalidDataException("Java 注解嵌套过深");
                switch (global::Microsoft.VisualBasic.Strings.ChrW(this.U1()))
                {
                    case 's':
                        {
                            return new global::Newtonsoft.Json.Linq.JValue(this.Utf(this.U2()));
                        }
                    case 'Z':
                        {
                            return new global::Newtonsoft.Json.Linq.JValue((global::System.Convert.ToInt64(this.Constant(this.U2()), global::System.Globalization.CultureInfo.InvariantCulture)) != 0L);
                        }
                    case 'B':
                    case 'C':
                    case 'D':
                    case 'F':
                    case 'I':
                    case 'J':
                    case 'S':
                        {
                            this.Skip(2L);
                            return global::Newtonsoft.Json.Linq.JValue.CreateNull();
                        }
                    case 'e':
                        {
                            this.Skip(4L);
                            return global::Newtonsoft.Json.Linq.JValue.CreateNull();
                        }
                    case 'c':
                        {
                            this.Skip(2L);
                            return global::Newtonsoft.Json.Linq.JValue.CreateNull();
                        }
                    case '@':
                        {
                            global::System.String Kind = (global::System.String)null;
                            return this.ReadAnnotation(Depth, ref Kind);
                        }
                    case '[':
                        {
                            var Result = new global::Newtonsoft.Json.Linq.JArray();
                            global::System.Int32 Count = this.U2();
                            for (global::System.Int32 Index = 1, loopTo = Count; Index <= loopTo; Index++)
                                Result.Add(this.ReadElement((Depth) + (1)));
                            return Result;
                        }

                    default:
                        {
                            throw new global::System.IO.InvalidDataException("未知的 Java 注解值");
                        }
                }
            }
        }

}
