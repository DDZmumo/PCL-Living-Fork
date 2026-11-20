using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCLCS;

public sealed partial class ModpackUpdatePreviewController<TFile, TPackage, TChange, TPlan, TKind, TButton, TScroll>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile> where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange>
    where TKind : struct, Enum where TButton : FrameworkElement where TScroll : ScrollViewer {
    private static readonly SemaphoreSlim IconGate = new(2);
    private readonly CancellationTokenSource iconCancellation = new();
    private readonly Dictionary<string, Task<ImageSource?>> iconCache = new(StringComparer.OrdinalIgnoreCase);

    private UIElement WithIcon(UIElement body, TChange change) {
        Grid row = new(); row.ColumnDefinitions.Add(new() { Width = new GridLength(46) }); row.ColumnDefinitions.Add(new());
        string category = (change.Relative ?? "").Split('/')[0].ToLowerInvariant();
        string shape = category == "mods" ? "M2,9 L16,2 30,9 30,25 16,32 2,25 Z M2,9 L16,16 30,9 M16,16 L16,32"
            : category == "shaderpacks" ? "M16,8 A8,8 0 1 1 15.99,8 M16,1 L16,5 M16,27 L16,31 M1,16 L5,16 M27,16 L31,16 M5,5 L8,8 M24,24 L27,27 M5,27 L8,24 M24,8 L27,5"
            : category is "resourcepacks" or "datapacks" ? "M2,3 L30,3 30,29 2,29 Z M3,24 L12,14 19,21 24,15 30,22 M21,8 A2,2 0 1 1 20.99,8"
            : "M6,2 L21,2 28,9 28,31 6,31 Z M21,2 L21,10 28,10 M11,17 L23,17 M11,23 L23,23";
        var fallback = new System.Windows.Shapes.Path { Data = Geometry.Parse(shape), Width = 30, Height = 30, Stretch = Stretch.Uniform, StrokeThickness = 1.6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        fallback.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "ColorBrushGray3"); row.Children.Add(fallback);
        Image image = new() { Width = 34, Height = 34, Stretch = Stretch.Uniform, Clip = new RectangleGeometry(new Rect(0, 0, 34, 34), 6, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Tag = "preview-file-icon" };
        row.Children.Add(image); Grid.SetColumn(body, 1); row.Children.Add(body);
        string? file = change.ApplyAuthor ? change.Incoming?.Payload : null;
        if (file is null && plan.Root is not null && change.LocalRelative is not null) {
            string root = Path.GetFullPath(plan.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string local = Path.GetFullPath(Path.Combine(root, change.LocalRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (local.StartsWith(root, StringComparison.OrdinalIgnoreCase)) file = local;
        }
        file ??= change.Incoming?.Payload;
        if (file is not null && category is "mods" or "shaderpacks" or "resourcepacks" or "datapacks") LoadIcon(image, fallback, file, category);
        return row;
    }
    private async void LoadIcon(Image image, FrameworkElement fallback, string file, string category) {
        try {
            Task<ImageSource?> read;
            lock (checkGate) {
                if (closed) return;
                if (!iconCache.TryGetValue(file, out read!)) {
                    var token = iconCancellation.Token;
                    read = Task.Run(async () => {
                        await IconGate.WaitAsync(token).ConfigureAwait(false);
                        try {
                            token.ThrowIfCancellationRequested();
                            if (!File.Exists(file)) return null;
                            string? entry = category == "mods" ? PackUpdateModInspection.Read(file, plan.Package!.Minecraft, plan.Package.Loaders, token).IconPath : "pack.png";
                            token.ThrowIfCancellationRequested();
                            return ModDependencyIconFiles.Read(file, entry);
                        } finally { IconGate.Release(); }
                    }, token);
                    iconCache.Add(file, read);
                }
            }
            ImageSource? source = await read.ConfigureAwait(false);
            if (source is null || closed || view.Dispatcher.HasShutdownStarted) return;
            await view.Dispatcher.InvokeAsync(() => { if (!closed) {
                image.Source = source;
                // Clip the actual Uniform content rectangle, including wide
                // artwork whose corners sit inside the square letterbox.
                if (source.Width > 0 && source.Height > 0) {
                    double scale = 34 / Math.Max(source.Width, source.Height), width = source.Width * scale, height = source.Height * scale;
                    double radius = Math.Min(6, Math.Min(width, height) / 2);
                    image.Clip = new RectangleGeometry(new Rect((34 - width) / 2, (34 - height) / 2, width, height), radius, radius);
                }
                fallback.Visibility = Visibility.Collapsed;
            } });
        } catch (Exception) { /* Optional artwork never blocks update decisions. */ }
    }
}
