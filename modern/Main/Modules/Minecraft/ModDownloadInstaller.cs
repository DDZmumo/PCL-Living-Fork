// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using MeloongCore;
using PCLCS;

namespace PCL;

internal static class ModDownloadInstaller {
    private static string Key(string path) => Path.GetFullPath(PathUtils.RemoveExtendedPrefix(path)).TrimEnd('\\','/');
    internal static bool MatchesVerifiedProject(string path, ResourceProjectModelData project, CancellationToken token) {
        var entry = LocalResourceLoaders.LocalResourceLoader.Output?.ToArray().FirstOrDefault(mod => mod is not null &&
            string.Equals(Key(mod.StorageKey),Key(path),StringComparison.OrdinalIgnoreCase));
        var cached = entry?.Project; string expected = entry?.ProjectVersion?.Hash;
        if (cached is null || cached.Platform != project.Platform || string.IsNullOrWhiteSpace(cached.Id) || string.IsNullOrWhiteSpace(project.Id) ||
            ResourceProjectKey.Get(cached.Platform,cached.Id) != ResourceProjectKey.Get(project.Platform,project.Id) || string.IsNullOrWhiteSpace(expected)) return false;
        using HashAlgorithm hash = expected.Length switch { 40 => SHA1.Create(),64 => SHA256.Create(),128 => SHA512.Create(),32 => MD5.Create(),_ => null };
        if (hash is null) return false;
        using var stream = File.OpenRead(path); byte[] buffer = new byte[65536]; int count;
        while ((count=stream.Read(buffer,0,buffer.Length))>0) { token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,count,buffer,0); }
        hash.TransformFinalBlock(Array.Empty<byte>(),0,0);
        return Convert.ToHexString(hash.Hash).Equals(expected,StringComparison.OrdinalIgnoreCase);
    }
    internal static IReadOnlyCollection<string> ReadIds(string path, ModDependencyInspector.InspectionContext context, CancellationToken token) {
        var report = ModDependencyInspector.ReadSingleArchive(path, context, token);
        if (report.MetadataIncomplete || report.Mods.Any(mod => mod.IncompatibleLoader)) return null;
        return report.Mods.Where(mod => !mod.IsNested && !mod.IsBuiltin && !mod.IsMetadataOnly && !string.IsNullOrWhiteSpace(mod.Id))
            .Select(mod => mod.Id).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static ModMinecraft.McInstance Owner(string target, ModMinecraft.McInstance preferred) {
        string root = Path.GetDirectoryName(Key(target));
        IEnumerable<ModMinecraft.McInstance> candidates = new[] { preferred, ModMinecraft.McInstanceSelected }
            .Concat(ModMinecraft.McInstanceList.Values.SelectMany(items => items)).Where(instance => instance is not null).Distinct();
        return candidates.FirstOrDefault(instance => instance.IsLoaded && string.Equals(Key(Path.Combine(instance.PathIndie,"mods")),root,StringComparison.OrdinalIgnoreCase));
    }
    private static ModDependencyInspector.InspectionContext Context(ModMinecraft.McInstance owner, ResourceVersion version) {
        if (owner is not null) return ModDependencyInspector.ContextForInstance(owner);
        var context = new ModDependencyInspector.InspectionContext { MinecraftVersion = version.GameVersions?.FirstOrDefault(), IgnoreJavaRequirements = true };
        if (version.ModLoaders.HasFlag(ModLoaders.Fabric)) context.LoaderVersions["fabricloader"] = "";
        if (version.ModLoaders.HasFlag(ModLoaders.Forge)) context.LoaderVersions["forge"] = "";
        if (version.ModLoaders.HasFlag(ModLoaders.NeoForge)) context.LoaderVersions["neoforge"] = "";
        context.LoaderKnown = context.LoaderVersions.Count > 0;
        return context;
    }
    internal static ModLoader.LoaderTask<string,string> CreateInstallTask(ResourceProjectModelData project, ResourceVersion version, string payload, string target, ModMinecraft.McInstance owner) {
        var context = Context(owner,version);
        return new ModLoader.LoaderTask<string,string>("检查并替换已有模组", task => {
            CancellationToken token = task.CreateCancellationToken();
            ModFileInstallResult result = null;
            Exception failure = null;
            // The worker owns the synchronous mutation and rollback. Canceling the UI task
            // signals its token, but cannot release the instance lease before rollback drains.
            var worker = new RedoableWorker(() => {
                result = new ModFileInstall((path,ct) => ReadIds(path,context,ct),(path,ct)=>MatchesVerifiedProject(path,project,ct)) {
                    CleanupWarning = error => Logger.Warn(error,"清理已完成的 Mod 安装事务副本失败")
                }.Install(payload,target,token,
                    (done,total) => task.Progress = total == 0 ? 0 : Math.Min(0.99, (double)done/total));
            });
            worker.Failed += error => failure = error;
            string lockedPath = owner?.PathVersion ?? Path.GetDirectoryName(Path.GetFullPath(target));
            bool acquired = false;
            try {
                while (!(acquired = ModMinecraft.McInstanceLock.TryAcquire(worker,lockedPath))) { token.ThrowIfCancellationRequested(); Thread.Sleep(50); }
                token.ThrowIfCancellationRequested(); worker.Start(); worker.WaitIfRunning();
                if (failure is not null) throw failure;
                token.ThrowIfCancellationRequested();
                if (!worker.LastSucceeded || result is null) throw new IOException("模组安装未完成，原文件已保留。");
                task.Output = result.Destination;
                ModBase.RunInUi(() => {
                    if(result.ReplacedFiles>0) ModMain.Hint($"已安装，并替换 {result.ReplacedFiles} 个同一模组的已有文件。",ModMain.HintType.Green);
                    if (owner is not null && ModMain.FrmInstanceMod is not null && ReferenceEquals(PageInstanceLeft.Instance,owner)) ModMain.FrmInstanceMod.LoaderRun(ModLoader.LoaderFolderRunType.ForceRun);
                });
            } finally { if (acquired) ModMinecraft.McInstanceLock.Release(worker,lockedPath); }
        }) { ProgressWeight = 1, InterruptOnCancel = false };
    }
    internal static void Start(ResourceProjectModelData project, ResourceVersion version, string target, ResourceDownloadReason reason, ModMinecraft.McInstance preferred) {
        if (!ModModpackMode.ConfirmModAction("安装 Mod")) return;
        var owner = Owner(target, preferred);
        string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PCLLivingFork","Cache","ModDownloads");
        string stage = Path.Combine(cacheRoot,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string payload = Path.Combine(stage,"download" + Path.GetExtension(target));
        var download = new ModNet.LoaderDownload("下载模组文件", new[] { version.ToNetFile(payload,(ResourceVersion.DownloadReason)(int)reason,null,ModLoaders.None) }) { ProgressWeight = 6, Block = true };
        var install = CreateInstallTask(project,version,payload,target,owner);
        var loader = new ModLoader.LoaderCombo<string>("Mod 下载："+project.TranslatedName+" ",new List<ModLoader.LoaderBase>{download,install}) {
            OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly
        };
        // This guard also drains an install still rolling back after the combo is canceled.
        var cleanup = new RedoableWorker(LoaderFileOperation.CreateWorkload(loader,""));
        cleanup.Stopped += () => {
            try {
                string full=Path.GetFullPath(stage);
                if (Path.GetDirectoryName(full)!=Path.GetFullPath(cacheRoot) || !Guid.TryParseExact(Path.GetFileName(full),"N",out _)) throw new IOException("无效的 Mod 下载暂存目录。");
                if (Directory.Exists(full) && (File.GetAttributes(full)&FileAttributes.ReparsePoint)==0) Directory.Delete(full,true);
            } catch(Exception error) { Logger.Warn(error,"清理 Mod 下载暂存失败"); }
        };
        cleanup.Start(); ModLoader.LoaderTaskbarAdd(loader);
        ModMain.FrmMain?.BtnExtraDownload.ShowRefresh(); ModMain.FrmMain?.BtnExtraDownload.Ribble();
    }
}
