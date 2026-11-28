// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCLLF.Wpf.Motion;
using CheckController = PCLCS.ModDependencyCheckViewController<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ResourceVersion, PCL.ModDependencyAutoFill.Item, PCL.ModDependencyActivation.Item, PCL.ModDependencyAutoFill.Plan, System.Windows.Controls.Border>;

namespace PCL;

internal sealed class GlassModDependencyCheckView : DockPanel, IGlassDialogContent
{
    public double PreferredWidth => CheckController.PreferredWidth;

    internal GlassModDependencyCheckView(string caption, ModDependencyInspector.InspectionReport report, ModDependencyAutoFill.Plan? plan)
    {
        var introduction = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 9) };
        introduction.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        SetDock(introduction, Dock.Top);
        Children.Add(introduction);

        var body = new Grid();
        var styles = GlassPageResources.GetSharedResources();
        CheckController.Build(body, new StackPanel(), report, plan, new()
        {
            VersionDisplay = version => version.Display,
            IssueKey = ModDependencyActivation.IssueKey,
            CreateCard = CreateGroup,
            AddCardChild = (border, child) => ((Grid)border.Child).Children.Add(child),
            CollapseDetails = (border, child) =>
            {
                var header = (TextBlock)((Grid)border.Child).Children[0];
                ((Grid)border.Child).Children.Remove(header);
                header.Margin = new Thickness();
                if (child is FrameworkElement content) content.Margin = new Thickness(2, 2, 2, 10);
                border.Padding = new Thickness(13, 1, 13, 1);
                border.Child = new Expander { Header = header, Content = child, Style = (Style)styles["GlassPresentationExpander"] };
            },
            EnsureCaption = _ => { },
            CreateScroll = content =>
            {
                var scroll = new ScrollViewer { Content = content, Style = (Style)styles["GlassPresentationScrollViewer"] };
                SmoothScroll.SetIsEnabled(scroll, true);
                return scroll;
            },
            CreateWarningHint = CreateWarning,
            DefaultIcon = () => (ImageSource)new MyBitmap(ModBase.PathImage + "Icons/NoIcon.png")
        });
        Children.Add(body);
    }

    private static Border CreateGroup(string title, Thickness margin)
    {
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 11, 14, 0) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var border = new Border { Child = new Grid { Children = { heading } },
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(.7), Margin = margin };
        border.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        border.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return border;
    }

    private static FrameworkElement CreateWarning(string text, Thickness margin)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationWarnText");
        var hint = new Border { Child = label, Margin = margin, Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(.7) };
        hint.SetResourceReference(Border.BackgroundProperty, "GlassPresentationWarnFill");
        hint.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationWarnEdge");
        return hint;
    }
}
