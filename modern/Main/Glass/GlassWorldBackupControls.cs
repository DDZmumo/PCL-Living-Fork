// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using Instance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal sealed class GlassWorldBackupSettings : ContentControl
{
    private readonly ComboBox _source, _policy;
    private readonly CheckBox _auto;
    private readonly TextBox _retention;
    private readonly StackPanel _overrides, _count;
    private Instance? _owner;
    private bool _binding, _dirty;

    internal GlassWorldBackupSettings(GlassPageResources resources)
    {
        var body = new StackPanel();
        _source = Choice("跟随全局设置", "实例独立配置");
        body.Children.Add(Field("配置来源", _source));
        _overrides = new StackPanel();
        _auto = new CheckBox { Content = "正常退出游戏或更新 Minecraft 前自动备份世界", Style = resources.Style("GlassPresentationCheckBox"),
            Margin = new Thickness(0, 6, 0, 8) };
        _overrides.Children.Add(_auto);
        _policy = Choice("全部保留", "保留最近若干份");
        _overrides.Children.Add(Field("保留策略", _policy));
        _retention = new TextBox { Width = 68, MaxLength = 2, Style = resources.Style("GlassPresentationTextBox"),
            ToolTip = "保留 1 到 50 份自动备份。手动备份不计入上限。" };
        _count = Field("保留份数", _retention);
        _overrides.Children.Add(_count);
        body.Children.Add(_overrides);
        var header = resources.Text("存档自动备份");
        header.FontWeight = FontWeights.SemiBold;
        var choice = new Expander { Header = header, Content = body, Style = resources.Style("GlassPresentationExpander") };
        Content = GlassPageResources.Plate(choice, new Thickness(0, 0, 0, 12));
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Focusable = false;
        _source.SelectionChanged += (_, _) =>
        {
            if (_binding || _owner is not { } owner || _source.SelectedIndex < 0) return;
            int mode = _source.SelectedIndex;
            if (Commit()) BackupPreferences.SetWorldMode(owner, mode);
            Bind(owner);
        };
        _auto.Click += (_, _) =>
        {
            if (_owner is not null && !_binding && _source.SelectedIndex == 1)
                Settings.Set("VersionWorldBackupAutoEnabled", _auto.IsChecked == true, _owner);
        };
        _policy.SelectionChanged += (_, _) =>
        {
            if (_owner is null || _binding || _source.SelectedIndex != 1 || _policy.SelectedIndex < 0) return;
            _dirty = false;
            Settings.Set("VersionWorldBackupKeepAll", _policy.SelectedIndex == 0, _owner);
            Bind(_owner);
        };
        _retention.TextChanged += (_, _) =>
        {
            if (!_binding && _owner is not null && _overrides.IsVisible && _count.IsVisible && _retention.IsKeyboardFocusWithin) _dirty = true;
        };
        _retention.LostKeyboardFocus += (_, _) => Commit();
        _retention.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        Bind(null);

        ComboBox Choice(params string[] options) => new() { ItemsSource = options, MinWidth = 220, Style = resources.Style("GlassPresentationComboBox") };
        StackPanel Field(string label, UIElement editor)
        {
            var text = resources.Note(label, 13);
            text.Margin = new Thickness(0, 0, 0, 5);
            return new StackPanel { Children = { text, editor }, Margin = new Thickness(0, 4, 0, 8) };
        }
    }

    internal void Bind(Instance? instance)
    {
        bool preserve = instance is not null && _owner is not null && _dirty && _retention.IsKeyboardFocusWithin &&
            PathUtils.IsSame(instance.PathVersion, _owner.PathVersion);
        _owner = instance;
        _binding = true;
        try
        {
            bool independent = instance is not null && BackupPreferences.WorldMode(instance) == 1;
            bool keepAll = instance is not null && Settings.Get<bool>("VersionWorldBackupKeepAll", instance);
            IsEnabled = instance is not null;
            _source.SelectedIndex = independent ? 1 : 0;
            _policy.SelectedIndex = keepAll ? 0 : 1;
            _auto.IsChecked = instance is not null && Settings.Get<bool>("VersionWorldBackupAutoEnabled", instance);
            _overrides.Visibility = independent ? Visibility.Visible : Visibility.Collapsed;
            _count.Visibility = keepAll ? Visibility.Collapsed : Visibility.Visible;
            if (!preserve)
            {
                _retention.Text = (instance is null ? 5 : Settings.Get<int>("VersionWorldBackupRetention", instance)).ToString();
                _dirty = false;
            }
        }
        finally { _binding = false; }
    }

    internal bool Commit()
    {
        if (_owner is not { } instance || _binding || !_dirty) return true;
        _dirty = false;
        if (BackupPreferences.WorldMode(instance) != 1 || Settings.Get<bool>("VersionWorldBackupKeepAll", instance)) { Bind(instance); return true; }
        int previous = Settings.Get<int>("VersionWorldBackupRetention", instance);
        bool valid = int.TryParse(_retention.Text, out int count) && count is >= 1 and <= 50;
        if (valid && count != previous) Settings.Set("VersionWorldBackupRetention", count, instance);
        _binding = true;
        try { _retention.Text = (valid ? count : previous).ToString(); }
        finally { _binding = false; }
        if (!valid) ModMain.Hint("每个世界保留的备份份数必须为 1 到 50。", ModMain.HintType.Red);
        return valid;
    }
}

internal sealed class GlassWorldBackupGroup : ContentControl, IResourcePageBackupGroup<Instance>
{
    private readonly GlassPageResources _resources;
    private readonly Action _changed;
    private readonly Func<bool> _beforeCreate;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly StackPanel _entries = new();
    private readonly TextBlock _title, _status;
    private readonly Button _create;
    private readonly Expander _expand;
    private List<JObject> _points = new();
    private Instance? _owner;
    private string? _prefix;
    private bool _rendered;
    public FrameworkElement Element => this;
    public string FolderName { get; private set; } = "";
    public IEnumerable<string> SelectedIds => _selected;

    internal GlassWorldBackupGroup(GlassPageResources resources, Action changed, Func<bool> beforeCreate)
    {
        _resources = resources;
        _changed = changed;
        _beforeCreate = beforeCreate;
        _title = resources.Text("");
        _create = Button("立即备份", Create);
        _status = resources.Note("", 12);
        _status.Margin = new Thickness(0, 6, 0, 7);
        var content = new StackPanel { Children = { _create, _status, _entries } };
        _expand = new Expander { Header = _title, Content = content, Style = resources.Style("GlassPresentationExpander") };
        _expand.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, _expand)) BuildRows(); };
        Content = GlassPageResources.Card(_expand, GlassPageLayout.CardRole.Expander, GlassPageLayout.Mode.Settings);
        Margin = new Thickness(35, 4, 0, 10);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Focusable = false;
    }

    public void Bind(Instance instance, string name, List<JObject> points)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("存档文件夹名称无效。", nameof(name));
        bool sameWorld = ReferenceEquals(_owner, instance) && string.Equals(FolderName, name, StringComparison.OrdinalIgnoreCase);
        if (!sameWorld) _selected.Clear();
        _owner = instance;
        FolderName = name;
        _prefix = null;
        var shown = points.Where(point => string.Equals((string?)point["world"], name, StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch((string?)point["id"] ?? "", @"\A[a-f0-9]{32}\z"))
            .GroupBy(point => (string)point["id"]!, StringComparer.Ordinal).Select(group => group.First())
            .OrderByDescending(point => point["created"]!.ToObject<DateTime>()).ToList();
        int oldCount = _selected.Count;
        _selected.IntersectWith(shown.Select(point => (string)point["id"]!));
        try { _ = QuickPlayWorlds.WorldPath(instance.PathIndie, name); _create.IsEnabled = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { _create.IsEnabled = false; }
        _status.Text = _create.IsEnabled ? shown.Count == 0 ? "暂无备份，可先备份此存档。" : "按备份时间从新到旧排列。" : "原存档已不存在，可从以下备份恢复。";
        if (!sameWorld || _points.Count != shown.Count || !_points.Zip(shown, JToken.DeepEquals).All(equal => equal))
        {
            _points = shown;
            _entries.Children.Clear();
            _rendered = false;
            if (_expand.IsExpanded) BuildRows();
        }
        UpdateTitle();
        if (oldCount != _selected.Count) _changed();
    }

    public void PrefixTitle(string name) { _prefix = name; UpdateTitle(); }
    private void UpdateTitle() => _title.Text = (_prefix is null ? "" : _prefix + " · ") + $"备份（{_points.Count}）";
    public void ClearSelection(bool animate)
    {
        _selected.Clear();
        foreach (var check in _entries.Children.OfType<WrapPanel>().SelectMany(row => row.Children.OfType<CheckBox>())) check.IsChecked = false;
    }

    private void BuildRows()
    {
        if (_rendered) return;
        _rendered = true;
        foreach (JObject point in _points)
        {
            string id = (string)point["id"]!;
            DateTime created = point["created"]!.ToObject<DateTime>().ToLocalTime();
            var row = new WrapPanel { Margin = new Thickness(0, 3, 0, 4) };
            var check = new CheckBox { Content = created.ToString("yyyy-MM-dd HH:mm:ss"), IsChecked = _selected.Contains(id),
                Style = _resources.Style("GlassPresentationCheckBox"), Width = 181 };
            check.Click += (_, _) => { if (check.IsChecked == true) _selected.Add(id); else _selected.Remove(id); _changed(); };
            row.Children.Add(check);
            row.Children.Add(Button("恢复", () => WorldBackupsUi.Start(_owner, RestoreId: id)));
            row.Children.Add(Button("详情", () => ShowPoint(point)));
            row.Children.Add(Button("文件夹", () => OpenPoint(id)));
            row.Children.Add(Button("删除", () => WorldBackupsUi.Start(_owner, DeleteIds: new[] { id })));
            _entries.Children.Add(row);
        }
    }

    private void Create()
    {
        if (_owner is not null && _create.IsEnabled && _beforeCreate()) WorldBackupsUi.Start(_owner, WorldIdentifier: FolderName);
    }

    private void ShowPoint(JObject point)
    {
        int count = (int?)point["fileCount"] ?? (point["files"] as JObject)?.Count ?? 0;
        ModMain.MyMsgBox($"世界：{point["label"]}\n文件夹：{FolderName}\n备份时间：{point["created"]!.ToObject<DateTime>().ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
            $"备份原因：{point["reason"]}\n存档大小：{StringUtils.FormatByteSize((long?)point["bytes"] ?? 0)}\n压缩大小：{StringUtils.FormatByteSize((long?)point["archiveBytes"] ?? 0)}\n文件数量：{count}" +
            "\n\n恢复时会校验完整备份，并先自动保存当前世界。", "存档备份详情");
    }

    private void OpenPoint(string id)
    {
        if (_owner is null || !Regex.IsMatch(id, @"\A[a-f0-9]{32}\z")) return;
        try
        {
            string root = WorldBackups.Store(_owner.PathIndie);
            string folder = Path.Combine(root, "points", id);
            InstanceResourceStore.EnsureWithin(root, folder);
            if (Directory.Exists(folder)) ModBase.OpenExplorer(folder);
            else ModMain.Hint("此备份已移动或删除，请刷新存档列表。");
        }
        catch (Exception error) { Logger.Warn(error, "打开存档备份目录失败"); ModMain.Hint(error.Message, ModMain.HintType.Red); }
    }

    private Button Button(string text, Action run)
    {
        var button = new Button { Content = text, Style = _resources.Style("GlassPresentationButton"), Margin = new Thickness(0, 0, 7, 0) };
        button.Click += (_, _) => run();
        return button;
    }
}
