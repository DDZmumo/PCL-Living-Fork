// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCLCS;

public interface IInstanceCloneSelectionCard<TNode> where TNode : InstanceCloneGroupNodeData<TNode> {
    FrameworkElement Element { get; }
    bool IsSwapped { get; set; }
    void SetContent(Panel content);
    void SetContentBuilder(IEnumerable<TNode> items, Func<TNode, FrameworkElement> builder);
    void BuildContent();
}
public sealed class InstanceCloneSelectionHost<TInstance, TCard, TNode>
        where TInstance : class where TCard : notnull where TNode : InstanceCloneGroupNodeData<TNode> {
    public TCard HiddenCategory = default!, StarCategory = default!;
    public Func<TCard, bool> IsCollapsedCategory = null!;
    public Func<TInstance, bool> IsLocked = null!, AllowsInstance = null!;
    public Func<TInstance, string> VersionPath = null!, Name = null!, Info = null!;
    public Func<string?> SelectedPath = null!;
    public Func<TInstance, Action<object, EventArgs>, FrameworkElement> CreateRow = null!;
    public Action<object, EventArgs> InitializeRow = null!;
    public Action<object, string> SetRowInfo = null!;
    public Func<string, Thickness, IInstanceCloneSelectionCard<TNode>> CreateCard = null!;
    public Func<object, IInstanceCloneSelectionCard<TNode>> AdaptCard = null!;
    public double SwappedHeight;
}
public sealed class InstanceCloneSelectionPlan<TInstance, TCard, TNode, TSet>
        where TInstance : class where TCard : notnull where TNode : InstanceCloneGroupNodeData<TNode> where TSet : InstanceCloneGroupSetData<TNode> {
    public readonly Dictionary<TCard, List<TInstance>> VisibleCards;
    public readonly Dictionary<string, TInstance> InstancesByPath;
    public readonly string? SelectedPath;
    private readonly TSet mainGroups, starGroups;
    private readonly InstanceCloneGroupsEngine<TNode, TSet> groups;
    private readonly InstanceCloneSelectionHost<TInstance, TCard, TNode> host;
    internal InstanceCloneSelectionPlan(Dictionary<TCard, List<TInstance>> visibleCards, Dictionary<string, TInstance> instancesByPath,
        TSet mainGroups, TSet starGroups, string? selectedPath, InstanceCloneGroupsEngine<TNode, TSet> groups,
        InstanceCloneSelectionHost<TInstance, TCard, TNode> host) {
        VisibleCards = visibleCards; InstancesByPath = instancesByPath; this.mainGroups = mainGroups; this.starGroups = starGroups;
        SelectedPath = selectedPath; this.groups = groups; this.host = host;
    }
    public List<TNode> Roots(TCard card, IEnumerable<TInstance> instances) {
        TSet set = EqualityComparer<TCard>.Default.Equals(card, host.StarCategory) ? starGroups : mainGroups;
        return instances.Select(instance => groups.Find(set, host.VersionPath(instance))).Where(node => node is not null && node.ParentFolder is null).Select(node => node!).ToList();
    }
    public List<TInstance> Instances(IEnumerable<TNode> roots) => roots.SelectMany(groups.Flatten).Select(node => InstancesByPath[node.VersionFolder!]).ToList();
}

/// <summary>LF 来源分组的可见集合、真实惰性版本行和原生折叠卡片编排。</summary>
// LF PageSelectRight.xaml.vb clone-group sections at
// f4aa0fae62c3ee8af24c81e61844c8fdd13c8983. No equivalent CE feature reused.
public sealed class InstanceCloneSelectionPresenter<TInstance, TCard, TNode, TSet>
        where TInstance : class where TCard : notnull where TNode : InstanceCloneGroupNodeData<TNode> where TSet : InstanceCloneGroupSetData<TNode> {
    private readonly InstanceCloneGroupsEngine<TNode, TSet> groups;
    private readonly InstanceCloneSelectionHost<TInstance, TCard, TNode> host;
    public InstanceCloneSelectionPresenter(InstanceCloneGroupsEngine<TNode, TSet> groups, InstanceCloneSelectionHost<TInstance, TCard, TNode> host) { this.groups = groups; this.host = host; }
    public InstanceCloneSelectionPlan<TInstance, TCard, TNode, TSet> Prepare(IEnumerable<KeyValuePair<TCard, List<TInstance>>> cards, Func<bool> showHidden) {
        Dictionary<TCard, List<TInstance>> visibleCards = new();
        foreach (KeyValuePair<TCard, List<TInstance>> card in cards.ToArray()) {
            if (EqualityComparer<TCard>.Default.Equals(card.Key, host.HiddenCategory) ^ showHidden()) continue;
            visibleCards.Add(card.Key, card.Value.Where(instance => !host.IsLocked(instance) && host.AllowsInstance(instance)).ToList());
        }
        Dictionary<string, TInstance> instancesByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (TInstance instance in visibleCards.Values.SelectMany(items => items)) instancesByPath[groups.Key(host.VersionPath(instance))] = instance;
        TSet mainGroups = groups.Build(visibleCards.Where(card => !EqualityComparer<TCard>.Default.Equals(card.Key, host.StarCategory)).SelectMany(card => card.Value).Select(instance => host.VersionPath(instance)), CancellationToken.None);
        TSet starGroups = groups.Build(visibleCards.ContainsKey(host.StarCategory) ? visibleCards[host.StarCategory].Select(instance => host.VersionPath(instance)) : Enumerable.Empty<string>(), CancellationToken.None);
        string? selectedPath = host.SelectedPath();
        return new InstanceCloneSelectionPlan<TInstance, TCard, TNode, TSet>(visibleCards, instancesByPath, mainGroups, starGroups, selectedPath, groups, host);
    }
    public void RenderCategory(Panel main, TCard category, string cardName, List<TNode> roots, int count,
        InstanceCloneSelectionPlan<TInstance, TCard, TNode, TSet> plan) {
        string cardTitle = cardName + (string.Equals(cardName, "收藏夹", StringComparison.Ordinal) ? "" : " (" + count + ")");
        IInstanceCloneSelectionCard<TNode> card = host.CreateCard(cardTitle, new Thickness(0, 0, 0, 15));
        StackPanel stack = new() { Margin = new Thickness(20, host.SwappedHeight, 18, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0, 0) };
        card.SetContent(stack);
        card.SetContentBuilder(roots, node => BuildCloneChoice(node, plan.InstancesByPath, plan.SelectedPath));
        main.Children.Add(card.Element);
        if (host.IsCollapsedCategory(category) && !roots.Any(node => groups.Contains(node, plan.SelectedPath!))) card.IsSwapped = true;
        else card.BuildContent();
    }
    public FrameworkElement BuildCloneChoice(TNode node, Dictionary<string, TInstance> instancesByPath, string? selectedPath) {
        TInstance instance = instancesByPath[node.VersionFolder!];
        FrameworkElement row = host.CreateRow(instance, (item, args) => {
            host.InitializeRow(item, args);
            if (node.DeclaredSourceFolder is not null) host.SetRowInfo(item, host.Info(instance) + " · 来源：" + Path.GetFileName(node.DeclaredSourceFolder));
        });
        if (node.Children.Count == 0) return row;
        StackPanel container = new() { Tag = node.VersionFolder }; container.Children.Add(row);
        IInstanceCloneSelectionCard<TNode> card = host.CreateCard($"测试副本 ({groups.Flatten(node).Count - 1}) · 来源：{host.Name(instance)}", new Thickness(14, 3, 0, 9));
        card.Element.Tag = node.VersionFolder;
        StackPanel stack = new() { Margin = new Thickness(12, host.SwappedHeight, 12, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0, 0) };
        card.SetContent(stack);
        card.SetContentBuilder(node.Children, child => BuildCloneChoice(child, instancesByPath, selectedPath));
        if (node.Children.Any(child => groups.Contains(child, selectedPath!))) card.BuildContent(); else card.IsSwapped = true;
        container.Children.Add(card.Element);
        return container;
    }
    public void ExpandSingle(Panel main) {
        if (main.Children.Count == 1) { IInstanceCloneSelectionCard<TNode> card = host.AdaptCard(main.Children[0]); if (card.IsSwapped) card.IsSwapped = false; }
    }
}
