// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PCL
{
    // LF 50b028da5fcad2226d74d901a41d8c43a249bfb2 complete updater behavior.
    // The native-only N005 bundle identity and actual process path adaptations
    // remain in the original four-argument replacement/rollback transaction.
    public static partial class ModLauncherUpdate
    {
        private const string Repository = ModBase.LauncherRepository;
        private const string CompatibilityRepository = "DDZmumo/LivingPCL2-Re";
        private const string ReleasePage = ModBase.LauncherReleasesUrl + "/latest";
        internal const string CnbManifestUrl = ModBase.LauncherCnbUrl + "/-/git/raw/updates/latest.json";
        internal const string CnbPreviewManifestUrl = ModBase.LauncherCnbUrl + "/-/git/raw/updates/latest-preview.json";
        internal const string GitHubLatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
        internal const string GitHubReleaseListUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";
        private const string GitHubReleaseCatalogUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=100";
        private const string CnbReleaseCatalogUrl = ModBase.LauncherCnbUrl + "/-/releases?page_size=100";
        private static int CheckRunning;

        internal enum UpdateSource { Cnb = 0, GitHub = 1 }
        internal enum UpdateChannel { Stable = 0, StableAndPrerelease = 1 }

        internal static UpdateChannel PreferredUpdateChannel()
            => Settings.Get<int>("ToolLauncherUpdateChannel") == 1 ? UpdateChannel.StableAndPrerelease : UpdateChannel.Stable;
        internal static bool ChangeUpdateChannel(int Value, Func<bool> Confirm)
        {
            if (Value != 0 && Value != 1) return false;
            if (Value == (int)PreferredUpdateChannel()) return true;
            if (Value == 1 && (Confirm is null || !Confirm())) return false;
            Settings.Set("ToolLauncherUpdateChannel", Value);
            ResetDeferredUpdateIndicator();
            return true;
        }
        internal static UpdateSource PreferredUpdateSource()
            => Settings.Get<int>("ToolLauncherUpdateSource") == (int)UpdateSource.GitHub ? UpdateSource.GitHub : UpdateSource.Cnb;
        private static string UpdateSourceName(UpdateSource Source) => Source == UpdateSource.GitHub ? "GitHub" : "CNB";

        internal sealed class UpdatePromptView : StackPanel
        {
            internal double PreferredWidth => 570;
            internal readonly MyComboBox SourcePicker = new MyComboBox { Width = 240, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 0, 0) };
            internal UpdateSource SelectedSource => SourcePicker.SelectedIndex == 1 ? UpdateSource.GitHub : UpdateSource.Cnb;
            internal UpdatePromptView(string Tag, UpdateSource Source)
            {
                Children.Add(new TextBlock { Text = "当前版本：" + ModBase.VersionBaseName + Constants.vbCrLf + (Tag.Contains("-") ? "最新预发布版：" : "最新正式版：") + Tag, Margin = new Thickness(2, 0, 2, 18), LineHeight = 24 });
                SourcePicker.Items.Add(new MyComboBoxItem { Content = "CNB（国内）" });
                SourcePicker.Items.Add(new MyComboBoxItem { Content = "GitHub Releases" });
                SourcePicker.SelectedIndex = (int)Source;
                DockPanel Row = new DockPanel { Margin = new Thickness(2, 0, 2, 16) };
                Row.Children.Add(new TextBlock { Text = "本次下载源", VerticalAlignment = VerticalAlignment.Center });
                Row.Children.Add(SourcePicker); Children.Add(Row);
                TextBlock Note = new TextBlock { Text = "下载源选择仅本次生效。下载并校验完成后，启动器将自动替换并重新启动。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 0) };
                Note.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3"); Children.Add(Note);
            }
        }
        internal sealed class UpdateTransport
        {
            internal Func<string, JObject> ReadJson = Url => JObject.Parse(ModNet.NetRequestByClient(Url, Accept: "application/json", Timeout: 5000, RequireJson: true));
            internal Func<string, JArray> ReadReleaseList = Url => JArray.Parse(ModNet.NetRequestByClient(Url,
                Accept: Url.StartsWith(ModBase.LauncherCnbUrl + "/", StringComparison.Ordinal) ? "application/vnd.cnb.api+json" : "application/json",
                Timeout: 5000, RequireJson: true));
            internal Func<string, string> ReadText = Url => ModNet.NetRequestByClient(Url, Timeout: 8000);
            internal Action<string, string, FileChecker> Download = DownloadWithTask;
        }

        internal static bool NewerRelease(string Tag, string Current) => NewerReleaseForChannel(Tag, Current, UpdateChannel.Stable);
        private static bool ParseReleaseVersion(string Value, ref Version Core, ref string Preview)
        {
            var Match = System.Text.RegularExpressions.Regex.Match(Value ?? "", @"\A[vV]?(\d+\.\d+\.\d+(?:\.\d+)?)(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?\z");
            if (!Match.Success || !Version.TryParse(Match.Groups[1].Value, out Core)) return false;
            Core = new Version(Core.Major, Core.Minor, Math.Max(0, Core.Build), Math.Max(0, Core.Revision));
            Preview = Match.Groups[2].Value;
            return true;
        }
        internal static int CompareReleaseVersions(string First, string Second)
        {
            bool firstDev = First == "dev_edition", secondDev = Second == "dev_edition";
            if (firstDev || secondDev)
            {
                int baseline = CompareReleaseVersions(firstDev ? ModBase.ReleaseBaselineVersion : First, secondDev ? ModBase.ReleaseBaselineVersion : Second);
                return baseline != 0 ? baseline : firstDev == secondDev ? 0 : firstDev ? 1 : -1;
            }
            Version A = null, B = null; string AP = null, BP = null;
            if (!ParseReleaseVersion(First, ref A, ref AP) || !ParseReleaseVersion(Second, ref B, ref BP)) throw new ArgumentException("发布版本号无效。");
            int Result = A.CompareTo(B);
            if (Result != 0) return Result;
            if (AP == "" || BP == "") return AP == BP ? 0 : AP == "" ? 1 : -1;
            var AT = System.Text.RegularExpressions.Regex.Matches(AP.ToLowerInvariant(), "[0-9]+|[a-z]+").Cast<System.Text.RegularExpressions.Match>().Select(Item => Item.Value).ToArray();
            var BT = System.Text.RegularExpressions.Regex.Matches(BP.ToLowerInvariant(), "[0-9]+|[a-z]+").Cast<System.Text.RegularExpressions.Match>().Select(Item => Item.Value).ToArray();
            for (int Index = 0; Index <= Math.Min(AT.Length, BT.Length) - 1; Index++)
            {
                bool AN = char.IsDigit(AT[Index][0]), BN = char.IsDigit(BT[Index][0]);
                if (AN && BN)
                {
                    string AV = AT[Index].TrimStart('0'), BV = BT[Index].TrimStart('0');
                    Result = AV.Length.CompareTo(BV.Length);
                    if (Result == 0) Result = string.CompareOrdinal(AV, BV);
                }
                else if (AN != BN) Result = AN ? -1 : 1;
                else Result = string.CompareOrdinal(AT[Index], BT[Index]);
                if (Result != 0) return Result;
            }
            return AT.Length.CompareTo(BT.Length);
        }
        internal static bool NewerReleaseForChannel(string Tag, string Current, UpdateChannel Channel)
        {
            if (Current == "dev_edition") Current = ModBase.ReleaseBaselineVersion;
            Version Core = null; string Preview = null;
            if (!ParseReleaseVersion(Tag, ref Core, ref Preview) || (Preview != "" && Channel == UpdateChannel.Stable)) return false;
            if (!ParseReleaseVersion(Current, ref Core, ref Preview)) return false;
            return CompareReleaseVersions(Tag, Current) > 0;
        }
        internal static bool ValidAssetUrl(string Value)
        {
            if (!Uri.TryCreate(Value, UriKind.Absolute, out Uri Url) || Url.Scheme != "https" || Url.UserInfo != "" || !Url.IsDefaultPort || Url.Fragment != "") return false;
            if (Url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return Url.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase) || Url.AbsolutePath.StartsWith("/" + CompatibilityRepository + "/releases/download/", StringComparison.OrdinalIgnoreCase);
            return Url.Host.Equals("cnb.cool", StringComparison.OrdinalIgnoreCase) && Url.AbsolutePath.StartsWith("/" + ModBase.LauncherCnbRepository + "/-/releases/download/", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool ShouldPromptForUpdate(string Tag, string Current, string IgnoredTag)
            => NewerReleaseForChannel(Tag, Current, PreferredUpdateChannel()) && !string.Equals(Tag, IgnoredTag, StringComparison.Ordinal);
        internal static string IgnoreTagAfterChoice(string IgnoredTag, string Tag, int Choice)
        {
            if (Choice == 2) return Tag;
            if (Choice == 1 && string.Equals(IgnoredTag, Tag, StringComparison.Ordinal)) return "";
            return IgnoredTag;
        }
        internal static JObject ReleaseExecutable(JObject Info)
        {
            JArray Assets = Info["assets"] as JArray;
            if (Assets is null)
                throw new Exception("发布中没有下载附件。");
            var Executables = Assets.OfType<JObject>().Where(Asset =>
                {
                    string Name = Asset["name"]?.ToString();
                    return Name is not null && Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                }).ToList();
            var Candidates = Executables.Where(Asset => Asset["name"].ToString().StartsWith("PCL-Living-Fork-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (Candidates.Count == 0)
                Candidates = Executables.Where(Asset => Asset["name"].ToString().StartsWith("PCL Living Fork - ", StringComparison.OrdinalIgnoreCase)).ToList();
            if (Candidates.Count == 0)
                Candidates = Executables.Where(Asset => Asset["name"].ToString().StartsWith("LivingPCL2-Re-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (Candidates.Count != 1)
                throw new Exception("无法确定本版本的启动器 EXE。");
            if (!ValidAssetUrl(Candidates[0]["browser_download_url"]?.ToString()))
                throw new Exception("下载地址不属于本项目。");
            return Candidates[0];
        }

        internal static string ManifestHash(string Manifest, string Name)
        {
            foreach (var Line in Manifest.Replace(Constants.vbCr, "").Split(Constants.vbLf))
            {
                var Match = System.Text.RegularExpressions.Regex.Match(Line, @"\A([0-9a-fA-F]{64})\s+\*?(.+?)\s*\z");
                if (Match.Success && (Match.Groups[2].Value ?? "") == (Name ?? ""))
                    return Match.Groups[1].Value.ToLowerInvariant();
            }
            throw new Exception("发布附件缺少对应的 SHA256 校验值。");
        }


        private static JObject HashAsset(JObject Info)
        {
            JObject Asset = ((JArray)Info["assets"]).OfType<JObject>().SingleOrDefault(Item => Item["name"]?.ToString() == "SHA256SUMS.txt");
            if (Asset is null || !ValidAssetUrl(Asset["browser_download_url"]?.ToString())) throw new Exception("发布中没有可信的校验文件。");
            return Asset;
        }
        private static void ValidateReleaseMetadata(JObject Info, UpdateChannel Channel)
        {
            Version Parsed = null; string Preview = null;
            if (Info is null || !ParseReleaseVersion(Info["tag_name"]?.ToString(), ref Parsed, ref Preview)) throw new Exception("发布标签不是可识别的版本号。");
            foreach (string Field in new[] { "draft", "prerelease" })
                if (Info[Field] is null || Info[Field].Type != JTokenType.Boolean) throw new Exception("更新源的发布状态无效。");
            if ((bool)Info["draft"] || (Channel == UpdateChannel.Stable && (bool)Info["prerelease"])) throw new Exception("更新源尚无符合当前通道的发布。");
            if ((bool)Info["prerelease"] != (Preview != "")) throw new Exception("发布标记与版本号不一致。");
        }
        private static void ValidateReleaseInfo(JObject Info, bool Cnb, UpdateChannel Channel)
        {
            ValidateReleaseMetadata(Info, Channel);
            JObject Executable = ReleaseExecutable(Info), Manifest = HashAsset(Info);
            if (Executable["size"]?.Type != JTokenType.Integer || (long)Executable["size"] <= 0 || (long)Executable["size"] > 1024L * 1024 * 1024) throw new Exception("更新附件大小无效。");
            foreach (JObject Asset in new[] { Executable, Manifest })
            {
                Uri Url = new Uri(Asset["browser_download_url"].ToString()); string Name = Asset["name"].ToString();
                if (Path.GetFileName(Name) != Name || Url.Host != (Cnb ? "cnb.cool" : "github.com")) throw new Exception("更新附件与所选更新源不一致。");
                string Prefix = Cnb ? "/" + ModBase.LauncherCnbRepository + "/-/releases/download/" : "/" + Repository + "/releases/download/";
                string Expected = Prefix + Uri.EscapeDataString(Info["tag_name"].ToString()) + "/" + Uri.EscapeDataString(Name);
                if (!string.Equals(Url.AbsolutePath, Expected, StringComparison.Ordinal)) throw new Exception("更新附件不属于所选发布版本。");
            }
        }
        internal static JObject FindLatestRelease(string Current, UpdateTransport Transport = null)
            => FindLatestReleaseForSource(Current, PreferredUpdateSource(), Transport);
        internal static List<JObject> GetPageReleases(UpdateSource source, UpdateChannel channel, UpdateTransport transport = null)
        {
            transport ??= new UpdateTransport();
            Exception failure = null;
            foreach (UpdateSource selected in new[] { source, source == UpdateSource.Cnb ? UpdateSource.GitHub : UpdateSource.Cnb })
            {
                try
                {
                    var releases = new List<JObject>();
                    var seenPages = new HashSet<string>(StringComparer.Ordinal);
                    for (int page = 1; ; page++)
                    {
                        string endpoint = selected == UpdateSource.GitHub ? GitHubReleaseCatalogUrl : CnbReleaseCatalogUrl;
                        JArray entries = transport.ReadReleaseList(endpoint + "&page=" + page);
                        if (entries.Count == 0) break;
                        string fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entries.ToString(Newtonsoft.Json.Formatting.None))));
                        if (!seenPages.Add(fingerprint)) throw new Exception("更新源重复返回同一页，未能读取完整版本列表。");
                        foreach (JObject candidate in entries.OfType<JObject>())
                        {
                            try { ValidateReleaseMetadata(candidate, channel); releases.Add((JObject)candidate.DeepClone()); }
                            catch (Exception error) { error.ThrowIfCanceled(); }
                        }
                        if (entries.Count < 100) break;
                    }
                    return releases.GroupBy(value => value["tag_name"]!.ToString(), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
                }
                catch (Exception error) { error.ThrowIfCanceled(); failure = error; }
            }
            throw new Exception("未能读取完整版本列表，请稍后重试或切换更新源。", failure);
        }
        internal static bool CanInstallPageRelease(JObject release)
        {
            try
            {
                bool cnb = new Uri(ReleaseExecutable(release)["browser_download_url"]!.ToString()).Host == "cnb.cool";
                ValidateReleaseInfo(release, cnb, UpdateChannel.StableAndPrerelease);
                return true;
            }
            catch (Exception error) { error.ThrowIfCanceled(); return false; }
        }
        internal static void RequestSelectedUpdate(JObject release, UpdateSource source)
        {
            if (Interlocked.CompareExchange(ref CheckRunning, 1, 0) != 0) { ModMain.Hint("正在检查或准备更新，请稍候……"); return; }
            JObject selected = (JObject)release.DeepClone();
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool cnb = new Uri(ReleaseExecutable(selected)["browser_download_url"]!.ToString()).Host == "cnb.cool";
                    ValidateReleaseInfo(selected, cnb, PreferredUpdateChannel());
                    if (!NewerReleaseForChannel(selected["tag_name"]!.ToString(), ModBase.VersionBaseName, PreferredUpdateChannel())) { ModMain.Hint("当前没有更新的版本。"); return; }
                    UpdatePromptView view = null;
                    if (ModMain.MyMsgBoxConfirmContent("请选择本次更新的下载源。", "更新 PCL Living Fork", "下载并更新", () => { view = new UpdatePromptView(selected["tag_name"]!.ToString(), source); return view; }, "取消", IsWarn: false) != 1) return;
                    UpdateSource downloadSource = source;
                    ModBase.RunInUiWait(() => downloadSource = view.SelectedSource);
                    Settings.Set("ToolLauncherUpdateIgnoredTag", IgnoreTagAfterChoice(Settings.Get<string>("ToolLauncherUpdateIgnoredTag"), selected["tag_name"]!.ToString(), 1));
                    ClearDeferredUpdate();
                    DownloadAndUpdate(selected, downloadSource);
                }
                catch (Exception error) { UpdateFailure(PclLogger.ModernizeRuntimeMessage(error.GetDisplay(false))); }
                finally { Interlocked.Exchange(ref CheckRunning, 0); }
            });
        }
        internal static JObject FindLatestReleaseForSource(string Current, UpdateSource Source, UpdateTransport Transport = null)
            => FindLatestReleaseForChannel(Current, Source, PreferredUpdateChannel(), Transport);
        internal static JObject FindLatestReleaseForChannel(string Current, UpdateSource Source, UpdateChannel Channel, UpdateTransport Transport = null)
        {
            if (Transport is null) Transport = new UpdateTransport();
            JObject Best = null; Exception Failure = null;
            string[] Endpoints = Channel == UpdateChannel.Stable
                ? Source == UpdateSource.GitHub ? new[] { GitHubLatestUrl, CnbManifestUrl } : new[] { CnbManifestUrl, GitHubLatestUrl }
                : Source == UpdateSource.GitHub ? new[] { GitHubReleaseListUrl, CnbPreviewManifestUrl, GitHubLatestUrl, CnbManifestUrl } : new[] { CnbPreviewManifestUrl, GitHubReleaseListUrl, CnbManifestUrl, GitHubLatestUrl };
            foreach (string Endpoint in Endpoints)
            {
                try
                {
                    bool Cnb = Endpoint == CnbManifestUrl || Endpoint == CnbPreviewManifestUrl;
                    JObject Info = null;
                    if (Endpoint == GitHubReleaseListUrl)
                    {
                        foreach (JObject Candidate in Transport.ReadReleaseList(Endpoint).OfType<JObject>().Take(30))
                        {
                            try
                            {
                                ValidateReleaseInfo(Candidate, false, Channel);
                                if (Info is null || CompareReleaseVersions(Candidate["tag_name"].ToString(), Info["tag_name"].ToString()) > 0) Info = Candidate;
                            }
                            catch (Exception ex) { ex.ThrowIfCanceled(); }
                        }
                        if (Info is null) throw new Exception("GitHub 尚无符合当前通道的完整发布。");
                    }
                    else
                    {
                        Info = Transport.ReadJson(Endpoint);
                        ValidateReleaseInfo(Info, Cnb, Channel);
                    }
                    if (Best is null || CompareReleaseVersions(Info["tag_name"].ToString(), Best["tag_name"].ToString()) > 0) Best = Info;
                    if (Source == UpdateSource.GitHub && !Cnb) return Info;
                    if (NewerReleaseForChannel(Info["tag_name"].ToString(), Current, Channel)) return Info;
                }
                catch (Exception ex)
                {
                    ex.ThrowIfCanceled(); Failure = ex;
                    Logger.Info("更新源暂不可用：" + (Endpoint == CnbManifestUrl || Endpoint == CnbPreviewManifestUrl ? "CNB" : "GitHub"));
                }
            }
            if (Best is not null) return Best;
            throw new Exception("CNB 与 GitHub 更新源均未返回可用发布。", Failure);
        }
        internal static string DownloadUpdate(JObject Info, string Staged, UpdateTransport Transport = null)
        {
            if (Transport is null) Transport = new UpdateTransport();
            bool Cnb = new Uri(ReleaseExecutable(Info)["browser_download_url"].ToString()).Host == "cnb.cool";
            ValidateReleaseInfo(Info, Cnb, UpdateChannel.StableAndPrerelease);
            string ExpectedHash = null;
            try
            {
                ExpectedHash = ManifestHash(Transport.ReadText(HashAsset(Info)["browser_download_url"].ToString()), ReleaseExecutable(Info)["name"].ToString());
                DownloadCandidate(Info, Staged, ExpectedHash, Transport);
                return ExpectedHash;
            }
            catch (Exception ex)
            {
                ex.ThrowIfCanceled();
                Logger.Info((Cnb ? "CNB" : "GitHub") + " 下载未完成，尝试备用源的同版本附件。");
            }
            JObject Fallback = ReleaseForDownloadSource(Info, Cnb ? UpdateSource.GitHub : UpdateSource.Cnb, Transport);
            string FallbackHash = ManifestHash(Transport.ReadText(HashAsset(Fallback)["browser_download_url"].ToString()), ReleaseExecutable(Fallback)["name"].ToString());
            if (ExpectedHash is not null && !string.Equals(ExpectedHash, FallbackHash, StringComparison.OrdinalIgnoreCase)) throw new Exception("CNB 与 GitHub 的更新校验值不一致，已停止更新。");
            DownloadCandidate(Fallback, Staged, FallbackHash, Transport);
            return FallbackHash;
        }
        internal static JObject ReleaseForDownloadSource(JObject Info, UpdateSource Source, UpdateTransport Transport = null)
        {
            if (Transport is null) Transport = new UpdateTransport();
            bool Cnb = new Uri(ReleaseExecutable(Info)["browser_download_url"].ToString()).Host == "cnb.cool";
            ValidateReleaseInfo(Info, Cnb, UpdateChannel.StableAndPrerelease);
            if (Cnb == (Source == UpdateSource.Cnb)) return (JObject)Info.DeepClone();
            JObject Selected;
            if (Source == UpdateSource.GitHub)
                Selected = Transport.ReadJson("https://api.github.com/repos/" + Repository + "/releases/tags/" + Uri.EscapeDataString(Info["tag_name"].ToString()));
            else
            {
                Selected = (JObject)Info.DeepClone();
                foreach (JObject Asset in ((JArray)Selected["assets"]).OfType<JObject>())
                {
                    string Name = Asset["name"]?.ToString();
                    if (Name is null || Path.GetFileName(Name) != Name) throw new Exception("备用附件名称无效。");
                    Asset["browser_download_url"] = ModBase.LauncherCnbUrl + "/-/releases/download/" + Uri.EscapeDataString(Selected["tag_name"].ToString()) + "/" + Uri.EscapeDataString(Name);
                }
            }
            ValidateReleaseInfo(Selected, Source == UpdateSource.Cnb, UpdateChannel.StableAndPrerelease);
            if (Selected["tag_name"].ToString() != Info["tag_name"].ToString()) throw new Exception("所选源的发布版本与已确认版本不一致。");
            return Selected;
        }
        internal static ModLoader.LoaderCombo<int> CreateUpdateDownloadTask(string Url, string Target, FileChecker Check)
        {
            string Source = new Uri(Url).Host == "cnb.cool" ? "CNB" : "GitHub";
            string Version = System.Text.RegularExpressions.Regex.Match(Url, @"/(v[0-9.]+(?:-[0-9A-Za-z.-]+)?)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
            ModNet.LoaderDownload Download = new ModNet.LoaderDownload("下载启动器更新", new[] { new ModNet.NetFile(new[] { Url }, Target, Check) }) { ProgressWeight = 1 };
            return new ModLoader.LoaderCombo<int>("下载启动器更新：" + Version + "（" + Source + "）", new List<ModLoader.LoaderBase> { Download }) { OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly };
        }
        private static void DownloadWithTask(string Url, string Target, FileChecker Check)
        {
            var Work = CreateUpdateDownloadTask(Url, Target, Check);
            if (ModMain.FrmMain is null) Work.WaitForExit();
            else
            {
                ModBase.RunInUiWait(() =>
                {
                    Work.Start(1); ModLoader.LoaderTaskbarAdd(Work);
                    ModMain.FrmMain.BtnExtraDownload.ShowRefresh(); ModMain.FrmMain.BtnExtraDownload.Ribble();
                });
                Work.WaitForExit(StartIfNeeded: false);
            }
        }
        private static void DownloadCandidate(JObject Info, string Staged, string Hash, UpdateTransport Transport)
        {
            JObject Asset = ReleaseExecutable(Info);
            Transport.Download(Asset["browser_download_url"].ToString(), Staged, new FileChecker { Hash = Hash, ActualSize = (long)Asset["size"] });
            ValidateExecutable(Staged, Hash);
        }
        internal static void CheckForUpdates(bool Startup)
        {
            if (Interlocked.CompareExchange(ref CheckRunning, 1, 0) != 0)
            {
                if (!Startup) ModMain.Hint("正在检查更新，请稍候……");
                return;
            }
            UpdateSource Source = PreferredUpdateSource();
            if (!Startup) ModMain.Hint("正在通过 " + UpdateSourceName(Source) + " 检查 PCL Living Fork 更新……");
            ModBase.RunInNewThread(() =>
            {
                try
                {
                    JObject Info = FindLatestReleaseForSource(ModBase.VersionBaseName, Source);
                    string Tag = Info["tag_name"]?.ToString();
                    if (!NewerReleaseForChannel(Tag, ModBase.VersionBaseName, PreferredUpdateChannel()))
                    {
                        ClearDeferredUpdate();
                        if (!Startup) ModMain.MyMsgBox($"当前版本：{ModBase.VersionBaseName}{Constants.vbCrLf}当前通道最新版本：{Tag}{Constants.vbCrLf}{Constants.vbCrLf}当前没有更新的版本。", "PCL Living Fork 更新");
                        return;
                    }
                    string IgnoredTag = Settings.Get<string>("ToolLauncherUpdateIgnoredTag");
                    if (Startup && !ShouldPromptForUpdate(Tag, ModBase.VersionBaseName, IgnoredTag)) { RememberDeferredUpdate(Info, Source); return; }
                    ClearDeferredUpdate();
                    UpdatePromptView View = null; UpdateSource DownloadSource = Source;
                    int Choice = ModMain.MyMsgBoxConfirmContent("请选择本次更新的下载源。", "发现 PCL Living Fork 新版本", "下载并更新", () => { View = new UpdatePromptView(Tag, Source); return View; }, "暂不更新", IsWarn: false);
                    if (Choice == 1)
                    {
                        ModBase.RunInUiWait(() => DownloadSource = View.SelectedSource);
                        Settings.Set("ToolLauncherUpdateIgnoredTag", IgnoreTagAfterChoice(IgnoredTag, Tag, Choice));
                        ClearDeferredUpdate();
                        DownloadAndUpdate(Info, DownloadSource);
                    }
                    else if (Choice == 2) {
                        Settings.Set("ToolLauncherUpdateIgnoredTag", IgnoreTagAfterChoice(IgnoredTag, Tag, Choice));
                        RememberDeferredUpdate(Info, Source);
                    }
                }
                catch (Exception ex)
                {
                    if (Startup) Logger.Info("启动更新检查未完成，将在下次启动时重试。");
                    else UpdateFailure(ex.Message);
                }
                finally { Interlocked.Exchange(ref CheckRunning, 0); }
            }, "PCL Living Fork 更新检查", ThreadPriority.BelowNormal);
        }
        private static void DownloadAndUpdate(JObject Info, UpdateSource Source)
        {
            try
            {
                if (InstanceDeletionOperation.HasActiveOperations) throw new Exception("版本删除尚未完成，请稍后再更新启动器。");
                string Folder = Path.Combine(Paths.Base, "PCL", "Updates"); Directory.CreateDirectory(Folder);
                string Staged = Path.Combine(Folder, ModBase.LauncherExecutableName);
                ModMain.Hint("正在下载 PCL Living Fork 更新……");
                string ExpectedHash = DownloadUpdate(ReleaseForDownloadSource(Info, Source), Staged);
                if (ModNet.HasDownloadingTask()) throw new Exception("仍有下载任务正在进行。请等待任务结束后重试更新。");
                string Target = Process.GetCurrentProcess().MainModule.FileName;
                string Arguments = $"--livingpcl-update {Process.GetCurrentProcess().Id} \"{Path.GetFileName(Target)}\" {ExpectedHash}";
                using IDisposable ShutdownReservation = InstanceDeletionOperation.TryReserveShutdown() ?? throw new Exception("版本删除尚未完成，请稍后再更新启动器。");
                var Helper = Process.Start(new ProcessStartInfo(Staged, Arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Folder });
                if (Helper is null) throw new Exception("无法启动更新替换程序。");
                if (Helper.WaitForExit(500)) throw new Exception("更新替换程序未能正常启动。");
                ModBase.RunInUiWait(() => ModMain.FrmMain.EndProgram(SendWarning: false));
            }
            catch (OperationCanceledException) { ModMain.Hint("已取消启动器更新。"); }
            catch (ThreadInterruptedException) { ModMain.Hint("已取消启动器更新。"); }
            catch (Exception ex) { UpdateFailure(ex.Message); }
        }
        private static void UpdateFailure(string Reason)
        {
            bool GitHubFirst = PreferredUpdateSource() == UpdateSource.GitHub;
            switch (ModMain.MyMsgBox("更新未完成，现有启动器已保留。" + Constants.vbCrLf + Constants.vbCrLf + Reason, "PCL Living Fork 更新失败", GitHubFirst ? "GitHub 下载页" : "国内下载页", GitHubFirst ? "CNB 备用" : "GitHub 备用", "取消", IsWarn: true))
            {
                case 1: ModBase.OpenWebsite(GitHubFirst ? ReleasePage : ModBase.LauncherCnbUrl + "/-/releases"); break;
                case 2: ModBase.OpenWebsite(GitHubFirst ? ModBase.LauncherCnbUrl + "/-/releases" : ReleasePage); break;
            }
        }
        internal static void ValidateExecutable(string FileName, string ExpectedHash)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(ExpectedHash ?? "", @"\A[0-9a-fA-F]{64}\z"))
                throw new Exception("更新校验值无效。");
            using (var Stream = File.OpenRead(FileName))
            using (var Hash = SHA256.Create())
            {
                string Actual = BitConverter.ToString(Hash.ComputeHash(Stream)).Replace("-", "");
                if (!Actual.Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("更新文件 SHA256 校验失败。");
            }
            if (!IsLivingExecutable(FileName))
                throw new Exception("更新文件不是 PCL Living Fork 启动器。");
        }

        // 兼容新旧构建的程序集名称，且产品信息须属于 LivingPCL，避免覆盖官方程序。
        internal static bool IsLivingExecutable(string FileName)
        {
            try
            {
                // Preserve the original managed candidate validation exactly.
                string Identity = AssemblyName.GetAssemblyName(FileName).Name;
                return (Identity == "LivingPCL" || Identity == "Plain Craft Launcher 2") && new[] { "LivingPCL", ModBase.LauncherDisplayName }.Contains(FileVersionInfo.GetVersionInfo(FileName).ProductName, StringComparer.Ordinal);
            }
            catch (BadImageFormatException)
            {
                // A native apphost has no managed assembly header. Its bounded
                // v6 manifest and actual entry DLL must prove LF identity; a
                // VersionInfo caption alone cannot authorize replacement.
                return ModernBundleIdentity.IsLivingExecutable(FileName, ModBase.LauncherDisplayName);
            }
        }

        // 沿用官方 PCL 的流程：运行已下载的新 EXE，由新进程替换退出后的旧 EXE。
        internal static void ReplaceExecutable(string Staged, string Target, string ExpectedHash, bool Restart)
        {
            ValidateExecutable(Staged, ExpectedHash);
            string Temporary = Target + ".updating-" + Guid.NewGuid().ToString("N");
            string Backup = Target + ".update-backup-" + Guid.NewGuid().ToString("N");
            bool Replaced = false;
            bool HadTarget = File.Exists(Target);
            if (HadTarget && !IsLivingExecutable(Target))
                throw new Exception("更新目标不是 PCL Living Fork 启动器。");
            try
            {
                File.Copy(Staged, Temporary);
                ValidateExecutable(Temporary, ExpectedHash);
                if (HadTarget)
                {
                    File.Replace(Temporary, Target, Backup, true);
                }
                else
                {
                    File.Move(Temporary, Target);
                }
                Replaced = true;
                if (Restart)
                {
                    var Started = Process.Start(new ProcessStartInfo(Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Target) });
                    if (Started is null || Started.WaitForExit(3000))
                        throw new Exception("新版本启动失败。");
                }
                if (File.Exists(Backup))
                    File.Delete(Backup);
            }
            catch
            {
                if (Replaced)
                {
                    if (HadTarget && File.Exists(Backup))
                    {
                        File.Copy(Backup, Target, true);
                        File.Delete(Backup);
                    }
                    else if (!HadTarget && File.Exists(Target))
                    {
                        File.Delete(Target);
                    }
                }
                throw;
            }
            finally
            {
                if (File.Exists(Temporary))
                    File.Delete(Temporary);
            }
        }

        internal static bool HandleUpdateCommand(string[] Arguments)
        {
            if (Arguments.Length == 0 || Arguments[0] != "--livingpcl-update")
                return false;
            try
            {
                if (Arguments.Length != 4)
                    throw new Exception("更新参数不完整。");
                int ParentId;
                if (!int.TryParse(Arguments[1], out ParentId) || ParentId <= 0)
                    throw new Exception("更新进程号无效。");
                // Single-file EntryAssembly.Location is empty. Use the actual
                // running apphost, keeping the original four-argument protocol.
                string Staged = Environment.ProcessPath;
                if (string.IsNullOrEmpty(Staged)) throw new Exception("无法确定更新程序的位置。");
                string UpdateFolder = Path.GetDirectoryName(Staged);
                string PclFolder = Path.GetDirectoryName(UpdateFolder);
                if (!Path.GetFileName(UpdateFolder).Equals("Updates", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(PclFolder).Equals("PCL", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("更新程序位置无效。");
                string Name = Arguments[2];
                if ((Path.GetFileName(Name) ?? "") != (Name ?? "") || !Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("更新目标无效。");
                string Target = Path.Combine(Path.GetDirectoryName(PclFolder), Name);
                ValidateExecutable(Staged, Arguments[3]);
                Process Parent = null;
                try
                {
                    Parent = Process.GetProcessById(ParentId);
                }
                catch (ArgumentException)
                {
                }
                if (Parent is not null)
                {
                    if (!string.Equals(Parent.MainModule.FileName, Target, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("更新进程与目标不一致。");
                    if (!Parent.WaitForExit(20000))
                        throw new Exception("原启动器尚未退出。");
                }
                ReplaceExecutable(Staged, Target, Arguments[3], true);
            }
            catch (Exception ex)
            {
                if (MessageBox.Show("自动更新未完成，请从发布页手动更新。" + Constants.vbCrLf + ex.Message, "PCL Living Fork 更新失败", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true });
                }
            }
            Environment.Exit(0);
            return true;
        }
    }
}
