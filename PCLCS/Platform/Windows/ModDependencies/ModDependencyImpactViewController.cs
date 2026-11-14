// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCLCS;

// LF Controls/ModDependencyImpactView.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged at the C016 start; MPL-2.0 for PCLLF-original code. No PCL 5ef81e4 counterpart exists.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModCompDependency.cs was reviewed
// (CE Plain Craft Launcher 2/LICENCE; MPL-2.0 for PCLLF-original code); platform candidates remain references, not new hard constraints.
public static class ModDependencyImpactViewController<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TItem>
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
    where TItem : ModDependencyImpactItemData, new() {
    public const double PreferredWidth = 760;

    public static List<TItem> Collect(TReport report, IEnumerable<string> paths) {
        HashSet<string> removed = new(paths.Select(FilePathKey), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TItem> result = new(StringComparer.OrdinalIgnoreCase);
        Action<string, string> add = (fileName, reason) => {
            string key = FilePathKey(fileName);
            if (!result.TryGetValue(key, out TItem? entry)) {
                TMod? owner = report.Mods.FirstOrDefault(modInfo => !modInfo.IsNested && SameFilePath(modInfo.RootPath, fileName));
                entry = new TItem { Name = owner?.Name ?? Path.GetFileName(fileName), FileName = fileName,
                    Icon = ModDependencyIconFiles.Read(fileName, owner?.IconPath) };
                result.Add(key, entry);
            }
            if (!entry.Reasons.Contains(reason)) entry.Reasons.Add(reason);
        };
        foreach (TImpact impact in ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.GetImpacts(report, paths)) {
            add(impact.Mod.RootPath, "本地参考 · " + (impact.IsDefinite ? "" : "待确认 · ") + impact.Reason);
        }
        HashSet<string> unavailable = new(removed, StringComparer.OrdinalIgnoreCase);
        bool changed = true;
        while (changed) {
            changed = false;
            foreach (TOnlineFile file in report.OnlineFiles.Where(value => value.Enabled && value.IsSupplementTarget && !unavailable.Contains(FilePathKey(value.FullPath)))) {
                foreach (TOnlineDependency requirement in file.Matches.Where(match => match.IsExact).SelectMany(match => match.Dependencies)
                    .Where(value => value.Kind == "required" && value.EnabledPaths.Count > 0 && value.EnabledPaths.All(path => unavailable.Contains(FilePathKey(path))))) {
                    bool direct = requirement.EnabledPaths.All(path => removed.Contains(FilePathKey(path)));
                    add(file.FullPath, (direct ? "" : "间接受影响 · ") + requirement.Platform + " 平台前置：" + (requirement.ProjectName ?? requirement.ProjectId));
                    if (unavailable.Add(FilePathKey(file.FullPath))) changed = true;
                }
            }
        }
        return result.Values.ToList();
    }

    public static void Build(Grid view, Grid cards, List<TItem> items, string actionName, Func<ImageSource> defaultIcon) {
        view.Height = Math.Min(440, 55 + Math.Ceiling(items.Count / 2.0) * 100);
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        view.Children.Add(new TextBlock { Text = "以下 " + items.Count + " 个模组可能受影响。仍然" + actionName + "所选模组吗？", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 10), FontSize = 13, Foreground = ModDependencyVisualTheme.Brush("ColorBrushTextSecondary") });
        cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
        ScrollViewer scroll = new() { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); view.Children.Add(scroll);
        for (int index = 0; index < items.Count; index++) {
            if (index % 2 == 0) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TItem entry = items[index];
            Grid content = new();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new Image { Source = entry.Icon ?? defaultIcon(), Width = 28, Height = 28, Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Top, Clip = new RectangleGeometry(new Rect(0, 0, 28, 28), 5, 5) });
            StackPanel text = new();
            text.Children.Add(new TextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ModDependencyVisualTheme.Brush("ColorBrushTextSecondary") });
            TextBlock reason = new() { Text = string.Join("\r\n", entry.Reasons), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
            reason.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedDark"); text.Children.Add(reason);
            Grid.SetColumn(text, 2); content.Children.Add(text);
            Border box = new() { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Background = ModDependencyVisualTheme.Brush("ColorBrushWhite"), Padding = new Thickness(9), Margin = new Thickness(4), MinHeight = 64, Tag = entry, ToolTip = entry.FileName };
            box.SetResourceReference(Border.BorderBrushProperty, "ColorBrushRedLight");
            Grid.SetColumn(box, index % 2); Grid.SetRow(box, index / 2); cards.Children.Add(box);
        }
    }

    private static string FilePathKey(string path) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.FilePathKey(path);
    private static bool SameFilePath(string first, string second) => ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.SameFilePath(first, second);
}
