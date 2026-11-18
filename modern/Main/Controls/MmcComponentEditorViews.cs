using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF view, using the existing pack dialog and LF input/card controls.
internal sealed class MmcComponentEditorView : ModpackDialogView {
    private enum EditKind { Version, Enable, Disable, Patch, Reset }
    private const int PageSize = 40;
    private readonly MmcComponentEditorModel model;
    private readonly List<MmcComponentEditorLocalFile> locals = [];
    private string uid, version, patchText, javaPath, newLocalName = "";
    private MmcComponentEditorFileFact patchFile;
    private EditKind kind;
    private int section, page;
    internal MmcComponentEditorView(MmcComponentEditorModel model, MmcComponentEditorChoice previous = null) : base(
        "MMC 组件与个人覆盖", "查看作者声明与当前生效结果；每次修改一个已有组件。", 3) {
        this.model = model;
        AddFilter("当前组件 · " + model.Saved.Resolved.Resolution.Profile.Components.Length, 0, true, Show);
        AddFilter("本次修改", 1, true, Show);
        AddFilter("本地运行库", 2, true, Show);
        Header.Children.Add(Label("当前已发布环境：" + EnvironmentText(model.PublishedEnvironment), 12, margin: new Thickness(0, 8, 0, 0)));
        Header.Children.Add(StatusHint("新增直接组件需通过整合包来源更新；自动依赖由组件声明解析。", (int)MyHint.Themes.Blue));
        Header.Children.Add(StatusHint("Java 统一使用“实例设置 → Java”中的选择。预览会检查目标组件的实际兼容要求；需要更换 Java 时请在实例设置中调整。", (int)MyHint.Themes.Blue));
        if (previous is not null) {
            SelectComponent(previous.Edit.Uid);
            var edit = previous.Edit;
            kind = edit.Reset ? EditKind.Reset : edit.PatchContentUtf8 is not null ? EditKind.Patch : edit.ExactVersion is not null ? EditKind.Version : edit.Enabled == true ? EditKind.Enable : EditKind.Disable;
            version = edit.ExactVersion ?? version; patchText = edit.PatchContentUtf8 ?? patchText;
            patchFile = previous.PatchFile; javaPath = previous.JavaPath;
            if (!previous.LocalFiles.IsDefault) locals.AddRange(previous.LocalFiles);
        }
        RefreshConfirmation(); Show(previous is null ? 0 : 1);
    }
    internal MmcComponentEditorChoice ReadChoice() {
        if (!Ready()) throw new IOException("请先选择已有组件，并填写本次修改。");
        var edit = kind switch {
            EditKind.Version => new MmcInstanceComponentEdit(uid, ExactVersion: version.Trim()),
            EditKind.Enable => new MmcInstanceComponentEdit(uid, Enabled: true),
            EditKind.Disable => new MmcInstanceComponentEdit(uid, Enabled: false),
            EditKind.Patch => new MmcInstanceComponentEdit(uid, PatchContentUtf8: patchText),
            _ => new MmcInstanceComponentEdit(uid, Reset: true)
        };
        return new(edit) { LocalFiles = locals.ToImmutableArray(), JavaPath = javaPath, PatchFile = kind == EditKind.Patch ? patchFile : null };
    }
    private void SelectComponent(string value) {
        if (!model.Saved.Package.Components.Any(component => component.Uid == value)) throw new IOException("自动依赖不能作为新的直接组件编辑。");
        uid = value;
        var current = model.Saved.Resolved.Resolution.Profile.Components.Single(component => component.Uid == uid);
        version = current.Version ?? ""; patchText = model.PatchTexts[uid]; patchFile = null; kind = EditKind.Version;
        RefreshConfirmation();
    }
    private bool Ready() {
        if (uid is null) return false;
        var current = model.Saved.Resolved.Resolution.Profile.Components.Single(value => value.Uid == uid);
        var author = model.Saved.Package.Components.Single(value => value.Uid == uid);
        return kind switch {
            EditKind.Version => !string.IsNullOrWhiteSpace(version),
            EditKind.Enable => !current.Enabled,
            EditKind.Disable => current.Enabled && !author.Important && !author.DependencyOnly,
            EditKind.Patch => !string.IsNullOrWhiteSpace(patchText) && System.Text.Encoding.UTF8.GetByteCount(patchText) <= 16 * 1024 * 1024,
            _ => model.Source.Definition.ComponentOverrides.Any(value => value.Uid == uid)
        };
    }
    private void RefreshConfirmation() => SetCanConfirm(Ready());
    private void Show(int value) { section = value; page = 0; Render(); }
    private void Render() {
        BeginFilter(section);
        if (section == 0) RenderComponents();
        else if (section == 1) RenderEdit();
        else RenderInputs();
    }
    private void RenderComponents() {
        var components = model.Saved.Resolved.Resolution.Profile.Components;
        foreach (var component in components.Skip(page * PageSize).Take(PageSize)) {
            var body = new StackPanel();
            body.Children.Add(Label("当前生效：" + (component.Enabled ? "启用" : "禁用") + " · " + (component.Version ?? "未指定版本") + " · " + SourceName(component.SourceKind), 12, true));
            var author = model.Saved.Package.Components.FirstOrDefault(value => value.Uid == component.Uid);
            body.Children.Add(Label(author is null ? "作者未直接声明此项，由依赖关系解析。" : "作者声明：" + (author.RequestedVersion ?? "由依赖决定版本") + " · " + (author.EffectiveEnabled ? "启用" : "禁用") +
                (author.LocalPatch is null ? " · 远程组件元数据" : " · 包内补丁") + (author.Important || author.DependencyOnly ? " · 不能直接禁用" : ""), 12, margin: new Thickness(0, 6, 0, 0)));
            var personal = model.Source.Definition.ComponentOverrides.FirstOrDefault(value => value.Uid == component.Uid);
            body.Children.Add(Label(personal is null ? "个人覆盖：无" : "个人覆盖：" +
                (personal.Enabled is { } enabled ? (enabled ? "启用；" : "禁用；") : "") +
                (personal.Version is { } exact ? "版本 " + exact + "；" : "") +
                (personal.PatchReference is { } patch ? "补丁 " + patch.Sha256[..12] : "保留其余作者设置"), 12, margin: new Thickness(0, 6, 0, 0)));
            if (author is not null) {
                var edit = new MyButton { Text = uid == component.Uid ? "修改此组件（已选择）" : "修改此组件", Width = 176, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
                edit.Click += (_, _) => { SelectComponent(component.Uid); Show(1); }; body.Children.Add(edit);
            }
            Rows.Children.Add(Section(component.Uid, body));
        }
        AddPaging(components.Length);
    }
    private void RenderEdit() {
        if (uid is null) { Rows.Children.Add(Label("请先在“当前组件”中选择要修改的组件。", 13, margin: new Thickness(8, 12, 0, 0))); return; }
        var body = new StackPanel();
        body.Children.Add(Label("目标组件：" + uid, 13, true));
        var author = model.Saved.Package.Components.Single(value => value.Uid == uid);
        var options = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Option(string text, EditKind value, bool enabled = true) {
            var radio = RadioOption(text, kind == value, () => { kind = value; RefreshConfirmation(); Render(); });
            radio.IsEnabled = enabled; options.Children.Add(radio);
        }
        var current = model.Saved.Resolved.Resolution.Profile.Components.Single(value => value.Uid == uid);
        Option("指定精确版本（从 MMC 组件元数据获取）", EditKind.Version);
        Option("启用此组件", EditKind.Enable, !current.Enabled);
        Option("禁用此组件", EditKind.Disable, current.Enabled && !author.Important && !author.DependencyOnly);
        Option("编辑或选择组件 JSON 补丁", EditKind.Patch);
        Option("移除该组件的全部个人覆盖，恢复作者设置", EditKind.Reset, model.Source.Definition.ComponentOverrides.Any(value => value.Uid == uid));
        body.Children.Add(options);
        if (kind == EditKind.Version) {
            var input = new MyTextBox { Text = version, HintText = "输入组件的精确版本号", Margin = new Thickness(0, 10, 0, 0) };
            input.TextChanged += (_, _) => { version = input.Text; RefreshConfirmation(); }; body.Children.Add(input);
            body.Children.Add(Label("现有个人补丁会由本次所选版本的真实元数据替换。不会改成普通 Forge / Fabric 安装流程。", 12, margin: new Thickness(0, 8, 0, 0)));
        } else if (kind == EditKind.Patch) {
            var select = new MyButton { Text = "选择本地 JSON…", Width = 150, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 8) };
            select.Click += (_, _) => Pick(() => {
                string path = MeloongCore.Wpf.Dialogs.SelectFile("选择组件 JSON 补丁", false, filter: new[] { (new[] { "json" }, "JSON 补丁") }).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(path)) return;
                var read = MmcComponentEditorUi.ReadPatchFile(path); patchText = read.Text; patchFile = read.Fact; Render(); RefreshConfirmation();
            });
            body.Children.Add(select);
            if (patchFile is not null) body.Children.Add(Label("已选择：" + patchFile.AbsolutePath + " · SHA256 " + patchFile.Sha256[..12], 12, margin: new Thickness(0, 0, 0, 8)));
            var input = new MyTextBox { Text = patchText, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 170, MaxHeight = 260 };
            input.TextChanged += (_, _) => { patchText = input.Text; patchFile = null; RefreshConfirmation(); }; body.Children.Add(input);
            body.Children.Add(Label("JSON 中的 UID 必须保持为所选组件；修改文本后按本次内嵌补丁提交。未知关键字段会阻止预览。", 12, margin: new Thickness(0, 8, 0, 0)));
        } else if (kind == EditKind.Reset) body.Children.Add(StatusHint("仅移除此组件的启停、版本和补丁覆盖。运行库与实际启动配置仍需重新解析、校验。", (int)MyHint.Themes.Blue));
        Rows.Children.Add(Section("本次修改", body));
    }
    private void RenderInputs() {
        var body = new StackPanel();
        body.Children.Add(Label("本地运行库须与候选组件声明的 MMC-filename 相同。选择后会实际读取文件，固定长度与 SHA256；缺少或不匹配时不能提交。", 12));
        foreach (var local in locals.ToArray()) {
            var line = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            line.Children.Add(Label(local.FileName + " ← " + local.AbsolutePath, 12));
            var remove = new MyButton { Text = "移除此项", Width = 96, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            remove.Click += (_, _) => { locals.Remove(local); Render(); }; line.Children.Add(remove); body.Children.Add(line);
        }
        var name = new MyTextBox { Text = newLocalName, HintText = "目标文件名，例如 custom.jar（留空采用所选文件名）", Margin = new Thickness(0, 12, 0, 0) };
        name.TextChanged += (_, _) => newLocalName = name.Text; body.Children.Add(name);
        var add = new MyButton { Text = "选择真实本地 JAR…", Width = 170, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        add.Click += (_, _) => Pick(() => {
            string path = MeloongCore.Wpf.Dialogs.SelectFile("选择组件需要的本地运行库", false, filter: new[] { (new[] { "jar" }, "Java 运行库") }).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(path)) return;
            string target = string.IsNullOrWhiteSpace(newLocalName) ? Path.GetFileName(path) : newLocalName.Trim();
            model.Source.Paths.ResolveRelative(MmcPathRole.LocalLibraries, target);
            if (target.Contains('/') || target.Contains('\\')) throw new IOException("目标必须是单个文件名。");
            locals.RemoveAll(value => value.FileName.Equals(target, StringComparison.OrdinalIgnoreCase)); locals.Add(new(target, Path.GetFullPath(path))); newLocalName = ""; Render();
        });
        body.Children.Add(add); Rows.Children.Add(Section("显式选择本地运行库", body));
    }
    private void AddPaging(int count) {
        if (count <= PageSize) return;
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = new MyButton { Text = "上一页", Width = 90, Height = 30, IsEnabled = page > 0, Margin = new Thickness(8) };
        var next = new MyButton { Text = "下一页", Width = 90, Height = 30, IsEnabled = (page + 1) * PageSize < count, Margin = new Thickness(8) };
        previous.Click += (_, _) => { page--; Render(); }; next.Click += (_, _) => { page++; Render(); };
        line.Children.Add(previous); line.Children.Add(Label((page + 1) + " / " + ((count + PageSize - 1) / PageSize), 12, margin: new Thickness(8, 15, 8, 0))); line.Children.Add(next); Rows.Children.Add(line);
    }
    private static string SourceName(MmcComponentSourceKind kind) => kind switch { MmcComponentSourceKind.PackageLocal => "作者包内补丁", MmcComponentSourceKind.RemoteMetadata => "已固定远程元数据", _ => "个人 JSON 补丁" };
    internal static string EnvironmentText(MmcRuntimeEnvironment value) => "Java " + value.JavaMajor + " · " + value.OperatingSystem + " / " + value.Architecture + " / " + value.JavaBitness + " 位";
    private static void Pick(Action action) { try { action(); } catch (Exception error) { MeloongCore.Logger.Error(error, "无法选择 MMC 组件文件", MeloongCore.LogBehavior.Alert); } }
}

internal sealed class MmcComponentEditorPreviewView : ModpackDialogView {
    private readonly MmcComponentEditorConfirmation confirmation;
    private int section;
    internal MmcComponentEditorPreviewView(MmcComponentEditorConfirmation confirmation) : base(
        "MMC 组件实际预览", "只有本次固定候选可以提交；修改选择后须重新解析。", 4) {
        this.confirmation = confirmation;
        AddFilter("组件与覆盖", 0, true, Render);
        AddFilter("实际运行差异", 1, true, Render);
        AddFilter("诊断", 2, true, Render);
        AddFilter("显式来源", 3, true, Render);
        if (confirmation.Result.Preview is { } preview) {
            var digest = Label("已固定候选 · " + preview.CandidateSha256[..12], 12, margin: new Thickness(0, 8, 0, 0)); digest.ToolTip = preview.CandidateSha256; Header.Children.Add(digest);
            Header.Children.Add(Label("候选环境：" + MmcComponentEditorView.EnvironmentText(preview.Selection.Environment), 12, margin: new Thickness(0, 6, 0, 0)));
            if (preview.Differences.Any(value => value.Field == "minecraft")) Header.Children.Add(StatusHint("Minecraft 版本将变化，当前已有存档全部备份成功后才会提交。", (int)MyHint.Themes.Yellow));
        }
        SetCanConfirm(confirmation.CanCommit); Render(confirmation.CanCommit ? 1 : 2);
    }
    private void Render(int value) {
        section = value; BeginFilter(section);
        if (section == 2) {
            if (!string.IsNullOrWhiteSpace(confirmation.Result.Failure)) Rows.Children.Add(StatusHint(confirmation.Result.Failure, (int)MyHint.Themes.Red));
            var diagnostics = confirmation.Result.Diagnostics.AddRange(confirmation.Result.Preview?.Diagnostics ?? []).Distinct().ToArray();
            foreach (var diagnostic in diagnostics) Rows.Children.Add(StatusHint(diagnostic.Code + " · " + diagnostic.Source + "\n" + diagnostic.Message,
                (int)(diagnostic.Severity == MmcDiagnosticSeverity.Error ? MyHint.Themes.Red : diagnostic.Severity == MmcDiagnosticSeverity.Warning ? MyHint.Themes.Yellow : MyHint.Themes.Blue)));
            if (diagnostics.Length == 0 && string.IsNullOrWhiteSpace(confirmation.Result.Failure)) Rows.Children.Add(Label("真实解析未报告诊断。", 13, margin: new Thickness(8, 12, 0, 0)));
            return;
        }
        if (section == 3) {
            var inputs = confirmation.Inputs.Concat(confirmation.JavaImage is null ? [] : new[] { confirmation.JavaImage }).ToArray();
            foreach (var input in inputs) Rows.Children.Add(Section(input.FileName, Label(input.AbsolutePath + "\n" + input.SizeBytes + " 字节\nSHA256 " + input.Sha256, 12)));
            if (inputs.Length == 0) Rows.Children.Add(Label("本次未添加外部文件；运行库来源仅限后端已固定的作者原件或已采用原件。", 13, margin: new Thickness(8, 12, 0, 0)));
            return;
        }
        var differences = (confirmation.Result.Preview?.Differences ?? []).Where(item =>
            (item.Field.StartsWith("component:", StringComparison.Ordinal) || item.Field == "userOverrides") == (section == 0)).ToArray();
        foreach (var difference in differences) {
            var body = new StackPanel();
            body.Children.Add(Label("当前", 12, true)); body.Children.Add(Value(difference.Before));
            body.Children.Add(Label("修改后", 12, true, margin: new Thickness(0, 10, 0, 0))); body.Children.Add(Value(difference.After));
            Rows.Children.Add(Section(FieldName(difference.Field), body));
        }
        if (differences.Length == 0) Rows.Children.Add(Label("此项没有差异。", 13, margin: new Thickness(8, 12, 0, 0)));
    }
    private static MyTextBox Value(string text) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 30, MaxHeight = 180, Margin = new Thickness(0, 6, 0, 0) };
    private static string FieldName(string value) => value switch {
        "minecraft" => "Minecraft 版本", "mainClass" => "游戏入口", "mainJar" => "游戏主运行库", "jvmArguments" => "JVM 参数", "gameArguments" => "游戏参数",
        "tweakers" => "启动 Tweaker", "classpath" => "运行库", "natives" => "本机运行库", "javaPolicy" => "Java 要求", "environment" => "Java 与平台环境", "userOverrides" => "个人覆盖",
        _ => value.StartsWith("component:", StringComparison.Ordinal) ? value[10..] : value
    };
}
