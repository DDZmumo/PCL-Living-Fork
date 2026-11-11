using System.Collections;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;

namespace PCLLF.Native.Facade.Tests;
internal static class VisualAssetGalleryChecks
{
    internal static void Run(VisualAssetChecks.Scope scope)
    {
        string shots = Path.Combine(scope.Root, "screenshots");
        foreach (string name in new[] { "one.png", "two.png", "three.png" }) VisualAssetChecks.WritePng(Path.Combine(shots, name));
        object kind = Native.Enum("PCL.InstanceResourceKind", "Screenshots")!;
        object items = Native.Static("PCL.InstanceResourceStore", "Scan", shots, shots, kind, CancellationToken.None)!;
        object[] entries = ((IEnumerable)items).Cast<object>().ToArray();
        Native.Check(entries.Length == 3, "Actual screenshot scan lost fixture PNGs.");
        PageInstanceResources page = (PageInstanceResources)Activator.CreateInstance(typeof(PageInstanceResources), Native.Flags, null, new[] { kind }, null)!;
        Grid panel = new() { Background = (Brush)new BrushConverter().ConvertFromString("#e4effa")! };
        panel.Children.Add(page);
        try
        {
            Native.Call(page.GetType(), "ShowResources", page, shots, shots, items);
            UniformGrid grid = VisualAssetChecks.Find<UniformGrid>(page, "PanScreenshots");
            VisualAssetChecks.Check("gallery", 61, grid.Columns == 2 && grid.Children.Count == 3 && grid.Visibility == Visibility.Visible && page.FindName("CardPreview") is null);
            object[] displayed = grid.Children.Cast<FrameworkElement>().Select(tile => Native.Get(tile, "Entry")!).ToArray();
            Native.Check(displayed.Length == entries.Length && displayed.ToHashSet().SetEquals(entries), "Gallery sorting changed the scanned screenshot set.");
            // Click indices refer to the current visible order, including the active addition-time direction.
            entries = displayed;
            int opened = 0; ProcessStartInfo? info = null;
            Action<ProcessStartInfo> open = value => { opened++; info = value; };
            void Click(object entry, int count) => Native.Call(page.GetType(), "ScreenshotClick", page, entry, count, open);
            bool Checked(int index) => (bool)Native.Property(grid.Children[index].GetType(), "Checked").GetValue(grid.Children[index])!;
            Click(entries[0], 1); VisualAssetChecks.Check("gallery", 66, opened == 0 && Checked(0));
            Click(entries[0], 2); VisualAssetChecks.Check("gallery", 68, opened == 1 && info is not null && info.UseShellExecute && info.FileName == (string)Native.Get(entries[0], "FullPath")! && info.Arguments == "");
            Click(entries[1], 1); VisualAssetChecks.Check("gallery", 70, !Checked(0) && Checked(1) && opened == 1);
            Native.Call(page.GetType(), "ChangeAllSelected", page, true);
            VisualAssetChecks.Check("gallery", 72, Enumerable.Range(0, 3).All(Checked) && VisualAssetChecks.Find<FrameworkElement>(page, "CardSelect").Visibility == Visibility.Visible && VisualAssetChecks.Find<UIElement>(page, "BtnExport").IsEnabled);
            Native.Call(page.GetType(), "ChangeAllSelected", page, false); VisualAssetChecks.Check("gallery", 74, VisualAssetChecks.Find<FrameworkElement>(page, "CardSelect").Visibility == Visibility.Collapsed);
            var search = VisualAssetChecks.Find<PCL.MySearchBox>(page, "SearchBox");
            search.Text = "two"; VisualAssetChecks.Check("gallery", 76, grid.Children.Count == 1); search.Text = "";
            VisualAssetChecks.Find<FrameworkElement>(page, "PanLoad").Visibility = Visibility.Collapsed;
            FrameworkElement content = VisualAssetChecks.Find<FrameworkElement>(page, "PanAllBack"); content.Visibility = Visibility.Visible; content.Opacity = 1;
            foreach (FrameworkElement tile in grid.Children)
            {
                object entry = Native.Get(tile, "Entry")!; string path = (string)Native.Get(entry, "FullPath")!;
                BitmapSource bitmap = (BitmapSource)Native.Static("PCL.InstanceResourceStore", "LoadImage", path, 640)!;
                Native.Check(bitmap.IsFrozen && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Actual Main thumbnail was not frozen/decoded.");
                VisualAssetChecks.Find<Image>(tile, "Thumbnail").Source = bitmap;
                using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            VisualAssetChecks.Render(panel, Path.Combine(scope.Root, "screenshot-gallery.png"), 780, 720);
            Click(entries[0], 1); VisualAssetChecks.Check("gallery", 83, opened == 1);
        }
        finally
        {
            // This original gallery fixture is never Loaded, so AutoRun:false
            // must leave its genuine resource Loader unstarted. If a regression
            // did start it, retain its actual Thread before Cancel can clear it.
            object loader = Native.Get(page, "ResourceLoader")!;
            Thread? producer = Native.Get(loader, "LastRunningThread") as Thread;
            try
            {
                Native.Call(page.GetType(), "PageUnloaded", page); page.PageOnForceExit(); panel.Children.Clear();
                VisualAssetChecks.Pump(() => producer is null || !producer.IsAlive, "gallery resource producer stopped", 6000);
                VisualAssetChecks.Drain();
                Native.Check(producer is null, "Original unloaded gallery unexpectedly started a real resource worker.");
            }
            catch (Exception error) { VisualAssetChecks.FailClosed(scope.Environment, "Gallery producer/UI cleanup failed", error); }
        }
        // Real Main card with its original host delegates: genuine async Task.Run decode,
        // RunInUi dispatch and real XAML; no readonly host replacement or manual Source assignment.
        RealCard(scope, entries[0]);
        CancellationAndDelete(scope, shots, kind, items, entries[0]);
    }
    private static void CancellationAndDelete(VisualAssetChecks.Scope scope, string shots, object kind, object items, object first)
    {
        string path = (string)Native.Get(first, "FullPath")!, recycle = Path.Combine(scope.Root, "screenshot-recycle");
        byte[] original = File.ReadAllBytes(path);
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            bool observed = false;
            try { Native.Static("PCL.InstanceResourceStore", "DeleteMany", shots, items, canceled.Token); }
            catch (OperationCanceledException error) { observed = error.CancellationToken == canceled.Token; }
            Native.Check(observed && File.ReadAllBytes(path).SequenceEqual(original) && !Directory.Exists(recycle), "Actual screenshot pre-canceled deletion changed payload/created recycle entries or lost token identity.");
            string source = Path.Combine(scope.Root, "input/canceled.png"); VisualAssetChecks.WritePng(source);
            observed = false;
            try { Native.Static("PCL.InstanceResourceStore", "ImportFile", shots, shots, kind, source, canceled.Token); }
            catch (OperationCanceledException error) { observed = error.CancellationToken == canceled.Token; }
            Native.Check(observed && File.Exists(source) && !File.Exists(Path.Combine(shots, "canceled.png")) && !Directory.EnumerateFiles(shots, "*.pcl-copy-*").Any(), "Actual screenshot canceled import lost its original source/left destination or staging bytes.");
        }
        object fresh = Native.Static("PCL.InstanceResourceStore", "Scan", shots, shots, kind, CancellationToken.None)!;
        Native.Static("PCL.InstanceResourceStore", "DeleteMany", shots, fresh, CancellationToken.None);
        Native.Check(!File.Exists(path) && !Directory.Exists(recycle), "Actual screenshot deletion retained its payload or created a recycle record.");
    }
    private static void RealCard(VisualAssetChecks.Scope scope, object entry)
    {
        MyScreenshotItem card = new() { Width = 320, Height = 220 };
        Native.Call(card.GetType(), "Bind", card, entry);
        Grid viewport = new(); viewport.Children.Add(card);
        Window window = new() { Content = viewport, Width = 400, Height = 300, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        object controller = Native.Get(card, "Controller")!;
        bool submitted = false, terminalSeen = false;
        try
        {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Pump(() => card.IsLoaded && viewport.ActualHeight > 0, "genuine Main screenshot card Loaded");
            Native.Call(card.GetType(), "UpdateViewport", card, viewport); submitted = true;
            Image image = VisualAssetChecks.Find<Image>(card, "Thumbnail");
            VisualAssetChecks.Pump(() => image.Source is not null && (bool)Native.Get(controller, "ready")! && !(bool)Native.Get(controller, "loading")!, "genuine Main async thumbnail completion");
            terminalSeen = true;
            Native.Check(image.Source is BitmapSource bitmap && bitmap.IsFrozen && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Actual Main async PNG dimensions/freeze changed.");
            BitmapSource decoded = (BitmapSource)image.Source;
            FormatConvertedBitmap bgra = new(decoded, PixelFormats.Bgra32, null, 0); byte[] pixel = new byte[4]; bgra.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Native.Check(pixel.SequenceEqual(new byte[] { 237, 149, 100, 255 }), "Actual Main async thumbnail did not decode the fixture CornflowerBlue pixels.");
            string path = (string)Native.Get(entry, "FullPath")!;
            using (FileStream unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            viewport.Children.Remove(card); VisualAssetChecks.Pump(() => !card.IsLoaded && image.Source is null, "genuine Main card unload release");
        }
        finally
        {
            try
            {
                // Do not Release/revise the pending valid request before its completion:
                // waiting for ready proves its actual UI terminal callback ran.
                if (submitted && !terminalSeen) VisualAssetChecks.Pump(() => (bool)Native.Get(controller, "ready")! && !(bool)Native.Get(controller, "loading")!, "Main image operation terminal callback", 8000);
                Native.Call(card.GetType(), "ReleaseThumbnail", card); window.Content = null; window.Close(); VisualAssetChecks.Drain();
            }
            catch (Exception error) { VisualAssetChecks.FailClosed(scope.Environment, "Actual Main image task did not quiesce", error); }
        }
    }
}
