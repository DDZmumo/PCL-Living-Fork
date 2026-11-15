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
    private void RenderGraph(TReport report, List<TNode> leftNodes, List<TNode> rightNodes, List<TNode> embeddedNodes, List<TNode> conflictNodes,
        TMod? primary, string fullPath, bool useLocal, bool hasPlatform, int neededCount) {
        double centerX = leftNodes.Count == 0 ? 4.0 : 232.0, rightX = centerX + 228;
        GraphCanvas.Width = rightX + NodeWidth + 4;
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
            node.Height = Math.Max(50, Math.Ceiling(content.DesiredSize.Height) + 16);
        }
        double leftHeight = StackHeight(leftNodes), rightHeight = StackHeight(rightNodes);
        double middle = 34 + Math.Max(0, Math.Min(100, (Math.Max(leftHeight, rightHeight) - center.Height) / 2));
        double leftTop = Math.Max(34, middle + center.Height / 2 - leftHeight / 2);
        if (leftNodes.Count > 0) Header(leftNodes.Any(item => item.IsCandidate) ? "被这些模组依赖（含候选）" : "被这些模组依赖", 4, leftTop - 25);
        Header("当前模组", centerX, middle - 25);
        Header(useLocal ? "需要的依赖（本地参考）" : "需要的依赖（平台声明）", rightX, 9);
        double y = leftTop;
        foreach (TNode node in leftNodes) {
            Connector(new Point(4 + NodeWidth, y + node.Height / 2), new Point(centerX, middle + center.Height / 2), false, node.IsCandidate);
            AddNode(node, 4, y); y += node.Height + RowGap;
        }
        y = 34;
        foreach (TNode node in rightNodes) {
            Connector(new Point(centerX + NodeWidth, middle + center.Height / 2), new Point(rightX, y + node.Height / 2), false, node.IsCandidate);
            AddNode(node, rightX, y); y += node.Height + RowGap;
        }
        double conflictTop = middle + center.Height + (leftNodes.Count == 0 ? 44 : 22);
        if (embeddedNodes.Count > 0) {
            double embeddedTop = middle + center.Height + 32;
            Connector(new Point(centerX + NodeWidth / 2, middle + center.Height), new Point(centerX + NodeWidth / 2, embeddedTop - 6), false, down: true);
            Header("已内嵌模组（" + embeddedNodes.Count + "）", centerX, embeddedTop);
            y = embeddedTop + 25;
            foreach (TNode node in embeddedNodes) { AddNode(node, centerX, y); y += node.Height + RowGap; }
            conflictTop = y + 12;
        }
        Header("存在冲突", centerX, conflictTop);
        y = conflictTop + 25;
        foreach (TNode node in conflictNodes) {
            Connector(new Point(centerX + NodeWidth, middle + center.Height / 2), new Point(centerX + NodeWidth, y + node.Height / 2), true);
            AddNode(node, centerX, y); y += node.Height + RowGap;
        }
        AddNode(center, centerX, middle, true);
        if (leftNodes.Count == 0 && embeddedNodes.Count == 0) EmptyLabel("被依赖：暂无", centerX, middle + center.Height + 10);
        if (rightNodes.Count == 0) EmptyLabel("暂无", rightX, 34);
        if (conflictNodes.Count == 0) EmptyLabel("未发现", centerX, conflictTop + 25);
        GraphCanvas.Height = Math.Max(Math.Max(leftTop + leftHeight, 34 + rightHeight), conflictTop + 25 + Math.Max(20, StackHeight(conflictNodes))) + 12;
        view.Height = Math.Min(480, GraphCanvas.Height + 55);
        int candidateCount = leftNodes.Count(item => item.IsCandidate);
        TextBlock summary = new() { FontSize = 12, Foreground = ModDependencyVisualTheme.Brush("ColorBrushGray2"), Margin = new Thickness(4, 8, 4, 0), TextWrapping = TextWrapping.Wrap,
            Text = "需要 " + neededCount + " 项 · 被依赖 " + (leftNodes.Count - candidateCount) + " 项 · 冲突 " + conflictNodes.Count + " 项" };
        if (candidateCount > 0) summary.Text += " · 候选关联 " + candidateCount + " 项";
        if (embeddedNodes.Count > 0) summary.Text += " · 已内嵌 " + embeddedNodes.Count + " 项";
        if (alternatives.Count > 0) summary.Text += "\r\n此文件已禁用，当前可用：" + string.Join("、", alternatives);
        if (candidateCount > 0 || rightNodes.Any(item => item.IsCandidate)) summary.Text += "\r\n灰色虚线表示提供方待确认。";
        if (rightNodes.Any(item => item.IsPlatformRequirement)) summary.Text += "\r\n依赖是否满足按本地加载声明核对，平台信息作为补充。";
        if (report.OnlineAttempted && !hasPlatform) summary.Text += "\r\n未查到精确的平台声明，当前以本地声明作为参考。";
        if (report.OnlineTimedOut) summary.Text += "\r\n网络查询超时，当前以本地声明作为主要参考。";
        if (report.OnlineTimedOut || report.OnlineCancelled) summary.Text += "\r\n平台查询未完成，已显示的结果可能不全。";
        Grid.SetRow(summary, 1); view.Children.Add(summary);
    }
    private static double StackHeight(List<TNode> nodes) => nodes.Count == 0 ? 0 : nodes.Sum(item => item.Height) + (nodes.Count - 1) * RowGap;
    private void Header(string text, double x, double y) {
        TextBlock label = new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ModDependencyVisualTheme.Brush("ColorBrushGray2") };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); GraphCanvas.Children.Add(label);
    }
    private void EmptyLabel(string text, double x, double y) {
        TextBlock label = new() { Text = text, FontSize = 12, Foreground = ModDependencyVisualTheme.Brush("ColorBrushGray2") };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); GraphCanvas.Children.Add(label);
    }
    private static Brush Accent(bool conflict, bool possible = false) {
        if (conflict) return ModDependencyVisualTheme.Brush("ColorBrushRedDark");
        if (possible) return ModDependencyVisualTheme.Brush("ColorBrushGray3");
        return Application.Current.TryFindResource("ColorBrush3") as Brush ?? Brushes.RoyalBlue;
    }
    private ImageSource DefaultIcon(TNode node) {
        string name = node.Identity switch { "mod:minecraft" => "Blocks/Grass.png", "mod:fabricloader" => "Blocks/Fabric.png", "mod:forge" => "Blocks/Anvil.png", "mod:neoforge" => "Blocks/NeoForge.png", _ => "Icons/NoIcon.png" };
        return host.DefaultIcon(name);
    }
    private Grid NodeContent(TNode node) {
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Image icon = new() { Source = DefaultIcon(node), Width = 28, Height = 28, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, 28, 28), 5, 5) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality); content.Children.Add(icon);
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = node.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = ModDependencyVisualTheme.Brush("ColorBrushTextSecondary") });
        text.Children.Add(new TextBlock { Text = node.Condition, FontSize = 11, Foreground = Accent(node.IsConflict || node.NeedsAttention, node.IsCandidate),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Visibility = SameText(node.Condition, "") ? Visibility.Collapsed : Visibility.Visible });
        Grid.SetColumn(text, 2); content.Children.Add(text); return content;
    }
    private void AddNode(TNode node, double x, double y, bool focus = false) {
        Grid content = NodeContent(node);
        Border box = new() { Width = NodeWidth, Height = node.Height, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(focus ? 2 : 1),
            BorderBrush = Accent(node.IsConflict || node.NeedsAttention, node.IsCandidate), Background = ModDependencyVisualTheme.Brush("ColorBrushWhite"), Padding = new Thickness(8, 5, 8, 5), Tag = node,
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
        System.Windows.Shapes.Path line = new() { Data = new PathGeometry(new[] { figure }), Stroke = Accent(conflict, possible), StrokeThickness = 1.5, IsHitTestVisible = false };
        if (conflict || possible) line.StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 });
        GraphCanvas.Children.Add(line);
        GraphCanvas.Children.Add(new Polygon { Points = new PointCollection(down ? new[] { target, new Point(target.X - 4, target.Y - 6), new Point(target.X + 4, target.Y - 6) } : new[] { target, new Point(target.X + (conflict ? 6 : -6), target.Y - 4), new Point(target.X + (conflict ? 6 : -6), target.Y + 4) }),
            Fill = Accent(conflict, possible), IsHitTestVisible = false });
    }
}
