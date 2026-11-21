using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static partial class DependencyCases<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile,TOptions,TCache> {
    private static void GraphLayout(TReport report, string selected, string configJar, TOnlineFile peer, TOnlineProject peerMatch) {
        const string longName = "A very long installed mod name that must wrap completely rather than disappear behind an ellipsis";
        TReport large = new() { Context = report.Context }; TMod focus = GModel("large_focus", longName); large.Mods.Add(focus);
        for (int i = 0; i < 40; i++) { TMod mod = GModel("need_" + i, "Needed " + i); large.Mods.Add(mod); focus.Dependencies.Add(GRequired(mod.Id!)); }
        for (int i = 0; i < 32; i++) { TMod mod = GModel("consumer_" + i, "Consumer " + i); mod.Dependencies.Add(GRequired(focus.Id!)); large.Mods.Add(mod); }
        for (int i = 0; i < 27; i++) { TMod mod = GModel("conflict_" + i, "Conflict " + i); large.Mods.Add(mod); TDependency dependency = GRequired(mod.Id!); dependency.KindValue = ModDependencyKind.Incompatible; focus.Dependencies.Add(dependency); }
        GRebuild(large); Grid graph = GView(large, focus.RootPath!); List<Border> nodes = GNodes(graph); G(246, nodes.Count == 100); GMeasure(graph, 720, 480);
        Border longBox = nodes.Single(node => GT(node) == longName); TextBlock longTitle = (TextBlock)((Panel)((Panel)longBox.Child).Children[1]).Children[0]; G(250, longTitle.ActualHeight > 25 && longTitle.DesiredSize.Height <= longBox.ActualHeight - 8);
        ScrollViewer scroll = (ScrollViewer)graph.Children[0]; scroll.ScrollToBottom(); graph.UpdateLayout(); Border last = nodes.Single(node => GT(node) == "Needed 39"); G(253, Canvas.GetTop(last) + last.Height <= scroll.VerticalOffset + scroll.ViewportHeight + 1);
        Type impactType = Native.Type("PCL.ModDependencyImpactView"); peer.IsSupplementTarget = true; TOnlineDependency requires = new() { Platform = "Modrinth", Kind = "required", ProjectName = "Selected mod" }; requires.EnabledPaths.Add(selected); peerMatch.Dependencies.Add(requires);
        IList platformImpacts = (IList)Native.Call(impactType, "Collect", null, report, new[] { configJar })!;
        G(259, platformImpacts.Cast<object>().Count(item => (string)Native.Get(item, "Name")! == "Selected mod") == 1 && platformImpacts.Cast<object>().Count(item => (string)Native.Get(item, "Name")! == "Consumer") == 1); peerMatch.Dependencies.Clear();
        IList impacts = (IList)Native.Call(impactType, "Collect", null, large, new[] { focus.RootPath! })!; G(262, impacts.Count == 32);
        Grid impact = (Grid)Activator.CreateInstance(impactType, Native.Flags, null, [impacts, "disable"], null)!; Grid cards = (Grid)Native.Get(impact, "Cards")!; G(266, cards.ColumnDefinitions.Count == 2 && cards.Children.Count == 32);
        for (int i = 0; i < cards.Children.Count; i++) G(267, Grid.GetColumn(cards.Children[i]) == i % 2 && ((Border)cards.Children[i]).BorderThickness.Left > 0);
        // Exercise the original optional impact-preview assertion with synthetic
        // real Main records, so CI coverage does not depend on an external JAR.
        ModMain.MyMsgBoxConverter warning = new() { Title = "模组依赖影响", Text = "Impact preview", IsWarn = true, Button1 = "继续禁用", Button2 = "取消", ContentFactory = () => (FrameworkElement)Activator.CreateInstance(impactType, Native.Flags, null, [impacts, "禁用"], null)! };
        MyMsgText impactModal = new(warning); ((RotateTransform)impactModal.FindName("TransformRotate")).Angle = 0; ((TranslateTransform)impactModal.FindName("TransformPos")).Y = 0;
        G(278, ((MyButton)impactModal.FindName("Btn1")).ColorType.ToString() == "Red" && ((MyButton)impactModal.FindName("Btn2")).Text == warning.Button2 && warning.Result is null);
        Native.Check(impactModal.FindName("PanCaption") is ContentControl { Content: Grid }, "Impact confirmation did not use the real Main view.");
        GraphDialog(report, selected);
    }

    private static void GraphDialog(TReport report, string selected) {
        ModMain.MyMsgBoxConverter converter = new() { Title = "模组依赖关系", Text = report.DescribeFile(selected), ContentFactory = () => GView(report, selected) };
        MyMsgText modal = new(converter); ContentControl caption = (ContentControl)modal.FindName("PanCaption");
        G(296, caption.Content?.GetType() == Native.Type("PCL.ModDependencyGraphView")); G(297, ((MyButton)modal.FindName("Btn1")).Text == converter.Button1);
        ((RotateTransform)modal.FindName("TransformRotate")).Angle = 0; ((TranslateTransform)modal.FindName("TransformPos")).Y = 0; Native.Call(caption.Content!.GetType(), "LoadIconsForPreview", caption.Content);
        FormMain? previous = ModMain.FrmMain; Grid smallHost = new();
        try {
            GMeasure(smallHost, 520, 420);
            // Only the genuine modal-host parts are used, as in the original
            // fixture. Never call the real main-window/startup constructor.
            FormMain shell = (FormMain)RuntimeHelpers.GetUninitializedObject(typeof(FormMain)); Native.Property(typeof(FormMain), "BtnExtraBack").SetValue(shell, new MyExtraButton()); Native.Property(typeof(FormMain), "PanMsg").SetValue(shell, smallHost); ModMain.FrmMain = shell;
            Native.Call(modal.GetType(), "RefreshContentBounds", modal); smallHost.Children.Add(modal); GMeasure(smallHost, 520, 420);
            MyButton button = (MyButton)modal.FindName("Btn1"); Point position = button.TransformToAncestor(modal).Transform(new Point(0, 0)); G(315, modal.ActualWidth <= 471 && modal.ActualHeight <= 371 && position.Y + button.ActualHeight <= modal.ActualHeight + 1);
            Grid graph = (Grid)caption.Content; ScrollViewer scroll = (ScrollViewer)graph.Children[0]; Canvas canvas = GCanvas(graph);
            G(318, (canvas.Width <= scroll.ViewportWidth + 1 || scroll.ScrollableWidth > 0) && (canvas.Height <= scroll.ViewportHeight + 1 || scroll.ScrollableHeight > 0));
            GlassGraphTheme(modal, canvas);
            Native.Check(!modal.IsLoaded && converter.Result is null && System.Windows.Application.Current.Windows.Count == 0, "Offscreen graph dialog entered a modal/real-window workflow or implicitly confirmed.");
        } finally { smallHost.Children.Clear(); caption.Content = null; ModMain.FrmMain = previous; }
    }

    private static void GlassGraphTheme(MyMsgText modal, Canvas graph) {
        using var presentation = GlassPresentation.Create(modal);
        var glass = (Canvas)Native.Call(typeof(GlassPresentationSession), "Project", presentation, graph, false)!;
        var links = graph.Children.OfType<Shape>().ToArray();
        var projected = glass.Children.OfType<Shape>().ToArray();
        Native.Check(links.Length != 0 && links.Length == projected.Length, "Glass projection dropped dependency connectors.");
        foreach (bool dark in new[] { true, false }) {
            presentation.View.Resources["GlassTextBrush"] = dark ? Brushes.White : Brushes.Black;
            presentation.View.Resources["GlassAccentBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
            presentation.Refresh();
            foreach (var (source, target) in links.Zip(projected)) {
                string brush = source.Tag switch {
                    ModDependencyGraphRelation.Conflict => "GlassPresentationWarnText",
                    ModDependencyGraphRelation.Candidate => "GlassPresentationSecondary",
                    _ => "GlassPresentationAccent"
                };
                Native.Check(ReferenceEquals(target is Polygon ? target.Fill : target.Stroke, presentation.View.FindResource(brush)) &&
                    target.StrokeDashArray.SequenceEqual(source.StrokeDashArray) &&
                    (source is not Polygon points || target is Polygon arrow && arrow.Points.SequenceEqual(points.Points)),
                    "Dependency connectors lost the current palette, their dash pattern, or the arrowhead.");
            }
            Native.Check(glass.Children.OfType<Border>().All(node => node.CornerRadius.TopLeft == 9 && node.Background is SolidColorBrush),
                "Dependency cards did not adopt the projected theme's field and selection surfaces.");
        }
    }
}
