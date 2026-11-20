using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PCL;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static class ThemeTransitionChecks {
    internal static void Run(FixtureEnvironment environment) {
        int suppression = ModAnimation.AniControlEnabled;
        System.Collections.IDictionary cache = Settings.Entries["SystemDebugAnim"].ValueCache;
        var saved = new List<System.Collections.DictionaryEntry>();
        foreach (System.Collections.DictionaryEntry entry in cache) saved.Add(entry);
        var app = System.Windows.Application.Current;
        Window? previousMain = app.MainWindow;
        ShutdownMode previousMode = app.ShutdownMode;
        var root = new Grid { Background = Brushes.White };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new TextBlock { Text = "PCL Living Fork", Foreground = Brushes.Navy });
        var window = new Window { Width = 500, Height = 280, Content = root, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        Image? Overlay() => root.Children.OfType<Image>().SingleOrDefault(image => Equals(image.Tag, "theme-transition"));
        void Apply(Brush brush) => Native.Static("PCL.ThemeTransition", "Apply", root, (Action)(() => root.Background = brush));
        try {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ModAnimation.AniControlEnabled = 0; Native.Cache("SystemDebugAnim", 9);
            bool coldApplied = false;
            Native.Static("PCL.ThemeTransition", "Apply", null, (Action)(() => coldApplied = true));
            Native.Check(coldApplied, "Theme changes before XAML construction were lost.");
            Apply(Brushes.MidnightBlue);
            Native.Check(Overlay() is null && ReferenceEquals(root.Background, Brushes.MidnightBlue), "Cold unloaded controls should apply their saved theme without capturing a transition.");
            root.Background = Brushes.White;
            window.Show(); window.UpdateLayout();
            Apply(Brushes.MidnightBlue);
            if (SystemParameters.ClientAreaAnimation) {
                Image first = Overlay() ?? throw new InvalidOperationException("Theme switch has no crossfade frame.");
                Native.Check(!first.IsHitTestVisible && !first.Focusable && Grid.GetRowSpan(first) == 2 && ReferenceEquals(root.Background, Brushes.MidnightBlue),
                    "Crossfade delayed actual palette changes, intercepted input, or failed to cover all rows.");
                VisualAssetChecks.Pump(() => first.HasAnimatedProperties && first.Opacity is > 0 and < .95, "Theme crossfade did not interpolate opacity.");
                Apply(Brushes.White);
                Image replacement = Overlay() ?? throw new InvalidOperationException("Interrupted theme switch lost its composited frame.");
                Native.Check(!ReferenceEquals(first, replacement) && first.Source is null && !first.HasAnimatedProperties,
                    "Repeated toggling retained the previous snapshot/clock.");
                VisualAssetChecks.Pump(() => Overlay() is null, "Theme crossfade did not release its frame after completion.");
                Native.Check(replacement.Source is null && !replacement.HasAnimatedProperties, "Completed crossfade retained bitmap memory or opacity animation.");
                Apply(Brushes.MidnightBlue);
                root.Visibility = Visibility.Collapsed;
                Native.Check(Overlay() is null, "Hiding the page retained the transition snapshot.");
                root.Visibility = Visibility.Visible; window.UpdateLayout();
                Apply(Brushes.White);
                window.Width += 10; window.UpdateLayout();
                Native.Check(Overlay() is null, "Resizing left a stale stretched theme snapshot.");
                Apply(Brushes.MidnightBlue);
                window.Content = null;
                VisualAssetChecks.Pump(() => !root.IsLoaded && Overlay() is null, "Unloading retained the transition snapshot or pending animation.");
                window.Content = root; window.UpdateLayout();
                try {
                    Native.Static("PCL.ThemeTransition", "Apply", root, (Action)(() => throw new InvalidOperationException("theme fixture sentinel")));
                    throw new Exception("Theme transition swallowed a failed palette change.");
                } catch (InvalidOperationException error) when (error.Message == "theme fixture sentinel") {
                    Native.Check(Overlay() is null, "A failed palette change left a snapshot over the interface.");
                }
            } else Native.Check(Overlay() is null, "Windows reduced-motion setting did not suppress the theme transition.");
            Native.Cache("SystemDebugAnim", 30);
            Apply(Brushes.White);
            Native.Check(Overlay() is null && ReferenceEquals(root.Background, Brushes.White), "Disabled animations delayed theme changes or created a snapshot.");
            Native.Cache("SystemDebugAnim", 9); ModAnimation.AniControlEnabled = 1;
            Apply(Brushes.MidnightBlue);
            Native.Check(Overlay() is null, "Native animation suppression was ignored by theme transitions.");
            ModAnimation.AniControlEnabled = 0;
            Native.Cache("SystemDebugAnim", 9);
            VisualAssetChecks.Pump(() => root.IsLoaded && root.IsVisible, "reattach the theme fixture");
            CheckWorkspacePalette(root);

            Native.Check(!window.IsActive, "Offscreen transition fixture took desktop focus.");
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS theme crossfade: real WPF opacity interpolation, immediate underlying changes, rapid-toggle replacement, completed/hidden/resized/unloaded cleanup, native disabled/reduced motion and input passthrough.");
        } finally {
            window.Content = null; window.Close(); app.MainWindow = previousMain; app.ShutdownMode = previousMode;
            cache.Clear(); foreach (var entry in saved) cache.Add(entry.Key, entry.Value);
            ModAnimation.AniControlEnabled = suppression;
        }
    }

    private static void CheckWorkspacePalette(Grid root)
    {
        var panel = new GlassSurface { Width = 80, Height = 60, HorizontalAlignment = HorizontalAlignment.Right };
        GlassMaterial.SetIsEnabled(panel, false);
        root.Children.Add(panel);
        Color dark = Color.FromRgb(24, 24, 24), light = Color.FromRgb(242, 245, 248);
        var brush = new SolidColorBrush(dark);
        brush.Freeze();

        void Switch(Color target, bool animate)
        {
            brush = (SolidColorBrush)Native.Static("PCL.ThemeTransition", "BlendPaletteBrush", brush, target, animate)!;
            GlassMaterial.SetFallbackColor(root, target);
            GlassMaterial.SetFallbackBrush(root, brush);
        }
        byte Sample()
        {
            root.UpdateLayout();
            Point point = panel.TranslatePoint(new Point(panel.ActualWidth / 2, panel.ActualHeight / 2), root);
            var capture = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            capture.Render(root);
            byte[] pixel = new byte[4];
            capture.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
            return pixel[2];
        }

        try
        {
            Switch(dark, false);
            VisualAssetChecks.Pump(() => panel.IsLoaded && panel.ActualWidth > 0 && panel.ActualHeight > 0, "load the glass palette sample");
            if (!SystemParameters.HighContrast)
            {
                byte start = Sample();
                Native.Check(start == dark.R,
                    $"The glass panel did not draw the original fallback palette (red={start}, inherited brush={ReferenceEquals(GlassMaterial.GetFallbackBrush(panel), brush)}).");
            }

            bool animate = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            Switch(light, animate);
            Native.Check(!root.Children.OfType<Image>().Any() && !root.Children.OfType<Border>().Any(border => Equals(border.Tag, "glass-theme-fade")),
                "Workspace color changes put a veil or screenshot over the controls.");
            if (animate)
            {
                Native.Check(brush.Color == dark && Equals(Native.Static("PCL.ThemeTransition", "PaletteTarget", brush), light),
                    "The workspace jumped to its target before its first color frame.");
                VisualAssetChecks.Pump(() => brush.Color.R > dark.R && brush.Color.R < light.R, "live intermediate theme color");
                byte sample = Sample();
                Native.Check(sample > dark.R && sample < light.R, "The fallback glass paint skipped the intermediate palette.");
                Color displayed = brush.Color;
                var sameBrush = brush;
                Switch(dark, true);
                Native.Check(ReferenceEquals(sameBrush, brush) && brush.Color == displayed,
                    "Rapid switching snapped the workspace instead of reversing from its displayed color.");
                VisualAssetChecks.Pump(() => brush.Color == dark && !brush.HasAnimatedProperties, "finish the reversed palette");
            }
            else
            {
                Native.Check(brush.Color == light && !brush.HasAnimatedProperties, "Reduced motion started a workspace color animation.");
            }
            Switch(light, animate: false);
            Native.Check(brush.Color == light && !brush.HasAnimatedProperties,
                "An immediate change left an obsolete color animation.");
            if (!SystemParameters.HighContrast) Native.Check(Sample() == light.R, "Glass fallback did not adopt the final palette.");
        }
        finally
        {
            root.Children.Remove(panel);
            root.ClearValue(GlassMaterial.FallbackBrushProperty);
            root.ClearValue(GlassMaterial.FallbackColorProperty);
        }
    }
}
