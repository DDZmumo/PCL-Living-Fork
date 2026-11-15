using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

// LF Resource/MyResourceItem.xaml.vb f4aa0fae62c3ee8af24c81e61844c8fdd13c8983, LF LICENCE.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Comp/MyCompItem.xaml.cs was reviewed
// (their Plain Craft Launcher 2/LICENCE). LF's current-page context and existing mouse/animation behavior are retained.
public sealed class ResourceItemController<TProject, TResult, TRequest, TInstance, TState, TCard, TAnimation>
    where TProject : ResourceProjectModelData
    where TResult : ResourceSearchResultData<TProject>
    where TRequest : ResourceSearchRequestData<TProject, TResult>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum {
    private readonly ResourceItemHost<TProject, TRequest, TInstance> host;
    private readonly DownloadPageAnimationHost<TAnimation> animation;
    private bool mouseDown;
    private string? stateLast;
    private Border? rectBack;
    public bool CanInteraction = true;
    public ResourceItemController(ResourceItemHost<TProject, TRequest, TInstance> host, DownloadPageAnimationHost<TAnimation> animation) { this.host = host; this.animation = animation; }
    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    public string Title { get => host.View().Title.Text; set { if (!Same(host.View().Title.Text, value)) host.View().Title.Text = value; } }
    public string SubTitle {
        get => host.View().RawTitle?.Text ?? "";
        set {
            TextBlock raw = host.View().RawTitle;
            if (Same(raw.Text, value)) return;
            raw.Text = value; raw.Visibility = Same(value, "") ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    public string Description { get => host.View().Info.Text; set { if (!Same(host.View().Info.Text, value)) host.View().Info.Text = value; } }
    public void ShowDescriptionToolTip() {
        ResourceItemView view = host.View();
        view.InfoToolTip.Content = view.Info.Text; view.Info.ToolTip = host.TextTrimmed(view.Info) ? view.InfoToolTip : null;
    }
    public void SetTags(List<string> tags) {
        Panel panel = host.View().Tags; panel.Children.Clear(); panel.Visibility = tags.Any() ? Visibility.Visible : Visibility.Collapsed;
        foreach (string text in tags) {
            Border border = new() { Background = host.Brush("#11000000"), Padding = new Thickness(3, 1, 3, 1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 3, 0), SnapsToDevicePixels = true, UseLayoutRounding = false };
            border.Child = new TextBlock { Text = text, Foreground = host.Brush("#868686"), FontSize = 11 }; panel.Children.Add(border);
        }
    }
    public void MouseUp(object sender, MouseButtonEventArgs args) {
        if (!mouseDown) return;
        host.RaiseClick(sender, args);
        if (args.Handled) return;
        Logger.Info("按下资源工程列表项：" + host.View().Title.Text);
    }
    public void Click(FrameworkElement sender) {
        List<string> titles = new();
        if (host.CurrentPage() == host.ResourceDetailPage && host.DetailAvailable()) {
            foreach (var card in host.ExpandedCards()) if (!Same(card.Title, "") && !card.IsSwapped) titles.Add(card.Title);
            Logger.Info("记录当前已展开的卡片：" + string.Join("、", titles));
        }
        TRequest? search = null;
        TInstance? downloadTarget = host.CurrentInstance();
        if (host.CurrentPage() == host.DownloadPage && host.DownloadAvailable()) { var context = host.CurrentDownloadContext(); search = context.Search; downloadTarget = context.Instance; }
        object[] arguments = BuildDetailArguments(sender.Tag as TProject, host.CurrentPage(), search, host.CurrentArguments(), downloadTarget, host.DownloadPage, host.ResourceDetailPage, host.InstanceSetupPage);
        arguments[1] = titles; host.OpenDetail(arguments);
    }
    public static object[] BuildDetailArguments(TProject? project, int page, TRequest? search, object[]? existing, TInstance? instance, int downloadPage, int detailPage, int instancePage) {
        ResourceTypes type = project is null ? ResourceTypes.Any : project.Types;
        string? version = null; ModLoaders loader = ModLoaders.None; TInstance? downloadTarget = null;
        if (page == downloadPage && search is not null) { type = search.Type; version = search.GameVersion; if (type == ResourceTypes.Mod) loader = search.ModLoaders; if(instance is not null && Same(search.GameVersion,instance.Version.VanillaName)) downloadTarget = instance; }
        else if (page == detailPage) { type = ResourceTypes.Any; if (existing is not null && existing.Length > 2) version = existing[2] as string; if (existing is not null && existing.Length > 3 && existing[3] is ModLoaders value) loader = value; if (existing is not null && existing.Length > 5) downloadTarget = existing[5] as TInstance; }
        else if (page == instancePage && instance is not null) { version = instance.Version.VanillaName; if (type.HasFlagF(ResourceTypes.Mod)) loader = instance.Version.ModLoaders; downloadTarget = instance; }
        if (type == 0) type = ResourceTypes.Any;
        return new object[] { project!, new List<string>(), version!, loader, type, downloadTarget! };
    }
    public void MouseDown() { if (host.View().Root.IsMouseOver && CanInteraction) mouseDown = true; }
    public void MouseLeave() => mouseDown = false;
    public Border RectBack {
        get {
            if (rectBack is not null) return rectBack;
            Border rect = new() { Name = "RectBack", CornerRadius = new CornerRadius(3), RenderTransform = new ScaleTransform(0.8, 0.8), RenderTransformOrigin = new Point(0.5, 0.5),
                BorderThickness = new Thickness(host.PixelSize(1)), SnapsToDevicePixels = true, IsHitTestVisible = false, Opacity = 0 };
            rect.SetResourceReference(Border.BackgroundProperty, "ColorBrush7"); rect.SetResourceReference(Border.BorderBrushProperty, "ColorBrush6");
            Grid.SetColumnSpan(rect, 999); Grid.SetRowSpan(rect, 999); host.View().Root.Children.Insert(0, rect); rectBack = rect; return rectBack;
        }
    }
    public void RefreshColor() {
        if (!CanInteraction) return;
        Grid root = host.View().Root; string state; int time;
        if (root.IsMouseOver) { state = mouseDown ? "MouseDown" : "MouseOver"; time = 120; } else { state = "Idle"; time = 180; }
        if (Same(stateLast, state)) return; stateLast = state;
        string name = "MyResourceItem Color " + host.Uuid();
        if (root.IsLoaded && animation.Disabled() == 0) {
            List<TAnimation> plan = new();
            if (root.IsMouseOver) {
                plan.Add(animation.Color(RectBack, Border.BackgroundProperty, mouseDown ? "ColorBrush6" : "ColorBrushBg1", time, 0, DownloadPageEase.None));
                plan.Add(animation.Opacity(RectBack, 1 - RectBack.Opacity, time, 0, DownloadPageEase.OutFluent));
                plan.Add(animation.Scale(RectBack, (mouseDown ? 0.996 : 1) - ((ScaleTransform)RectBack.RenderTransform).ScaleX, time * 1.2, 0, DownloadPageEase.OutFluent, false));
            } else {
                plan.Add(animation.Opacity(RectBack, -RectBack.Opacity, time, 0, DownloadPageEase.None));
                plan.Add(animation.Color(RectBack, Border.BackgroundProperty, mouseDown ? "ColorBrush6" : "ColorBrush7", time, 0, DownloadPageEase.None));
                plan.Add(animation.Scale(RectBack, 0.996 - ((ScaleTransform)RectBack.RenderTransform).ScaleX, time, 0, DownloadPageEase.OutFluent, false));
                plan.Add(animation.Scale(RectBack, -0.196, 1, 0, DownloadPageEase.None, true));
            }
            animation.Start(plan, name, false);
        } else { animation.Stop(name); if (rectBack is not null) RectBack.Opacity = 0; }
    }
}
