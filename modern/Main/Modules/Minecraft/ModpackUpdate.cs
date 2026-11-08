// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using global::System.IO.Compression;
using System.IO.Compression;
using System.Linq;
using System.Net;
using global::System.Net.Http;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using UpdateEngine = global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>;
using ThrottleDebounce;

namespace PCL
{

    internal class PackUpdateFile : global::PCLCS.PackUpdateFileData
    {
    }
    internal class PackUpdatePackage : global::PCLCS.PackUpdatePackageData<global::PCL.PackUpdateFile>
    {
    }
    internal class PackUpdateChange : global::PCLCS.PackUpdateChangeData<global::PCL.PackUpdateFile>
    {
    }
    internal class PackUpdatePlan : global::PCLCS.PackUpdatePlanData<global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange>
    {
    }

    // 原反射入口和 DTO 类型保留；事务、清单、冲突与恢复全部在 C#。
    internal sealed class ModpackUpdate
    {
        internal const global::System.String ManifestName = @"PCL\ModpackOrigin.json";
        internal static readonly global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile> Engine = new global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>(new global::PCLCS.PackUpdateHost<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>()
        {
            CreateFile = new Func<global::PCL.PackUpdateFile>(() => new global::PCL.PackUpdateFile()),
            CreatePackage = new Func<global::PCL.PackUpdatePackage>(() => new global::PCL.PackUpdatePackage()),
            CreateChange = new Func<global::PCL.PackUpdateChange>(() => new global::PCL.PackUpdateChange()),
            CreatePlan = new Func<global::PCL.PackUpdatePlan>(() => new global::PCL.PackUpdatePlan()),
            EnsureWithin = new Action<global::System.String, global::System.String>((Root, FileName) => global::PCL.InstanceResourceStore.EnsureWithin(Root, FileName)),
            AtomicWrite = new Action<global::System.String, global::System.Byte[]>((FileName, Bytes) => global::PCL.LocalGameData.AtomicWrite(FileName, Bytes, (global::System.String)null, false)),
            Request = new Func<global::System.String, global::System.Net.Http.HttpMethod, global::System.String, global::Newtonsoft.Json.Linq.JToken>((Url, Method, Body) => global::PCL.ModDownload.DlModRequest(Url, Method, Body, Body is null ? (global::System.String)(null) : ("application/json"))),
            ParseVersion = new Func<global::Newtonsoft.Json.Linq.JObject, global::PCLCS.ResourceTypes, global::PCLCS.ResourceVersionModelData>((Data, Kind) => global::PCL.ResourceVersion.FromPlatformJson(Data, Kind)),
            CreateDownload = new Func<global::System.Collections.Generic.List<global::System.String>, global::System.String, global::System.String, global::PCL.ModNet.NetFile>((Urls, FileName, Hash) => new global::PCL.ModNet.NetFile(Urls, FileName, new global::MeloongCore.FileChecker() { Hash = Hash }, true)),
            Download = global::PCL.ModpackUpdate.DownloadFiles,
            TempFolder = new Func<global::System.String>(() => global::PCL.ModMain.RequestTaskTempFolder()),
            InstanceGameRoot = new Func<global::System.String, global::System.String>(VersionFolder => new global::PCL.ModMinecraft.McInstance(VersionFolder).PathIndie),
            Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message))
        });
        private static readonly global::PCLCS.PackUpdateDownloads<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::MeloongCore.ProgressProvider> Downloads = new global::PCLCS.PackUpdateDownloads<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::MeloongCore.ProgressProvider>(new global::PCLCS.PackUpdateDownloadHost<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::MeloongCore.ProgressProvider>()
        {
            CreateLoader = new Func<global::System.String, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>, global::PCL.ModNet.LoaderDownload>((Name, Files) => new global::PCL.ModNet.LoaderDownload(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files)),
            LockState = new Func<global::PCL.ModNet.LoaderDownload, global::System.Object>(Loader => Loader.LockState),
            Cancel = new Action<global::PCL.ModNet.LoaderDownload>(Loader => Loader.Cancel()),
            Start = new Action<global::PCL.ModNet.LoaderDownload>(Loader => Loader.Start()),
            Wait = new Action<global::PCL.ModNet.LoaderDownload, global::System.Threading.CancellationToken, global::MeloongCore.ProgressProvider, global::System.Boolean>((Loader, Token, Progress, StartIfNeeded) => Loader.WaitForExit(c: Token, p: Progress, StartIfNeeded: StartIfNeeded)),
            SetProgress = new Action<global::MeloongCore.ProgressProvider, global::System.Double>((Progress, Value) => Progress.Set(Value))
        });
        internal static global::System.String RelativePath(global::System.String Value)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.RelativePath(Value);
        }
        internal static global::System.String Target(global::System.String Root, global::System.String Relative)
        {
            return global::PCL.ModpackUpdate.Engine.Target(Root, Relative);
        }
        internal static global::System.String Digest(global::System.String FileName, global::System.String Algorithm = "sha1")
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.Digest(FileName, Algorithm);
        }
        internal static string DigestForInspection(string fileName, string algorithm, CancellationToken token)
        {
            return UpdateEngine.Digest(fileName, algorithm, token);
        }
        private static global::System.Boolean Matches(global::System.String FileName, global::System.String Expected)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.Matches(FileName, Expected);
        }
        private static global::System.String ValidHash(global::System.String Algorithm, global::System.String Value)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.ValidHash(Algorithm, Value);
        }
        private static global::System.Boolean IsPrivate(global::System.String Relative)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.IsPrivate(Relative);
        }
        private static global::System.Boolean IsPersonal(global::System.String Relative)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.IsPersonal(Relative);
        }
        private static global::System.Boolean PersonalAreaExists(global::System.String Root, global::System.String Relative)
        {
            return global::PCL.ModpackUpdate.Engine.PersonalAreaExists(Root, Relative);
        }
        private static global::Newtonsoft.Json.Linq.JObject JsonEntry(global::System.IO.Compression.ZipArchiveEntry Entry)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.JsonEntry(Entry);
        }
        internal static global::PCL.PackUpdatePackage ReadPackage(global::System.String ArchivePath, global::System.String Stage, global::System.Threading.CancellationToken Token, global::System.Func<global::System.String, global::System.Net.Http.HttpMethod, global::System.String, global::Newtonsoft.Json.Linq.JToken> Request = null)
        {
            return global::PCL.ModpackUpdate.Engine.ReadPackage(ArchivePath, Stage, Token, Request);
        }
        private static void AddFile(global::PCL.PackUpdatePackage Package, global::PCL.PackUpdateFile Value)
        {
            global::PCL.ModpackUpdate.Engine.AddFile(Package, Value);
        }
        internal static global::Newtonsoft.Json.Linq.JObject Manifest(global::PCL.PackUpdatePackage Package)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.Manifest(Package);
        }
        internal static global::Newtonsoft.Json.Linq.JObject ReadManifest(global::System.String VersionFolder)
        {
            return global::PCL.ModpackUpdate.Engine.ReadManifest(VersionFolder);
        }
        internal static global::Newtonsoft.Json.Linq.JObject Detect(global::System.String VersionFolder)
        {
            return global::PCL.ModpackUpdate.Engine.Detect(VersionFolder);
        }
        internal static global::PCL.PackUpdatePlan Plan(global::System.String Root, global::Newtonsoft.Json.Linq.JObject Baseline, global::PCL.PackUpdatePackage Package)
        {
            return global::PCL.ModpackUpdate.Engine.Plan(Root, Baseline, Package);
        }
        internal static void DownloadChanges(global::PCL.PackUpdatePlan Plan, global::System.String Stage, global::System.Threading.CancellationToken Token, global::System.Action<global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>, global::System.Threading.CancellationToken> Download = null)
        {
            global::PCL.ModpackUpdate.Engine.DownloadChanges(Plan, Stage, Token, Download);
        }
        private static global::System.Collections.Generic.HashSet<global::System.String> ModIds(global::System.String FileName)
        {
            return global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.ModIds(FileName);
        }
        private static void PreserveModCollisions(global::PCL.PackUpdatePlan Plan)
        {
            global::PCL.ModpackUpdate.Engine.PreserveModCollisions(Plan);
        }
        internal static void DownloadFiles(global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::System.Threading.CancellationToken Token)
        {
            global::PCL.ModpackUpdate.DownloadFilesWithProgress(Files, Token, (global::MeloongCore.ProgressProvider)null);
        }
        internal static void DownloadFilesWithProgress(global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> Files, global::System.Threading.CancellationToken Token, global::MeloongCore.ProgressProvider Progress)
        {
            global::PCL.ModpackUpdate.Downloads.Run(Files, Token, Progress);
        }
        internal static global::System.String Commit(global::PCL.PackUpdatePlan Plan, global::System.String VersionFolder, global::System.Threading.CancellationToken Token, global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> ExtraFiles = null, global::System.Action<global::System.Int32> BeforeApply = null)
        {
            return global::PCL.ModpackUpdate.Engine.Commit(Plan, VersionFolder, Token, ExtraFiles, BeforeApply);
        }
        internal static global::System.String ApplyTransaction(global::System.String GameRoot, global::System.String VersionFolder, global::System.Collections.Generic.List<global::System.Tuple<global::System.String, global::System.String, global::System.String>> Operations, global::System.Threading.CancellationToken Token, global::System.Action<global::System.Int32> BeforeApply = null, global::System.String Box = null)
        {
            return global::PCL.ModpackUpdate.Engine.ApplyTransaction(GameRoot, VersionFolder, Operations, Token, BeforeApply, Box);
        }
        private static void ReplaceFrom(global::System.String Source, global::System.String Destination)
        {
            global::PCLCS.PackUpdateEngine<global::PCL.PackUpdateFile, global::PCL.PackUpdatePackage, global::PCL.PackUpdateChange, global::PCL.PackUpdatePlan, global::PCL.ModNet.NetFile>.ReplaceFrom(Source, Destination);
        }
        internal static string ApplyTransactionWithProgress(string GameRoot, string VersionFolder, List<Tuple<string, string, string>> Operations, CancellationToken Token, Action<double> Progress, Action<int> BeforeApply = null, string Box = null)
        {
            return Engine.ApplyTransactionWithProgress(GameRoot, VersionFolder, Operations, Token, Progress, BeforeApply, Box);
        }
        internal static global::System.Collections.Generic.List<global::System.String> Pending(global::System.String VersionFolder)
        {
            return global::PCL.ModpackUpdate.Engine.Pending(VersionFolder);
        }
        internal static void Recover(global::System.String VersionFolder, global::System.String GameRoot)
        {
            global::PCL.ModpackUpdate.Engine.Recover(VersionFolder, GameRoot);
        }
        internal static void RecordInstalled(global::System.String ArchivePath, global::System.String VersionFolder, global::Newtonsoft.Json.Linq.JObject Source = null)
        {
            global::PCL.ModpackUpdate.Engine.RecordInstalled(ArchivePath, VersionFolder, Source);
        }
    }
}
