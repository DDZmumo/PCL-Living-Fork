// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Inspector = PCL.ModDependencyInspector;
using PCLCS;
using PCLLF.Wpf.Motion;
using GraphController = PCLCS.ModDependencyGraphController<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ModDependencyGraphView.GraphNode>;

namespace PCL
{

    // 原 LF 图节点身份与弹窗入口；完整关系、布局、渲染及异步图标生命周期由 C# 维护。
    internal sealed class ModDependencyGraphView : Grid, IGlassDialogContent
    {

        private readonly GraphController Controller;
        internal readonly Canvas GraphCanvas;
        public double PreferredWidth
        {
            get
            {
                return Controller.PreferredWidth;
            }
        }

        internal class GraphNode : ModDependencyGraphNodeData
        {
        }

        internal ModDependencyGraphView(Inspector.InspectionReport report, string fullPath)
            : this(report, fullPath, report.DescribeFile(fullPath)) { }

        internal ModDependencyGraphView(Inspector.InspectionReport report, string fullPath, string details)
        {
            Controller = new GraphController(this, report, fullPath, new ModDependencyGraphHost()
            {
                DefaultIcon = new Func<string, ImageSource>(Name => (ImageSource)new MyBitmap(ModBase.PathImage + Name)),
                ReadIcon = ModDependencyIcon.Read
            });
            GraphCanvas = Controller.GraphCanvas;
            // The graph supplies its own summary and needs to fit the dialog's scrollable viewport.
            MaxHeight = Height;
            ClearValue(HeightProperty);
            var viewport = (ScrollViewer)Children[0];
            viewport.SetResourceReference(StyleProperty, "GlassPresentationScrollViewer");
            SmoothScroll.SetIsEnabled(viewport, true);
            ApplyGraphPalette();
            AddTextDetails(viewport, (TextBlock)Children[1], details);
        }

        private void AddTextDetails(ScrollViewer graph, TextBlock summary, string details)
        {
            var report = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 8, 0) };
            report.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
            var text = new ScrollViewer {
                Content = report, Visibility = Visibility.Collapsed,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            text.SetResourceReference(StyleProperty, "GlassPresentationScrollViewer");
            SmoothScroll.SetIsEnabled(text, true);
            SetRow(text, GetRow(graph));
            SetRowSpan(text, 2);
            Children.Add(text);

            var toggle = new Button { Content = "查看文字详情", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            toggle.SetResourceReference(StyleProperty, "GlassPresentationButton");
            toggle.Click += (_, _) => {
                bool showText = text.Visibility != Visibility.Visible;
                graph.Visibility = summary.Visibility = showText ? Visibility.Hidden : Visibility.Visible;
                if (string.IsNullOrEmpty(summary.Text)) summary.Visibility = Visibility.Collapsed;
                text.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = showText ? "查看关系图" : "查看文字详情";
            };
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetRow(toggle, RowDefinitions.Count - 1);
            Children.Add(toggle);
        }

        private void ApplyGraphPalette()
        {
            // Match the backup's graph-node and connector tones with live glass resources.
            foreach (UIElement element in GraphCanvas.Children)
            {
                switch (element)
                {
                    case Border { Tag: GraphNode node, Child: Grid content } tile:
                        bool selected = tile.BorderThickness.Left > 1;
                        bool warning = node.IsConflict || node.NeedsAttention;
                        tile.CornerRadius = new CornerRadius(9);
                        tile.SetResourceReference(Border.BackgroundProperty, selected ? "GlassPresentationSelected" : "GlassPresentationField");
                        tile.SetResourceReference(Border.BorderBrushProperty, warning ? "GlassPresentationWarnEdge" :
                            selected ? "GlassPresentationFocusEdge" : "GlassPresentationEdge");
                        var labels = (StackPanel)content.Children[1];
                        ((TextBlock)labels.Children[0]).SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
                        ((TextBlock)labels.Children[1]).SetResourceReference(TextBlock.ForegroundProperty, warning ?
                            "GlassPresentationWarnText" : "GlassPresentationSecondary");
                        break;
                    case Shape { Tag: ModDependencyGraphRelation relation } connector:
                        connector.SetResourceReference(connector is Polygon ? Shape.FillProperty : Shape.StrokeProperty, relation switch {
                            ModDependencyGraphRelation.Conflict => "GlassPresentationWarnText",
                            ModDependencyGraphRelation.Candidate => "GlassPresentationSecondary",
                            _ => "GlassPresentationAccent"
                        });
                        break;
                    case TextBlock label:
                        label.SetResourceReference(TextBlock.ForegroundProperty, label.FontWeight == FontWeights.SemiBold ?
                            "GlassPresentationText" : "GlassPresentationSecondary");
                        break;
                }
            }
            ((TextBlock)Children[1]).SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        }

        internal void LoadIconsForPreview()
        {
            Controller.LoadIconsForPreview();
        }
    }
}
