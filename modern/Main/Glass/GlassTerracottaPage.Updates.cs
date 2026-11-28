// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

internal sealed partial class GlassTerracottaPage
{
    /// <summary>
    /// 联机 → 组件更新. The first plate is the installed component: the release in use (the bundled, launcher-tested one
    /// or a newer one installed here), whether its files still match their SHA-256, and repair / return-to-bundled
    /// actions. The second asks upstream for its latest release and, when a newer one ships a verifiable package for
    /// this machine, offers to install it with the caveats spelled out. Everything goes through
    /// <see cref="TerracottaLobbyController"/>.
    /// </summary>
    private sealed class TerracottaUpdateView
    {
        private readonly GlassTerracottaPage _owner;
        private readonly TerracottaLobbyController _page;
        internal StackPanel Content { get; } = new() { Margin = new Thickness(0, 6, 0, 18) };

        // ---- Installed component ----
        private readonly TextBlock _version, _origin, _health;
        private readonly Ellipse _healthDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _verify, _repair, _bundled;
        private readonly FrameworkElement _workLoader;
        private readonly BlockCurveLoader _workSpinner;
        private readonly TextBlock _workCaption;

        // ---- Upstream ----
        private readonly Button _check, _install, _releasePage;
        private readonly FrameworkElement _checkLoader;
        private readonly BlockCurveLoader _checkSpinner;
        private readonly TextBlock _resultTitle, _resultMeta, _notes, _unavailable, _checkedAt, _error, _notice;
        private readonly Border _caution;
        private readonly StackPanel _result = new(), _newer = new();
        private TerracottaUpstream? _shown;

        internal TerracottaUpdateView(GlassTerracottaPage owner, TerracottaLobbyController page)
        {
            _owner = owner; _page = page;

            // ---------- Installed component ----------
            var component = new StackPanel();
            component.Children.Add(owner.Section("陶瓦联机组件", "联机由陶瓦联机组件完成；启动器默认使用经过适配验证的版本。"));
            var versionRow = new StackPanel { Orientation = Orientation.Horizontal };
            _version = owner.PlainText(""); _version.FontSize = 28; _version.FontWeight = FontWeights.Bold; _version.VerticalAlignment = VerticalAlignment.Center;
            _version.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            _origin = owner.PlainText(""); _origin.FontSize = 12; _origin.Margin = new Thickness(12, 4, 0, 0); _origin.VerticalAlignment = VerticalAlignment.Center;
            versionRow.Children.Add(_version); versionRow.Children.Add(_origin);
            component.Children.Add(versionRow);
            var healthRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _health = owner.Note("", 13);
            healthRow.Children.Add(_healthDot); healthRow.Children.Add(_health);
            component.Children.Add(healthRow);
            (_workLoader, _workSpinner, _workCaption) = Loader(36);
            component.Children.Add(_workLoader);
            var componentActions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            _verify = owner.ActionButton("校验文件", () => _ = _page.VerifyComponentAsync());
            _repair = owner.ActionButton("修复组件", () => _ = _page.RepairComponentAsync(), accent: true);
            _bundled = owner.ActionButton("", () => _ = _page.UseBundledAsync());
            Button back = owner.ActionButton("返回联机大厅", () => _page.ShowSection(TerracottaLobbyController.LobbySection.Lobby));
            foreach (Button button in new[] { _repair, _verify, _bundled, back }) { button.Margin = new Thickness(0, 0, 8, 8); componentActions.Children.Add(button); }
            component.Children.Add(componentActions);
            Content.Children.Add(GlassPageResources.FormPlate(component));

            // ---------- Upstream ----------
            var upstream = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel title = owner.Section("上游更新", "查询陶瓦联机在 GitHub 上的最新发布。");
            header.Children.Add(title);
            _check = owner.ActionButton("检查更新", () => _ = _page.CheckUpdatesAsync(), accent: true);
            _check.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(_check, 1); header.Children.Add(_check);
            upstream.Children.Add(header);
            (_checkLoader, _checkSpinner, _) = Loader(40, "正在查询 GitHub…");
            upstream.Children.Add(_checkLoader);

            _resultTitle = owner.PlainText(""); _resultTitle.FontSize = 18; _resultTitle.FontWeight = FontWeights.SemiBold;
            _resultTitle.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            _resultMeta = owner.Note("", 12.5); _resultMeta.Margin = new Thickness(0, 3, 0, 0);
            _result.Children.Add(_resultTitle); _result.Children.Add(_resultMeta);
            _notes = owner.PlainText(""); _notes.FontSize = 13; _notes.TextWrapping = TextWrapping.Wrap; _notes.LineHeight = 21;
            _notes.MaxHeight = 21 * 10; _notes.TextTrimming = TextTrimming.CharacterEllipsis; _notes.Margin = new Thickness(0, 12, 0, 0);
            _newer.Children.Add(_notes);
            var caution = owner.PlainText($"这个版本尚未由启动器适配。安装包会按上游提供的 SHA-256 校验；如果安装后无法正常启动，会自动改回 {TerracottaComponent.Version}。");
            caution.FontSize = 12.5; caution.TextWrapping = TextWrapping.Wrap;
            caution.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationCautionText");
            _caution = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 14, 0, 0), Child = caution };
            _caution.SetResourceReference(Border.BackgroundProperty, "GlassPresentationCautionFill");
            _caution.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationCautionEdge");
            _newer.Children.Add(_caution);
            _unavailable = owner.PlainText(""); _unavailable.FontSize = 12.5; _unavailable.TextWrapping = TextWrapping.Wrap; _unavailable.Margin = new Thickness(0, 10, 0, 0);
            _unavailable.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
            _newer.Children.Add(_unavailable);
            var upstreamActions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            _install = owner.ActionButton("安装并使用", () => _ = _page.InstallUpstreamAsync(), accent: true);
            _releasePage = owner.ActionButton("查看发布页", () => { if (_page.Upstream is { } release) ModBase.OpenWebsite(release.Page); });
            foreach (Button button in new[] { _install, _releasePage }) { button.Margin = new Thickness(0, 0, 8, 0); upstreamActions.Children.Add(button); }
            _newer.Children.Add(upstreamActions);
            _result.Children.Add(_newer);
            upstream.Children.Add(_result);
            _checkedAt = owner.Note("", 11.5); _checkedAt.Margin = new Thickness(0, 12, 0, 0); _checkedAt.Opacity = .85;
            upstream.Children.Add(_checkedAt);
            Content.Children.Add(GlassPageResources.FormPlate(upstream));

            // ---------- Outcome of the last operation ----------
            _error = owner.PlainText(""); _error.FontSize = 13; _error.TextWrapping = TextWrapping.Wrap; _error.Margin = new Thickness(4, 0, 4, 8);
            _error.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
            _notice = owner.PlainText(""); _notice.FontSize = 13; _notice.TextWrapping = TextWrapping.Wrap; _notice.Margin = new Thickness(4, 0, 4, 8);
            _notice.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationAccent");
            Content.Children.Insert(0, _notice);
            Content.Children.Insert(0, _error);
        }

        private (FrameworkElement View, BlockCurveLoader Spinner, TextBlock Caption) Loader(double size, string caption = "")
        {
            var spinner = new BlockCurveLoader { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
            spinner.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
            TextBlock text = _owner.Note(caption, 12.5); text.TextAlignment = TextAlignment.Center; text.Margin = new Thickness(0, 10, 0, 0);
            var view = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4), Visibility = Visibility.Collapsed, Children = { spinner, text } };
            return (view, spinner, text);
        }

        private static void ShowLoader(FrameworkElement view, BlockCurveLoader spinner, bool running)
        {
            if (view.Visibility == Shown(running)) return;
            view.Visibility = Shown(running);
            spinner.IsActive = running;
            if (running) StaggeredEntrance.Play(new[] { view });
        }

        internal void Pull()
        {
            TerracottaLobbyController page = _page;
            bool idle = page.IsIdle, checking = page.CheckingUpdates, working = !idle && !checking, canChange = page.CanChangeComponent;
            TerracottaRelease selected = page.SelectedRelease;

            // ---------- Installed component ----------
            _version.Text = "v" + selected.Version;
            _origin.Text = selected.IsBundled ? "启动器适配版本" : "手动安装的版本";
            _origin.SetResourceReference(TextBlock.ForegroundProperty, selected.IsBundled ? "GlassPresentationAccent" : "GlassPresentationCautionText");
            TerracottaHealth? health = page.Health;
            _health.Text = health switch
            {
                TerracottaHealth.Intact => "组件文件完好，SHA-256 校验通过",
                TerracottaHealth.Damaged => "组件文件与记录的 SHA-256 不符，建议修复",
                TerracottaHealth.Missing => "组件尚未下载，启用联机或点击下载时安装",
                _ => "尚未校验组件文件"
            };
            _healthDot.SetResourceReference(Shape.FillProperty, health switch
            {
                TerracottaHealth.Intact => "GlassPresentationAccent",
                TerracottaHealth.Damaged => "GlassPresentationDanger",
                _ => "GlassPresentationSecondary"
            });
            _workCaption.Text = page.ActivityLabel ?? "";
            ShowLoader(_workLoader, _workSpinner, working);
            _repair.Content = health == TerracottaHealth.Missing ? "下载组件" : "修复组件";
            _repair.Visibility = Shown(health is TerracottaHealth.Damaged or TerracottaHealth.Missing);
            _repair.IsEnabled = canChange;
            _repair.ToolTip = canChange ? null : "请先退出房间";
            _verify.IsEnabled = idle;
            _bundled.Content = $"改回 {TerracottaComponent.Version}";
            AutomationProperties.SetName(_bundled, $"改回 {TerracottaComponent.Version}");
            _bundled.Visibility = Shown(!selected.IsBundled);
            _bundled.IsEnabled = canChange;

            // ---------- Upstream ----------
            ShowLoader(_checkLoader, _checkSpinner, checking);
            _check.IsEnabled = idle;
            _check.Content = page.Upstream is null ? "检查更新" : "重新检查";
            TerracottaUpstream? upstream = page.Upstream;
            _result.Visibility = Shown(upstream is not null && !checking);
            if (upstream is not null)
            {
                bool newer = upstream.IsNewerThan(selected.Version);
                string date = upstream.Published is { } published ? " · 发布于 " + published.LocalDateTime.ToString("yyyy-MM-dd") : "";
                _resultTitle.Text = newer ? $"发现新版本 v{upstream.Version}" : selected.Version == upstream.Version ? "已在使用最新版本" : "当前版本不低于上游最新版";
                _resultTitle.SetResourceReference(TextBlock.ForegroundProperty, newer ? "GlassPresentationAccent" : "GlassPresentationText");
                _resultMeta.Text = $"上游最新 v{upstream.Version}{date} · 当前使用 v{selected.Version}";
                _newer.Visibility = Shown(newer);
                if (!ReferenceEquals(_shown, upstream))
                {
                    _shown = upstream;
                    _notes.Text = GlassPageResources.Preview(upstream.Notes);
                    if (newer && _owner.Live(Content)) StaggeredEntrance.Play(_result.Children.Cast<UIElement>());
                }
                _unavailable.Text = upstream.Unavailable ?? "";
                _unavailable.Visibility = Shown(upstream.Unavailable is not null);
                _install.Visibility = Shown(upstream.Package is not null);
                _install.IsEnabled = canChange;
                _install.ToolTip = canChange ? null : "请先退出房间";
            }
            _checkedAt.Text = page.CheckedAt is { } at ? "上次检查 · " + at.ToString("HH:mm") : "";
            _checkedAt.Visibility = Shown(page.CheckedAt is not null && !checking);

            _error.Text = page.UpdateError;
            _error.Visibility = Shown(page.UpdateError.Length > 0);
            _notice.Text = page.UpdateNotice;
            _notice.Visibility = Shown(page.UpdateNotice.Length > 0);
        }
    }
}
