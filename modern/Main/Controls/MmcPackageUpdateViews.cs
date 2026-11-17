using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

// Original LF view using the same controls as the existing pack-update dialog.
internal sealed class MmcPackageUpdatePreviewView : ModpackDialogView {
    private const int PageSize = 40;
    private readonly MmcInstancePackageUpdatePreview preview;
    private readonly bool editable;
    private readonly Dictionary<string, bool> components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MmcPackageFileDecision> files = new(StringComparer.Ordinal);
    private readonly MmcPackageFileDifference[] orderedFiles;
    private readonly MyCheckBox acknowledge;
    private bool allowUnverified;
    private int section, page;

    internal MmcPackageUpdatePreviewView(MmcInstancePackageUpdatePreview preview, bool editable) : base(
        preview.PlatformRelease?.File.FileName ?? Path.GetFileName(preview.Selection.ArchivePath),
        preview.PlatformRelease is { } release ? release.Platform + " · 项目 " + release.ProjectId + " · 发布 " + release.VersionId : "组件、运行环境与作者文件均来自所选本地 ZIP。", 4) {
        this.preview = preview; this.editable = editable; allowUnverified = preview.Choices.AllowUnverifiedDependencies;
        foreach (var difference in preview.Components.Where(value => value.HasUserOverride && value.KeepUserOverride.HasValue))
            components[difference.Uid] = difference.KeepUserOverride.Value;
        foreach (var difference in preview.Files.Where(value => value.CanChoose)) {
            var existing = preview.Choices.Files.FirstOrDefault(value => value.ChangeId == difference.ChangeId);
            if (existing is not null) files[difference.ChangeId] = existing;
            else if (!difference.Conflict) files[difference.ChangeId] = new(difference.ChangeId, difference.Choice, difference.KeepDisabled);
        }
        orderedFiles = preview.Files.OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (editable) {
            acknowledge = new MyCheckBox { Text = "允许本次无法验证的模组依赖（明确错误仍会阻止更新）", Checked = allowUnverified,
                Margin = new Thickness(0, 10, 0, 0), ToolTip = "默认关闭。请先查看兼容性提示，再决定是否接受无法验证的依赖。" };
            acknowledge.Change += (_, _) => allowUnverified = acknowledge.Checked; Header.Children.Add(acknowledge);
        } else {
            var fixedSelection = Label("已固定本次选择 · " + preview.PreviewSha256[..12], 12, color: "ColorBrushGray2", margin: new Thickness(0, 8, 0, 0));
            fixedSelection.ToolTip = preview.PreviewSha256; Header.Children.Add(fixedSelection);
            if (!preview.Errors.IsEmpty || preview.RequiresChoices)
                Header.Children.Add(StatusHint("此结果尚不能提交，请修改选择并重新核对。", (int)MyHint.Themes.Red));
        }
        AddFilter("组件 · " + preview.Components.Length, 0, true, Show);
        AddFilter("文件 · " + preview.Files.Length, 1, true, Show);
        AddFilter("运行环境 · " + preview.RuntimeChanges.Length, 2, true, Show);
        AddFilter("兼容性提示 · " + (preview.Warnings.Length + preview.Errors.Length), 3, true, Show);
        RefreshConfirmation(); Show(!preview.Errors.IsEmpty ? 3 : preview.Components.Any(value => value.Conflict) ? 0 : 1);
    }

    internal MmcInstancePackageUpdateChoices ReadChoices() {
        if (!SelectionsComplete()) throw new IOException("请先逐项选择冲突组件和文件的处理方式。");
        return new(components.Select(value => new MmcPackageComponentDecision(value.Key, value.Value)).ToImmutableArray(),
            files.Values.ToImmutableArray(), allowUnverified);
    }
    private bool SelectionsComplete() => preview.Components.All(value => !value.HasUserOverride || !value.Conflict || components.ContainsKey(value.Uid)) &&
        preview.Files.All(value => !value.Conflict || !value.CanChoose || files.ContainsKey(value.ChangeId));
    private void RefreshConfirmation() => SetCanConfirm(editable ? SelectionsComplete() : preview.Errors.IsEmpty && !preview.RequiresChoices);
    private void ChangedSelection() {
        allowUnverified = false;
        if (acknowledge is not null) acknowledge.Checked = false;
        RefreshConfirmation();
    }
    private void Show(int value) { section = value; page = 0; Render(); }
    private void Render() {
        BeginFilter(section);
        int count = section switch { 0 => preview.Components.Length, 1 => orderedFiles.Length, 2 => preview.RuntimeChanges.Length, _ => preview.Warnings.Length + preview.Errors.Length };
        if (section == 3) {
            if (!editable) Rows.Children.Add(StatusHint(allowUnverified ? "已明确允许本次无法验证的模组依赖。" : "本次未允许无法验证的模组依赖。", (int)MyHint.Themes.Blue));
            if (preview.ChangesMinecraftVersion) Rows.Children.Add(StatusHint("Minecraft 版本将变化。已有存档全部备份成功后才会发布更新。", (int)MyHint.Themes.Yellow));
            foreach (string error in preview.Errors.Skip(page * PageSize).Take(PageSize)) Rows.Children.Add(StatusHint(error, (int)MyHint.Themes.Red));
            int warningStart = Math.Max(0, page * PageSize - preview.Errors.Length), available = Math.Max(0, PageSize - Math.Max(0, preview.Errors.Length - page * PageSize));
            foreach (string warning in preview.Warnings.Skip(warningStart).Take(available)) Rows.Children.Add(StatusHint(warning, (int)MyHint.Themes.Yellow));
        } else if (section == 0) foreach (var value in preview.Components.Skip(page * PageSize).Take(PageSize)) AddComponent(value);
        else if (section == 1) foreach (var value in orderedFiles.Skip(page * PageSize).Take(PageSize)) AddFile(value);
        else foreach (var value in preview.RuntimeChanges.Skip(page * PageSize).Take(PageSize)) {
            var body = new StackPanel(); body.Children.Add(Label("当前：" + value.Before, 12)); body.Children.Add(Label("更新后：" + value.After, 12, margin: new Thickness(0, 6, 0, 0)));
            Rows.Children.Add(Section(RuntimeName(value.Field), body));
        }
        if (count == 0) Rows.Children.Add(Label("此项没有差异。", 13, margin: new Thickness(8, 12, 0, 0)));
        if (count > PageSize) {
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var previous = new MyButton { Text = "上一页", IsEnabled = page > 0, Width = 90, Height = 30, Margin = new Thickness(8) };
            var next = new MyButton { Text = "下一页", IsEnabled = (page + 1) * PageSize < count, Width = 90, Height = 30, Margin = new Thickness(8) };
            previous.Click += (_, _) => { page--; Render(); }; next.Click += (_, _) => { page++; Render(); };
            navigation.Children.Add(previous); navigation.Children.Add(Label((page + 1) + " / " + ((count + PageSize - 1) / PageSize), 12, margin: new Thickness(8, 15, 8, 0))); navigation.Children.Add(next); Rows.Children.Add(navigation);
        }
    }
    private void AddComponent(MmcPackageComponentDifference difference) {
        var body = new StackPanel();
        body.Children.Add(Label("旧包声明：" + ComponentSummary(difference.Before), 12));
        body.Children.Add(Label("新包声明：" + ComponentSummary(difference.AuthorAfter), 12, margin: new Thickness(0, 6, 0, 0)));
        if (difference.HasUserOverride) {
            if (editable) {
                var options = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                if (!difference.Removed) options.Children.Add(RadioOption("保留个人组件覆盖", components.TryGetValue(difference.Uid, out bool keep) && keep,
                    () => { components[difference.Uid] = true; ChangedSelection(); }));
                options.Children.Add(RadioOption(difference.Removed ? "接受新包移除该组件，并移除个人覆盖" : "采用新包组件，移除个人覆盖",
                    components.TryGetValue(difference.Uid, out bool author) && !author, () => { components[difference.Uid] = false; ChangedSelection(); }));
                body.Children.Add(options);
            } else body.Children.Add(Label(difference.KeepUserOverride == true ? "结果：保留个人组件覆盖" : "结果：采用新包组件", 12, true, margin: new Thickness(0, 8, 0, 0)));
        }
        Rows.Children.Add(Section(difference.Uid + (difference.Conflict ? " · 需要选择" : ""), body));
    }
    private void AddFile(MmcPackageFileDifference difference) {
        var body = new StackPanel();
        body.Children.Add(Label(OwnershipName(difference.Ownership) + (difference.LocalRelative is { } local && local != difference.RelativePath ? " · 当前路径：" + local : ""), 12));
        if (!string.IsNullOrEmpty(difference.Reason)) body.Children.Add(Label(difference.Reason, 12, margin: new Thickness(0, 6, 0, 0)));
        string authorText = "采用新包方案";
        string personalText = difference.Ownership is PackUpdateOwnership.Deleted or PackUpdateOwnership.OptionalAbsent ? "保持本地未安装状态" : "保留当前文件状态";
        if (editable && difference.CanChoose) {
            files.TryGetValue(difference.ChangeId, out var selected);
            bool keepDisabled = selected?.KeepDisabled ?? difference.KeepDisabled;
            var options = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            MyCheckBox disabled = null;
            void Select(PackUpdateChoice choice) {
                files[difference.ChangeId] = new(difference.ChangeId, choice, disabled?.Checked ?? keepDisabled);
                if (disabled is not null) disabled.IsEnabled = choice == PackUpdateChoice.UseAuthor;
                ChangedSelection();
            }
            options.Children.Add(RadioOption(personalText, selected?.Choice == PackUpdateChoice.PreservePersonal, () => Select(PackUpdateChoice.PreservePersonal)));
            options.Children.Add(RadioOption(authorText, selected?.Choice == PackUpdateChoice.UseAuthor, () => Select(PackUpdateChoice.UseAuthor)));
            if (difference.Ownership == PackUpdateOwnership.Disabled) {
                disabled = new MyCheckBox { Text = "采用新包文件时仍保持禁用", Checked = keepDisabled,
                    IsEnabled = selected?.Choice == PackUpdateChoice.UseAuthor, Margin = new Thickness(0, 8, 0, 0) };
                disabled.Change += (_, _) => { if (files.TryGetValue(difference.ChangeId, out var choice)) files[difference.ChangeId] = choice with { KeepDisabled = disabled.Checked }; ChangedSelection(); };
                options.Children.Add(disabled);
            }
            body.Children.Add(options);
        } else body.Children.Add(Label("结果：" + (difference.ApplyAuthor ? authorText + (difference.KeepDisabled && difference.Ownership == PackUpdateOwnership.Disabled ? "，仍保持禁用" : "") : personalText), 12, true, margin: new Thickness(0, 8, 0, 0)));
        Rows.Children.Add(Section(difference.RelativePath + (difference.Conflict ? " · 冲突" : ""), body));
    }
    private static string ComponentSummary(string value) {
        if (value == "absent") return "未包含";
        try {
            var data = JObject.Parse(value);
            return data.Value<string>("Version") + (data.Value<bool?>("Root") == true ? " · 直接组件" : " · 依赖组件") +
                (data.Value<bool?>("Disabled") == true ? " · 禁用" : "") + " · 补丁 " + (data.Value<string>("PatchSha256") ?? "")[..Math.Min(12, (data.Value<string>("PatchSha256") ?? "").Length)];
        } catch (Newtonsoft.Json.JsonException) { return value; }
    }
    private static string OwnershipName(PackUpdateOwnership value) => value switch {
        PackUpdateOwnership.AuthorUnchanged => "作者文件", PackUpdateOwnership.AuthorRenamed => "已改名的作者文件",
        PackUpdateOwnership.PersonalModified => "个人修改", PackUpdateOwnership.PersonalAdded => "个人添加",
        PackUpdateOwnership.Disabled => "当前已禁用", PackUpdateOwnership.Deleted => "本地已删除", PackUpdateOwnership.OptionalAbsent => "未安装的可选文件",
        PackUpdateOwnership.Ambiguous => "归属不明确", _ => "尚未确认归属"
    };
    private static string RuntimeName(string value) => value switch {
        "minecraft" => "Minecraft 版本", "mainClass" => "游戏入口", "javaPolicy" => "Java 要求", "jvmArguments" => "JVM 参数",
        "gameArguments" => "游戏参数", "classpath" => "运行库", "natives" => "本机运行库", "assetIndex" => "游戏资源",
        _ => value.StartsWith("private:", StringComparison.Ordinal) ? "实例运行库 · " + value[8..] : value
    };
}
