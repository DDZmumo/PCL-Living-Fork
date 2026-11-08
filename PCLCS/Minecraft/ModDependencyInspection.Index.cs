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

 public static class Index {

        public const global::System.Int32 Schema = 4;
        public const global::System.Int64 MaxBytes = (((32L) * 1024L) * 1024L);
        public static string CacheDirectory { get; set; } = global::System.IO.Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), "PCLLivingFork", "Cache", "ModDependencyIndex");
        private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, global::System.Threading.SemaphoreSlim> Gates = new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, global::System.Threading.SemaphoreSlim>(global::System.StringComparer.Ordinal);
        private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, Index.MemoryEntry> Memory = new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, Index.MemoryEntry>(global::System.StringComparer.Ordinal);
        private static readonly global::Newtonsoft.Json.JsonSerializerSettings JsonOptions = new global::Newtonsoft.Json.JsonSerializerSettings()
        {
            PreserveReferencesHandling = global::Newtonsoft.Json.PreserveReferencesHandling.Objects,
            ReferenceLoopHandling = global::Newtonsoft.Json.ReferenceLoopHandling.Serialize,
            ObjectCreationHandling = global::Newtonsoft.Json.ObjectCreationHandling.Replace,
            ConstructorHandling = global::Newtonsoft.Json.ConstructorHandling.AllowNonPublicDefaultConstructor,
            TypeNameHandling = global::Newtonsoft.Json.TypeNameHandling.None,
            MetadataPropertyHandling = global::Newtonsoft.Json.MetadataPropertyHandling.ReadAhead,
            MaxDepth = 128
        };

        private class Document
        {
            public global::System.Int32 Version = Index.Schema;
            public global::System.String Root;
            public global::System.String Scope;
            public global::System.String Fingerprint;
            public global::System.String Generation;
            public TReport Tree;
        }
        private class MemoryEntry
        {
            public global::System.String Json;
            public global::System.String DiskStamp;
            public global::System.Boolean Persisted;
        }
        private class Inventory
        {
            public global::System.String Signature;
            public global::System.Collections.Generic.HashSet<global::System.String> Files = new global::System.Collections.Generic.HashSet<global::System.String>(global::System.StringComparer.OrdinalIgnoreCase);
        }

        private static global::System.String RootKey(global::System.String Root)
        {
            return FilePathKey(Root).TrimEnd(global::System.IO.Path.DirectorySeparatorChar, global::System.IO.Path.AltDirectorySeparatorChar);
        }
        private static global::System.String Digest(global::System.String Value)
        {
            using (var Hash = global::System.Security.Cryptography.SHA256.Create())
            {
                return global::System.BitConverter.ToString(Hash.ComputeHash(global::System.Text.Encoding.UTF8.GetBytes(Value))).Replace("-", "").ToLowerInvariant();
            }
        }
        private static global::System.String ParseScope(TContext Context)
        {
            return ((((((((Context.LoaderKnown + "|") + Context.MinecraftVersion) + "|") + Context.LegacyForgeVersionFolder) + "|") + IsLegacyForge(Context)) + "|") + global::System.String.Join(";", Context.LoaderVersions.Keys.OrderBy(Id => Id, global::System.StringComparer.Ordinal)));
        }
        public static global::System.String IndexPath(global::System.String Root, TContext Context)
        {
            return global::System.IO.Path.Combine(Index.CacheDirectory, Index.Digest((Index.RootKey(Root).ToUpperInvariant() + "|") + Index.ParseScope(Context)) + ".json");
        }
        public static void ClearMemory()
        {
            Index.Memory.Clear();
        }

        private static Index.Inventory Capture(global::System.String Root, TContext Context, global::System.Threading.CancellationToken Token)
        {
            var Result = new Index.Inventory();
            var Lines = new global::System.Collections.Generic.List<global::System.String>();
            var Folders = new global::System.Collections.Generic.List<global::System.String>() { Index.RootKey(Root) };
            if (((!(global::System.String.IsNullOrEmpty(Context.LegacyForgeVersionFolder))) && (global::System.Text.RegularExpressions.Regex.IsMatch(Context.LegacyForgeVersionFolder, "^[0-9.]+$"))))
            {
                Folders.Add(global::System.IO.Path.Combine(Index.RootKey(Root), Context.LegacyForgeVersionFolder));
            }
            foreach (var Folder in Folders)
            {
                Token.ThrowIfCancellationRequested();
                if (!(global::System.IO.Directory.Exists(Folder)))
                {
                    Lines.Add("missing|" + Folder.ToUpperInvariant());
                    continue;
                }
                var Attributes = global::System.IO.File.GetAttributes(Folder);
                Lines.Add((("directory|" + Folder.ToUpperInvariant()) + "|") + ((global::System.Int32)(Attributes & global::System.IO.FileAttributes.ReparsePoint)));
                if (((global::System.Int32)(((Attributes & global::System.IO.FileAttributes.ReparsePoint))) != (0)))
                    continue;
                foreach (var FullPath in global::System.IO.Directory.EnumerateFiles(Folder, "*", global::System.IO.SearchOption.TopDirectoryOnly).Where(IsModFile).OrderBy(Value => Value, global::System.StringComparer.OrdinalIgnoreCase))
                {
                    Token.ThrowIfCancellationRequested();
                    var Info = new global::System.IO.FileInfo(FullPath);
                    global::System.String Key = FilePathKey(FullPath);
                    Result.Files.Add(Key);
                    Lines.Add((((((((Key.ToUpperInvariant() + "|") + Info.Length) + "|") + Info.LastWriteTimeUtc.Ticks) + "|") + Info.CreationTimeUtc.Ticks) + "|") + ((global::System.Int32)(Info.Attributes & global::System.IO.FileAttributes.ReparsePoint)));
                }
            }
            Result.Signature = Index.Digest(global::System.String.Join(global::Microsoft.VisualBasic.Constants.vbLf, Lines));
            return Result;
        }
        private static global::System.String DiskStamp(global::System.String FileName)
        {
            try
            {
                var Info = new global::System.IO.FileInfo(FileName);
                return (Info.Exists) ? ((Info.Length + "|") + Info.LastWriteTimeUtc.Ticks) : ("missing");
            }
            catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
            {
                return "unavailable";
            }
        }
        private static Index.Document ReadDocument(global::System.String FileName)
        {
            try
            {
                global::System.String Stamp = Index.DiskStamp(FileName);
                Index.MemoryEntry Saved = (Index.MemoryEntry)null;
                global::System.String Content;
                global::System.Boolean Persisted;
                if (((Index.Memory.TryGetValue(FileName, out Saved)) && ((Saved.DiskStamp ?? "") == (Stamp ?? ""))))
                {
                    Content = Saved.Json;
                    Persisted = Saved.Persisted;
                }
                else
                {
                    var Info = new global::System.IO.FileInfo(FileName);
                    if ((((!(Info.Exists)) || ((Info.Length) > (Index.MaxBytes))) || ((global::System.Int32)(((Info.Attributes & global::System.IO.FileAttributes.ReparsePoint))) != (0))))
                        return (Index.Document)null;
                    using (var Stream = new global::System.IO.FileStream(FileName, global::System.IO.FileMode.Open, global::System.IO.FileAccess.Read, global::System.IO.FileShare.ReadWrite | global::System.IO.FileShare.Delete))
                    using (var Reader = new global::System.IO.StreamReader(Stream, global::System.Text.Encoding.UTF8, true))
                    {
                        var Builder = new global::System.Text.StringBuilder();
                        var Buffer = new global::System.Char[4096];
                        global::System.Int32 Count;
                        do
                        {
                            Count = Reader.Read(Buffer, 0, Buffer.Length);
                            if (((global::System.Int64)((Builder.Length) + (Count)) > (Index.MaxBytes)))
                                throw new global::System.IO.InvalidDataException("依赖索引超过读取上限");
                            Builder.Append(Buffer, 0, Count);
                        }
                        while (((Count) > (0)));
                        Content = Builder.ToString();
                    }
                    if (((global::System.Int64)(Content.Length) > (Index.MaxBytes)))
                        return (Index.Document)null;
                    if (((Index.Memory.Count) > (16)))
                        Index.Memory.Clear();
                    Persisted = true;
                    Index.Memory[FileName] = new Index.MemoryEntry() { Json = Content, DiskStamp = Stamp, Persisted = true };
                }
                var Result = global::Newtonsoft.Json.JsonConvert.DeserializeObject<Document>(Content, Index.JsonOptions);
                if (Result?.Tree is not null)
                    Result.Tree.IndexPersisted = Persisted;
                return Result;
            }
            catch (global::System.Exception ex) when ((((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)) || (ex is global::Newtonsoft.Json.JsonException)))
            {
                return (Index.Document)null;
            }
        }
        private static global::System.Boolean Valid(Index.Document Document, global::System.String Root, global::System.String Scope, Index.Inventory Files)
        {
            try
            {
                if (((((((Document is null || ((Document.Version) != (Index.Schema))) || (!(global::System.String.Equals(Document.Root, Index.RootKey(Root), global::System.StringComparison.OrdinalIgnoreCase)))) || ((Document.Scope ?? "") != (Scope ?? ""))) || ((Document.Fingerprint ?? "") != (Files.Signature ?? ""))) || Document.Tree?.Context is null) || (global::System.String.IsNullOrEmpty(Document.Generation))))
                    return false;
                var Tree = Document.Tree;
                if ((((((((((Tree.Mods is null || Tree.Builtins is null) || Tree.Edges is null) || Tree.Issues is null) || Tree.ReverseDependencies is null) || Tree.Notices is null) || Tree.OnlineFiles is null) || Tree.OnlineNotes is null) || Tree.Context.LoaderVersions is null) || Tree.Context.AdditionalBuiltins is null))
                    return false;
                if (Tree.Mods.Any(Item => ((((Item is null || (Item.IsBuiltin)) || Item.Dependencies is null) || Item.Provides is null) || (!(Files.Files.Contains(FilePathKey(Item.RootPath)))))))
                    return false;
                if (Tree.Builtins.Any(Item => (Item is null || (!(Item.IsBuiltin)))))
                    return false;
                foreach (var Item in Tree.Mods)
                {
                    foreach (var Requirement in Item.Dependencies)
                    {
                        if (!(Index.ValidRequirement(Requirement, new global::System.Collections.Generic.HashSet<TDependency>(), 0)))
                            return false;
                    }
                }
                if (Tree.OnlineFiles.Any(Item => (((Item is null || Item.Matches is null) || Item.Candidates is null) || (!(Files.Files.Contains(FilePathKey(Item.FullPath)))))))
                    return false;
                foreach (var Info in Tree.OnlineFiles)
                {
                    foreach (var Match in Info.Matches)
                    {
                        if ((Match is null || Match.Dependencies is null))
                            return false;
                        foreach (var Requirement in Match.Dependencies)
                        {
                            if (Requirement?.VerifiedLoaderIds is null || Requirement.VerifiedLoaderIds.Count > 512 ||
                                Requirement.VerifiedLoaderIds.Any(Id => string.IsNullOrWhiteSpace(Id) || Id.Length > 256 || Id.Any(char.IsControl))) return false;
                            if (((((Requirement is null || Requirement.ProjectPaths is null) || Requirement.MatchedPaths is null) || Requirement.EnabledPaths is null) || (Requirement.ProjectPaths.Concat(Requirement.MatchedPaths).Concat(Requirement.EnabledPaths).Any(Value => !(Files.Files.Contains(FilePathKey(Value)))))))
                                return false;
                        }
                    }
                }
                var Nodes = new global::System.Collections.Generic.HashSet<TMod>(Tree.Mods.Concat((global::System.Collections.Generic.IEnumerable<TMod>)Tree.Builtins));
                var Owners = new global::System.Collections.Generic.HashSet<TMod>((global::System.Collections.Generic.IEnumerable<TMod>)Tree.Mods);
                var KnownEdges = new global::System.Collections.Generic.HashSet<TEdge>((global::System.Collections.Generic.IEnumerable<TEdge>)Tree.Edges);
                if (Tree.Edges.Any(Edge => (((Edge is null || (!(Owners.Contains(Edge.Owner)))) || (!(Edge.Owner.Dependencies.Contains(Edge.Requirement)))) || (Edge.Providers.Any(Item => !(Nodes.Contains(Item)))))))
                    return false;
                if (Tree.Issues.Any(Issue => ((Issue is null || (!(Owners.Contains(Issue.Owner)))) || (Issue.RelatedMods.Any(Item => !(Nodes.Contains(Item)))))))
                    return false;
                return Tree.ReverseDependencies.Values.All(Edges => Edges.All(Edge => KnownEdges.Contains(Edge)));
            }
            catch (global::System.Exception ex) when (((((ex is global::System.ArgumentException) || (ex is global::System.InvalidOperationException)) || (ex is global::System.NullReferenceException)) || (ex is global::System.IO.IOException)))
            {
                return false;
            }
        }
        private static global::System.Boolean ValidRequirement(TDependency Value, global::System.Collections.Generic.HashSet<TDependency> Stack, global::System.Int32 Depth)
        {
            if ((((Value is null || Value.Children is null) || ((Depth) > (32))) || (!(Stack.Add(Value)))))
                return false;
            global::System.Boolean Result = ((Value.Children.All(Child => Index.ValidRequirement(Child, Stack, (Depth) + (1)))) && (((Value.Unless is null || (Index.ValidRequirement(Value.Unless, Stack, (Depth) + (1)))))));
            Stack.Remove(Value);
            return Result;
        }
        private static void Save(global::System.String FileName, Index.Document Value, global::System.Threading.CancellationToken Token)
        {
            Token.ThrowIfCancellationRequested();
            Value.Tree.IndexPersisted = false;
            global::System.String Content = global::Newtonsoft.Json.JsonConvert.SerializeObject(Value, global::Newtonsoft.Json.Formatting.None, Index.JsonOptions);
            if (((global::System.Int64)(global::System.Text.Encoding.UTF8.GetByteCount(Content)) > (Index.MaxBytes)))
                return;
            global::System.String Temporary = (((FileName + ".") + global::System.Guid.NewGuid().ToString("N")) + ".tmp");
            try
            {
                global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(FileName));
                if (((global::System.Int32)(((global::System.IO.File.GetAttributes(global::System.IO.Path.GetDirectoryName(FileName)) & global::System.IO.FileAttributes.ReparsePoint))) != (0)))
                    throw new global::System.IO.IOException("索引目录是链接目录");
                global::System.IO.File.WriteAllText(Temporary, Content, new global::System.Text.UTF8Encoding(false));
                Token.ThrowIfCancellationRequested();
                if (global::System.IO.File.Exists(FileName))
                {
                    if (((global::System.Int32)(((global::System.IO.File.GetAttributes(FileName) & global::System.IO.FileAttributes.ReparsePoint))) != (0)))
                        throw new global::System.IO.IOException("索引文件是链接文件");
                    global::System.IO.File.Replace(Temporary, FileName, (global::System.String)null);
                }
                else
                {
                    global::System.IO.File.Move(Temporary, FileName);
                }
                Value.Tree.IndexPersisted = true;
            }
            // The graph still works in memory when the persistent cache is temporarily unavailable.
            catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
            {
            }
            finally
            {
                if (global::System.IO.File.Exists(Temporary))
                {
                    try
                    {
                        global::System.IO.File.Delete(Temporary);
                    }
                    catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
                    {
                    }
                }
            }
            if (((Index.Memory.Count) > (16)))
                Index.Memory.Clear();
            Index.Memory[FileName] = new Index.MemoryEntry() { Json = Content, DiskStamp = Index.DiskStamp(FileName), Persisted = Value.Tree.IndexPersisted };
        }
        private static void Bind(TReport Tree, Index.Document Value, global::System.String FileName)
        {
            Tree.IndexFile = FileName;
            Tree.IndexFingerprint = Value.Fingerprint;
            Tree.IndexGeneration = Value.Generation;
        }

        public static TReport Inspect(global::System.String Root, TContext Context, global::System.Threading.CancellationToken Token, global::System.Boolean Force)
        {
            Token.ThrowIfCancellationRequested();
            global::System.String FileName = Index.IndexPath(Root, Context);
            var Gate = Index.Gates.GetOrAdd(FileName, Key => new global::System.Threading.SemaphoreSlim(1, 1));
            Gate.Wait(Token);
            try
            {
                using (var Lease = new Index.IndexLease(Index.Digest(FileName.ToUpperInvariant()), Token))
                {
                    for (global::System.Int32 Attempt = 0; Attempt <= 1; Attempt++)
                    {
                        Token.ThrowIfCancellationRequested();
                        Index.Inventory Before;
                        try
                        {
                            Before = Index.Capture(Root, Context, Token);
                        }
                        catch (global::System.Exception ex) when (((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)))
                        {
                            return ScanRaw(Root, Context, Token, Force);
                        }
                        var Saved = (Force) ? (Index.Document)(null) : (Index.ReadDocument(FileName));
                        if (Index.Valid(Saved, Root, Index.ParseScope(Context), Before))
                        {
                            var Result = Saved.Tree;
                            Result.IndexHit = true;
                            if (!(global::Newtonsoft.Json.Linq.JToken.DeepEquals(global::Newtonsoft.Json.Linq.JObject.FromObject(Context), global::Newtonsoft.Json.Linq.JObject.FromObject(Result.Context))))
                            {
                                Reevaluate(Result, Context, Token);
                                Result.IndexReevaluated = true;
                                Index.Save(FileName, Saved, Token);
                            }
                            if (((Index.Capture(Root, Context, Token).Signature ?? "") != (Before.Signature ?? "")))
                                continue;
                            Index.Bind(Result, Saved, FileName);
                            return Result;
                        }
                        var Tree = ScanRaw(Root, Context, Token, Force);
                        if (((Index.Capture(Root, Context, Token).Signature ?? "") != (Before.Signature ?? "")))
                            continue;
                        if (Tree.IndexReadFailed)
                            return Tree;
                        var Value = new Index.Document()
                        {
                            Root = Index.RootKey(Root),
                            Scope = Index.ParseScope(Context),
                            Fingerprint = Before.Signature,
                            Generation = global::System.Guid.NewGuid().ToString("N"),
                            Tree = Tree
                        };
                        Index.Save(FileName, Value, Token);
                        Index.Bind(Tree, Value, FileName);
                        return Tree;
                    }
                    throw new global::System.IO.IOException("模组文件在建立依赖索引期间发生变化，请稍后重试。");
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        public static void RememberOnline(global::System.String Root, TReport Tree, global::System.Threading.CancellationToken Token)
        {
            if (((Token.IsCancellationRequested) || (global::System.String.IsNullOrEmpty(Tree.IndexFile))))
                return;
            var Gate = Index.Gates.GetOrAdd(Tree.IndexFile, Key => new global::System.Threading.SemaphoreSlim(1, 1));
            global::System.Boolean Acquired = false;
            try
            {
                Gate.Wait(Token);
                Acquired = true;
                using (var Lease = new Index.IndexLease(Index.Digest(Tree.IndexFile.ToUpperInvariant()), Token))
                {
                    var Files = Index.Capture(Root, Tree.Context, Token);
                    var Current = Index.ReadDocument(Tree.IndexFile);
                    if ((((!(Index.Valid(Current, Root, Index.ParseScope(Tree.Context), Files))) || ((Current.Generation ?? "") != (Tree.IndexGeneration ?? ""))) || ((Files.Signature ?? "") != (Tree.IndexFingerprint ?? ""))))
                        return;
                    Current.Tree = Tree;
                    Index.Save(Tree.IndexFile, Current, Token);
                }
            }
            // An unsuccessful supplement never invalidates the previously committed local tree.
            catch (global::System.Exception ex) when ((((ex is global::System.IO.IOException) || (ex is global::System.UnauthorizedAccessException)) || (ex is global::System.OperationCanceledException)))
            {
            }
            finally
            {
                if (Acquired)
                    Gate.Release();
            }
        }

        private sealed class IndexLease : global::System.IDisposable
        {
            private readonly global::System.Threading.Mutex Mutex;
            private global::System.Boolean Owned;
            public IndexLease(global::System.String Key, global::System.Threading.CancellationToken Token)
            {
                this.Mutex = new global::System.Threading.Mutex(false, @"Local\PCLLF.DependencyIndex." + Key);
                try
                {
                    do
                    {
                        Token.ThrowIfCancellationRequested();
                        try
                        {
                            this.Owned = this.Mutex.WaitOne(50);
                        }
                        catch (global::System.Threading.AbandonedMutexException )
                        {
                            this.Owned = true;
                        }
                    }
                    while (!(this.Owned));
                }
                catch
                {
                    this.Mutex.Dispose();
                    throw;
                }
            }
            public void Dispose()
            {
                if (this.Owned)
                    this.Mutex.ReleaseMutex();
                this.Mutex.Dispose();
            }
        }

 }
}
