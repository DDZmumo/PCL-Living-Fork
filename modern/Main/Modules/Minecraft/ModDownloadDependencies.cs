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
using System.Windows.Media;
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
        bool missing = plan.Dependencies.Any(item => !item.Installed);
        int result = ModMain.MyMsgBoxConfirmContent(
            Summary(plan), "下载 Mod 与必需前置", !installable ? "仅下载此模组" : missing ? "安装并补全" : "下载此模组", () => CreateView(plan),
            CancelText: "取消", ThirdText: installable && missing ? "仅下载此模组" : "", IsWarn: false);
        return result == 1 ? installable ? ModDependencyDownloadChoice.WithDependencies : ModDependencyDownloadChoice.PrimaryOnly :
            result == 3 ? ModDependencyDownloadChoice.PrimaryOnly : ModDependencyDownloadChoice.Cancel;
    }

    internal static ModDownloadDependencyView CreateView(ModDownloadDependencyPlan<ResourceVersion> plan) {
        var panel = new ModDownloadDependencyView { Width = 490, MaxWidth = 490 };
        panel.Children.Add(Label(Summary(plan), 0, 10));
        panel.Children.Add(Label(string.IsNullOrEmpty(plan.MinecraftVersion) ? "尚未确定目标游戏环境" :
            "Minecraft " + plan.MinecraftVersion + " · " + plan.Loader, 0, 10));
        var rows = new StackPanel();
        if (plan.PrimaryProject is not null) rows.Children.Add(Card(plan.PrimaryProject, plan.PrimaryProject.TranslatedName,
            plan.PrimaryVersion?.Display ?? plan.PrimaryVersion?.FileName, "当前模组", false));
        foreach (var item in plan.Dependencies) {
            rows.Children.Add(Card(item.Project, item.Name, item.Failure ?? item.Version?.Display ?? item.Version?.FileName ?? "未找到兼容版本",
                item.Failure is not null ? "需手动核对" : item.Installed ? "已安装" : "将安装", item.Failure is not null));
        }
        foreach (string problem in plan.Problems) rows.Children.Add(Label(problem, 0, 10));
        panel.Children.Add(new MyScrollViewer { Content = rows, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var prompt = new MyCheckBox { Text = "以后下载时检查必需前置", Margin = new Thickness(0, 12, 0, 0), Checked = Settings.Get<bool>("ToolDownloadModDependencies") };
        prompt.Change += (_, user) => { if (user) Settings.Set("ToolDownloadModDependencies", prompt.Checked); };
        panel.Children.Add(prompt);
        return panel;
    }
    private static Border Card(ResourceProjectModelData project, string title, string version, string status, bool warning) {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string fallback = ModBase.PathImage + "Icons/NoIcon.png";
        var icon = new MyImage { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center,
            LoadingSource = fallback, FallbackSource = fallback, Source = project?.LogoUrl ?? fallback,
            Stretch = Stretch.Uniform, Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 5, 5) };
        content.Children.Add(icon);
        var description = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1"); description.Children.Add(name);
        var detail = Label(version, 4, 0); detail.FontSize = 12; description.Children.Add(detail);
        Grid.SetColumn(description, 1); content.Children.Add(description);
        var state = Label(status, 0, 0); state.VerticalAlignment = VerticalAlignment.Center;
        state.SetResourceReference(TextBlock.ForegroundProperty, warning ? "ColorBrushRedLight" : "ColorBrush3");
        Grid.SetColumn(state, 2); content.Children.Add(state);
        var card = new Border { Child = content, Padding = new Thickness(12), Margin = new Thickness(0, 0, 4, 10),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        card.SetResourceReference(Border.BackgroundProperty, "ColorBrushWhite");
        card.SetResourceReference(Border.BorderBrushProperty, warning ? "ColorBrushRedLight" : "ColorBrush5");
        return card;
    }
    private static string Summary(ModDownloadDependencyPlan<ResourceVersion> plan) => !plan.CanInstall
        ? "部分必需前置无法自动安装，可取消后手动核对，或仅下载当前模组。"
        : plan.Dependencies.Count == 0 ? "此版本未声明必需前置，可以直接下载。"
        : plan.Dependencies.All(item => item.Installed) ? "必需前置均已安装，只需下载当前模组。"
        : "此模组需要以下前置。确认后会补齐缺失项，已安装的前置会保留。";
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
