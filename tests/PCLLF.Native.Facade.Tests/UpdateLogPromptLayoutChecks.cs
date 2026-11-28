using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL;
using PCLLF.Presentation.Foundation;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

// Measure the actual glass dialog and its content together with the action row.
internal static class UpdateLogPromptLayoutChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        Application app = Application.Current;
        ResourceDictionary original = app.Resources;
        bool originalDark = LegacyThemePalette.IsDarkTheme;
        FieldInfo[] fields = Native.Type("PCL.ModSecret").GetFields(Native.Flags).Where(field => field.IsStatic && field.FieldType == typeof(ModBase.MyColor)).ToArray();
        object?[] colors = fields.Select(field => field.GetValue(null)).ToArray();
        ResourceDictionary palette = new();
        palette.MergedDictionaries.Add(original);
        foreach (DictionaryEntry entry in original)
            if (entry.Value is Freezable value) palette[entry.Key] = value.CloneCurrentValue();
        app.Resources = palette;
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                RestoreColors();
                Native.Static("PCL.ModTheme", "PrepareThemeTones", dark);
                Native.Static("PCL.ModTheme", "ApplyThemePalette");
                foreach ((double width, double height) in new[] { (900d, 634d), (900d, 470d), (900d, 540d), (900d, 800d), (550d, 540d) })
                    CheckPanel(width, height, dark);
            }
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS update-note layout: actual glass dialog bounds, light/dark palettes, complete fixed checkbox and independently scrolling Markdown.");
        }
        finally
        {
            RestoreColors();
            Native.Call(typeof(LegacyThemePalette), "Prepare", null, originalDark);
            app.Resources = original;
        }

        void RestoreColors() { for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, colors[i]); }
    }

    private static void CheckPanel(double width, double windowHeight, bool dark)
    {
        double panelHeight = windowHeight - 36;
        Grid panel = new() { Background = LegacyThemePalette.Brush("ColorBrushBackground") };
        string markdown = "# 完整发布说明\n\n" + string.Join("\n\n", Enumerable.Range(1, 90).Select(i => $"- 第 {i} 项：长更新说明必须独立滚动，正文收缩时仍完整显示原生复选框。"));
        FrameworkElement body = (FrameworkElement)Activator.CreateInstance(Native.Type("PCL.ReleaseNotesPromptView"), Native.Flags, null,
            new object?[] { markdown, false, (Func<bool, bool>)(_ => true), (Action<string>)(_ => throw new InvalidOperationException("Browser forbidden")) }, null)!;
        var request = new ModMain.MyMsgBoxConverter
        {
            Title = ModBase.LauncherDisplayName + " " + ModBase.VersionBaseName + " 更新说明",
            Button1 = "确定", Button2 = "查看发布页", ContentFactory = () => body, UseNativeContent = true
        };
        Type dialogType = Native.Type("PCL.GlassMessageDialog");
        var modal = (Grid)Activator.CreateInstance(dialogType, Native.Flags, null,
            [request, (Action)(() => { }), (Action<FrameworkElement>)(_ => { })], null)!;
        using var dialog = (IDisposable)modal;
        Native.Call(dialogType, "RefreshPalette", modal);
        ((SpringMotion)Native.Get(modal, "_motion")!).Snap(1);
        panel.Children.Add(modal);
        try
        {
            Measure(panel, width, panelHeight);
            Pump();
            panel.UpdateLayout();
            var card = (GlassSurface)Native.Get(modal, "_card")!;
            var viewport = (ContentControl)Native.Get(modal, "_nativeContentHost")!;
            var checkbox = (CheckBox)Native.Get(body, "Suppress")!;
            var notes = (FlowDocumentScrollViewer)Native.Get(body, "Notes")!;
            var buttons = (WrapPanel)((Grid)card.Child).Children[2];
            Native.Check(viewport.ActualHeight > 0 && checkbox.ActualHeight >= checkbox.MinHeight,
                "The glass dialog content and checkbox did not measure.");
            Rect check = Bounds(checkbox, viewport), button = Bounds(buttons, panel), checkInPanel = Bounds(checkbox, panel);
            Preview(panel, $"-notes-{(dark ? "dark" : "light")}-{windowHeight:0}-{width:0}");
            string geometry = $"window={windowHeight}, width={width}, bodyMax={body.MaxHeight:0.##}, body={body.ActualHeight:0.##}, viewport={viewport!.ActualHeight:0.##}, checkbox={check}, buttonTop={button.Top:0.##}";
            Native.Check(check.Left >= -0.5 && check.Top >= -0.5 && check.Right <= viewport.ActualWidth + 0.5 && check.Bottom <= viewport.ActualHeight + 0.5,
                "Update-note checkbox is clipped by the real caption viewport: " + geometry);
            Native.Check(checkInPanel.Bottom <= button.Top - 1 && !checkInPanel.IntersectsWith(button), "Update-note checkbox intersects the real button area: " + geometry);
            Native.Check(Math.Abs(card.ActualWidth - Math.Min(720, width - 48)) < 1 && card.ActualHeight <= panelHeight - 40 + 1,
                "Update-note dialog ignored its preferred width/height constraints: " + geometry);
            Native.Check(SystemParameters.HighContrast || card.Background is SolidColorBrush background &&
                (dark ? background.Color.R < 70 : background.Color.R >= 240), "Update-note dialog lost the light/dark palette.");
            Native.Check(ReferenceEquals(checkbox.Foreground, checkbox.FindResource("GlassPresentationText")) &&
                ReferenceEquals(notes.Document.Foreground, checkbox.Foreground),
                "The release notes and their checkbox did not use the dialog's current text brush.");

            ScrollViewer scroll = Descendants(notes).OfType<ScrollViewer>().FirstOrDefault(view => view.ScrollableHeight > 0)
                ?? throw new InvalidOperationException("Long update notes did not get a real independently scrolling Markdown viewport: " + geometry);
            Rect beforeScroll = Bounds(checkbox, panel);
            scroll.ScrollToBottom(); panel.UpdateLayout(); Pump(); panel.UpdateLayout();
            Native.Check(scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                "The end of long Markdown cannot be reached without scrolling the native footer: " + geometry);
            Native.Check(Bounds(checkbox, panel) == beforeScroll && Bounds(checkbox, viewport).Bottom <= viewport.ActualHeight + 0.5,
                "Scrolling Markdown moved/clipped the fixed native footer: " + geometry);
            Native.Check(!modal.IsLoaded && Application.Current.Windows.Count == 0, "Layout acceptance entered real launcher/window startup.");
            Console.WriteLine("PASS update-note constrained layout " + geometry);
        }
        finally { panel.Children.Clear(); }
    }

    private static Rect Bounds(FrameworkElement element, Visual target) => element.TransformToAncestor(target).TransformBounds(new Rect(element.RenderSize));
    private static void Measure(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Preview(FrameworkElement panel, string suffix)
    {
        string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, "--preview-path");
        if (i < 0 || i + 1 >= args.Length) return;
        string output = Path.GetFullPath(args[i + 1]);
        RenderTargetBitmap bitmap = new((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
        PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + suffix + ".png")); png.Save(file);
    }
}
