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
        Loaded += (_, _) => Refresh(); Unloaded += (_, _) => revision++;
        BtnOnline.Click += (_, _) => Refresh();
        BtnLocal.Click += (_, _) => UpdatePack(false);
        BtnBackups.Click += (_, _) => { if (Current()) ModpackUpdateFlow.OpenBackups(displayed); };
        TextSearchVersion.TextChanged += (_, _) => RenderVersions();
        foreach (var button in PanReleaseTypes.Children.OfType<MyRadioButton>()) {
            button.LabText.Margin = new Thickness(-2, 0, 10, 0);
            button.Check += (sender, _) => {
                releaseType = Convert.ToInt32(((MyRadioButton)sender).Tag);
                RenderVersions();
            };
        }
    }
    private bool Current() => displayed is not null && ReferenceEquals(displayed, PageInstanceLeft.Instance);
    private void UpdatePack(bool online) { if (Current()) ModpackUpdateFlow.Start(displayed, online); }
    public async void Refresh() {
        int current = ++revision;
        displayed = PageInstanceLeft.Instance;
        var owner = displayed;
        record = null; catalog.Clear(); catalogError = null; catalogLoading = false; RenderVersions();
        BtnOnline.IsEnabled = BtnLocal.IsEnabled = BtnBackups.IsEnabled = false;
        if (owner is null) { LabVersion.Text = "未选择版本"; return; }
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
            LabName.Text = record?["name"]?.ToString() ?? owner.Name;
            LabVersion.Text = record?["version"]?.ToString() ?? "未记录整合包版本";
            var source = record?["source"] as JObject;
            LabSource.Text = (source?["platform"]?.ToString() ?? "本地整合包") + (record?["minecraft"] is null ? "" : " · Minecraft " + record["minecraft"]);
            BtnOnline.IsEnabled = source is not null;
            BtnLocal.IsEnabled = BtnBackups.IsEnabled = true;
            HintState.Theme = record is null ? MyHint.Themes.Yellow : MyHint.Themes.Blue;
            HintState.Text = record is null ? "尚未找到整合包记录。从文件更新时，可选择正确的原始整合包补全记录。" : "以整合包新版为准，保留兼容的个人修改；确认更新前可查看全部文件变更。";
            RenderVersions();
            if (source is not null) _ = LoadVersionsAsync((JObject)record.DeepClone(), current);
            });
        } catch (Exception error) {
            if (Dispatcher.HasShutdownStarted) return;
            try { await Dispatcher.InvokeAsync(() => {
            if (current != revision || !Current()) return;
            if (!recordRead) LabVersion.Text = "记录读取失败";
            HintState.Theme = MyHint.Themes.Yellow; HintState.Text = (recordRead ? "页面加载失败：" : "") + error.Message;
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
        string id = record?["source"]?["version"]?.ToString();
        return !string.IsNullOrEmpty(id) ? version.Id == id : version.Version == record?["version"]?.ToString();
    }
    private void RenderVersions() {
        if (PanVersions is null) return;
        PanVersions.Children.Clear();
        string query = TextSearchVersion.Text.Trim();
        bool Match(string text) => string.IsNullOrEmpty(query) || (text ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
        var matches = catalog.Where(version => Match((version.Display ?? version.Version ?? version.Id) + " " + version.Version + " " + string.Join("、", version.GameVersions ?? new()))).ToList();
        bool localRecord = record is not null && !catalog.Any(IsCurrent);
        bool localMatch = localRecord && Match(record["version"]?.ToString() + " " + record["minecraft"]?.ToString());
        RadioRelease.Text = "正式版（" + matches.Count(version => version.ReleaseTypeCode == 1) + "）";
        RadioBeta.Text = "测试版（" + matches.Count(version => version.ReleaseTypeCode == 2) + "）";
        RadioAlpha.Text = "早期测试版（" + matches.Count(version => version.ReleaseTypeCode == 3) + "）";
        RadioAll.Text = "全部（" + (matches.Count + (localMatch ? 1 : 0)) + "）";
        foreach (var version in matches.Where(version => releaseType == 0 || version.ReleaseTypeCode == releaseType).OrderByDescending(version => version.ReleaseDate)) {
            string title = version.Display ?? version.Version ?? version.Id;
            string games = string.Join("、", version.GameVersions ?? new());
            bool current = IsCurrent(version);
            var row = VersionRow(title, current ? "当前已安装版本" : version.ReleaseDate.ToString("yyyy-MM-dd") + " · Minecraft " + games, current);
            string type = version.ReleaseTypeCode is 1 or 2 or 3 ? version.ReleaseTypeDisplay : "发布类型未知";
            Badge(row, type, false);
            if (!current) row.Click += (_, _) => {
                if (!Current()) return;
                string info = "版本：" + title + "\r\nMinecraft：" + games + "\r\n发布类型：" + type;
                if (!version.DownloadAvailable) { ModMain.MyMsgBox(info + "\r\n此版本没有可用的下载文件。", "整合包版本信息"); return; }
                if (ModMain.MyMsgBox(info + "\r\n\r\n下载后会先预览文件变更和检查模组组合。", "更新整合包", "下载并预览", "取消") == 1 && Current())
                    ModpackUpdateFlow.StartSelected(displayed, version.Id);
            };
            PanVersions.Children.Add(row);
        }
        if (releaseType == 0 && localMatch)
            PanVersions.Children.Add(VersionRow(record["version"]?.ToString() ?? "当前版本", "本地整合包记录 · 发布类型未记录", true));
        string channel = releaseType switch { 1 => "正式版", 2 => "测试版", 3 => "早期测试版", _ => "全部版本" };
        LabListStatus.Text = catalogLoading ? "正在读取发布平台的版本列表…" : catalogError is not null ? "读取失败：" + catalogError :
            record?["source"] is null ? "没有在线来源记录，可从文件更新。" : "已获取 " + catalog.Count + " 个版本，" + channel + "显示 " + PanVersions.Children.Count + " 个；点击版本查看并选择更新。";
        if (!catalogLoading && localRecord && releaseType != 0)
            LabListStatus.Text += " 当前版本仅有本地记录，发布类型未记录，可在“全部”中查看。";
        LabListEmpty.Visibility = !catalogLoading && catalogError is null && PanVersions.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LabListEmpty.Text = query.Length == 0 ? "暂无" + (releaseType == 0 ? "可显示的版本" : channel) + "。" : "没有找到符合搜索条件的" + (releaseType == 0 ? "版本" : channel) + "。";
    }
    private MyListItem VersionRow(string title, string info, bool current) {
        // The optional instance artwork was already resolved and decoded above.
        // Do not ask each row to reopen the stored, possibly relative logo path.
        var row = new MyListItem { Title = title, Info = info, Height = 62, Type = current ? MyListItem.CheckType.None : MyListItem.CheckType.Clickable, Logo = ModBase.PathImage + "Blocks/Grass.png", Margin = new Thickness(3, 0, 3, 6) };
        row.ColumnLogo.MinWidth = 55;
        if (row.PathLogo is Canvas canvas && ImgPack.Source is not null) canvas.Background = new ImageBrush(ImgPack.Source) { Stretch = Stretch.Uniform };
        if (current) {
            var frame = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(39, 139, 84)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Tag = "current-pack-version" };
            Grid.SetColumnSpan(frame, 5); Grid.SetRowSpan(frame, 4); Panel.SetZIndex(frame, 1); row.Children.Add(frame);
            Badge(row, "当前版本", true);
        }
        return row;
    }
    private static void Badge(MyListItem row, string text, bool current) {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, current ? "ColorBrush1" : "ColorBrush3");
        var badge = new Border { Child = label, CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false };
        badge.SetResourceReference(Border.BackgroundProperty, "ColorBrush8");
        row.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
    }
}
