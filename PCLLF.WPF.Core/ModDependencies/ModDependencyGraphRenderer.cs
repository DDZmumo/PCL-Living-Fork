// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PCLCS;

// LF Controls/ModDependencyGraphView.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// blob 4a112a072c8cd30cfba21d272167f97c6bf5ddab, under the repository LICENCE.
// Absent from PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModLocalComp.cs/GetLogo was
// checked as C# reference (CE Plain Craft Launcher 2/LICENCE; MPL-2.0 for PCLLF-original code); there is no
// equivalent graph view. No CE implementation or graph policy is copied.
public partial class ModDependencyGraphController<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TNode>
    where TContext : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ContextData, new()
    where TMod : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ModData, new()
    where TDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.DependencyData, new()
    where TEdge : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.EdgeData, new()
    where TIssue : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IssueData, new()
    where TImpact : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ImpactData, new()
    where TReport : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ReportData, new()
    where TOnlineProject : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineProjectData, new()
    where TOnlineDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineDependencyData, new()
    where TOnlineFile : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineFileData, new()
    where TNode : ModDependencyGraphNodeData, new() {
    // Layout: up to three lanes - mods that depend on this one, this mod (with its embedded mods and conflicts below),
    // and what it needs. Each lane has a soft background and a plain-language title so the direction reads at a glance.
    private const double LaneTop = 40, LanePad = 10, ColumnGap = 64;
    private void RenderGraph(TReport report, List<TNode> leftNodes, List<TNode> rightNodes, List<TNode> embeddedNodes, List<TNode> conflictNodes,
        TMod? primary, string fullPath, bool useLocal, bool hasPlatform, int neededCount) {
        double leftX = LanePad + 4, centerX = leftNodes.Count == 0 ? leftX : leftX + NodeWidth + ColumnGap, rightX = centerX + NodeWidth + ColumnGap;
        GraphCanvas.Width = rightX + NodeWidth + LanePad + 4;
        TNode center = new() { Title = primary?.Name ?? System.IO.Path.GetFileName(fullPath),
            Condition = primary is null ? "未识别本地声明" : (primary.Enabled ? "" : "已禁用 · ") + primary.Version, IsCandidate = primary is not null && !primary.Enabled,
            FileName = primary?.RootPath, IconPath = primary?.IconPath };
        List<string> alternatives = new();
        if (primary is not null && !primary.Enabled) {
            List<string> ids = ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.Identities(primary).Keys.ToList();
            foreach (TMod provider in report.Mods.Where(item => item.Enabled && !item.IsMetadataOnly && !item.IsCandidate && !item.IncompatibleLoader && ids.Intersect(ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.Identities(item).Keys).Any())) {
                TMod container = ContainerMod(report, provider);
                string description = provider.Name + " " + provider.Version + (provider.IsNested ? "（由 " + container.Name + " 内嵌提供）" : "（已启用）");
                if (!alternatives.Contains(description)) alternatives.Add(description);
            }
            if (alternatives.Count > 0) center.Details = "此文件已禁用。当前可用的同类前置：\r\n" + string.Join("\r\n", alternatives);
        }
        foreach (TNode node in leftNodes.Concat(rightNodes).Concat(embeddedNodes).Concat(conflictNodes).Concat(new[] { center })) {
            Grid content = NodeContent(node);
            content.Measure(new Size(NodeWidth - 20, double.PositiveInfinity));
            node.Height = Math.Max(52, Math.Ceiling(content.DesiredSize.Height) + 18);
        }
        double leftHeight = StackHeight(leftNodes), rightHeight = StackHeight(rightNodes);
        double middle = LaneTop + Math.Max(0, Math.Min(100, (Math.Max(leftHeight, rightHeight) - center.Height) / 2));
        double leftTop = Math.Max(LaneTop, middle + center.Height / 2 - leftHeight / 2);
        double y = leftTop;
        var lanes = new List<(double X, string Title)>();
        if (leftNodes.Count > 0) lanes.Add((leftX, leftNodes.Any(item => item.IsCandidate) ? "依赖它的模组（含待确认）" : "依赖它的模组"));
        lanes.Add((centerX, "当前模组"));
        lanes.Add((rightX, useLocal ? "它需要的前置" : "它需要的前置（平台声明）"));
        foreach (TNode node in leftNodes) {
            Connector(new Point(leftX + NodeWidth, y + node.Height / 2), new Point(centerX, middle + center.Height / 2), false, node.IsCandidate);
            AddNode(node, leftX, y); y += node.Height + RowGap;
        }
        y = LaneTop;
        foreach (TNode node in rightNodes) {
            Connector(new Point(centerX + NodeWidth, middle + center.Height / 2), new Point(rightX, y + node.Height / 2), false, node.IsCandidate);
            AddNode(node, rightX, y); y += node.Height + RowGap;
        }
        double conflictTop = middle + center.Height + (leftNodes.Count == 0 ? 44 : 26);
        if (embeddedNodes.Count > 0) {
            double embeddedTop = middle + center.Height + 34;
            Connector(new Point(centerX + NodeWidth / 2, middle + center.Height), new Point(centerX + NodeWidth / 2, embeddedTop - 6), false, down: true);
            Header("内嵌在其中（" + embeddedNodes.Count + "）", centerX + 4, embeddedTop);
            y = embeddedTop + 24;
            foreach (TNode node in embeddedNodes) { AddNode(node, centerX, y); y += node.Height + RowGap; }
            conflictTop = y + 14;
        }
        Header(conflictNodes.Count == 0 ? "冲突" : "与它冲突（" + conflictNodes.Count + "）", centerX + 4, conflictTop);
        y = conflictTop + 24;
        foreach (TNode node in conflictNodes) {
            Connector(new Point(centerX + NodeWidth, middle + center.Height / 2), new Point(centerX + NodeWidth, y + node.Height / 2), true);
            AddNode(node, centerX, y); y += node.Height + RowGap;
        }
        AddNode(center, centerX, middle, true);
        if (leftNodes.Count == 0 && embeddedNodes.Count == 0) EmptyLabel("没有模组依赖它", centerX + 4, middle + center.Height + 10);
        if (rightNodes.Count == 0) EmptyLabel("不需要额外前置", rightX + 4, LaneTop + 4);
        if (conflictNodes.Count == 0) EmptyLabel("未发现冲突", centerX + 4, conflictTop + 24);
        GraphCanvas.Height = Math.Max(Math.Max(leftTop + leftHeight, LaneTop + rightHeight), conflictTop + 24 + Math.Max(20, StackHeight(conflictNodes))) + 16;
        // Lanes go behind everything else: soft rounded columns with their title at the top.
        int laneIndex = 0;
        foreach (var lane in lanes) {
            Rectangle background = new() { Width = NodeWidth + LanePad * 2, Height = GraphCanvas.Height - 8, RadiusX = 12, RadiusY = 12, Opacity = .55, IsHitTestVisible = false };
            background.SetResourceReference(Shape.FillProperty, "GlassPresentationField");
            Canvas.SetLeft(background, lane.X - LanePad); Canvas.SetTop(background, 4);
            GraphCanvas.Children.Insert(laneIndex++, background);
            Header(lane.Title, lane.X + 4, 13);
        }
        view.Height = Math.Min(680, Math.Max(480, GraphCanvas.Height + 190));

        // Lead above the graph: what this mod needs, who needs it, and whether anything conflicts.
        int candidateCount = leftNodes.Count(item => item.IsCandidate);
        int attention = rightNodes.Count(item => item.NeedsAttention);
        ModDependencyTone tone = conflictNodes.Count > 0 ? ModDependencyTone.Danger : attention > 0 ? ModDependencyTone.Caution : ModDependencyTone.Accent;
        string headline = conflictNodes.Count > 0 ? "与 " + conflictNodes.Count + " 个模组冲突" :
            attention > 0 ? attention + " 个前置已禁用，需要启用" : "依赖关系一览";
        var chips = new List<Border> {
            ModDependencyVisuals.Chip("需要 " + neededCount + " 个前置", neededCount > 0 ? ModDependencyTone.Accent : ModDependencyTone.Neutral),
            ModDependencyVisuals.Chip("被 " + (leftNodes.Count - candidateCount) + " 个模组依赖", ModDependencyTone.Neutral)
        };
        if (candidateCount > 0) chips.Add(ModDependencyVisuals.Chip(candidateCount + " 个关联待确认", ModDependencyTone.Neutral));
        if (embeddedNodes.Count > 0) chips.Add(ModDependencyVisuals.Chip("内嵌 " + embeddedNodes.Count + " 个", ModDependencyTone.Neutral));
        chips.Add(ModDependencyVisuals.Chip(conflictNodes.Count == 0 ? "无冲突" : "冲突 " + conflictNodes.Count, conflictNodes.Count > 0 ? ModDependencyTone.Danger : ModDependencyTone.Neutral));
        Border lead = ModDependencyVisuals.Lead(conflictNodes.Count > 0 ? "✕" : attention > 0 ? "!" : "⇄", tone, headline, chips);
        lead.Margin = new Thickness(0, 0, 0, 10);
        StackPanel top = new() { Children = { lead, Legend() } };

        List<string> notes = new();
        if (alternatives.Count > 0) notes.Add("此文件已禁用，当前可用：" + string.Join("、", alternatives));
        if (rightNodes.Any(item => item.IsPlatformRequirement)) notes.Add("依赖是否满足按本地加载声明核对，平台信息作为补充。");
        if (report.OnlineAttempted && !hasPlatform) notes.Add("未查到精确的平台声明，当前以本地声明作为参考。");
        if (report.OnlineTimedOut) notes.Add("网络查询超时，当前以本地声明作为主要参考。");
        if (report.OnlineTimedOut || report.OnlineCancelled) notes.Add("平台查询未完成，已显示的结果可能不全。");
        TextBlock summary = ModDependencyVisuals.Text(string.Join("\r\n", notes), 12, false, "GlassPresentationSecondary");
        summary.Margin = new Thickness(4, notes.Count == 0 ? 0 : 8, 4, 0);
        // The notes stay the view's second child (hosts read it); the lead follows.
        Grid.SetRow(summary, 2); view.Children.Add(summary);
        Grid.SetRow(top, 0); view.Children.Add(top);
    }

    // What the lines mean, shown once above the graph instead of explained in the notes.
    private static WrapPanel Legend() {
        WrapPanel legend = new() { Margin = new Thickness(4, 0, 0, 8) };
        void Item(string caption, string key, bool dashed) {
            Line sample = new() { X1 = 0, Y1 = 6, X2 = 22, Y2 = 6, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            sample.SetResourceReference(Shape.StrokeProperty, key);
            if (dashed) sample.StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 });
            TextBlock text = ModDependencyVisuals.Text(caption, 12, false, "GlassPresentationSecondary");
            text.VerticalAlignment = VerticalAlignment.Center;
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0), Children = { sample, text } });
        }
        Item("已确认的依赖", "GlassPresentationAccent", false);
        Item("提供方待确认", "GlassPresentationSecondary", true);
        Item("冲突", "GlassPresentationDangerText", true);
        return legend;
    }
    private static double StackHeight(List<TNode> nodes) => nodes.Count == 0 ? 0 : nodes.Sum(item => item.Height) + (nodes.Count - 1) * RowGap;
    private void Header(string text, double x, double y) {
        TextBlock label = new() { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); GraphCanvas.Children.Add(label);
    }
    private void EmptyLabel(string text, double x, double y) {
        TextBlock label = new() { Text = text, FontSize = 12 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); GraphCanvas.Children.Add(label);
    }
    private static Brush Accent(bool conflict, bool possible = false) {
        if (conflict) return ModDependencyVisualTheme.Brush("ColorBrushRedDark");
        if (possible) return ModDependencyVisualTheme.Brush("ColorBrushGray3");
        return Application.Current.TryFindResource("ColorBrush3") as Brush ?? Brushes.RoyalBlue;
    }
    // Status of a node as a badge: conflict ✕, disabled prerequisite !, unconfirmed ?, otherwise none.
    private static (string Glyph, ModDependencyTone Tone)? Status(TNode node) =>
        node.IsConflict ? ("✕", ModDependencyTone.Danger) :
        node.NeedsAttention ? ("!", ModDependencyTone.Caution) :
        node.IsCandidate ? ("?", ModDependencyTone.Neutral) : null;
    // Provenance prefixes stay in the data and tooltip; the node itself shows only what differs between nodes.
    private static string? ShownCondition(string? condition) => condition?.Replace("本地参考 · ", "").Replace("平台前置 · ", "平台 · ");
    private ImageSource DefaultIcon(TNode node) {
        string name = node.Identity switch { "mod:minecraft" => "Blocks/Grass.png", "mod:fabricloader" => "Blocks/Fabric.png", "mod:forge" => "Blocks/Anvil.png", "mod:neoforge" => "Blocks/NeoForge.png", _ => "Icons/NoIcon.png" };
        return host.DefaultIcon(name);
    }
    private Grid NodeContent(TNode node) {
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Image icon = new() { Source = DefaultIcon(node), Width = 30, Height = 30, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 6, 6) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality); content.Children.Add(icon);
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        string? shown = ShownCondition(node.Condition);
        text.Children.Add(new TextBlock { Text = node.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = ModDependencyVisualTheme.Brush("ColorBrushTextSecondary") });
        text.Children.Add(new TextBlock { Tag = node, Text = shown, FontSize = 11.5, Foreground = Accent(node.IsConflict || node.NeedsAttention, node.IsCandidate),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Visibility = SameText(shown, "") ? Visibility.Collapsed : Visibility.Visible });
        Grid.SetColumn(text, 2); content.Children.Add(text);
        if (Status(node) is { } status) {
            Grid badge = ModDependencyVisuals.Badge(status.Glyph, status.Tone, 18);
            badge.Margin = new Thickness(6, 0, 0, 0);
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 3); content.Children.Add(badge);
        }
        return content;
    }
    private void AddNode(TNode node, double x, double y, bool focus = false) {
        Grid content = NodeContent(node);
        Border box = new() { Width = NodeWidth, Height = node.Height, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(focus ? 2 : 1),
            BorderBrush = Accent(node.IsConflict || node.NeedsAttention, node.IsCandidate), Background = ModDependencyVisualTheme.Brush("ColorBrushWhite"), Padding = new Thickness(9, 7, 9, 7), Tag = node,
            ToolTip = node.Title + (SameText(node.Condition, "") ? "" : "\r\n" + node.Condition) + (string.IsNullOrEmpty(node.Details) ? "" : "\r\n" + node.Details), Child = content };
        if (focus) box.SetResourceReference(Border.BackgroundProperty, "ColorBrush8");
        Canvas.SetLeft(box, x); Canvas.SetTop(box, y); GraphCanvas.Children.Add(box);
        if (!string.IsNullOrEmpty(node.FileName) && !string.IsNullOrEmpty(node.IconPath)) iconImages.Add(box, (Image)content.Children[0]);
    }
    private static string IconKey(TNode node) => FilePathKey(node.FileName!).ToUpperInvariant() + "|" + node.IconPath;
    private void RefreshVisibleIcons() {
        if (!view.IsLoaded) return;
        foreach (KeyValuePair<Border, Image> pair in iconImages) {
            Border box = pair.Key; TNode node = (TNode)box.Tag;
            double top = Canvas.GetTop(box);
            if (top + box.Height < graphScroll.VerticalOffset || top > graphScroll.VerticalOffset + graphScroll.ViewportHeight) continue;
            if (iconCache.TryGetValue(IconKey(node), out ImageSource? picture)) { if (picture is not null) pair.Value.Source = picture; }
            else if (pendingIcons.Add(box)) LoadIconAsync(box, node, iconGeneration);
        }
    }
    private async void LoadIconAsync(Border box, TNode node, int generation) {
        ImageSource? picture = null;
        try {
            await IconSlots.WaitAsync();
            try { picture = await Task.Run(() => host.ReadIcon(node.FileName!, node.IconPath!)); }
            finally { IconSlots.Release(); }
            if (view.Dispatcher.HasShutdownStarted) return;
            await view.Dispatcher.InvokeAsync(() => {
                pendingIcons.Remove(box);
                if (generation == iconGeneration && view.IsLoaded) { iconCache[IconKey(node)] = picture; if (picture is not null) iconImages[box].Source = picture; }
                RefreshVisibleIcons();
            });
        } catch { /* Icon failures retain the default image and never change dependency relations. */ }
    }
    public void LoadIconsForPreview() {
        foreach (KeyValuePair<Border, Image> pair in iconImages) {
            TNode node = (TNode)pair.Key.Tag;
            ImageSource? picture = host.ReadIcon(node.FileName!, node.IconPath!);
            if (picture is not null) pair.Value.Source = picture;
        }
    }
    private void Connector(Point from, Point target, bool conflict, bool possible = false, bool down = false) {
        PathFigure figure = new() { StartPoint = from };
        double half = (target.X - from.X) / 2;
        if (down) figure.Segments.Add(new LineSegment(target, true));
        else figure.Segments.Add(new BezierSegment(new Point(from.X + (conflict ? 26 : half), from.Y), new Point(target.X + (conflict ? 26 : -half), target.Y), target, true));
        ModDependencyGraphRelation relation = conflict ? ModDependencyGraphRelation.Conflict :
            possible ? ModDependencyGraphRelation.Candidate : ModDependencyGraphRelation.Confirmed;
        Brush brush = Accent(conflict, possible);
        System.Windows.Shapes.Path line = new() { Tag = relation, Data = new PathGeometry(new[] { figure }), Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = possible ? .7 : 1 };
        if (conflict || possible) line.StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.5 });
        GraphCanvas.Children.Add(line);
        GraphCanvas.Children.Add(new Polygon { Points = new PointCollection(down ? new[] { target, new Point(target.X - 4, target.Y - 6), new Point(target.X + 4, target.Y - 6) } : new[] { target, new Point(target.X + (conflict ? 6 : -6), target.Y - 4), new Point(target.X + (conflict ? 6 : -6), target.Y + 4) }),
            Tag = relation, Fill = brush, IsHitTestVisible = false });
    }
}
