using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore.Extensions;

namespace PCLCS;

public static class InstanceComponentRevision { public static int Next(ref int value) => Interlocked.Increment(ref value); }
public class InstanceComponentRequestData<TInstance> where TInstance : class {
    public TInstance Target = null!;
    public McVersion Version = null!;
    public string? GameFolder;
    public int Revision;
}
public class InstanceComponentResultData<TRequest, TItem> where TRequest : class where TItem : InstalledComponentSummaryItemData {
    public TRequest Request = null!;
    public List<TItem> Items = null!;
}
public sealed class InstanceComponentsHost<TInstance, TRequest, TResult, TItem, TCard, TImage>
    where TInstance : class where TRequest : InstanceComponentRequestData<TInstance> where TResult : InstanceComponentResultData<TRequest, TItem>
    where TItem : InstalledComponentSummaryItemData where TCard : Panel where TImage : FrameworkElement {
    public Func<TInstance?> Instance = null!;
    public Func<bool> Modpack = null!;
    public Func<int> NextRevision = null!, Revision = null!;
    public Func<TRequest> CreateRequest = null!;
    public Func<TResult> CreateResult = null!;
    public Func<TInstance, bool> HasSavedIsolation = null!, Isolation = null!;
    public Func<TInstance, string> VersionFolder = null!;
    public Func<string> MinecraftFolder = null!, ImageRoot = null!;
    public Func<TInstance, McVersion> Version = null!;
    public Func<TInstance, McComponents?> Components = null!;
    public Action<TRequest, bool> Start = null!;
    public Action Cancel = null!;
    public Action<Exception, string> Warn = null!;
    public Panel Cards = null!;
    public Func<string, TImage> CreateImage = null!;
    public Action<TImage, Stretch> SetStretch = null!;
    public Func<string, TCard> CreateCard = null!;
    public Action<TCard> TouchTitle = null!;
}
public sealed class InstanceComponentsTask<TRequest, TResult> {
    public Func<TRequest> Input = null!;
    public Func<TResult?> Output = null!;
    public Action<TResult> SetOutput = null!;
    public Func<bool> IsCanceled = null!;
}
public sealed class InstanceComponentsController<TInstance, TRequest, TResult, TItem, TCard, TImage>
    where TInstance : class where TRequest : InstanceComponentRequestData<TInstance> where TResult : InstanceComponentResultData<TRequest, TItem>
    where TItem : InstalledComponentSummaryItemData where TCard : Panel where TImage : FrameworkElement {
    private readonly InstanceComponentsHost<TInstance, TRequest, TResult, TItem, TCard, TImage> host;
    private readonly InstalledComponentSummaryEngine<TItem> summary;
    public InstanceComponentsController(InstanceComponentsHost<TInstance, TRequest, TResult, TItem, TCard, TImage> host, InstalledComponentSummaryEngine<TItem> summary) { this.host = host; this.summary = summary; }
    public void Refresh() {
        int revision = host.NextRevision();
        if (!host.Modpack() || host.Instance() is null) { host.Cancel(); host.Cards.Children.Clear(); return; }
        TInstance target = host.Instance()!; string? folder = null;
        if (host.HasSavedIsolation(target)) folder = host.Isolation(target) ? host.VersionFolder(target) : host.MinecraftFolder();
        TRequest request = host.CreateRequest(); request.Target = target; request.Version = host.Version(target); request.GameFolder = folder; request.Revision = revision;
        host.Start(request, true);
    }
    public void Read(InstanceComponentsTask<TRequest, TResult> task) {
        TRequest request = task.Input(); List<TItem> items;
        try {
            McComponents? components = host.Components(request.Target);
            var local = request.GameFolder is null ? null : InstalledComponentSummaryEngine<TItem>.ReadLocalMods(Path.Combine(request.GameFolder, "mods"));
            items = summary.Describe(request.Version, components, local);
        } catch (Exception ex) { if (ex.IsCanceled()) throw; host.Warn(ex, "读取已安装组件信息失败"); items = summary.Describe(request.Version); }
        if (task.IsCanceled()) return;
        TResult result = host.CreateResult(); result.Request = request; result.Items = items; task.SetOutput(result);
    }
    public void Loaded(InstanceComponentsTask<TRequest, TResult> task) {
        TResult? result = task.Output();
        if (result is null || result.Request.Revision != host.Revision() || !host.Modpack() || !ReferenceEquals(host.Instance(), result.Request.Target)) return;
        Show(result.Items);
    }
    public void Show(IEnumerable<TItem> items) {
        host.Cards.Children.Clear();
        foreach (TItem item in items) {
            Grid row = new() { Height = 18, Margin = new Thickness(132, 11, 20, 0), VerticalAlignment = VerticalAlignment.Top };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TImage icon = host.CreateImage(host.ImageRoot() + "Blocks/" + item.Icon + ".png"); icon.Width = 18; icon.Height = 18; host.SetStretch(icon, Stretch.Uniform); icon.Margin = new Thickness(0, 0, 7, 0);
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.Linear); row.Children.Add(icon);
            TextBlock label = new() { Text = item.Version, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1); row.Children.Add(label);
            TCard card = host.CreateCard(item.Name); card.Height = 40; card.Margin = new Thickness(0, 0, 0, 12); card.IsHitTestVisible = false; card.Children.Add(row); host.TouchTitle(card); host.Cards.Children.Add(card);
        }
    }
}
