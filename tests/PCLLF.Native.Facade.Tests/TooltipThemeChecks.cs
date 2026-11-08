using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MeloongCore.Wpf;
using PCL;
using PCLLF.Presentation.Foundation;

namespace PCLLF.Native.Facade.Tests;

internal static class TooltipThemeChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        var app = System.Windows.Application.Current;
        var previousMain = app.MainWindow;
        int previousMode = Settings.Get<int>("UiLauncherColorMode");
        var owner = new Button
        {
            Content = "自动补全模组依赖",
            ToolTip = "发现缺失的必需前置后先确认，再下载匹配当前版本和加载器的模组，复查后启动。",
            // Exercise the real renderer without moving the desktop pointer:
            // disabled owners enter the same Show path without a mouse hit test.
            IsEnabled = false
        };
        ToolTipService.SetShowOnDisabled(owner, true);
        ToolTipService.SetInitialShowDelay(owner, 0);
        var slider = new MySlider { Width = 160 };
        var content = new StackPanel(); content.Children.Add(owner); content.Children.Add(slider);
        var window = new Window
        {
            Content = content, Width = 760, Height = 120,
            Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false
        };
        ToolTip? nativeTooltip = null;
        Type renderer = typeof(ModernTooltipService);
        Border Card() => (Border)Native.Field(renderer, "popupCard").GetValue(null)!;
        Popup Popup() => (Popup)Native.Field(renderer, "popup").GetValue(null)!;
        void Mode(int value)
        {
            Native.Cache("UiLauncherColorMode", value);
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
        }
        void Open()
        {
            Native.Call(renderer, "BeginShow", null, owner, new Point(10, 10));
            VisualAssetChecks.Pump(() => Popup().IsOpen && Card().IsLoaded && Card().Opacity >= 0.99,
                "The actual modern tooltip did not finish opening.");
        }
        void Check(string phase)
        {
            Border card = Card();
            SolidColorBrush background = (SolidColorBrush)card.Background;
            SolidColorBrush border = (SolidColorBrush)card.BorderBrush;
            var text = (TextBlock)card.Child;
            SolidColorBrush foreground = (SolidColorBrush)text.Foreground;
            Console.WriteLine($"DIAG actual tooltip {phase}: dark={LegacyThemePalette.IsDarkTheme}, background={background.Color}, border={border.Color}, foreground={foreground.Color}");
            Native.Check(background.Color == LegacyThemePalette.Brush("ColorBrushPopup").Color,
                "The actual modern tooltip retained a light background: " + phase);
            Native.Check(border.Color == LegacyThemePalette.Brush("ColorBrushGray5").Color &&
                foreground.Color == LegacyThemePalette.Brush("ColorBrush1").Color,
                "The actual modern tooltip border/text did not follow the palette: " + phase);
            Native.Check(text.Text == owner.ToolTip.ToString() && card.CornerRadius == new CornerRadius(8),
                "Tooltip theming changed its content or native rounded shape.");
        }
        try
        {
            Mode(1);
            window.Show();
            window.UpdateLayout();
            Open();
            Check("first dark open");
            var sliderPopup = (Popup)slider.FindName("Popup");
            sliderPopup.Opacity = 0; sliderPopup.PlacementTarget = slider; sliderPopup.IsOpen = true;
            VisualAssetChecks.Pump(() => sliderPopup.IsOpen && ((Border)sliderPopup.Child).IsLoaded, "The actual slider hint did not open.");
            var sliderHint = (Border)sliderPopup.Child;
            Native.Check(((SolidColorBrush)sliderHint.Background).Color == LegacyThemePalette.Brush("ColorBrushPopup").Color,
                "The native slider hint retained a light background in dark mode.");
            Mode(0);
            Check("open dark to light");
            Native.Check(((SolidColorBrush)sliderHint.Background).Color == LegacyThemePalette.Brush("ColorBrushPopup").Color,
                "The native slider hint did not follow the light palette.");
            sliderPopup.IsOpen = false;
            Mode(1);
            Check("open light to dark");
            owner.ToolTip = "更新后的提示内容";
            VisualAssetChecks.Pump(() => Card().Child is TextBlock text && text.Text == owner.ToolTip.ToString(),
                "The open tooltip did not refresh its actual content.");
            Check("dark content replacement");
            Native.Call(renderer, "Close", null, false);
            Open();
            Check("dark reopen");
            Native.Call(renderer, "Close", null, false);
            Mode(0);
            Open();
            Check("light reopen");
            Native.Call(renderer, "Close", null, false);

            var customText = new TextBlock { Text = "自定义提示内容", Foreground = Brushes.Orange };
            var customTooltip = new ToolTip { Content = customText };
            owner.ToolTip = customTooltip;
            Mode(1);
            Open();
            Native.Check(Card().Child is ContentPresenter && customTooltip.Content is null,
                "The modern renderer lost its original custom-content borrowing behavior.");
            foreach (int mode in new[] { 0, 1 })
            {
                Mode(mode);
                Native.Check(ReferenceEquals(customText.Foreground, Brushes.Orange) &&
                    ((SolidColorBrush)Card().Background).Color == LegacyThemePalette.Brush("ColorBrushPopup").Color,
                    "The palette adapter overwrote an explicit custom-content foreground or lost the popup palette.");
            }
            Native.Call(renderer, "Close", null, false);
            Native.Check(ReferenceEquals(customTooltip.Content, customText),
                "Closing the modern tooltip did not return its borrowed content.");

            ModernTooltipService.SetUseModernTooltip(owner, false);
            nativeTooltip = new ToolTip { Content = "原生提示内容", PlacementTarget = owner };
            owner.ToolTip = nativeTooltip;
            int opened = 0, closed = 0;
            nativeTooltip.Opened += (_, _) => opened++;
            nativeTooltip.Closed += (_, _) => closed++;
            Native.Call(renderer, "BeginShow", null, owner, new Point(10, 10));
            Native.Check(!Popup().IsOpen && ReferenceEquals(owner.ToolTip, nativeTooltip),
                "The modern renderer intercepted an owner that opted into native tooltip behavior.");
            nativeTooltip.IsOpen = true;
            VisualAssetChecks.Pump(() => nativeTooltip.IsLoaded && opened == 1,
                "The opted-out native tooltip did not open through its original WPF lifecycle.");
            foreach (int mode in new[] { 1, 0, 1 })
            {
                Mode(mode);
                nativeTooltip.ApplyTemplate();
                nativeTooltip.UpdateLayout();
                var border = (Border)VisualTreeHelper.GetChild(nativeTooltip, 0);
                Native.Check(((SolidColorBrush)border.Background).Color == LegacyThemePalette.Brush("ColorBrushPopup").Color &&
                    ((SolidColorBrush)nativeTooltip.Foreground).Color == LegacyThemePalette.Brush("ColorBrush1").Color &&
                    nativeTooltip.IsOpen && opened == 1 && closed == 0 && !Popup().IsOpen,
                    "The native tooltip lost the shared palette or its separate WPF lifetime.");
            }
            nativeTooltip.IsOpen = false;
            VisualAssetChecks.Pump(() => closed == 1, "The native tooltip did not raise its original Closed event.");
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS actual tooltip palettes: modern first-open/switch/replace/reopen, custom-content borrowing and explicit foreground, native opt-out and independent Opened/Closed lifecycle.");
        }
        finally
        {
            ((Popup)slider.FindName("Popup")).IsOpen = false;
            if (nativeTooltip is not null) nativeTooltip.IsOpen = false;
            Native.Call(renderer, "Close", null, false);
            window.Content = null;
            window.Close();
            app.MainWindow = previousMain;
            Mode(previousMode);
        }
    }
}
