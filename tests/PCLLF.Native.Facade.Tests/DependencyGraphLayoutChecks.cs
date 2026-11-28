using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCL;
using PCLCS;
using PCLLF.Presentation.Shell;

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
        ImpactDialogs((List<ModDependencyImpactView.Item>)impacts);
        GraphDialog(report, selected);
    }

    private static void ImpactDialogs(List<ModDependencyImpactView.Item> impacts) {
        string fallbackText = string.Join("\r\n", impacts.Select(item => item.Name + "：" + string.Join("；", item.Reasons)));
        object runtime = Native.Field(typeof(ModMain), "NativeMessages").GetValue(null)!;
        var messages = (MainMessageHost<ModMain.MyMsgBoxConverter, ModMain.MyMsgBoxType>)Native.Get(runtime, "host")!;
        var saved = (messages.CanPresent, messages.Queue, messages.IsUiThread, messages.DragStop);
        var queue = new ConcurrentQueue<ModMain.MyMsgBoxConverter>();
        messages.CanPresent = () => true;
        messages.Queue = () => queue;
        messages.IsUiThread = () => false;
        messages.DragStop = () => { };
        try {
            var confirm = LocalModPageRuntime.CreateHost(null).ImpactConfirm;
            foreach (string action in new[] { "禁用", "删除" }) {
                Exception? failure = null;
                bool observed = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => {
                    Native.Check(queue.TryDequeue(out var request), "The actual impact factory did not queue a dialog.");
                    try {
                        using var dialog = new GlassMessageDialog(request!, () => { }, _ => { });
                        GMeasure(dialog, 920, 660);
                        var body = ((ContentControl)Native.Get(dialog, "_nativeContentHost")!).Content as ModDependencyImpactView;
                        Native.Check(request!.Text == fallbackText && body?.Cards.Children.Count == impacts.Count,
                            "The impact factory lost its fallback or omitted affected mods.");
                        string[] text = RenderedText(dialog).ToArray();
                        string summary = $"以下 {impacts.Count} 个模组可能受影响。仍然{action}所选模组吗？";
                        Native.Check(text.Count(value => value == summary) == 1 && !text.Contains(fallbackText),
                            "The impact dialog repeated its fallback item list or lost the count confirmation.");
                        for (int index = 0; index < impacts.Count; index++) {
                            var item = impacts[index];
                            string[] cardText = RenderedText(body!.Cards.Children[index]).ToArray();
                            string reasons = string.Join("\r\n", item.Reasons);
                            Native.Check(text.Count(value => value == item.Name) == 1 && cardText.Count(value => value == reasons) == 1,
                                "An affected mod was duplicated or lost its reason text.");
                        }
                        var buttons = (Button[])Native.Get(dialog, "_buttons")!;
                        G(278, Equals(buttons[0].Foreground, buttons[0].FindResource("GlassPresentationDanger")) &&
                            Equals(buttons[0].Content, "继续" + action) && Equals(buttons[1].Content, "取消") &&
                            buttons[1].Visibility == Visibility.Visible && buttons[1].ActualHeight > 0 && request.Result is null);
                        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent, buttons[1]));
                        observed = true;
                    }
                    catch (Exception error) { failure = error; }
                    finally { request!.WaitFrame.Continue = false; }
                }));
                int answer = confirm(fallbackText, action, impacts);
                if (failure is not null) throw new InvalidOperationException("The native impact dialog check failed.", failure);
                Native.Check(observed && answer == 2 && queue.IsEmpty, "Cancel did not return from the real impact confirmation.");
            }
        }
        finally { (messages.CanPresent, messages.Queue, messages.IsUiThread, messages.DragStop) = saved; }
    }

    private static IEnumerable<string> RenderedText(DependencyObject root) {
        if (root is TextBlock text) yield return text.Text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (string childText in RenderedText(VisualTreeHelper.GetChild(root, index)))
                yield return childText;
    }

    private static void GraphDialog(TReport report, string selected) {
        string details = report.DescribeFile(selected) + "\n" + string.Concat(Enumerable.Repeat("Supplemental diagnostics\n", 48));
        LocalModPageRuntime.CreateHost(null).ContentMessage(details, "模组依赖关系", () =>
            (FrameworkElement)Activator.CreateInstance(Native.Type("PCL.ModDependencyGraphView"), Native.Flags, null, [report, selected, details], null)!);
        Native.Check(ModMain.WaitingMyMsgBox.TryDequeue(out ModMain.MyMsgBoxConverter? request) && request.ContentFactory is not null,
            "The graph entry did not queue native dependency content.");
        var converter = request!;
        using var modal = new GlassMessageDialog(converter, () => { }, _ => { });
        var caption = (ContentControl)Native.Get(modal, "_nativeContentHost")!;
        G(296, caption.Content?.GetType() == Native.Type("PCL.ModDependencyGraphView")); G(297, Equals(((Button[])Native.Get(modal, "_buttons")!)[0].Content, converter.Button1));
        Native.Call(caption.Content!.GetType(), "LoadIconsForPreview", caption.Content);
        FormMain? previous = ModMain.FrmMain; Grid smallHost = new();
        try {
            smallHost.Children.Add(modal); GMeasure(smallHost, 520, 420);
            var card = (FrameworkElement)Native.Get(modal, "_card")!;
            Button button = ((Button[])Native.Get(modal, "_buttons")!)[0];
            Point position = button.TransformToAncestor(card).Transform(new Point());
            G(315, card.ActualWidth <= 473 && card.ActualHeight <= 381 && position.Y + button.ActualHeight <= card.ActualHeight + 1);
            Grid graph = (Grid)caption.Content; ScrollViewer scroll = (ScrollViewer)graph.Children[0]; Canvas canvas = GCanvas(graph);
            G(318, scroll.ViewportWidth > 0 && scroll.ViewportHeight > 0 &&
                (canvas.Width <= scroll.ViewportWidth + 1 || scroll.ScrollableWidth > 0) && (canvas.Height <= scroll.ViewportHeight + 1 || scroll.ScrollableHeight > 0));
            Button toggle = graph.Children.OfType<Button>().Single();
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
            GMeasure(smallHost, 520, 420);
            ScrollViewer textScroll = graph.Children.OfType<ScrollViewer>().Single(view => view != scroll);
            Native.Check(textScroll.Visibility == Visibility.Visible && textScroll.ScrollableHeight > 0 &&
                textScroll.Content is TextBlock text && text.Text == details, "The full dependency details are not accessible.");
            Native.Check(!modal.IsLoaded && converter.Result is null && System.Windows.Application.Current.Windows.Count == 0, "Offscreen graph dialog entered a modal/real-window workflow or implicitly confirmed.");
        } finally { smallHost.Children.Clear(); caption.Content = null; ModMain.FrmMain = previous; }
    }
}
