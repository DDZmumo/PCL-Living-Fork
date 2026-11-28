// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using System.Windows.Documents;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace PCL;

public partial class PageInstancePackUpdate : IRefreshable {
    private int revision;

    private ModMinecraft.McInstance displayed;
    private JObject record;
    private List<ResourceVersion> catalog = new();
    private string catalogError;
    private bool catalogLoading;
    private int releaseType = 1;
    internal Func<JObject, List<ResourceVersion>> ReadVersions = ModpackUpdateFlow.GetAvailableVersions;
    public PageInstancePackUpdate() {
        InitializeComponent();
        GlassDialogControls.Callout(HintState);
        AttachScroll(PanBack);
        GlassDialogControls.UpdateNotice(HintState, "选择新版本后，可以预览文件变更并检查模组兼容性。", DialogTone.Information);
        var placeholder = Theme.Note("搜索版本名称或 Minecraft 版本", 13);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(12, 0, 12, 0);
        placeholder.IsHitTestVisible = false;
        GlassPageResources.BindTextPlaceholder(TextSearchVersion, placeholder);
        SearchHost.Children.Add(placeholder);
        Loaded += (_, _) => Refresh(); Unloaded += (_, _) => { revision++; CancelMmcRead(); };
        BtnOnline.Click += (_, _) => Refresh();
        BtnLocal.Click += (_, _) => UpdatePack(false);
        BtnBackups.Click += (_, _) => { if (mmcCaptured) OpenMmcHistory(); else if (Current()) ModpackUpdateFlow.OpenBackups(displayed); };
        BtnOrigin.Click += (_, _) => BindMmcOrigin();
        TextSearchVersion.TextChanged += (_, _) => RenderVersions();
        foreach (var button in PanReleaseTypes.Children.OfType<RadioButton>()) {
            button.Checked += (_, _) => {
                releaseType = Convert.ToInt32(button.Tag);
                RenderVersions();
            };
        }
    }
    private bool Current() => mmcCaptured ? MmcOwnerCurrent() : displayed is not null && ReferenceEquals(displayed, LauncherInstanceContext.Instance);
    private void UpdatePack(bool online) { if (mmcCaptured) UpdateMmcLocal(); else if (Current()) ModpackUpdateFlow.Start(displayed, online); }
    public async void Refresh() {
        int current = ++revision;
        CancelMmcRead();
        if (mmcCaptured) { mmcRefreshCompletion = RefreshMmcAsync(current); await mmcRefreshCompletion; return; }
        displayed = LauncherInstanceContext.Instance;
        var owner = displayed;
        record = null; catalog.Clear(); catalogError = null; catalogLoading = false; RenderVersions();
        BtnOnline.IsEnabled = BtnLocal.IsEnabled = BtnBackups.IsEnabled = false;
        if (owner is null) { LabVersion.Text = "未选择版本"; return; }
        var state = PCLCS.InstanceSidebarContext.Observe(owner.PathVersion,
            owner.Config.ContextStamp.Kind == PCLCS.MmcConfigurationContextKind.Managed);
        if (state.IsMmcFamily) {
            CaptureMmcOwner(owner, state.InstanceId);
            mmcRefreshCompletion = RefreshMmcAsync(current); await mmcRefreshCompletion; return;
        }
        LabName.Text = owner.Name; LabVersion.Text = "正在读取…"; LabSource.Text = "";
        try {
            string logo = owner.Logo;
            if (!string.IsNullOrWhiteSpace(logo) && !Path.IsPathRooted(logo) && !Uri.TryCreate(logo, UriKind.Absolute, out _))
                logo = Path.GetFullPath(Path.Combine(owner.PathVersion, logo));
            ImgPack.Source = string.IsNullOrWhiteSpace(logo) ? null : (ImageSource)new MyBitmap(logo);
        } catch { ImgPack.Source = null; }
        if (ImgPack.Source is null) try { ImgPack.Source = (ImageSource)new MyBitmap(ModBase.PathImage + "Blocks/Grass.png"); } catch { }
        bool recordRead = false;
        try {
            JObject data = await Task.Run(() => ModpackUpdate.ReadManifest(owner.PathVersion)).ConfigureAwait(false);
            recordRead = true;
            await Dispatcher.InvokeAsync(() => {
            if (current != revision || !Current()) return;
            record = data;
            string recordedName = record?["name"]?.ToString();
            LabVersion.Text = record?["version"]?.ToString() ?? "未记录整合包版本";
            // Some platforms record the version as the name; show the instance's name then rather than the version twice.
            LabName.Text = string.IsNullOrWhiteSpace(recordedName) || recordedName == LabVersion.Text ? owner.Name : recordedName;
            var source = record?["source"] as JObject;
            LabSource.Text = (source?["platform"]?.ToString() ?? "本地整合包") + (record?["minecraft"] is null ? "" : " · Minecraft " + record["minecraft"]);
            BtnOnline.IsEnabled = source is not null;
            BtnLocal.IsEnabled = BtnBackups.IsEnabled = true;
            GlassDialogControls.UpdateNotice(HintState, record is null
                ? "尚未找到整合包记录。从文件更新时，可选择正确的原始整合包补全记录。"
                : "以整合包新版为准，保留兼容的个人修改；确认更新前可查看全部文件变更。",
                record is null ? DialogTone.Warning : DialogTone.Information);
            RenderVersions();
            if (source is not null) _ = LoadVersionsAsync((JObject)record.DeepClone(), current);
            });
        } catch (Exception error) {
            if (Dispatcher.HasShutdownStarted) return;
            try { await Dispatcher.InvokeAsync(() => {
            if (current != revision || !Current()) return;
            if (!recordRead) LabVersion.Text = "记录读取失败";
            GlassDialogControls.UpdateNotice(HintState, (recordRead ? "页面加载失败：" : "") + error.Message, DialogTone.Warning);
            BtnBackups.IsEnabled = true;
            }); } catch (OperationCanceledException) { }
        }
    }
    private async Task LoadVersionsAsync(JObject baseline, int current) {
        catalogLoading = true; BtnOnline.IsEnabled = false; RenderVersions();
        try {
            List<ResourceVersion> versions = await Task.Run(() => ReadVersions(baseline)).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => { if (current == revision && Current()) { catalog = versions; catalogError = null; } });
        } catch (Exception error) {
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => { if (current == revision) catalogError = error.Message; });
        } finally {
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (current == revision && Current()) { catalogLoading = false; BtnOnline.IsEnabled = true; RenderVersions(); }
            });
        }
    }
    private bool IsCurrent(ResourceVersion version) {
        if (mmcCaptured) return IsCurrentMmcRelease(version);
        string id = record?["source"]?["version"]?.ToString();
        return !string.IsNullOrEmpty(id) ? version.Id == id : version.Version == record?["version"]?.ToString();
    }
    private void RenderVersions() {
        if (PanVersions is null) return;
        PanVersions.Children.Clear();
        string query = TextSearchVersion.Text.Trim();
        bool Match(string text) => string.IsNullOrEmpty(query) || (text ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
        var matches = catalog.Where(version => Match((version.Display ?? version.Version ?? version.Id) + " " + version.Version + " " + string.Join("、", version.GameVersions ?? new()) + MmcReleaseSearchText(version))).ToList();
        bool localRecord = (mmcCaptured ? mmcReady : record is not null) && !catalog.Any(IsCurrent);
        string currentVersion = mmcCaptured ? mmcVersion : record?["version"]?.ToString();
        string currentMinecraft = mmcCaptured ? mmcMinecraft : record?["minecraft"]?.ToString();
        bool localMatch = localRecord && Match(currentVersion + " " + currentMinecraft);
        RadioRelease.Content = "正式版（" + matches.Count(version => version.ReleaseTypeCode == 1) + "）";
        RadioBeta.Content = "测试版（" + matches.Count(version => version.ReleaseTypeCode == 2) + "）";
        RadioAlpha.Content = "早期测试版（" + matches.Count(version => version.ReleaseTypeCode == 3) + "）";
        RadioAll.Content = "全部（" + (matches.Count + (localMatch ? 1 : 0)) + "）";
        foreach (var version in matches.Where(version => releaseType == 0 || version.ReleaseTypeCode == releaseType).OrderByDescending(version => version.ReleaseDate)) {
            string title = version.Display ?? version.Version ?? version.Id;
            string games = string.Join("、", version.GameVersions ?? new());
            bool current = IsCurrent(version);
            string type = version.ReleaseTypeCode is 1 or 2 or 3 ? version.ReleaseTypeDisplay : "发布类型未知";
            var row = VersionRow(title, mmcCaptured ? MmcReleaseInfo(version, current) : current
                ? "当前已安装版本" : version.ReleaseDate.ToString("yyyy-MM-dd") + " · Minecraft " + games, current, type);
            int rowRevision = revision;
            if (!current) row.Click += (_, _) => {
                if (mmcCaptured) { ChooseMmcVersion(version, rowRevision); return; }
                if (!Current()) return;
                string info = "版本：" + title + "\r\nMinecraft：" + games + "\r\n发布类型：" + type;
                if (!version.DownloadAvailable) { ModMain.MyMsgBox(info + "\r\n此版本没有可用的下载文件。", "整合包版本信息"); return; }
                if (ModMain.MyMsgBox(info + "\r\n\r\n下载后会先预览文件变更和检查模组组合。", "更新整合包", "下载并预览", "取消") == 1 && Current())
                    ModpackUpdateFlow.StartSelected(displayed, version.Id);
            };
            PanVersions.Children.Add(row);
        }
        if (releaseType == 0 && localMatch)
            PanVersions.Children.Add(VersionRow(currentVersion ?? "当前版本", (mmcCaptured ? "MMC 原整合包记录" : "本地整合包记录") + " · 发布类型未记录", true));
        string channel = releaseType switch { 1 => "正式版", 2 => "测试版", 3 => "早期测试版", _ => "全部版本" };
        LabListStatus.Text = mmcCaptured ? MmcListStatus(channel) : catalogLoading ? "正在读取发布平台的版本列表…" : catalogError is not null ? "读取失败：" + catalogError :
            record?["source"] is null ? "没有在线来源记录，可从文件更新。" : "已获取 " + catalog.Count + " 个版本，" + channel + "显示 " + PanVersions.Children.Count + " 个；点击版本查看并选择更新。";
        if (!catalogLoading && localRecord && releaseType != 0)
            LabListStatus.Text += " 当前版本仅有本地记录，发布类型未记录，可在“全部”中查看。";
        LabListEmpty.Visibility = !catalogLoading && catalogError is null && PanVersions.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LabListEmpty.Text = query.Length == 0 ? "暂无" + (releaseType == 0 ? "可显示的版本" : channel) + "。" : "没有找到符合搜索条件的" + (releaseType == 0 ? "版本" : channel) + "。";
    }
    private Button VersionRow(string title, string info, bool current, string releaseType = null) {
        var label = Theme.Text(title);
        label.FontWeight = FontWeights.SemiBold;
        var badges = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        if (current) badges.Children.Add(Badge("当前版本", current: true));
        if (releaseType is not null) badges.Children.Add(Badge(releaseType, current: false));
        var fields = new StackPanel { Children = { label, Theme.Note(info, 12), badges } };
        var icon = new Image { Source = ImgPack.Source, Width = 34, Height = 34, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var layout = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        layout.Children.Add(icon);
        layout.Children.Add(fields);
        var row = new Button { Content = layout, Tag = current ? "current-pack-version" : null,
            Style = Theme.Style(current ? "GlassPresentationBandRow" : "GlassPresentationListAction"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 58, Margin = new Thickness(0, 0, 0, 3),
            IsHitTestVisible = !current, Focusable = !current,
            ToolTip = current ? null : info };
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        System.Windows.Automation.AutomationProperties.SetHelpText(row, info);
        return row;
    }

    private Border Badge(string text, bool current) {
        var label = Theme.Note(text, 11);
        label.FontWeight = FontWeights.SemiBold;
        var badge = new Border { Child = label, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, current ? "GlassPresentationSelected" : "GlassPresentationField");
        return badge;
    }
}
