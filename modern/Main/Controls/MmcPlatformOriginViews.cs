using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF views using the existing pack-update dialog and LF controls; repository LICENCE applies.
internal sealed record MmcPlatformOriginInput(string OriginalZip, ResourcePlatforms Platform, string ProjectId, string VersionId);

internal sealed class MmcPlatformOriginStatusView : ModpackDialogView {
    internal MmcPlatformOriginStatusView(MmcInstanceOrigin origin, MmcPlatformOriginEvidence verified) : base("MMC 平台来源",
        verified is null ? "尚未核实平台来源。核实后可从同一项目选择在线更新。" : "已核对原整合包与平台发布文件，可选择同一项目的在线更新。", 1) {
        Header.Children.Add(StatusHint(verified is null ? "平台来源未核实" : "平台来源已核实", (int)(verified is null ? MyHint.Themes.Yellow : MyHint.Themes.Blue)));
        if (verified is not null) {
            Rows.Children.Add(Label(verified.Identity.Platform + " · 项目 " + verified.Identity.ProjectId, 14, true));
            Rows.Children.Add(Label("发布 ID：" + verified.Identity.VersionId + "\n发布文件：" + verified.SelectedFile.FileName,
                13, margin: new Thickness(0, 8, 0, 0)));
            Rows.Children.Add(DigestLabel("原包 SHA256", verified.Package.Sha256));
        } else if (origin.PlatformProjectIdentity is { } recorded) {
            Rows.Children.Add(Label("已记录：" + recorded.Platform + " · " + recorded.ProjectId + " · " + (recorded.VersionId ?? "未记录发布 ID"), 13));
            Rows.Children.Add(Label("这些记录尚未与原包和官方发布文件核对。", 12, margin: new Thickness(0, 8, 0, 0)));
        }
        Rows.Children.Add(Label("原整合包：" + origin.OriginalName, 12, margin: new Thickness(0, 16, 0, 0)));
    }
    internal static TextBlock DigestLabel(string caption, string digest) {
        var text = Label(caption + "：" + digest[..16] + "…", 12, color: "ColorBrushGray2", margin: new Thickness(0, 8, 0, 0));
        text.ToolTip = digest; return text;
    }
}

internal sealed class MmcPlatformOriginInputView : ModpackDialogView {
    internal readonly MyTextBox OriginalZip = new() { IsReadOnly = true, MinWidth = 350, Height = 30 };
    internal readonly MyComboBox Platform = new() { Height = 30, Width = 210, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly MyTextBox ProjectId = new() { MaxLength = 64, Height = 30, HintText = "平台稳定项目 ID" };
    internal readonly MyTextBox VersionId = new() { MaxLength = 64, Height = 30, HintText = "精确版本或文件 ID" };
    private readonly TextBlock releaseLabel = Label("发布 ID", 12);
    private readonly TextBlock validation = Label("请选择原 ZIP，并填写精确的平台项目与发布 ID。", 12, color: "ColorBrushGray2");
    internal MmcPlatformOriginInputView(MmcInstanceOrigin origin) : base("核实 MMC 平台来源",
        "使用最初导入的完整 ZIP，核对你明确选择的平台项目和发布文件。", 1) {
        var browse = new MyButton { Text = "选择原 ZIP", Width = 110, Height = 30, Margin = new Thickness(10, 0, 0, 0) };
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 12) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(OriginalZip);
        Rows.Children.Add(Label("原整合包 ZIP", 12)); Rows.Children.Add(row);
        browse.Click += (_, _) => {
            string path = MeloongCore.Wpf.Dialogs.SelectFile("选择最初导入的 MMC / Prism 整合包", false,
                filter: new[] { (new[] { "zip" }, "MMC / Prism 原整合包") }).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(path)) { OriginalZip.Text = path; OriginalZip.ToolTip = path; Refresh(); }
        };
        Platform.Items.Add(new MyComboBoxItem { Content = "Modrinth", Tag = ResourcePlatforms.Modrinth });
        Platform.Items.Add(new MyComboBoxItem { Content = "CurseForge", Tag = ResourcePlatforms.CurseForge });
        Platform.SelectedIndex = -1;
        Rows.Children.Add(Label("平台", 12)); Rows.Children.Add(Platform);
        Rows.Children.Add(Label("项目 ID", 12, margin: new Thickness(0, 12, 0, 5))); Rows.Children.Add(ProjectId);
        releaseLabel.Margin = new Thickness(0, 12, 0, 5); Rows.Children.Add(releaseLabel); Rows.Children.Add(VersionId);
        Rows.Children.Add(Label("请填写稳定项目 ID。Modrinth 填写精确版本 ID，CurseForge 填写文件 ID。下一步会列出实际发布文件，供你选择。", 12,
            color: "ColorBrushGray2", margin: new Thickness(0, 10, 0, 10)));
        Rows.Children.Add(Label("已解压的游戏目录不能代替原 ZIP。原包可改名，内容必须与当前实例记录相同。", 12, color: "ColorBrushGray2"));
        Rows.Children.Add(MmcPlatformOriginStatusView.DigestLabel("当前原包 SHA256", origin.PackageSha256));
        validation.Margin = new Thickness(0, 12, 0, 0); Rows.Children.Add(validation);
        Platform.SelectionChanged += (_, _) => { releaseLabel.Text = Platform.SelectedIndex == 1 ? "CurseForge 文件 ID" : "Modrinth 版本 ID"; Refresh(); };
        OriginalZip.TextChanged += (_, _) => Refresh(); ProjectId.TextChanged += (_, _) => Refresh(); VersionId.TextChanged += (_, _) => Refresh();
        Refresh();
    }
    internal MmcPlatformOriginInput ReadInput() {
        var input = Input(); MmcPlatformOriginCodec.ValidateIdentity(new(input.Platform.ToString(), input.ProjectId, input.VersionId));
        if (!File.Exists(input.OriginalZip)) throw new IOException("请选择存在的原整合包 ZIP。"); return input;
    }
    private MmcPlatformOriginInput Input() => new(OriginalZip.Text.Trim(), Platform.SelectedItem is MyComboBoxItem item ? (ResourcePlatforms)item.Tag : ResourcePlatforms.Any,
        ProjectId.Text.Trim(), VersionId.Text.Trim());
    private void Refresh() {
        bool complete = false;
        try { var input = Input(); MmcPlatformOriginCodec.ValidateIdentity(new(input.Platform.ToString(), input.ProjectId, input.VersionId)); complete = File.Exists(input.OriginalZip); }
        catch (ArgumentException) { }
        validation.Text = complete ? "下一步将从官方接口核对精确发布文件。" : "请选择原 ZIP，并填写平台、稳定项目 ID 和精确发布 ID。";
        SetCanConfirm(complete);
    }
}

internal sealed class MmcPlatformReleaseSelectView : ModpackDialogView {
    private const int PageSize = 40;
    private readonly ImmutableArray<MmcPlatformReleaseDescriptor> releases;
    private int page;
    internal MmcPlatformReleaseDescriptor Selected { get; private set; }
    internal readonly List<MyListItem> Selections = [];
    internal MmcPlatformReleaseSelectView(ImmutableArray<MmcPlatformReleaseDescriptor> releases, string title, string currentVersion) : base(title,
        "请选择一个完整发布文件。" + (string.IsNullOrEmpty(currentVersion) ? "" : " 当前发布 ID：" + currentVersion), 1) {
        this.releases = releases.IsDefault ? [] : releases;
        AddFilter("发布文件 · " + this.releases.Length, 0, true, _ => Render());
        SetCanConfirm(false); Render();
    }
    private void Render() {
        BeginFilter(0); Selections.Clear();
        foreach (var release in releases.Skip(page * PageSize).Take(PageSize)) {
            var item = new MyListItem { Title = release.File.FileName,
                Info = release.Platform + " · 项目 " + release.ProjectId + " · 发布 " + release.VersionId + " · " + Size(release.File.SizeBytes),
                Type = MyListItem.CheckType.RadioBox, Height = 68, PaddingLeft = 14, MinPaddingRight = 12,
                Margin = new Thickness(3, 0, 7, 8), Tag = release, Checked = release == Selected,
                ToolTip = "文件：" + release.File.FileName + "\n发布 ID：" + release.VersionId + "\nSHA1：" + (release.File.Sha1 ?? "未提供") + "\nSHA512：" + (release.File.Sha512 ?? "未提供") };
            item.Check += (_, _) => { Selected = release; SetCanConfirm(true); };
            Selections.Add(item); Rows.Children.Add(item);
        }
        if (releases.IsEmpty) Rows.Children.Add(StatusHint("该项目目前没有可选择的发布文件。", (int)MyHint.Themes.Yellow));
        if (releases.Length <= PageSize) return;
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = new MyButton { Text = "上一页", IsEnabled = page > 0, Width = 90, Height = 30, Margin = new Thickness(8) };
        var next = new MyButton { Text = "下一页", IsEnabled = (page + 1) * PageSize < releases.Length, Width = 90, Height = 30, Margin = new Thickness(8) };
        previous.Click += (_, _) => { page--; Render(); }; next.Click += (_, _) => { page++; Render(); };
        navigation.Children.Add(previous); navigation.Children.Add(Label((page + 1) + " / " + ((releases.Length + PageSize - 1) / PageSize), 12, margin: new Thickness(8, 15, 8, 0))); navigation.Children.Add(next);
        Rows.Children.Add(navigation);
    }
    internal static string Size(long bytes) => bytes >= 1024 * 1024 ? (bytes / (1024d * 1024)).ToString("0.##", CultureInfo.CurrentCulture) + " MiB" : bytes.ToString(CultureInfo.CurrentCulture) + " 字节";
}

internal sealed class MmcPlatformOriginConfirmView : ModpackDialogView {
    internal MmcPlatformOriginConfirmView(MmcPlatformOriginPreview preview) : base("确认已核实的平台来源", "原 ZIP 已与当前实例和所选平台发布文件逐项核对。", 1) {
        var release = preview.Selection.Release;
        Rows.Children.Add(Label(release.Platform + " · 项目 " + release.ProjectId, 14, true));
        Rows.Children.Add(Label("发布 ID：" + release.VersionId + "\n发布文件：" + release.File.FileName + "\n文件大小：" + MmcPlatformReleaseSelectView.Size(release.File.SizeBytes),
            13, margin: new Thickness(0, 10, 0, 0)));
        Rows.Children.Add(Label("所选原包：" + Path.GetFileName(preview.Selection.OriginalZip), 12, margin: new Thickness(0, 10, 0, 0)));
        Rows.Children.Add(MmcPlatformOriginStatusView.DigestLabel("原包 SHA256", preview.PackageSha256));
        Rows.Children.Add(StatusHint("保存后可从这个项目选择在线更新。本次会创建环境记录，组件、运行库和游戏文件保持当前状态。", (int)MyHint.Themes.Blue));
    }
}
