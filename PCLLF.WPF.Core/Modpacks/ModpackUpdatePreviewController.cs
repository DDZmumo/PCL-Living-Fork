using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

public sealed partial class ModpackUpdatePreviewController<TFile, TPackage, TChange, TPlan, TKind, TButton, TScroll>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile> where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange>
    where TKind : struct, Enum where TButton : FrameworkElement where TScroll : ScrollViewer {
    public readonly List<TChange> Changes;
    private readonly Dictionary<TKind, int> counts;
    private readonly ModpackDialogController<TButton, TScroll> view;
    private readonly Action<int> setKind;
    private readonly TPlan plan;
    private readonly StackPanel notices = new();
    private readonly Expander noticeDetails;
    private readonly TextBlock? noticeSummary;
    private readonly Func<TPlan, TPlan>? snapshot;
    private readonly Func<TPlan, CancellationToken, PackUpdatePreviewReview>? review;
    private readonly StackPanel compatibility = new();
    private readonly FrameworkElement? selectionStatus;
    private readonly FrameworkElement? compatibilitySection, fileSection;
    private readonly StackPanel? fileRows;
    private CancellationTokenSource? checking;
    private int revision, activeKind;
    private bool initialPagePending = true;
    private volatile bool closed;
    private readonly object checkGate = new();
    private readonly Dictionary<CancellationTokenSource, Task> readChecks = new();
    private static TKind Kind(int value) => (TKind)Enum.ToObject(typeof(TKind), value);
    private static bool Same(string? a, string? b) => (a ?? "") == (b ?? "");
    public ModpackUpdatePreviewController(ModpackDialogController<TButton, TScroll> view, TPlan plan, string currentMinecraft, Dictionary<ModLoaders, string> currentLoaders,
        Dictionary<TKind, int> counts, Action<int> setKind, Func<string, UIElement> hint,
        Func<TPlan, TPlan>? snapshot = null, Func<TPlan, CancellationToken, PackUpdatePreviewReview>? review = null) {
        this.view = view; this.counts = counts; this.setKind = setKind; this.plan = plan; Changes = plan.Changes.ToList();
        this.snapshot = snapshot; this.review = review;
        bool changed = !Same(currentMinecraft, plan.Package!.Minecraft) || !currentLoaders.OrderBy(pair => pair.Key).SequenceEqual(plan.Package.Loaders.OrderBy(pair => pair.Key));
        if (changed) notices.Children.Add(hint("游戏版本或加载器将更新。请核对兼容性；存档会保留，更新前自动备份。"));
        AddNotices("需要核对的文件与来源", plan.Warnings);
        if (plan.RuntimeWarnings.Count > 0 && plan.DependencyCheck is null) notices.Children.Add(hint("保留模组有兼容性信息需要核对，可展开下方详情。"));
        if (review is null) AddNotices("保留模组的兼容性", plan.RuntimeWarnings);
        if (review is null && plan.DependencyCheck is PackDependencyGraphResult graph) {
            if (graph.HasErrors) noticeSummary = ModpackDialogController<TButton, TScroll>.Label("默认方案有依赖问题，请核对详情并调整选择。", 12, color: "ColorBrushRedLight", margin: new Thickness(4, 0, 7, 6));
            else if (graph.HasUnknown) noticeSummary = ModpackDialogController<TButton, TScroll>.Label("部分兼容性声明待核对，确认时会重新检查所选组合。", 12, color: "ColorBrushGray2", margin: new Thickness(4, 0, 7, 6));
            if (graph.HasErrors) notices.Children.Add(hint("默认组合存在依赖或冲突问题，请调整下方选择。确认时会重新检查；有确定冲突时不会提交。"));
            else if (graph.HasUnknown) notices.Children.Add(hint("部分声明尚待确认，可展开详情核对；确认更新时会按所选组合重新检查。"));
            AddNotices("默认更新组合的依赖与冲突", graph.Errors);
            AddNotices("默认更新组合的待确认声明", graph.Warnings);
        }
        noticeDetails = new Expander { Header = "来源与兼容性详情", Content = notices, FontSize = 12, Margin = new Thickness(4, 0, 7, 9) };
        noticeDetails.SetResourceReference(Control.ForegroundProperty, "ColorBrush1");
        if (review is not null && snapshot is not null) {
            fileRows = new StackPanel();
            compatibilitySection = view.Section("更新检查", compatibility);
            fileSection = view.Section("文件变更", fileRows);
            selectionStatus = view.StatusHint("正在检查所选组合…", 0);
            selectionStatus.Tag = "preview-status";
            view.Header.Children.Add(selectionStatus);
        }
        foreach (int value in new[] { 0, 1, 2, 3 }) {
            counts[Kind(value)] = Changes.Count(item => Classify(item) == value);
            view.AddFilter((value == 3 ? "保留 / 自定义" : KindName(value)) + "（" + counts[Kind(value)] + "）", value, value == 3 || counts[Kind(value)] > 0, selected => { initialPagePending = false; ShowKind(selected); });
        }
        if (compatibilitySection is not null) view.AddFilter("更新检查", 4, true, selected => { initialPagePending = false; ShowKind(selected); });
        ShowKind(compatibilitySection is not null ? 4 : 3);
        if (review is not null && snapshot is not null) {
            view.Unloaded += (_, _) => { _ = CloseAndDrainAsync(); view.SetCanConfirm(false); };
            RecheckSelection();
        }
    }
    public static int Classify(TChange change) => change.PersonalCompatible || change.Operation == PackUpdateOperation.DisablePersonal || change.DisplayOnly || change.Conflict ? 3 : change.Incoming is null ? 2 : change.Before is null ? 0 : 1;
    public static string PackageTitle(TPackage package) {
        string name = package.Name?.Trim() ?? "", version = package.Version?.Trim() ?? "";
        return name.Length == 0 || Same(name, version) ? "整合包版本 " + version : version.Length == 0 ? name : name + " · " + version;
    }
    public static string KindName(int value) => value switch { 0 => "新增", 1 => "更新", 2 => "移除", _ => "个人文件与冲突" };
    public static string OwnershipName(PackUpdateOwnership ownership) => ownership switch {
        PackUpdateOwnership.AuthorUnchanged => "作者文件",
        PackUpdateOwnership.AuthorRenamed => "作者文件，玩家仅改名",
        PackUpdateOwnership.PersonalModified => "个人修改",
        PackUpdateOwnership.PersonalAdded => "个人新增",
        PackUpdateOwnership.Disabled => "玩家已禁用",
        PackUpdateOwnership.Deleted => "玩家已删除",
        PackUpdateOwnership.OptionalAbsent => "未安装的可选组件",
        PackUpdateOwnership.Ambiguous => "存在多个关联候选，待确认",
        _ => "身份或归属无法确认"
    };
    public static string RuntimeSummary(TPackage package, string minecraft, Dictionary<ModLoaders, string> loaders) {
        List<string> values = new() { "Minecraft " + (Same(minecraft, package.Minecraft) ? minecraft : minecraft + " → " + package.Minecraft) };
        foreach (ModLoaders loader in loaders.Keys.Union(package.Loaders.Keys).OrderBy(value => value)) {
            loaders.TryGetValue(loader, out string? before); package.Loaders.TryGetValue(loader, out string? after);
            values.Add(loader + " " + (Same(before, after) ? after : (before ?? "无") + " → " + (after ?? "移除")));
        }
        return string.Join(" · ", values);
    }
    public static string FolderName(string relative) {
        string[] parts = relative.Split('/'); if (parts.Length == 1) return "根目录文件";
        return parts[0].ToLowerInvariant() switch { "mods" => "模组", "config" or "defaultconfigs" => "配置", "resourcepacks" => "资源包", "shaderpacks" => "光影包", "datapacks" => "数据包", _ => parts[0] };
    }
    public void ShowKind(int value) {
        activeKind = value;
        setKind(value); view.BeginFilter(value);
        if (value == 4 && compatibilitySection is not null) {
            view.Rows.Children.Add(compatibilitySection);
            if (notices.Children.Count > 0) view.Rows.Children.Add(noticeDetails);
            return;
        }
        if (fileSection is not null) view.Rows.Children.Add(fileSection);
        StackPanel rows = fileRows ?? view.Rows;
        fileRows?.Children.Clear();
        // Notices share the finite file-list viewport; the fixed title/filter rows must stay compact.
        if (noticeSummary is not null) view.Rows.Children.Add(noticeSummary);
        if (compatibilitySection is null && notices.Children.Count > 0) view.Rows.Children.Add(noticeDetails);
        string description = value == 3 ? "保留可用的个人模组；兼容性问题请在“更新检查”页确认。" : value == 2 ? "作者新版的移除项；更新前会备份。" : value == 0 ? "新版新增文件与个人模组所需前置。" : "作者新版的更新项；可选操作以卡片选项为准。";
        rows.Children.Add(ModpackDialogController<TButton, TScroll>.Label(description, 12, color: "ColorBrushGray2", margin: new Thickness(4, 0, 7, 10)));
        foreach (var group in Changes.Where(item => Classify(item) == value).GroupBy(item => FolderName(item.Relative!)).OrderBy(item => item.Key)) {
            rows.Children.Add(ModpackDialogController<TButton, TScroll>.Label(group.Key + "（" + group.Count() + "）", 13, true, margin: new Thickness(4, 3, 7, 9)));
            List<Border> cards = new();
            foreach (TChange change in group.OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)) {
                StackPanel text = new(); TextBlock name = ModpackDialogController<TButton, TScroll>.Label(Path.GetFileName(change.Relative), 13, true);
                name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(name);
                string? folder = Path.GetDirectoryName(change.Relative!.Replace('/', Path.DirectorySeparatorChar));
                text.Children.Add(ModpackDialogController<TButton, TScroll>.Label((string.IsNullOrEmpty(folder) ? "根目录" : folder) + " · " + OwnershipName(change.Ownership), 12, false, "ColorBrushGray2", new Thickness(0, 4, 0, 0)));
                TextBlock? paths = null;
                if (!string.IsNullOrWhiteSpace(change.LocalRelative) && !string.Equals(change.LocalRelative, change.DestinationRelative, StringComparison.OrdinalIgnoreCase))
                    text.Children.Add(paths = ModpackDialogController<TButton, TScroll>.Label(Paths(change), 12, false, "ColorBrushGray2", new Thickness(0, 4, 0, 0)));
                if (change.Candidates.Count > 0)
                    text.Children.Add(ModpackDialogController<TButton, TScroll>.Label("待核对的本地候选：\r\n" + string.Join("\r\n", change.Candidates), 12, false, "ColorBrushGray2", new Thickness(0, 4, 0, 0)));
                if (!string.IsNullOrWhiteSpace(change.Reason))
                    text.Children.Add(ModpackDialogController<TButton, TScroll>.Label(change.Reason, 12, false, "ColorBrushGray2", new Thickness(0, 4, 0, 0)));
                if (change.CanChoose)
                    AddChoice(text, change);
                if (change.Incoming is not null && change.Ownership == PackUpdateOwnership.Disabled)
                    AddDisabledState(text, change, paths);
                Border box = ModpackDialogController<TButton, TScroll>.Frame(WithIcon(text, change)); box.ToolTip = change.Relative + "\r\n" + change.LocalRelative + "\r\n" + change.Reason; cards.Add(box);
            }
            if (review is not null || value == 3 && group.All(item => item.Relative!.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))) rows.Children.Add(ConflictGrid(cards));
            else foreach (Border card in cards) rows.Children.Add(card);
        }
        if (counts[Kind(value)] == 0) rows.Children.Add(ModpackDialogController<TButton, TScroll>.Label("此分类没有文件。", 13, margin: new Thickness(8)));
    }
    private static Grid ConflictGrid(List<Border> cards) {
        Grid grid = new();
        foreach (Border card in cards) grid.Children.Add(card);
        int columns = 0;
        void ArrangeCards(double width) {
            int count = width >= 600 ? 2 : 1;
            if (columns == count) return;
            columns = count;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            for (int column = 0; column < count; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int row = 0; row < (cards.Count + count - 1) / count; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int index = 0; index < cards.Count; index++) { Grid.SetColumn(cards[index], index % count); Grid.SetRow(cards[index], index / count); }
        }
        ArrangeCards(0);
        grid.SizeChanged += (_, args) => ArrangeCards(args.NewSize.Width);
        return grid;
    }
    private void AddNotices(string title, IEnumerable<string> notices) {
        string[] lines = notices.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
        if (lines.Length == 0) return;
        StackPanel content = new();
        foreach (string line in lines) content.Children.Add(ModpackDialogController<TButton, TScroll>.Label(line, 12, margin: new Thickness(4, 2, 4, 5)));
        Expander details = new() { Header = title + "（" + lines.Length + "）", Margin = new Thickness(0, 6, 0, 0),
            Content = content };
        details.SetResourceReference(Control.ForegroundProperty, "ColorBrush1");
        this.notices.Children.Add(details);
    }
    private static string Paths(TChange change) => "本地：" + change.LocalRelative + "\r\n" +
        (change.Operation == PackUpdateOperation.DisablePersonal ? "禁用目标：" + change.DestinationRelative :
            change.Incoming is not null ? "作者目标：" + change.DestinationRelative :
            change.BaselineRelative is not null ? "作者在新版中移除此文件" : "本地额外文件");
    private void AddChoice(StackPanel text, TChange change) {
        WrapPanel choices = new() { Margin = new Thickness(0, 8, 0, 0) };
        TextBlock result = ModpackDialogController<TButton, TScroll>.Label(change.Incoming is null ? "本次保留，不执行作者移除。" : "本次保留，不执行作者更新。", 12, color: "ColorBrushGray2", margin: new Thickness(0, 4, 0, 0));
        void RefreshResult() {
            if (change.Operation == PackUpdateOperation.DisablePersonal) {
                result.Text = change.Choice == PackUpdateChoice.UseAuthor ? "本次备份后禁用，保留文件供恢复。" : "本次继续启用，仍须通过完整组合检查。";
                result.Visibility = Visibility.Visible;
            } else if (change.PersonalCompatible && change.Choice == PackUpdateChoice.UseAuthor) {
                result.Text = "本次使用作者新版。"; result.Visibility = Visibility.Visible;
            } else {
                result.Text = change.Incoming is null ? "本次保留，不执行作者移除。" : "本次保留，不执行作者更新。";
                result.Visibility = change.AllowPreserveAuthor && change.Choice == PackUpdateChoice.PreservePersonal ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        FrameworkElement preserve = view.RadioOption(change.Operation == PackUpdateOperation.DisablePersonal ? "继续启用（需通过检查）" : "保留当前版本",
            change.Choice == PackUpdateChoice.PreservePersonal, () => { change.Choice = PackUpdateChoice.PreservePersonal; change.DisableAfterUpdate = false; plan.AllowUnverifiedDependencies = false; RefreshResult(); RecheckSelection(); });
        FrameworkElement author = view.RadioOption(change.Operation == PackUpdateOperation.DisablePersonal ? "本次禁用并保留文件" : change.Incoming is null ? "使用作者方案（移除此文件）" : "使用作者版本",
            change.Choice == PackUpdateChoice.UseAuthor, () => { change.Choice = PackUpdateChoice.UseAuthor; change.DisableAfterUpdate = false; plan.AllowUnverifiedDependencies = false; RefreshResult(); RecheckSelection(); });
        preserve.Margin = new Thickness(0, 0, 18, 4); preserve.Tag = PackUpdateChoice.PreservePersonal;
        author.Margin = new Thickness(0, 0, 0, 4); author.Tag = PackUpdateChoice.UseAuthor;
        choices.Children.Add(preserve); choices.Children.Add(author); text.Children.Add(choices);
        RefreshResult(); text.Children.Add(result);
    }
    private void AddDisabledState(StackPanel text, TChange change, TextBlock? paths) {
        void RefreshPaths() { if (paths is not null) paths.Text = Paths(change); }
        FrameworkElement disabled = view.CheckOption("更新到新版后继续保持禁用", change.KeepDisabled, selected => {
            change.KeepDisabled = selected; change.DisableAfterUpdate = false; plan.AllowUnverifiedDependencies = false; RefreshPaths(); RecheckSelection();
        });
        disabled.Margin = new Thickness(0, 8, 0, 0);
        disabled.ToolTip = "勾选：更新文件但保持禁用；取消勾选：更新后启用新版。目标占用或组合不兼容时不会提交。";
        text.Children.Add(disabled);
    }
    private async void RecheckSelection() {
        if (snapshot is null || review is null || closed) return;
        int generation = ++revision;
        CancelCheck();
        var cancellation = new CancellationTokenSource(); checking = cancellation;
        try {
            view.SetCanConfirm(false);
            if (selectionStatus is not null) {
                view.UpdateStatus(selectionStatus, "正在检查所选组合…", 0);
            }
            TPlan selected = snapshot(plan);
            Task<PackUpdatePreviewReview> read;
            lock (checkGate) {
                if (closed) return;
                read = Task.Run(() => review(selected, cancellation.Token), cancellation.Token);
                readChecks.Add(cancellation, read);
            }
            PackUpdatePreviewReview result = await read.ConfigureAwait(false);
            await ApplyLatestAsync(generation, () => {
                plan.DependencyCheck = result.Dependencies;
                compatibility.Children.Clear();
                compatibility.Children.Add(ModpackDialogController<TButton, TScroll>.Label(result.Actions.Count > 0
                    ? "以整合包新版为准，保留兼容的个人修改。选择后会自动重新检查。"
                    : result.Dependencies.HasErrors ? "请展开检查结果，核对需要调整的模组。" : "没有需要额外处理的模组，可继续核对下方文件变更。", 12, color: "ColorBrushGray2", margin: new Thickness(0, 0, 0, 12)));
                foreach (var action in result.Actions.OrderBy(action => action.Selected).ThenBy(action => action.Kind).ThenBy(action => action.Relative, StringComparer.OrdinalIgnoreCase)) {
                    TChange change = plan.Changes.Concat(plan.PreviewChanges).Single(item => item.Relative == action.Relative);
                    StackPanel body = new();
                    DockPanel heading = new();
                    TextBlock state = ModpackDialogController<TButton, TScroll>.Label(action.Selected ? "已选择" : "待确认", 12, color: action.Selected ? "ColorBrush3" : "ColorBrushGray2", margin: new Thickness(12, 0, 0, 0));
                    DockPanel.SetDock(state, Dock.Right); heading.Children.Add(state);
                    TextBlock filename = ModpackDialogController<TButton, TScroll>.Label(Path.GetFileName(action.Relative), 14, true);
                    filename.TextWrapping = TextWrapping.NoWrap; filename.TextTrimming = TextTrimming.CharacterEllipsis; filename.ToolTip = action.Relative;
                    heading.Children.Add(filename); body.Children.Add(heading);
                    string explanation = action.Kind == PackUpdatePreviewActionKind.UseAuthorVersion ? "个人版本不适用于新版整合包，建议恢复整包版本。"
                        : action.Kind == PackUpdatePreviewActionKind.EnableRequired ? "其他模组需要此前置，请确认更新后启用。"
                        : change.Operation == PackUpdateOperation.DisablePersonal ? "个人模组与新版组合不兼容，建议禁用并保留文件。"
                        : "整包自带的模组与目标组合不兼容，需要调整启用状态。";
                    body.Children.Add(ModpackDialogController<TButton, TScroll>.Label(explanation, 12, color: "ColorBrushGray2", margin: new Thickness(0, 5, 0, 10)));
                    string label = action.Kind == PackUpdatePreviewActionKind.UseAuthorVersion ? "使用整包新版"
                        : action.Kind == PackUpdatePreviewActionKind.EnableRequired ? change.Operation == PackUpdateOperation.EnablePersonal ? "启用本地的必需前置" : "启用整包版本的必需前置"
                        : "禁用此模组，保留文件";
                    FrameworkElement option = view.CheckOption(label, action.Selected, enabled => {
                        if (action.Kind == PackUpdatePreviewActionKind.UseAuthorVersion) {
                            change.Choice = enabled ? PackUpdateChoice.UseAuthor : PackUpdateChoice.PreservePersonal;
                            change.PreviewAuthorRepair = enabled;
                            change.DisableAfterUpdate = false;
                        } else if (action.Kind == PackUpdatePreviewActionKind.EnableRequired) {
                            change.KeepDisabled = !enabled; change.DisableAfterUpdate = false;
                            if (enabled) change.Choice = PackUpdateChoice.UseAuthor;
                        } else if (change.Operation == PackUpdateOperation.DisablePersonal) change.Choice = enabled ? PackUpdateChoice.UseAuthor : PackUpdateChoice.PreservePersonal;
                        else { change.DisableAfterUpdate = enabled; if (enabled) change.Choice = PackUpdateChoice.UseAuthor; }
                        plan.AllowUnverifiedDependencies = false; RecheckSelection();
                    });
                    option.Tag = "preview-repair:" + action.Kind + ":" + action.Relative;
                    body.Children.Add(option);
                    body.Children.Add(Details("查看原因与版本要求", new[] { action.Details }));
                    Border card = ModpackDialogController<TButton, TScroll>.Frame(WithIcon(body, change));
                    card.Margin = new Thickness(0, 0, 0, 10); card.Padding = new Thickness(14, 12, 14, 12);
                    compatibility.Children.Add(card);
                }
                if (result.Dependencies.HasErrors) {
                    // Keep uncovered errors visible, without repeating the same
                    // requirement under both its actionable card and a second list.
                    string[] remaining = result.Dependencies.Errors.Where(error => !result.Actions.Any(action => action.Details.Contains(error, StringComparison.Ordinal))).ToArray();
                    if (remaining.Length > 0) compatibility.Children.Add(Details("其他需要处理的问题（" + remaining.Length + "）", remaining, true));
                }
                string[] warnings = result.Dependencies.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
                if (warnings.Length > 0) {
                    compatibility.Children.Add(Details("待确认声明（" + warnings.Length + "）", warnings));
                }
                if (selectionStatus is not null) {
                    int pending = result.Actions.Count(action => !action.Selected);
                    view.SetFilterText(4, result.Dependencies.HasErrors ? "更新检查（" + Math.Max(1, pending) + "）" : "更新检查");
                    string status = result.Dependencies.HasErrors ? "请在“更新检查”页确认所需操作，完成后即可更新。"
                        : result.Dependencies.HasUnknown ? "未发现确定冲突；部分声明可在“更新检查”页核对。" : "所选组合检查通过，可以更新。";
                    view.UpdateStatus(selectionStatus, status, result.Dependencies.HasErrors || result.Dependencies.HasUnknown ? 2 : 0);
                }
                double offset = view.Scroller.VerticalOffset;
                if (initialPagePending) {
                    initialPagePending = false;
                    activeKind = result.Actions.Count > 0 || result.Dependencies.HasErrors || result.Dependencies.HasUnknown || result.Dependencies.Warnings.Count > 0 || plan.Warnings.Count > 0 ? 4 : 3;
                    offset = 0;
                }
                ShowKind(activeKind); view.Scroller.ScrollToVerticalOffset(offset);
                view.SetCanConfirm(!result.Dependencies.HasErrors);
            });
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested || closed || view.Dispatcher.HasShutdownStarted) { }
        catch (Exception error) {
            // The window may disappear while a worker or its Dispatcher operation is finishing.
            // In particular, do not let an exception in this async-void error path reach WPF.
            try {
                await ApplyLatestAsync(generation, () => {
                    view.SetCanConfirm(false);
                    if (selectionStatus is not null) {
                        view.UpdateStatus(selectionStatus, "组合检查未完成，请调整选择后重试：" + error.Message, 1);
                    }
                });
            } catch (Exception) { /* A closing/unavailable UI must not reopen confirmation. */ }
        } finally {
            lock (checkGate) {
                readChecks.Remove(cancellation);
                Interlocked.CompareExchange(ref checking, null, cancellation);
                cancellation.Dispose();
            }
        }
    }
    private static Expander Details(string title, IEnumerable<string> lines, bool expanded = false) {
        StackPanel content = new();
        foreach (string line in lines.Where(line => !string.IsNullOrWhiteSpace(line)).Distinct())
            content.Children.Add(ModpackDialogController<TButton, TScroll>.Label(line, 12, color: "ColorBrushGray2", margin: new Thickness(0, 4, 0, 4)));
        Expander details = new() { Header = title, Content = content, IsExpanded = expanded, FontSize = 12, Margin = new Thickness(0, 9, 0, 0) };
        details.SetResourceReference(Control.ForegroundProperty, "ColorBrushGray2");
        return details;
    }
    // May be called by the non-UI preview owner. Drain only file-reading tasks:
    // their UI continuations can be queued behind the modal's closing animation.
    public Task CloseAndDrainAsync() {
        Task[] reads;
        lock (checkGate) {
            closed = true;
            Interlocked.Increment(ref revision);
            foreach (CancellationTokenSource source in readChecks.Keys) source.Cancel();
            iconCancellation.Cancel();
            reads = readChecks.Values.Concat(iconCache.Values).ToArray();
        }
        return DrainReadsAsync(reads);
    }
    private static async Task DrainReadsAsync(Task[] reads) {
        try { await Task.WhenAll(reads).ConfigureAwait(false); }
        catch (Exception) { /* Each review reports failure only while its preview remains open. */ }
    }
    private async Task ApplyLatestAsync(int generation, Action apply) {
        if (closed || view.Dispatcher.HasShutdownStarted || view.Dispatcher.HasShutdownFinished) return;
        await view.Dispatcher.InvokeAsync(() => {
            if (closed || generation != revision || view.Dispatcher.HasShutdownStarted) return;
            apply();
        });
    }
    private void CancelCheck() {
        try { checking?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
