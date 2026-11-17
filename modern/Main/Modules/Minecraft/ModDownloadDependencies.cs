// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

internal enum ModDependencyDownloadChoice { Cancel, PrimaryOnly, WithDependencies }
internal sealed class ModDownloadDependencyView : StackPanel {
    internal double PreferredWidth => 590;
}

internal static class ModDownloadDependencies {
    internal static Func<ResourcePlatforms, string, CancellationToken, ResourceProjectModelData> ReadProject = (platform, id, token) => {
        token.ThrowIfCancellationRequested(); return ResourceProject.FromProjectId(id, platform);
    };
    internal static Func<ResourcePlatforms, string, CancellationToken, IReadOnlyList<ResourceVersion>> ReadVersions = (platform, id, token) => {
        token.ThrowIfCancellationRequested(); return ResourceVersion.FromProjectId(id, platform, false);
    };
    internal static Func<ResourcePlatforms, string, string, CancellationToken, ResourceVersion> ReadVersion = (platform, project, id, token) => {
        token.ThrowIfCancellationRequested();
        string url = platform == ResourcePlatforms.Modrinth ? "https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(id) :
            "https://api.curseforge.com/v1/mods/" + Uri.EscapeDataString(project ?? "") + "/files/" + Uri.EscapeDataString(id);
        JToken data = ModDownload.DlModRequest(url);
        token.ThrowIfCancellationRequested();
        return ResourceVersion.FromPlatformJson((JObject)(platform == ResourcePlatforms.CurseForge ? data["data"] : data), ResourceTypes.Mod);
    };
    internal static Func<ModDownloadDependencyPlan<ResourceVersion>, ModDependencyDownloadChoice> Select = null;

    internal static ModDownloadDependencyPlan<ResourceVersion> Prepare(ResourceProjectModelData project, ResourceVersion version,
        string target, ModMinecraft.McInstance owner, CancellationToken token) {
        string minecraft = "";
        ModLoaders loader = ModLoaders.None;
        if (owner is not null) {
            var context = ModDependencyInspector.ContextForInstance(owner);
            minecraft = context.MinecraftVersion;
            foreach (string name in context.LoaderVersions.Keys) {
                ModLoaders current = name switch { "fabricloader" => ModLoaders.Fabric,
                    "forge" => ModLoaders.Forge, "neoforge" => ModLoaders.NeoForge, _ => ModLoaders.None };
                if (version.ModLoaders.HasFlag(current) && current != ModLoaders.None) loader |= current;
            }
        } else {
            var versions = (version.ExactGameVersions ?? version.GameVersions).Distinct().ToArray();
            if (versions.Length == 1) minecraft = versions[0];
            loader = version.ModLoaders;
        }
        var installed = new InstalledHashes(Path.GetDirectoryName(Path.GetFullPath(target)));
        return new ModDownloadDependencyResolver<ResourceVersion> {
            Project = ReadProject, Versions = ReadVersions, ExactVersion = ReadVersion, Installed = installed.Contains
        }.Resolve(project, version, minecraft, loader, token);
    }

    internal static ModDependencyDownloadChoice Confirm(ModDownloadDependencyPlan<ResourceVersion> plan) {
        if (Select is not null) return Select(plan);
        bool installable = plan.CanInstall;
        int result = ModMain.MyMsgBoxConfirmContent(
            installable ? "此模组需要以下前置。确认后会一起下载并安装。" : "部分必需前置无法自动安装，可取消后手动核对，或仅下载当前模组。",
            "安装必需前置", installable ? "安装并补全" : "仅下载此模组", () => CreateView(plan),
            CancelText: "取消", ThirdText: installable ? "仅下载此模组" : "", IsWarn: false);
        return result == 1 ? installable ? ModDependencyDownloadChoice.WithDependencies : ModDependencyDownloadChoice.PrimaryOnly :
            result == 3 ? ModDependencyDownloadChoice.PrimaryOnly : ModDependencyDownloadChoice.Cancel;
    }

    internal static ModDownloadDependencyView CreateView(ModDownloadDependencyPlan<ResourceVersion> plan) {
        var panel = new ModDownloadDependencyView { Width = 490, MaxWidth = 490 };
        panel.Children.Add(Label(plan.CanInstall ? "此模组需要以下前置。可一起安装，或仅下载当前模组。" :
            "以下前置无法自动补全，可取消后手动核对，或仅下载当前模组。", 0, 10));
        panel.Children.Add(Label(string.IsNullOrEmpty(plan.MinecraftVersion) ? "尚未确定目标游戏环境" :
            "Minecraft " + plan.MinecraftVersion + " · " + plan.Loader, 0, 10));
        var rows = new StackPanel();
        foreach (var item in plan.Dependencies) {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var description = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var name = new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushTextSecondary");
            description.Children.Add(name);
            description.Children.Add(Label(item.Failure ?? item.Version?.Display ?? item.Version?.FileName ?? "未找到兼容版本", 3, 0));
            row.Children.Add(description);
            var state = Label(item.Failure is not null ? "需手动核对" : item.Installed ? "已安装" : "将安装", 0, 0);
            state.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(state, 1); row.Children.Add(state);
            rows.Children.Add(row);
        }
        foreach (string problem in plan.Problems) rows.Children.Add(Label(problem, 0, 10));
        panel.Children.Add(new MyScrollViewer { Content = rows, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var prompt = new MyCheckBox { Text = "以后下载时检查必需前置", Margin = new Thickness(0, 12, 0, 0), Checked = Settings.Get<bool>("ToolDownloadModDependencies") };
        prompt.Change += (_, user) => { if (user) Settings.Set("ToolDownloadModDependencies", prompt.Checked); };
        panel.Children.Add(prompt);
        return panel;
    }
    private static TextBlock Label(string text, double top, double bottom) {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, bottom) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3"); return label;
    }

    // Enabled files are identified by their bytes, including files renamed by
    // the user. Disabled files are proposed for installation and explicit enable.
    internal sealed class InstalledHashes {
        private readonly string folder;
        private readonly Dictionary<int, HashSet<string>> hashes = new();
        internal InstalledHashes(string folder) { this.folder = folder; }
        internal bool Contains(ResourceVersionModelData version, CancellationToken token) {
            string expected = version.Hash;
            if (string.IsNullOrWhiteSpace(expected) || expected.Length is not (32 or 40 or 64 or 128) || !Directory.Exists(folder)) return false;
            if (!hashes.TryGetValue(expected.Length, out var values)) {
                values = new(StringComparer.OrdinalIgnoreCase);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("请使用实际的模组文件夹检查前置。");
                int count = 0;
                foreach (string path in Directory.EnumerateFiles(folder, "*.jar")) {
                    token.ThrowIfCancellationRequested();
                    if (++count > 8192) throw new IOException("模组数量超过自动前置检查上限。");
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var hash = IncrementalHash.CreateHash(expected.Length switch { 32 => HashAlgorithmName.MD5, 40 => HashAlgorithmName.SHA1,
                        64 => HashAlgorithmName.SHA256, _ => HashAlgorithmName.SHA512 });
                    byte[] buffer = new byte[65536]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
                    values.Add(Convert.ToHexString(hash.GetHashAndReset()));
                }
                hashes.Add(expected.Length, values);
            }
            return values.Contains(expected);
        }
    }
}
