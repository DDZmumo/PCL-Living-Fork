using System.Collections;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class DependencyGraphChecks {
    internal static void Run(FixtureEnvironment environment) {
        string[] names = ["InspectionContext", "ModInfo", "Dependency", "DependencyEdge", "InspectionIssue", "RemovalImpact", "InspectionReport", "OnlineProjectInfo", "OnlineDependency", "OnlineFileInfo"];
        Type[] types = names.Select(name => Native.Type("PCL.ModDependencyInspector+" + name)).Concat(new[] { Native.Type("PCL.ModDependencyOnline+LookupOptions"), Native.Type("PCL.ModDependencyOnline+LookupCache") }).ToArray();
        Native.Check(types.All(type => type.Assembly == Native.Main), "Graph fixture must close over the actual twelve Main DTOs.");
        Native.Call(typeof(DependencyCases<,,,,,,,,,,,>).MakeGenericType(types), "Graph", null, environment);
    }
}

internal static partial class DependencyCases<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile,TOptions,TCache> {
    private static readonly List<Grid> GraphViews = new();
    private static string GraphRoot = "";
    internal static void Graph(FixtureEnvironment environment) {
        DependencyGraphSourceChecks.Reset();
        using Scope scope = new(environment, "dependency-graph");
        GraphRoot = scope.Folder("mods");
        try {
            var fixture = GraphLocal();
            var peer = GraphPlatform(fixture.Report, fixture.Selected, fixture.Config);
            GraphPinned(fixture.Report.Context);
            GraphBundles(fixture.Report.Context);
            GraphNested(fixture.Report.Context);
            GraphLayout(fixture.Report, fixture.Selected, fixture.Config, peer.Peer, peer.Match);
            DependencyGraphSourceChecks.Complete();
            environment.AssertNoForbiddenIo();
        } finally {
            foreach (Grid graph in GraphViews) {
                Native.Check(!graph.IsLoaded, "Offscreen graph unexpectedly started Loaded/icon background work.");
                if (graph.Parent is Panel panel) panel.Children.Remove(graph);
                else if (graph.Parent is ContentControl content) content.Content = null;
                graph.Children.Clear();
            }
            GraphViews.Clear(); GraphRoot = "";
        }
    }
    private static void G(int id, bool condition) => DependencyGraphSourceChecks.Check(id, condition);
    private static Grid GView(TReport report, string file) {
        Grid graph = (Grid)Activator.CreateInstance(Native.Type("PCL.ModDependencyGraphView"), Native.Flags, null, [report, file], null)!;
        Native.Check(!graph.IsLoaded && Native.Get(graph, "Controller")!.GetType().Assembly.GetName().Name == "PCLLF.WPF.Core", "Graph did not use the actual offscreen Core controller.");
        GraphViews.Add(graph); return graph;
    }
    private static Canvas GCanvas(Grid graph) => (Canvas)Native.Get(graph, "GraphCanvas")!;
    private static List<Border> GNodes(Grid graph) {
        List<Border> nodes = GCanvas(graph).Children.OfType<Border>().ToList();
        Native.Check(nodes.All(node => node.Tag is ModDependencyGraphNodeData && node.Tag.GetType().Assembly == Native.Main), "Graph nodes replaced original Main GraphNode identities."); return nodes;
    }
    private static ModDependencyGraphNodeData GD(Border node) => (ModDependencyGraphNodeData)node.Tag;
    private static string GT(Border node) => GD(node).Title ?? "";
    private static TMod GModel(string id, string name, string version = "1") => new() { Id = id, Name = name, Version = version, Enabled = true, RootPath = Path.Combine(GraphRoot, id + ".jar"), MetadataPath = "fabric.mod.json" };
    private static TDependency GRequired(string id) => new() { Id = id };
    private static void GRebuild(TReport report) { report.Edges.Clear(); report.Issues.Clear(); report.ReverseDependencies.Clear(); Native.Call(Inspector, "BuildReport", null, report, CancellationToken.None); }
    private static void GMap(TReport report) => Native.Call(OnlineApi, "MapRelations", null, report);
    private static TContext GContext() { TContext context = new() { MinecraftVersion = "1.21.11", LoaderKnown = true }; context.LoaderVersions.Add("fabricloader", "0.19.3"); return context; }
    private static string GJar(string id, string name, string version = "1.0", JObject? extra = null) {
        JObject data = new() { ["schemaVersion"] = 1, ["id"] = id, ["name"] = name, ["version"] = version };
        if (extra is not null) foreach (JProperty item in extra.Properties()) data[item.Name] = item.Value.DeepClone();
        Dictionary<string, object> entries = Entries(("fabric.mod.json", data.ToString()));
        if ((string?)data["icon"] == "icon.png") {
            DrawingVisual visual = new(); using (DrawingContext drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.Orange, null, new Rect(0, 0, 32, 32));
            RenderTargetBitmap bitmap = new(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using MemoryStream output = new(); encoder.Save(output); entries.Add("icon.png", output.ToArray());
        }
        return Jar(GraphRoot, id + ".jar", entries);
    }
    private static void GMeasure(FrameworkElement element, double width, double height) { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }
}
