// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace PCL;

internal sealed class GlassExportGroups
{
    private readonly FrameworkElement[] _options;
    private readonly List<Action> _refresh = new();
    private bool _queued;

    internal GlassExportGroups(StackPanel list)
    {
        _options = list.Children.Cast<FrameworkElement>().ToArray();
        list.Children.Clear();
        for (int i = 0; i < _options.Length; i++) {
            var head = _options[i];
            int first = i + 1;
            while (i + 1 < _options.Length && _options[i + 1].Margin.Left > 0) i++;
            var children = _options[first..(i + 1)];
            if (children.Length == 0) { list.Children.Add(head); continue; }
            list.Children.Add(Group(head, children));
        }
        void Queue(object sender, RoutedEventArgs args) {
            if (_queued) return;
            _queued = true;
            list.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => { _queued = false; Refresh(); }));
        }
        list.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(Queue));
        list.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(Queue));
        list.Loaded += Queue;
        Refresh();
    }

    // Selection follows each declared option's Visibility, independently of whether its expander is open.
    internal IEnumerable<CheckBox> ReadOptions(bool includeHidden) => Checkboxes(_options, includeHidden);

    private static IEnumerable<CheckBox> Checkboxes(IEnumerable<FrameworkElement> options, bool includeHidden)
    {
        foreach (var item in options) {
            if (!includeHidden && item.Visibility != Visibility.Visible) continue;
            if (item is CheckBox check) yield return check;
            else if (item is Panel panel)
                foreach (var sub in panel.Children.OfType<CheckBox>())
                    if (includeHidden || sub.Visibility == Visibility.Visible) yield return sub;
        }
    }

    internal void Refresh() { foreach (var update in _refresh) update(); }

    private FrameworkElement Group(FrameworkElement head, FrameworkElement[] children)
    {
        // Baseline GlassPresentation.Layout.cs, ProjectExportOptions, lines 278–332.
        var count = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var header = new Grid();
        GlassPageLayout.PreserveRowSize(header);
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        var body = new StackPanel { Margin = new Thickness(30, 0, 0, 4) };
        UniformGrid? row = null;
        foreach (var child in children) {
            if (child is UniformGrid block) {
                row = null;
                block.Margin = new Thickness();
                body.Children.Add(block);
            } else {
                if (row is null) { row = new UniformGrid { Columns = 2 }; body.Children.Add(row); }
                child.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(child);
            }
        }
        var expander = new Expander { Header = header, Content = body, IsExpanded = false,
            Style = GlassDialogControls.Style("GlassPresentationExpander") };
        AutomationProperties.SetName(expander, "导出选项分组");
        var host = new Grid { MinHeight = 36, Margin = new Thickness(0, 2, 0, 2) };
        GlassPageLayout.PreserveRowSize(host);
        head.Margin = new Thickness();
        head.VerticalAlignment = VerticalAlignment.Center;
        host.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = head });
        host.Children.Add(expander);
        _refresh.Add(() => {
            int total = 0, chosen = 0;
            foreach (var box in Checkboxes(children, false)) { total++; if (box.IsChecked == true) chosen++; }
            var parent = total > 0 ? header : host;
            if (!ReferenceEquals(head.Parent, parent)) {
                if (head.Parent is Panel previous) previous.Children.Remove(head);
                parent.Children.Insert(0, head);
            }
            expander.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            count.Text = $"已选 {chosen} / {total}";
        });
        return host;
    }
}
