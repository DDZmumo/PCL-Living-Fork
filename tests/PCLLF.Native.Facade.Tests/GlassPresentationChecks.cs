// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Presentation.Foundation;
using PCLLF.Presentation.Controls;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassPresentationChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "glass-presentation");
        int animation = ModAnimation.AniControlEnabled;
        ModAnimation.AniControlEnabled++;
        try
        {
            Controls();
            DropDownWheel();
            ClientOnlyActions();
            CollapsibleCard();
            ModalFocusEffects();
            ExtraTextStart(environment);
            GroupedRadioVeto();
            RetiredActions();
            DownloadVersionActions();
            ResourceRows();
            ResourceEntryActivation(scope.Root);
            ResourceSearchLayout();
            ResourceListPositions();
            ResourcePageSizeAndBatches();
            ResourceSearchTransitions();
            ResourceSearchFirstSource();
            HomepageCards();
            VirtualizedRows();
            ActualSettingsPage(scope.Root);
            SettingsChoiceLayout();
            InstanceMemoryOptimizationLayout();
            ModNoticeColors();
        }
        finally { ModAnimation.AniControlEnabled = animation; }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS glass presentation: distinct native WPF tree, real source click/check/preview/text/selection/value pipelines, reverse state, dynamic membership and disposal; genuine settings BAML with explicit old-brand exclusions.");
    }

    private static void HomepageCards()
    {
        const string markup = """
            <StackPanel>
                <StackPanel.Resources>
                    <SolidColorBrush x:Key="AuthoredInk" Color="#FF128080"/>
                    <ControlTemplate x:Key="AuthoredTemplate" TargetType="ContentControl">
                        <Border x:Name="TemplateFace" Background="{StaticResource AuthoredInk}" Padding="4">
                            <ContentPresenter/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="Tag" Value="hide">
                                <Setter TargetName="TemplateFace" Property="Opacity" Value="0.25"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </StackPanel.Resources>
                <Button x:Name="AuthoredAction" Content="初始名称"/>
                <TextBlock x:Name="AuthoredLabel" Text="{Binding Content, ElementName=AuthoredAction}"/>
                <ContentControl x:Name="AuthoredSection" Content="分组" Template="{StaticResource AuthoredTemplate}"/>
                <Border MaxHeight="140" HorizontalAlignment="Center">
                    <Image x:Name="FlexibleArtwork" Stretch="UniformToFill"/>
                </Border>
                <Border x:Name="AuthoredBadge" Width="170" Height="30" Margin="0,-20,0,0" Background="{DynamicResource ColorBrush3}">
                    <TextBlock x:Name="AuthoredBadgeLabel" Text="版本标签" HorizontalAlignment="Center" Foreground="{DynamicResource ColorBrush7}"/>
                </Border>
                <Border Width="90" Height="58" ClipToBounds="True" HorizontalAlignment="Left">
                    <Image x:Name="AuthoredCrop" Stretch="UniformToFill"/>
                </Border>
                <local:MyIconTextButton x:Name="AuthoredIconAction" Text="查看" Logo="M2,2 H18 V18 H2 Z"/>
                <local:MyCard x:Name="AuthoredCard" Title="推荐">
                    <StackPanel Margin="12,32,12,12">
                        <Viewbox x:Name="AuthoredGallery">
                            <StackPanel Orientation="Horizontal">
                                <Border Width="200" Height="40" Background="Green"/>
                                <Border Width="200" Height="40" Background="Orange"/>
                                <Border Width="200" Height="40" Background="Purple"/>
                                <Border Width="200" Height="40" Background="Blue"/>
                            </StackPanel>
                        </Viewbox>
                    </StackPanel>
                </local:MyCard>
            </StackPanel>
            """;
        var page = new PageLaunchRight();
        var source = (StackPanel)Native.Property(typeof(PageLaunchRight), "PanCustom").GetValue(page)!;
        object loader = Native.Get(page, "Native")!;
        void Load(string xaml) => Native.Call(loader.GetType(), "LoadContent", loader, xaml);

        Load(markup);
        var authored = (FrameworkElement)source.Children[0];
        var button = (Button)authored.FindName("AuthoredAction");
        var label = (TextBlock)authored.FindName("AuthoredLabel");
        var section = (ContentControl)authored.FindName("AuthoredSection");
        var gallery = (Viewbox)authored.FindName("AuthoredGallery");
        var artwork = (Image)authored.FindName("FlexibleArtwork");
        var crop = (Image)authored.FindName("AuthoredCrop");
        var badge = (Border)authored.FindName("AuthoredBadge");
        var badgeLabel = (TextBlock)authored.FindName("AuthoredBadgeLabel");
        var iconAction = (MyIconTextButton)authored.FindName("AuthoredIconAction");
        artwork.Source = crop.Source = new DrawingImage(new GeometryDrawing(Brushes.Tan, null, new RectangleGeometry(new Rect(0, 0, 640, 360))));
        int clicks = 0;
        button.Click += (_, _) => clicks++;

        int theme = Settings.Get<int>("UiLauncherColorMode");
        using var session = GlassPresentation.Create(page);
        var window = new Window { Content = session.View, Width = 850, Height = 610, Left = -30000, Top = -30000,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            var outlet = Find<StackPanel>(session.View, "PanCustom");
            Native.Check(source.Children.Count == 0 && ReferenceEquals(outlet.Children[0], authored) && authored.IsVisible,
                "The new shell did not present the original parsed homepage tree.");
            var face = (Border)section.Template.FindName("TemplateFace", section);
            section.Tag = "hide";
            button.Content = "更新名称";
            VisualAssetChecks.Drain();
            Native.Check(label.Text == "更新名称" && Math.Abs(face.Opacity - .25) < .01 &&
                face.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(18, 128, 128),
                "An authored template trigger, static resource or ElementName binding was lost.");
            Invoke(button);
            Native.Check(clicks == 1, "Hosting the authored homepage lost its original action.");
            foreach (int mode in new[] { 0, 1 }) {
                Native.Cache("UiLauncherColorMode", mode);
                Native.Static("PCL.ModSecret", "ThemeRefresh", -1);
                VisualAssetChecks.Drain();
                Color badgeBackground = ((SolidColorBrush)badge.Background).Color;
                Color badgeForeground = ((SolidColorBrush)badgeLabel.Foreground).Color;
                static double Luminance(byte value) => Math.Pow((value / 255d + .055) / 1.055, 2.4);
                double background = Luminance(badgeBackground.R), foreground = Luminance(badgeForeground.R);
                double contrast = (Math.Max(background, foreground) + .05) / (Math.Min(background, foreground) + .05);
                Native.Check(contrast >= 4.5 && badgeForeground.A == 255 && badge.Margin.Top == -20,
                    $"The native homepage's badge lost contrast or its authored placement. Theme {mode}, contrast {contrast:F2}.");
                var hover = (SolidColorBrush)iconAction.FindResource("ColorBrushBg1");
                iconAction.SetCurrentValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)));
                var animation = ModAnimation.AaColor(iconAction, Border.BackgroundProperty, "ColorBrushBg1", 100);
                Color animated = ((ModBase.MyColor)animation.Value).ToMediaColor();
                Native.Check(hover.Color.R == hover.Color.G && hover.Color.G == hover.Color.B && animated == hover.Color &&
                    Equals(iconAction.FindResource("ColorObjectBg1"), hover.Color),
                    "A native button animation did not resolve the scoped neutral color and its color-object alias.");
            }

            foreach (double width in new[] { 850d, 475d }) {
                window.Width = width; window.UpdateLayout(); VisualAssetChecks.Drain();
                var cards = ((StackPanel)gallery.Child).Children.Cast<FrameworkElement>().ToArray();
                Native.Check(cards.All(card => {
                    Rect bounds = card.TransformToVisual(gallery).TransformBounds(new Rect(card.RenderSize));
                    return bounds.Width > 20 && bounds.Left >= -1 && bounds.Right <= gallery.ActualWidth + 1;
                }), "A Viewbox authored in the original homepage clipped its gallery at " + width + " DIP.");
                Native.Check(artwork.Stretch == Stretch.Uniform && artwork.ActualHeight <= 140.5 &&
                    Math.Abs(artwork.ActualWidth / artwork.ActualHeight - 16d / 9) < .04 && crop.Stretch == Stretch.UniformToFill,
                    "The homepage cropped a flexible cover or changed its explicitly fixed crop.");
            }

            Load(markup);
            session.Refresh();
            Native.Check(ReferenceEquals(outlet.Children[0], authored), "Refresh rebuilt an unchanged original homepage.");
            session.Dispose();
            Native.Check(ReferenceEquals(source.Children[0], authored) && BindingOperations.IsDataBound(label, TextBlock.TextProperty) &&
                button.IsEnabled && artwork.Stretch == Stretch.UniformToFill && ReferenceEquals(section.Template.FindName("TemplateFace", section), face),
                "Retiring the glass page cleared the original homepage's bindings, template or input.");

            using var resumed = GlassPresentation.Create(page);
            window.Content = resumed.View;
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var resumedOutlet = Find<StackPanel>(resumed.View, "PanCustom");
            Native.Check(ReferenceEquals(resumedOutlet.Children[0], authored), "Reopening did not reuse the original homepage.");
            Load("""<TextBlock x:Name="ReplacementHome" Text="替换后的主页"/>""");
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(source.Children.Count == 0 && resumedOutlet.Children.Count == 1 &&
                ((FrameworkElement)resumedOutlet.Children[0]).FindName("ReplacementHome") is TextBlock { Text: "替换后的主页" },
                "A live homepage refresh wrote to its hidden original outlet.");
        } finally {
            window.Content = null; window.Close();
            Native.Cache("UiLauncherColorMode", theme);
            Native.Static("PCL.ModSecret", "ThemeRefresh", -1);
            VisualAssetChecks.Drain();
        }
    }

    private static void Controls()
    {
        var unloadedCombo = new MyComboBox { IsEditable = true, Template = null };
        unloadedCombo.Text = "1.21.1";
        unloadedCombo.RefreshColor();
        Native.Check(unloadedCombo.Text == "1.21.1", "Refreshing an editable combo before its template lost the user's text.");
        var source = new StackPanel();
        var button = new MyButton { Name = "ProjectedAction", Text = "实际按钮", Height = 35 };
        var check = new MyCheckBox { Name = "ProjectedToggle", Text = "实际设置", Height = 22 };
        var input = new MyTextBox { Name = "ProjectedInput", Text = "原始文本", HintText = "提示" };
        var help = new MySearchBox { Name = "HelpSearch", HintText = "搜索帮助" };
        var combo = new MyComboBox { Name = "ProjectedChoice" };
        combo.Items.Add(new MyComboBoxItem { Content = "第一项" }); combo.Items.Add(new MyComboBoxItem { Content = "第二项" }); combo.SelectedIndex = 0;
        var slider = new MySlider { Name = "ProjectedRange", MaxValue = 1000, Value = 250, GetHintText = new Func<int, string>(value => $"{value / 10}%") };
        var icon = new MyIconButton { Name = "ProjectedGlyph", ToolTip = "延迟图标" };
        var iconText = new MyIconTextButton { Name = "ProjectedGlyphText", Text = "实际图标按钮" };
        foreach (UIElement control in new UIElement[] { button, check, input, help, combo, slider, icon, iconText }) source.Children.Add(control);
        int clicks = 0, checks = 0, customs = 0; bool veto = true;
        button.Click += (_, _) => clicks++;
        check.PreviewChange += (_, args) => args.Handled = veto;
        check.Change += (_, _) => checks++;
        ControlServiceHost host = ControlServices.Current;
        Action<DependencyObject> previousCustom = host.RaiseCustomEvent;
        host.RaiseCustomEvent = sender => { if (ReferenceEquals(sender, button)) customs++; };
        GlassPresentationSession? session = null; Window? window = null;
        try
        {
            session = GlassPresentation.Create(source);
            window = Host(source, session.View);
            NoSharedVisuals(source, session.View);
            Button glyphButton = Find<Button>(session.View, "ProjectedGlyph");
            var glyph = Descendants(glyphButton).OfType<System.Windows.Shapes.Path>().Single();
            var textGlyph = Descendants(Find<Button>(session.View, "ProjectedGlyphText")).OfType<System.Windows.Shapes.Path>().Single();
            Native.Check(glyph.Data is null && textGlyph.Data is null, "Unset source glyphs should have safe empty new paths.");
            icon.Logo = "M0,0 H10 V10 H0 Z"; iconText.Logo = "M0,0 L10,5 0,10 Z"; session.Refresh();
            Native.Check(ReferenceEquals(glyph.Data, icon.Path.Data) && ReferenceEquals(textGlyph.Data, iconText.ShapeLogo.Data) &&
                ReferenceEquals(glyphButton, Find<Button>(session.View, "ProjectedGlyph")), "Delayed source glyphs did not update the existing new actions.");
            Button action = Find<Button>(session.View, "ProjectedAction");
            Native.Check(action.GetType() == typeof(Button) && action.ActualHeight >= 34, "Button is not a new native WPF action with a compact target size.");
            Invoke(action); Native.Check(clicks == 1 && customs == 1, "New action bypassed or duplicated the original click/custom pipeline.");
            CheckBox toggle = Find<CheckBox>(session.View, "ProjectedToggle");
            Toggle(toggle); Native.Check(!check.Checked && toggle.IsChecked == false && checks == 0, "A vetoed original checkbox changed or was not read back by the new toggle.");
            veto = false; Toggle(toggle); Native.Check(check.Checked && toggle.IsChecked == true && checks == 1, "New native toggle did not call the original user-change pipeline exactly once.");
            check.SetChecked(false, false); session.Refresh(); Native.Check(toggle.IsChecked == false, "Original controller checkbox changes did not update the new view.");
            TextBox text = Find<TextBox>(session.View, "ProjectedInput");
            CheckTextPlaceholder(text, "提示");
            CheckTextPlaceholder(Find<TextBox>(session.View, "HelpSearchInput"), "搜索帮助");
            text.Text = "新文本"; VisualAssetChecks.Drain(); Native.Check(input.Text == "新文本", "New input did not reach the genuine validating text control.");
            input.Text = "控制器更新"; session.Refresh(); Native.Check(text.Text == "控制器更新", "Original text state did not update the new input.");
            ComboBox choice = Find<ComboBox>(session.View, "ProjectedChoice");
            choice.SelectedIndex = 1; Native.Check(combo.SelectedIndex == 1 && ReferenceEquals(combo.SelectedItem, combo.Items[1]), "New selection did not preserve the original item identity.");
            combo.SelectedIndex = 0; session.Refresh(); Native.Check(choice.SelectedIndex == 0, "Original selection did not update the new ComboBox.");
            choice.ApplyTemplate();
            var dropdown = (ToggleButton)choice.Template.FindName("DropDownButton", choice);
            var dropdownPeer = UIElementAutomationPeer.CreatePeerForElement(dropdown) ?? new ToggleButtonAutomationPeer(dropdown);
            ((IToggleProvider)dropdownPeer.GetPattern(PatternInterface.Toggle)!).Toggle();
            VisualAssetChecks.Pump(() => ((Popup)choice.Template.FindName("PART_Popup", choice)).IsOpen, "projected dropdown opens through its actual button");
            choice.UpdateLayout(); VisualAssetChecks.Drain();
            var option = (ComboBoxItem)choice.ItemContainerGenerator.ContainerFromIndex(1);
            option.IsSelected = true;
            Native.Check(source.IsLoaded && combo.SelectedIndex == 1, "Dropdown selection did not reach the original source choice.");
            choice.IsDropDownOpen = false;
            Slider range = Find<Slider>(session.View, "ProjectedRange");
            range.Value = 600; Native.Check(slider.Value == 600, "New slider did not update the original semantic value.");
            slider.Value = 420; session.Refresh(); Native.Check(range.Value == 420, "Original slider did not update the new range control.");
            window.UpdateLayout();
            var track = (Track)range.Template.FindName("PART_Track", range);
            var thumb = track.Thumb;
            track.IncreaseRepeatButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Native.Check(thumb.IsDragging && thumb.IsMouseCaptured && range.Value != 420,
                "Pressing the track failed to move and capture the thumb in the same gesture.");
            double pressed = range.Value;
            double distance = track.ActualWidth / 5 * (pressed > 500 ? -1 : 1);
            thumb.RaiseEvent(new DragDeltaEventArgs(distance, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Native.Check(range.Value != pressed && source.IsLoaded && slider.Value == range.Value,
                "Continuing a drag from the track did not update the setting.");
            thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = Mouse.MouseUpEvent
            });
            Native.Check(!thumb.IsDragging && !thumb.IsMouseCaptured, "Releasing a track drag left the thumb captured.");
            range.Value = 420;
            window.UpdateLayout();
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            ToolTip? dragHint = null;
            VisualAssetChecks.Pump(() => (dragHint = PresentationSource.CurrentSources.OfType<PresentationSource>()
                .Where(popupSource => popupSource.RootVisual is not null).SelectMany(popupSource => Descendants(popupSource.RootVisual))
                .OfType<ToolTip>().FirstOrDefault(tip => tip.IsOpen && ReferenceEquals(tip.PlacementTarget, range))) is not null,
                "drag tooltip opens in the WPF popup source");
            Native.Check(dragHint!.IsOpen && Equals(dragHint.Content, "42%") && !dragHint.IsHitTestVisible,
                "Pressing a formatted slider thumb did not open a noninteractive value tooltip.");
            double startX = dragHint.PlacementRectangle.X;
            range.Value = 750; window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(Equals(dragHint.Content, "75%") && dragHint.PlacementRectangle.X > startX + 1,
                "The opened slider tooltip did not keep the formatter or follow the thumb.");
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Native.Check(!dragHint.IsOpen, "Releasing the thumb kept its tooltip open.");
            VisualAssetChecks.Pump(() => dragHint.PlacementTarget is null, "the drag tooltip detaches after its closed event");
            var later = new MyButton { Name = "DynamicAction", Text = "动态操作" }; source.Children.Add(later); session.Refresh();
            Native.Check(Find<Button>(session.View, "DynamicAction") != null, "A dynamically added original action was not projected.");
            source.Children.Remove(later); session.Refresh(); Native.Check(!Descendants(session.View).OfType<Button>().Any(item => item.Name == "DynamicAction"), "A removed original action remained visible.");
            var docRun = new System.Windows.Documents.Run("初始更新说明");
            var docViewer = new FlowDocumentScrollViewer { Name = "ReleaseNotesDoc", Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(docRun)) };
            source.Children.Add(docViewer);
            for (int iteration = 0; iteration < 32; iteration++) {
                docRun.Text = "更新说明校验 #" + iteration;
                session.Refresh();
                session.Refresh();
            }
            var projectedDoc = Find<FlowDocumentScrollViewer>(session.View, "ReleaseNotesDoc");
            Native.Check(new System.Windows.Documents.TextRange(projectedDoc.Document.ContentStart, projectedDoc.Document.ContentEnd).Text.Contains("更新说明校验 #31", StringComparison.Ordinal),
                "Projected FlowDocumentScrollViewer lost updated text or overflowed under checked arithmetic.");
            session.Dispose(); Native.Check(((ContentControl)session.View).Content is null, "Disposal retained the rendered tree.");
        }
        finally
        {
            session?.Dispose(); if (window != null) { window.Content = null; window.Close(); }
            host.RaiseCustomEvent = previousCustom;
            VisualAssetChecks.Drain();
        }
    }

    private static void DropDownWheel()
    {
        var source = new StackPanel();
        var combo = new MyComboBox { Name = "WallpaperFit" };
        for (int index = 0; index < 24; index++) combo.Items.Add(new MyComboBoxItem { Content = "壁纸位置 " + index });
        source.Children.Add(combo);
        var java = new MyComboBox { Name = "ComboAdvanceJava" };
        string longPath = @"C:\Program Files\" + new string('a', 140) + @"\bin\java.exe";
        java.Items.Add(new MyListItem { Title = longPath });
        source.Children.Add(java);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        var body = new StackPanel { Children = { session.View, new Border { Height = 900 } } };
        var page = new ScrollViewer { Content = body };
        Window? window = null;
        try {
            window = Host(source, page);
            var wallpaper = new DrawingImage(new GeometryDrawing(Brushes.CadetBlue, null, new RectangleGeometry(new Rect(0, 0, 850, 650))));
            wallpaper.Freeze();
            var root = (FrameworkElement)window.Content;
            GlassMaterial.SetWallpaperSource(root, wallpaper);
            GlassMaterial.SetWallpaperHost(root, root);
            ComboBox projected = Find<ComboBox>(session.View, "WallpaperFit");
            projected.IsDropDownOpen = true;
            var popup = (Popup)projected.Template.FindName("PART_Popup", projected);
            var options = (ScrollViewer)projected.Template.FindName("DropDownScroll", projected);
            var frost = (GlassSurface)projected.Template.FindName("DropDownSurface", projected);
            VisualAssetChecks.Pump(() => popup.IsOpen && options.ScrollableHeight > 0, "long wallpaper option list opens with its own scroll viewport");
            Native.Check(!frost.ExtendWallpaper && frost.BlurRadius == 24 && frost.Parent is Grid popupRoot && GlassPopupBackdrop.GetEnable(popupRoot),
                "The dropdown did not use the window-aware frost shared by popup surfaces.");
            Native.Check(Wheel(options, Mouse.PreviewMouseWheelEvent), "The dropdown did not consume the preview wheel over its own options.");
            VisualAssetChecks.Pump(() => options.VerticalOffset > 0, "options scroll over the popup");
            double offset = options.VerticalOffset;
            Native.Check(Wheel(page, Mouse.PreviewMouseWheelEvent), "The opened dropdown allowed the background page to handle the wheel.");
            VisualAssetChecks.Pump(() => options.VerticalOffset > offset, "a wheel over the page goes to the open options");
            Native.Check(page.VerticalOffset == 0, "The page moved behind the expanded options.");
            projected.IsDropDownOpen = false;
            VisualAssetChecks.Drain();
            var priority = Find<ComboBox>(session.View, java.Name);
            priority.Width = 260;
            window.UpdateLayout();
            priority.IsDropDownOpen = true;
            var surface = (GlassSurface)priority.Template.FindName("DropDownSurface", priority);
            VisualAssetChecks.Pump(() => surface.IsLoaded && surface.ActualWidth > 0, "Java priority popup opens");
            Native.Check(Math.Abs(surface.ActualWidth - priority.ActualWidth) < 1 &&
                Equals(surface.Background, surface.FindResource("GlassPresentationDialogFill")),
                "A long Java path widened the popup or lost its menu tint.");
            priority.IsDropDownOpen = false;
            VisualAssetChecks.Drain();
            Native.Check(!Wheel(page, Mouse.PreviewMouseWheelEvent), "The closed dropdown still intercepts the page's wheel input.");
            Wheel(page, Mouse.MouseWheelEvent);
            VisualAssetChecks.Pump(() => page.VerticalOffset > 0, "the background resumes scrolling after closing the options");
        } finally {
            if (window != null) { window.Content = null; window.Close(); }
            VisualAssetChecks.Drain();
        }

        static bool Wheel(UIElement target, RoutedEvent routedEvent) {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = routedEvent };
            target.RaiseEvent(wheel);
            return wheel.Handled;
        }
    }

    private static void CollapsibleCard()
    {
        var source = new StackPanel();
        var body = new StackPanel();
        var label = new TextBlock { Name = "CardPayload", Text = "可折叠内容" }; body.Children.Add(label);
        var card = new MyCard { Name = "CollapsibleCard", Title = "下载选项", CanSwap = true, BorderChild = body, SwapControl = body };
        var replicas = new StackPanel { Children = { new TextBlock { Text = "副本实例" } } };
        var nested = new MyCard { Name = "NestedCard", Title = "测试副本", CanSwap = true, BorderChild = replicas, SwapControl = replicas };
        body.Children.Add(nested);
        source.Children.Add(card);
        var deferredBody = new StackPanel();
        var deferredInput = new MyTextBox { Name = "DeferredCardInput", Text = "保留的输入" };
        deferredBody.Children.Add(deferredInput);
        for (int index = 0; index < 80; index++) deferredBody.Children.Add(new MyCheckBox { Text = $"高级选项 {index}" });
        var deferredCard = new MyCard { Name = "DeferredCard", Title = "高级选项", CanSwap = true, IsSwapped = true,
            BorderChild = deferredBody, SwapControl = deferredBody };
        source.Children.Add(deferredCard);
        var construction = System.Diagnostics.Stopwatch.StartNew();
        using GlassPresentationSession session = GlassPresentation.Create(source);
        construction.Stop();
        Window? window = null;
        try
        {
            window = Host(source, session.View);
            Expander expander = Descendants(Find<Border>(session.View, "CollapsibleCard")).OfType<Expander>().First();
            Expander child = Descendants(Find<Border>(session.View, "NestedCard")).OfType<Expander>().Single();
            TextBlock content = Find<TextBlock>(session.View, "CardPayload");
            Native.Check(expander.IsExpanded && content.IsVisible, "The genuine collapsible card did not project its content.");
            NoSharedVisuals(source, session.View);
            child.IsExpanded = false; VisualAssetChecks.Drain(); session.Refresh();
            Native.Check(nested.IsSwapped && !child.IsExpanded && !card.IsSwapped && expander.IsExpanded && content.IsVisible,
                "Collapsing a nested card also collapsed its parent.");
            child.IsExpanded = true; VisualAssetChecks.Drain(); session.Refresh();
            Native.Check(!nested.IsSwapped && child.IsExpanded && !card.IsSwapped && expander.IsExpanded,
                "Reopening a nested card changed its parent state or lost the original action.");
            expander.IsExpanded = false; VisualAssetChecks.Drain(); session.Refresh();
            Native.Check(card.IsSwapped && !expander.IsExpanded, "Collapsing the new card lost the original swap state.");
            expander.IsExpanded = true; VisualAssetChecks.Drain(); session.Refresh();
            Native.Check(!card.IsSwapped && ReferenceEquals(content, Find<TextBlock>(session.View, "CardPayload")),
                "Reopening the projected card replaced or reparented its live content.");
            var deferred = Descendants(Find<Border>(session.View, "DeferredCard")).OfType<Expander>().Single();
            var projectedBody = (StackPanel)deferred.Content;
            Console.WriteLine($"Collapsed settings fixture: initial projection {construction.Elapsed.TotalMilliseconds:F1} ms, closed content roots {projectedBody.Children.Count}.");
            Native.Check(!deferred.IsExpanded && projectedBody.Children.Count == 0, "Opening a page built the hidden body of an initially collapsed card.");
            deferred.IsExpanded = true; VisualAssetChecks.Drain(); session.Refresh();
            var liveInput = Find<TextBox>(session.View, "DeferredCardInput");
            liveInput.Text = "来自新界面";
            Native.Check(!deferredCard.IsSwapped && deferredInput.Text == "来自新界面", "The lazy card lost its original text binding.");
            deferred.IsExpanded = false; VisualAssetChecks.Drain(); session.Refresh();
            deferred.IsExpanded = true; VisualAssetChecks.Drain(); session.Refresh();
            Native.Check(ReferenceEquals(liveInput, Find<TextBox>(session.View, "DeferredCardInput")),
                "The already opened lazy card discarded its live controls on collapse.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void ClientOnlyActions()
    {
        var source = new StackPanel();
        var server = new MyIconButton { ToolTip = "下载服务端", Logo = "M0,0 H10 V10 H0 Z" };
        var log = new MyIconButton { ToolTip = "更新日志", Logo = "M0,0 L10,5 0,10 Z" };
        var row = new MyListItem { Name = "ClientResource", Title = "支持server的模组", Info = "说明中保留下载服务端等文字", Type = MyListItem.CheckType.Clickable, Buttons = new[] { server, log } };
        var ordinary = new Button { Name = "ClientActions", Content = "局域网联机" };
        var menu = new ContextMenu();
        foreach (string caption in new[] { "另存为", "下载服务端", "安装为服务端", "服务端文件", "服务器列表", "更新日志" }) menu.Items.Add(new MenuItem { Header = caption });
        ordinary.ContextMenu = menu; source.Children.Add(row); source.Children.Add(ordinary);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View); session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(!Descendants(session.View).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "下载服务端"), "Client presentation retained the actual server-download toolbar action.");
            Native.Check(Descendants(session.View).OfType<TextBlock>().Any(text => text.Text == row.Info) && Find<FrameworkElement>(session.View, "ClientResource") != null,
                "Client-only policy accidentally filtered a resource description/name mentioning server.");
            string[] projected = Find<Button>(session.View, "ClientActions").ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header.ToString()!).ToArray();
            Native.Check(projected.SequenceEqual(new[] { "另存为", "服务器列表", "更新日志" }), "Client context menu removed unrelated actions or retained a server install/download entry.");
            Native.Check(row.Buttons.Count() == 2 && menu.Items.Count == 6, "Client-only presentation policy changed its original business/source actions.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void ModalFocusEffects()
    {
        var root = new Grid();
        var background = new Grid { Background = Brushes.SteelBlue };
        background.Children.Add(new TextBlock { Text = "背景界面保持独立", FontSize = 24 });
        var scrim = new Border { Background = Brushes.Black };
        var dialog = new Border { Child = new TextBlock { Text = "弹窗文字保持清晰" }, Width = 280, Height = 160 };
        root.Children.Add(background); root.Children.Add(scrim); root.Children.Add(dialog);
        var window = new Window { Content = root, Width = 640, Height = 440, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        object? effects = null;
        try
        {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Type type = Native.Type("PCL.GlassModalEffects");
            effects = Activator.CreateInstance(type, Native.Flags, null, [root, background, scrim, dialog], null)!;
            Native.Call(type, "SetMessage", effects, new object());
            VisualAssetChecks.Pump(() => (double)Native.Property(type, "Progress").GetValue(effects)! > .999, "modal blur open endpoint");
            Native.Check(background.Effect is System.Windows.Media.Effects.BlurEffect { Radius: > 5.9 } && dialog.Effect is null &&
                PCLLF.Wpf.Glass.GlassMaterial.GetWallpaperSource(dialog) is BitmapSource { IsFrozen: true } && !background.IsHitTestVisible && dialog.IsHitTestVisible,
                "Modal focus did not blur its background, retain sharp foreground or supply an owned backdrop snapshot.");
            Native.Call(type, "SetMessage", effects, new object?[] { null });
            Native.Check(background.IsHitTestVisible && !dialog.IsHitTestVisible, "Modal dismissal delayed input ownership until the blur finished.");
            VisualAssetChecks.Pump(() => background.Effect is null && scrim.Visibility == Visibility.Collapsed, "modal blur close endpoint");
        }
        finally { (effects as IDisposable)?.Dispose(); window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void ExtraTextStart(FixtureEnvironment environment)
    {
        // Exercise the genuine page constructor, BAML and Initialized path. Its normal loader
        // Start sees a genuine finished in-memory cache and therefore creates no worker.
        var originalClient = ModDownload.DlClientListLoader;
        PropertyInfo refreshed = Native.Property(typeof(ModDownloadLib), "IsForgeRecommendedRefreshed");
        object? previousRefresh = refreshed.GetValue(null);
        int unexpectedLoads = 0;
        var cached = new ModLoader.LoaderTask<string, ModDownload.DlClientListResult>("Projection cached client list", _ => { unexpectedLoads++; throw new InvalidOperationException("Cached projection fixture started a worker."); });
        cached.Output = new ModDownload.DlClientListResult { Value = new Newtonsoft.Json.Linq.JObject { ["versions"] = new Newtonsoft.Json.Linq.JArray(
            new Newtonsoft.Json.Linq.JObject { ["id"] = "1.21.1", ["type"] = "release", ["releaseTime"] = new DateTime(2024, 8, 8, 0, 0, 0, DateTimeKind.Utc), ["url"] = "https://invalid.example/release.json" },
            new Newtonsoft.Json.Linq.JObject { ["id"] = "26w01a", ["type"] = "snapshot", ["releaseTime"] = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), ["url"] = "https://invalid.example/snapshot.json" }) } };
        cached.State = ModBase.LoadState.Finished;
        GlassPresentationSession? session = null; Window? window = null;
        try
        {
            ModDownload.DlClientListLoader = cached;
            refreshed.SetValue(null, true);
            var page = new PageDownloadInstall { VanillaName = "1.21.1", VanillaData = new Newtonsoft.Json.Linq.JObject { ["url"] = "https://invalid.example/fixture.json" } };
            Native.Check(page.GetType() == typeof(PageDownloadInstall) && unexpectedLoads == 0 && cached.LastRunningThread is null && cached.State == ModBase.LoadState.Finished,
                "The exact page constructor did not stay on its genuine finished-loader cache path.");
            var start = (MyExtraTextButton)page.FindName("BtnStart");
            ((MyTextBox)page.FindName("TextSelectName")).Text = "ProjectionInstallFixture";
            object controller = Native.Property(typeof(PageDownloadInstall), "Service").GetValue(page)!;
            object host = Native.Get(controller, "host")!;
            int requested = 0, clicked = 0;
            ModDownloadLib.McInstallRequest? captured = null;
            MouseButtonEventArgs? clickArgs = null;
            object? clickSender = null;
            Native.Set(host, "MinecraftFolder", (Func<string>)(() => environment.Data + Path.DirectorySeparatorChar));
            Native.Set(host, "StartRequested", (Action<ModDownloadLib.McInstallRequest>)(request => { requested++; captured = request; }));
            start.Click += (sender, args) => { clicked++; clickSender = sender; clickArgs = args; };
            start.Show = true; start.IsEnabled = true;
            session = GlassPresentation.Create(start);
            window = new Window { Content = session.View, Width = 420, Height = 180, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Button action = Find<Button>(session.View, "BtnStart");
            Native.Check(!page.IsLoaded && !start.IsLoaded && action.IsVisible, "The isolated real install button unexpectedly loaded its backend page.");
            Invoke(action);
            Native.Check(clicked == 1 && requested == 1 && captured?.NewInstanceName == "ProjectionInstallFixture" && captured.MinecraftJson == "https://invalid.example/fixture.json",
                "The new install action did not reach the actual page Click/start-request path exactly once.");
            Native.Check(ReferenceEquals(clickSender, start.PanClick) && clickArgs?.RoutedEvent == UIElement.MouseLeftButtonUpEvent &&
                ReferenceEquals(clickArgs.Source, start.PanClick) && clickArgs.Handled,
                "The projected install action lost its original logical sender, routed-event identity or handled completion.");
            start.Show = false; session.Refresh();
            Native.Check(action.Visibility == Visibility.Collapsed, "An unavailable ExtraText action remained visible after its source Show changed.");
            Native.Check(unexpectedLoads == 0 && cached.LastRunningThread is null, "Isolated install-button activation created loader work.");
            environment.AssertNoForbiddenIo();
        }
        finally
        {
            session?.Dispose();
            if (window != null) { window.Content = null; window.Close(); }
            VisualAssetChecks.Drain();
            ModDownload.DlClientListLoader = originalClient;
            refreshed.SetValue(null, previousRefresh);
        }
    }

    private static void GroupedRadioVeto()
    {
        var source = new StackPanel();
        var first = new MyRadioBox { Name = "RadioBefore", Text = "原选择" };
        var second = new MyRadioBox { Name = "RadioAfter", Text = "候选选择" };
        source.Children.Add(first); source.Children.Add(second); first.SetChecked(true, false);
        bool veto = true; int previews = 0, checks = 0;
        second.PreviewCheck += (_, args) => { previews++; args.Handled = veto; };
        second.Check += (_, _) => checks++;
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View);
            RadioButton before = Find<RadioButton>(session.View, "RadioBefore"), after = Find<RadioButton>(session.View, "RadioAfter");
            Select(after);
            Native.Check(first.Checked && !second.Checked && before.IsChecked == true && after.IsChecked == false && previews == 1 && checks == 0,
                "A vetoed grouped radio did not restore every visible peer to the actual source selection.");
            veto = false; Select(after);
            Native.Check(!first.Checked && second.Checked && before.IsChecked == false && after.IsChecked == true && previews == 2 && checks == 1,
                "A successful grouped radio activation duplicated preview/check or lost source/view grouping.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void RetiredActions()
    {
        var source = new StackPanel(); int clicked = 0, commands = 0, menuClicks = 0;
        var action = new MyButton { Name = "RetiredAction", Text = "可移除操作" };
        action.Click += (_, _) => clicked++;
        var input = new MyTextBox { Name = "RetiredInput", Text = "保留值" };
        var range = new Slider { Name = "RetiredRange", Minimum = 0, Maximum = 100, Value = 12 };
        var toggle = new CheckBox { Name = "RetiredToggle", IsChecked = false };
        var password = new PasswordBox { Name = "RetiredPassword", Password = "fixture-only" };
        var command = new CountingCommand(() => commands++);
        var native = new Button { Name = "RetiredNative", Content = "原生命令", Command = command };
        var menu = new ContextMenu();
        var originalItem = new MenuItem { Header = "可移除菜单", IsCheckable = true, Command = command };
        originalItem.Click += (_, _) => menuClicks++; menu.Items.Add(originalItem); native.ContextMenu = menu;
        foreach (UIElement item in new UIElement[] { action, input, range, toggle, password, native }) source.Children.Add(item);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View); session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Button removed = Find<Button>(session.View, "RetiredAction");
            IInvokeProvider removedInvoke = InvokePattern(removed);
            source.Children.Remove(action); session.Refresh();
            TryRetiredInvoke(removedInvoke); removed.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, removed));
            Native.Check(clicked == 0, "A retained removed-node action still reached its source callback.");
            TextBox oldInput = Find<TextBox>(session.View, "RetiredInput");
            Slider oldRange = Find<Slider>(session.View, "RetiredRange");
            CheckBox oldToggle = Find<CheckBox>(session.View, "RetiredToggle");
            PasswordBox oldPassword = Find<PasswordBox>(session.View, "RetiredPassword");
            Button oldNative = Find<Button>(session.View, "RetiredNative");
            var oldMenu = oldNative.ContextMenu!;
            var oldItem = (MenuItem)oldMenu.Items[0];
            ICommand oldMenuCommand = oldItem.Command!;
            IInvokeProvider pending = InvokePattern(oldNative);
            pending.Invoke(); int beforeDispose = commands;
            session.Dispose(); VisualAssetChecks.Drain();
            TryRetiredInvoke(pending);
            oldInput.Text = "退休后写入"; oldRange.Value = 99; oldToggle.IsChecked = true; oldPassword.Password = "discarded-fixture";
            oldNative.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, oldNative));
            oldMenuCommand.Execute(null); oldItem.IsChecked = true;
            oldItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, oldItem));
            VisualAssetChecks.Drain();
            Native.Check(input.Text == "保留值" && range.Value == 12 && toggle.IsChecked == false && password.Password == "fixture-only",
                "A retired nested input/binding still wrote to the original source state.");
            Native.Check(commands == beforeDispose && menuClicks == 0 && !originalItem.IsChecked && !oldMenuCommand.CanExecute(null),
                "A retained/queued command or menu activation survived owner disposal.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private sealed class CountingCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
    private static IInvokeProvider InvokePattern(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        return (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!;
    }
    private static void TryRetiredInvoke(IInvokeProvider invoke)
    {
        try { invoke.Invoke(); } catch (ElementNotEnabledException) { }
        VisualAssetChecks.Drain();
    }
    private static void Select(RadioButton radio)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(radio) ?? new RadioButtonAutomationPeer(radio);
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select(); VisualAssetChecks.Drain();
    }

    private static void ActualSettingsPage(string fixtureRoot)
    {
        var page = new PageSetupUI();
        var playbackSource = (StackPanel)page.FindName("PanMusicDetail");
        playbackSource.Visibility = Visibility.Visible;
        ((MySlider)page.FindName("SliderMusicVolume")).Value = 230;
        ((ISettingControl)page.FindName("CheckMusicVisualizer")).RefreshSetting("true");
        ((ISettingControl)page.FindName("ComboMusicVisualizerStyle")).RefreshSetting("0");
        var featureCard = (MyCard)page.FindName("CardSwitch");
        string[] removedFeatureNames = Descendants(featureCard).OfType<MyCheckBox>().Where(check => !string.IsNullOrEmpty(check.Name)).Select(check => check.Name).Append("CardSwitch").ToArray();
        using GlassPresentationSession session = GlassPresentation.Create(page);
        session.View.Width = 1000; session.View.Height = 650;
        session.View.Measure(new Size(1000, 650)); session.View.Arrange(new Rect(0, 0, 1000, 650)); session.View.UpdateLayout();
        session.Refresh(); session.View.UpdateLayout();
        VisualAssetChecks.Drain();
        // The source BAML/controller objects are retained as semantics, not inserted into this view.
        NoSharedVisuals(page, session.View);
        Native.Check(removedFeatureNames.Contains("CheckHiddenPageDownload") &&
            !Descendants(session.View).OfType<FrameworkElement>().Any(element => removedFeatureNames.Contains(element.Name)),
            "The new settings tree retained the removed feature-hiding card or one of its original setting controls.");
        Native.Check(Descendants(session.View).OfType<ComboBox>().Any(combo => combo.Name == "ComboLauncherColorMode" && combo.GetType() == typeof(ComboBox)), "The actual settings page did not create a new native appearance selector.");
        Native.Check(!Descendants(session.View).OfType<FrameworkElement>().Any(element => element.Name == "PanOfficialTheme"), "The new settings view retained the old official-purchase panel.");
        Native.Check(!Descendants(session.View).OfType<FrameworkElement>().Any(element => element.Name is "CardLogo" or "CheckBackgroundColorful"), "The new settings view retained unused legacy titlebar/rainbow controls.");
        Native.Check(!Descendants(session.View).OfType<FrameworkElement>().Any(element => element.Name is
            "ComboBackgroundMode" or "BtnBackgroundOpen" or "BtnBackgroundRefresh" or "BtnBackgroundClear"),
            "The new settings view exposed folder-based background selection.");
        Native.Check(!Descendants(session.View).OfType<CheckBox>().Any(check => AutomationProperties.GetName(check) == "打开启动器时显示 PCL 图标"), "The new settings view retained the old PCL-logo option.");
        foreach (CheckBox projectedCheck in Descendants(session.View).OfType<CheckBox>()) {
            projectedCheck.ApplyTemplate();
            Native.Check(projectedCheck.Template.FindName("SwitchTrack", projectedCheck) is null &&
                projectedCheck.Template.FindName("CheckMark", projectedCheck) is not null,
                "Settings MyCheckBox still used a pill switch instead of the bespoke PCL-LF checkbox: " + projectedCheck.Name);
        }
        Native.Check(Descendants(session.View).OfType<Border>().Any(border => border.Name == "CardLauncherSurface" && border.CornerRadius == new CornerRadius(10)), "The actual settings group did not separate its heading from the new field surface.");
        ComboBox appearance = Find<ComboBox>(session.View, "ComboLauncherColorMode");
        Native.Check(Math.Abs(appearance.ActualHeight - 34) * VisualTreeHelper.GetDpi(appearance).DpiScaleY <= 1 && Math.Abs(appearance.ActualWidth - 220) < 1,
            "Settings editor lost its compact fixed-height/right-column proportions: " + appearance.ActualWidth + " x " + appearance.ActualHeight);
        FrameworkElement section = Descendants(session.View).OfType<FrameworkElement>().Single(element => element.Name == "CardLauncher");
        Native.Check(section.ActualWidth > 0 && section.ActualWidth <= 800, "Settings sections exceeded the reference-scale content measure.");
        var music = Find<StackPanel>(session.View, "CardMusic");
        var gamePolicy = Find<ComboBox>(music, "ComboMusicGamePolicy");
        var startMusic = (MyCheckBox)page.FindName("CheckMusicStart");
        var stopMusic = (MyCheckBox)page.FindName("CheckMusicStop");
        Native.Check(Find<TextBlock>(music, "MusicVolumePercent").Text == "23%" &&
            Find<WrapPanel>(music, "MusicFileActions").Children.Count == 3 && gamePolicy.Items.Count == 3,
            "The music card lost its live volume, file actions or game policy choices.");
        gamePolicy.SelectedIndex = 1;
        Native.Check(startMusic.Checked && !stopMusic.Checked, "The play policy did not update the original mutually exclusive music switches.");
        gamePolicy.SelectedIndex = 2;
        Native.Check(!startMusic.Checked && stopMusic.Checked, "The pause policy did not update the original mutually exclusive music switches.");
        gamePolicy.SelectedIndex = 0;
        Native.Check(!startMusic.Checked && !stopMusic.Checked, "Keeping playback unchanged left a game music action enabled.");
        var visualize = Find<CheckBox>(music, "CheckMusicVisualizer");
        var spectrum = Find<ComboBox>(music, "ComboMusicVisualizerStyle");
        spectrum.SelectedIndex = 1;
        visualize.IsChecked = false;
        Native.Check(!spectrum.IsEnabled && ((ISettingControl)page.FindName("ComboMusicVisualizerStyle")).GetCurrentSetting() == "1" &&
            !((MyCheckBox)page.FindName("CheckMusicVisualizer")).Checked,
            "Disabling visualization lost the chosen line style or failed to reach the original settings control.");
        visualize.IsChecked = true;
        playbackSource.Visibility = Visibility.Collapsed;
        VisualAssetChecks.Drain();
        Native.Check(spectrum.IsEnabled && spectrum.SelectedIndex == 1 &&
            Find<StackPanel>(music, "MusicPlaybackSettings").Visibility == Visibility.Collapsed && visualize.Visibility == Visibility.Visible,
            "The visualization controls could not be configured when the music folder was empty.");
        playbackSource.Visibility = Visibility.Visible;
        // Cards frost the wallpaper beneath them; fields and other controls never carry their own material.
        Native.Check(Descendants(session.View).OfType<PCLLF.Wpf.Glass.GlassSurface>().All(surface => surface.BlurRadius > 0) &&
            !Descendants(session.View).OfType<Control>().Any(control => control is TextBoxBase or ComboBox or Slider or System.Windows.Controls.Primitives.ToggleButton or PasswordBox &&
                Descendants(control).OfType<PCLLF.Wpf.Glass.GlassSurface>().Any()),
            "Frosted material must belong to cards and plates, never to a field or toggle.");
        Native.Check(session.UnsupportedTypes.Count == 0, "Actual settings page contains unhandled presentation leaves: " + string.Join(", ", session.UnsupportedTypes));
        Native.Check(!page.IsLoaded, "Inspecting new settings presentation ran the original page's Loaded workflow.");
        foreach (int width in new[] { 1120, 810 }) RenderSettings(session.View, fixtureRoot, width);
    }

    private static void ResourceSearchLayout()
    {
        var source = new PageResource();
        source.PageType = PCLCS.ResourceTypes.Mod;
        source.SearchTags.Add(new MyComboBoxItem { Content = "全部", Tag = "", IsSelected = true });
        int searches = 0;
        object service = Native.Property(typeof(PageResource), "Service").GetValue(source)!;
        object searchHost = Native.Get(service, "host")!;
        Native.Set(searchHost, "Start", (Action)(() => searches++));
        Native.Set(searchHost, "ShouldStart", (Func<ResourceSearcher.SearchRequest, bool>)(_ => false));
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = new Window { Content = session.View, Width = 800, Height = 620, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            TextBox search = Find<TextBox>(session.View, "ResourceSearchInput");
            Button filter = Find<Button>(session.View, "ResourceFilterButton");
            Border panel = Find<Border>(session.View, "ResourceFilterPanel");
            Native.Check(!source.IsLoaded && panel.Visibility == Visibility.Collapsed && filter.Content?.ToString() == "筛选设置",
                "Resource search did not use the compact toolbar or unexpectedly ran the backend Loaded flow.");
            search.Text = "layout-only-query";
            Native.Check(((MyTextBox)source.FindName("TextSearchName")).Text == "layout-only-query", "The compact search field lost the actual search input.");
            var originalInput = (MyTextBox)source.FindName("TextSearchName");
            Binding liveBinding = search.GetBindingExpression(TextBox.TextProperty)!.ParentBinding;
            // IME preedit lives in the visible editor before the composed text reaches the source.
            search.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text)) {
                Source = originalInput, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit
            });
            search.SetCurrentValue(TextBox.TextProperty, "zhong");
            session.Refresh();
            Native.Check(search.Text == "zhong" && originalInput.Text == "layout-only-query",
                "Refreshing resource results overwrote uncommitted text in the search editor.");
            search.SetBinding(TextBox.TextProperty, liveBinding);
            originalInput.Text = "";
            Native.Check(search.Text.Length == 0, "Resetting the source left the visible search out of date.");
            CheckTextPlaceholder(search, "搜索");
            search.Text = "layout-only-query";
            Invoke(filter);
            VisualAssetChecks.Pump(() => panel.Height > 100 && panel.Opacity > .99, "resource filter expansion");
            Native.Check(Descendants(panel).OfType<ComboBox>().Count() == 3, "The compact filter did not preserve the three available original choices.");
            ((MyComboBox)source.FindName("ComboSearchLoader")).Visibility = Visibility.Visible;
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(Descendants(panel).OfType<ComboBox>().Count() == 4, "The expanding filter lost the original optional loader choice.");
            window.Width = 440; window.Height = 330; window.UpdateLayout(); VisualAssetChecks.Drain();
            var resultScroll = Find<ScrollViewer>(session.View, "ResourceResultsScroll");
            ((StackPanel)source.FindName("PanContent")).Visibility = Visibility.Visible;
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(resultScroll.ActualHeight >= 60 && panel.ActualHeight < session.View.ActualHeight - 100,
                "Expanded filters consumed the entire compact result viewport.");
            var filterScroll = (ScrollViewer)panel.Child;
            Native.Check(filterScroll.ScrollableHeight > 0, "Overflowing compact filters have no reachable scroll viewport.");
            window.Width = 800; window.Height = 620; window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(panel.ActualHeight > 160, "Expanded filters did not remeasure after the window grew.");
            var pages = (MyCard)source.FindName("CardPages"); pages.Visibility = Visibility.Visible; pages.IsEnabled = false;
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(!Find<Button>(session.View, "BtnPageRight").IsEnabled, "The new paging buttons bypassed the real loading-time disabled state.");
            source.Page = 3;
            Button apply = Descendants(panel).OfType<Button>().Single(button => Equals(button.Content, "应用筛选"));
            Invoke(apply);
            Native.Check(searches == 1 && source.Page == 0 && ((MyTextBox)source.FindName("TextSearchName")).Text == "layout-only-query",
                "Apply filters did not execute the genuine search callback exactly once and reset its page.");
            VisualAssetChecks.Pump(() => panel.Visibility == Visibility.Collapsed, "resource apply collapses filters");
            Invoke(filter);
            VisualAssetChecks.Pump(() => panel.Opacity > .99, "resource filters reopen");
            Invoke(filter);
            VisualAssetChecks.Pump(() => panel.Visibility == Visibility.Collapsed, "resource filter collapse");
            Native.Check(!Descendants(session.View).OfType<TextBlock>().Any(text => text.Text.StartsWith("搜索XXX", StringComparison.Ordinal)),
                "The old search card heading remained in the flat resource page.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void ResourceListPositions()
    {
        foreach (var kind in new[] { ResourceTypes.Mod, ResourceTypes.ResourcePack, ResourceTypes.DataPack })
        {
            var source = new PageResource { PageType = kind };
            ((StackPanel)source.FindName("PanContent")).Visibility = Visibility.Visible;
            var projects = (Panel)source.FindName("PanProjects");
            for (int index = 0; index < 24; index++) projects.Children.Add(new MyResourceItem { Title = "资源 " + index });
            Check(source, "ResourceResultsScroll", () => source.Page++);
        }
        var local = new PageInstanceResources();
        ((Grid)local.FindName("PanAllBack")).Visibility = Visibility.Visible;
        ((FrameworkElement)local.FindName("PanLoad")).Visibility = Visibility.Collapsed;
        var originalScroll = (ScrollViewer)local.FindName("PanBack");
        var longList = new StackPanel();
        for (int index = 0; index < 70; index++) longList.Children.Add(new TextBlock { Text = "本机资源 " + index, FontSize = 18 });
        originalScroll.Content = longList;
        Check(local, null, () => ((MySearchBox)local.FindName("SearchBox")).Text = "新的筛选");

        static void Check(FrameworkElement source, string? scrollName, Action newQuery)
        {
            var window = new Window { Width = 760, Height = 380, ShowActivated = false, ShowInTaskbar = false, Left = -30000, Top = -30000 };
            ScrollViewer Attach(GlassPresentationSession session)
            {
                window.Content = session.View;
                window.UpdateLayout();
                VisualAssetChecks.Drain();
                return Descendants(session.View).OfType<ScrollViewer>().First(viewer => scrollName is null || viewer.Name == scrollName);
            }
            try {
                window.Show();
                using (var before = GlassPresentation.Create(source)) {
                    ScrollViewer scroll = Attach(before);
                    Native.Check(scroll.ScrollableHeight > 350, $"The {source.GetType().Name} scroll fixture had only {scroll.ScrollableHeight:F0} DIP to scroll.");
                    scroll.ScrollToVerticalOffset(320);
                    VisualAssetChecks.Pump(() => Math.Abs(scroll.VerticalOffset - 320) < 1, "the resource list reaches its earlier position");
                }
                window.Content = new TextBlock { Text = "资源详情" };
                window.UpdateLayout();
                using (var after = GlassPresentation.Create(source)) {
                    ScrollViewer scroll = Attach(after);
                    VisualAssetChecks.Pump(() => Math.Abs(scroll.VerticalOffset - 320) < 1, "returning from details restores the resource offset");
                    newQuery();
                    after.Refresh();
                    VisualAssetChecks.Pump(() => scroll.VerticalOffset < 1, "a new resource query/page begins at the top");
                }
            } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
        }
    }

    private static void ResourcePageSizeAndBatches()
    {
        const string key = "ToolDownloadResourcePageSize";
        int saved = Settings.Get<int>(key);
        Native.Check(Settings.GetDefault(key) == "15", "New resource lists do not default to fifteen entries.");
        Native.Cache(key, 15);
        var source = new PageResource { PageType = ResourceTypes.ModPack };
        source.SearchTags.Add(new MyComboBoxItem { Content = "全部", Tag = "", IsSelected = true });
        var results = (StackPanel)source.FindName("PanProjects");
        var content = (StackPanel)source.FindName("PanContent");
        var pagination = (MyCard)source.FindName("CardPages");
        var warning = (MyHint)source.FindName("HintError");
        Native.Check(warning.Visibility == Visibility.Collapsed, "The resource page showed a provider warning before the first request finished.");
        var storage = source.Storage;
        storage.ModrinthTotal = storage.ModrinthOffset = 95;
        storage.CurseForgeTotal = storage.CurseForgeOffset = 0;
        for (int index = 0; index < 95; index++) {
            storage.Results.Add(new ResourceProject(new JObject {
                ["Platform"] = (int)ResourcePlatforms.Modrinth, ["Types"] = (int)ResourceTypes.ModPack,
                ["Id"] = "fixture-" + index, ["Slug"] = "fixture-" + index, ["RawName"] = "资源 " + index,
                ["Description"] = "分页样本", ["LastUpdate"] = new DateTime(2024, 1, 1), ["DownloadCount"] = 1,
                ["Tags"] = new JArray(), ["Drops"] = new JArray(210), ["ModLoaders"] = (int)ModLoaders.Fabric,
                ["Website"] = "https://example.invalid/fixture"
            }));
        }
        var remaining = storage.Results.Skip(40).ToArray();
        storage.Results.RemoveRange(40, remaining.Length);
        ((MyTextBox)source.FindName("TextSearchName")).Text = "应用过的搜索";
        var input = (ResourceSearcher.SearchRequest)Native.Call(typeof(PageResource), "LoaderInput", source)!;
        Native.Check(input.TargetResultCount == 15 && input.PresentationPageSize == 15, "The initial query did not ask for the new default.");
        using var finishSupplement = new ManualResetEventSlim();
        source.Loader = new ModLoader.LoaderTask<ResourceSearcher.SearchRequest, int>("分页样本", task => {
            Native.Check(finishSupplement.Wait(TimeSpan.FromSeconds(8)), "The resource supplement fixture was not released.");
            task.Input.Storage.Results.AddRange(remaining.Take(task.Input.TargetResultCount - storage.Results.Count));
        });
        source.Loader.Input = input;
        source.Loader.State = ModBase.LoadState.Finished;
        var wrapper = new ProjectedResourceLoaderPage { Child = source };
        Native.Call(typeof(PageResource), "PageResource_Loaded", source, source, new RoutedEventArgs());
        wrapper.PageOnEnter();
        void Render() => Native.Call(typeof(PageResource), "Load_OnFinish", source);
        Native.Check(results.Children.Count == 15, "The result page rendered a different default count.");
        pagination.Visibility = Visibility.Collapsed;

        using var view = GlassPresentation.Create(source);
        var window = new Window { Width = 750, Height = 390, Content = view.View, ShowActivated = false,
            ShowInTaskbar = false, Left = -30000, Top = -30000 };
        try {
            window.Show(); window.UpdateLayout();
            var slot = Find<ContentControl>(view.View, "ResourcePageSizeSlot");
            var selector = (ComboBox)slot.Content;
            Native.Check(slot.IsVisible && ((ComboBoxItem)selector.SelectedItem).Content?.ToString() == "15 条/页" &&
                !Descendants(Find<Border>(view.View, "ResourceFilterPanel")).Contains(selector),
                "The page-size selector was missing from the footer or incorrectly lived inside the filters.");
            source.Page = 2;
            ((MyTextBox)source.FindName("TextSearchName")).Text = "尚未提交的搜索";
            selector.SelectedIndex = 3;
            Native.Check(source.Page == 0 && Settings.Get<int>(key) == 60 &&
                source.Loader.Input.PresentationPageSize == 60 && source.Loader.Input.TargetResultCount == 60 &&
                source.Loader.Input.SearchText == "应用过的搜索" && !ReferenceEquals(source.Loader.Input.Storage, storage) &&
                source.Loader.Input.Storage.Results.SequenceEqual(storage.Results),
                "Changing page size did not persist, reuse the applied query, or reset to page one.");

            var scroll = Find<ScrollViewer>(view.View, "ResourceResultsScroll");
            var flatRows = Find<ContentControl>(view.View, "ResourceFlatResults");
            VisualAssetChecks.Pump(() => flatRows.Content is Panel { Children.Count: 40 } && scroll.ScrollableHeight > 1500,
                "show cached rows while the rest of the page is loading");
            scroll.ScrollToVerticalOffset(52 * 24);
            VisualAssetChecks.Pump(() => scroll.VerticalOffset > 1200, "scroll through the preview before completion");
            double before = scroll.VerticalOffset;
            finishSupplement.Set();
            VisualAssetChecks.Pump(() => source.Loader.State == ModBase.LoadState.Finished && content.Visibility == Visibility.Visible,
                "the real loader publishes its completed page");
            Native.Check(results.Children.Count == 60, "Completing the supplement removed the preview without publishing all sixty rows.");
            VisualAssetChecks.Pump(() => flatRows.Content is Panel { Children.Count: 60 } && scroll.ScrollableHeight > 2500,
                "the final list replaces the preview while keeping its extent");
            Native.Check(Math.Abs(scroll.VerticalOffset - before) < 1, "Finishing a supplement changed the reader's scroll position.");
            double extent = scroll.ExtentHeight;
            scroll.ScrollToVerticalOffset(52 * 48);
            VisualAssetChecks.Pump(() => results.Children[48] is MyResourceItem, "fast scroll prepares the destination viewport");
            Native.Check(results.Children[16] is IControlVirtualElement &&
                Math.Abs(scroll.ExtentHeight - extent) < 1, "Fast scrolling realized the skipped section or changed the scroll extent as rows became ready.");
            source.Storage.Results.AddRange(remaining.Skip(20));
            source.Page = 1;
            Render();
            Native.Check(results.Children.Count == 35 && !((MyIconButton)source.FindName("BtnPageRight")).IsEnabled,
                "The second page used a stale slice size or advertised an empty next page.");

            var pending = new ResourceSearcher.SearchRequest(source.PageType, source.Storage, 120) { PresentationPageSize = 60 };
            source.Loader.Input = pending;
            source.Loader.State = ModBase.LoadState.Loading;
            content.Visibility = Visibility.Collapsed;
            Native.Call(typeof(PageResource), "QueueSearchPreview", source, pending, remaining.Skip(20).Take(8).ToArray());
            var back = Find<Button>(view.View, "BtnPageLeft");
            var next = Find<Button>(view.View, "BtnPageRight");
            var progress = Find<ContentControl>(view.View, "ResourceSearchLoading");
            VisualAssetChecks.Pump(() => pagination.IsEnabled == false && progress.Visibility == Visibility.Visible,
                "show inline progress and disable the pager on the partially loaded page");
            Native.Check(back.IsVisible && next.IsVisible && !back.IsEnabled && !next.IsEnabled && slot.IsEnabled,
                "The partially loaded page hid its arrows or enabled navigation before it was complete.");
            source.Loader.State = ModBase.LoadState.Finished;
            content.Visibility = Visibility.Visible;
            Render();
            VisualAssetChecks.Pump(() => back.IsEnabled && !next.IsEnabled && progress.Visibility == Visibility.Collapsed,
                "allow going back after the final, short page has finished");
        }
        finally {
            finishSupplement.Set();
            window.Content = null; window.Close();
            source.Loader.Cancel();
            Settings.Set(key, saved);
            VisualAssetChecks.Drain();
        }
    }

    private static void CheckTextPlaceholder(TextBox input, string text)
    {
        var hint = ((Panel)input.Parent).Children.OfType<TextBlock>().Single(label => label.Text == text);
        MultiBinding binding = BindingOperations.GetMultiBinding(hint, UIElement.VisibilityProperty)!;
        Native.Check(binding.Bindings.OfType<Binding>().Any(part => ReferenceEquals(part.Source, input) &&
            part.Path.Path == nameof(UIElement.IsKeyboardFocusWithin)), "The placeholder did not track the visible editor's focus.");
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Native.Check(Equals(binding.Converter.Convert(["", true, text], typeof(Visibility), null, culture), Visibility.Hidden) &&
            Equals(binding.Converter.Convert(["", false, text], typeof(Visibility), null, culture), Visibility.Visible) &&
            Equals(binding.Converter.Convert(["中文", false, text], typeof(Visibility), null, culture), Visibility.Collapsed),
            "The placeholder can overlap preedit or committed text.");
    }

    private sealed class ProjectedResourceLoaderPage : MyPageRight
    {
        protected override bool UseProjectedNavigation => true;
    }

    private static void ResourceSearchTransitions()
    {
        var source = new PageResource();
        var wrapper = new ProjectedResourceLoaderPage();
        var projects = (StackPanel)source.FindName("PanProjects");
        var content = (FrameworkElement)source.FindName("PanContent");
        var loading = (FrameworkElement)source.FindName("PanLoad");
        projects.Children.Add(new TextBlock { Name = "PreviousSearchRow", Text = "旧结果" });
        PageLoaderState state = PageLoaderState.Finished;
        Action<PageLoaderState, PageLoaderState>? changed = null;
        wrapper.InitializeLoader(new PageLoaderBinding {
            ProjectContent = true, ReadState = () => state, ConnectState = callback => changed = callback,
            SetLoadingState = () => { }, ConnectClick = _ => { }
        }, loading, content, null, autoRun: false);
        wrapper.PageOnEnter();
        using var session = GlassPresentation.Create(source);
        var window = new Window { Content = session.View, Width = 800, Height = 620, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            var plate = Find<Grid>(session.View, "ResourceResultsPlate");
            var rows = Find<ContentControl>(plate, "ResourceFlatResults");
            var progress = Find<ContentControl>(session.View, "ResourceSearchLoading");
            var search = Find<TextBox>(session.View, "ResourceSearchInput");
            var old = Find<TextBlock>(plate, "PreviousSearchRow");
            Native.Check(rows.Opacity == 1, "Already loaded results replayed the search animation on page mount.");
            var stages = (StackPanel)rows.Parent;
            Native.Check(stages.Children.IndexOf(progress) == stages.Children.IndexOf(rows) + 1,
                "Search progress was not located beneath the list.");

            SetState(PageLoaderState.Loading);
            Native.Check(content.Visibility == Visibility.Collapsed && loading.Visibility == Visibility.Visible && wrapper.NativePageState == MyPageRightView.PageStates.LoaderStay,
                "Resource search retained the legacy loader wait.");
            if (PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation)
                VisualAssetChecks.Pump(() => rows.Opacity is > .08 and < .98 && !rows.IsHitTestVisible && old.IsVisible, "resource search old rows exit");
            projects.Children.Clear();
            projects.Children.Add(new MyVirtualizingElement<TextBlock>(() => new TextBlock { Name = "NewSearchRow", Text = "新结果" }));
            Native.Call(typeof(PageResource), "NotifyResultsChanged", source);
            SetState(PageLoaderState.Finished);
            Native.Check(content.Visibility == Visibility.Visible && loading.Visibility == Visibility.Collapsed && wrapper.NativePageState == MyPageRightView.PageStates.ContentStay,
                "A ready search was held behind a minimum loading time.");
            if (PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation)
                VisualAssetChecks.Pump(() => !old.IsVisible && rows.Visibility == Visibility.Visible && rows.Opacity < .99, "resource search new rows enter");
            VisualAssetChecks.Pump(() => rows.Opacity > .999 && progress.Visibility == Visibility.Collapsed &&
                Descendants(plate).OfType<TextBlock>().Any(row => row.Name == "NewSearchRow"), "resource search settles with viewport-realized results");
            session.Refresh();
            Native.Check(rows.IsHitTestVisible && rows.Opacity > .999 && ReferenceEquals(search, Find<TextBox>(session.View, "ResourceSearchInput")),
                "Realizing results replayed the entrance or replaced the editing surface.");

            // Local query changes replace rows without toggling the source loading panel.
            projects.Children.Clear();
            projects.Children.Add(new TextBlock { Name = "LocalSearchRow", Text = "收藏查询" });
            Native.Call(typeof(PageResource), "NotifyResultsChanged", source);
            VisualAssetChecks.Pump(() => Descendants(plate).OfType<TextBlock>().Any(row => row.Name == "LocalSearchRow") && rows.Opacity > .999,
                "local search publishes its replacement rows without polling");
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }

        void SetState(PageLoaderState next)
        {
            var previous = state;
            state = next;
            changed!(next, previous);
        }
    }

    private static void ResourceSearchFirstSource()
    {
        using var slow = new ManualResetEventSlim();
        var available = new TaskCompletionSource<IReadOnlyList<ResourceProject>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new ResourceSearcher.SearchResult();
        var request = new ResourceSearcher.SearchRequest(ResourceTypes.Mod, storage, 15) { PresentationPageSize = 15 };
        var engine = new ResourceSearchEngine<ResourceProject, ResourceSearcher.SearchResult>(new ResourceSearchHost<ResourceProject> {
            CreateProject = json => new ResourceProject(json), SimplifyChinese = text => text,
            WikiEntries = () => [], Info = _ => { }, Warning = _ => { }, Warn = (_, _) => { },
            StartThread = (action, name) => { var thread = new Thread(() => action()) { IsBackground = true, Name = name }; thread.Start(); return thread; },
            Request = url => {
                if (url.Contains("curseforge.com", StringComparison.Ordinal)) {
                    Native.Check(url.Contains("pageSize=8&", StringComparison.Ordinal), "CurseForge exceeded its share of the fifteen-result batch.");
                    if (!slow.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Timed out waiting for fixture to release CurseForge.");
                    return new JObject { ["data"] = new JArray(Project(ResourcePlatforms.CurseForge, "1")), ["pagination"] = new JObject { ["totalCount"] = 1 } };
                }
                Native.Check(url.Contains("limit=7&", StringComparison.Ordinal), "Modrinth exceeded its share of the fifteen-result batch.");
                return new JObject { ["hits"] = new JArray(Project(ResourcePlatforms.Modrinth, "fixture1")), ["total_hits"] = 1 };
            }
        });
        var run = Task.Run(() => engine.Run(request, new ResourceSearchTask {
            IsCanceled = () => false, Progress = () => 0, SetProgress = _ => { }
        }, results => available.TrySetResult(results)));
        try
        {
            Native.Check(available.Task.Wait(TimeSpan.FromSeconds(5)) && available.Task.Result.Single().Platform == ResourcePlatforms.Modrinth && !run.IsCompleted,
                "The ready source was not published before the slower one responded.");
            Native.Check(storage.Results.Count == 0, "Preliminary search rows changed the completed result store.");
        }
        finally { slow.Set(); run.GetAwaiter().GetResult(); }
        Native.Check(storage.Results.Select(project => project.Platform).ToHashSet().SetEquals([ResourcePlatforms.CurseForge, ResourcePlatforms.Modrinth]),
            "The final resource search lost one of the selected sources.");

        static JObject Project(ResourcePlatforms platform, string id) => new() {
            ["Platform"] = (int)platform, ["Types"] = (int)ResourceTypes.Mod, ["Id"] = id, ["Slug"] = "test-" + id,
            ["RawName"] = "Fixture " + id, ["Description"] = "Fixture resource " + id, ["LastUpdate"] = new DateTime(2024, 1, 1),
            ["DownloadCount"] = 42, ["Tags"] = new JArray(), ["Drops"] = new JArray(210), ["ModLoaders"] = (int)ModLoaders.Fabric,
            ["Website"] = "https://example.invalid/" + id
        };
    }

    private static void VirtualizedRows()
    {
        int first = 0, last = 0;
        var rows = new StackPanel();
        rows.Children.Add(new MyVirtualizingElement(() => { first++; return new MyButton { Name = "RealizedFirst", Text = "首个实际操作" }; }) { Height = 44 });
        rows.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1200, Fill = Brushes.Transparent });
        rows.Children.Add(new MyVirtualizingElement(() => { last++; return new MyButton { Name = "RealizedLast", Text = "末个实际操作" }; }) { Height = 44 });
        var source = new ScrollViewer { Content = rows, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        using GlassPresentationSession session = GlassPresentation.Create(source);
        session.View.Width = 500; session.View.Height = 120;
        Window? window = null;
        try
        {
            Native.Check(first == 0 && last == 0, "Constructing presentation eagerly realized hidden original rows.");
            window = Host(source, session.View);
            VisualAssetChecks.Pump(() => first == 1, "New visible viewport did not realize the first original semantic row.");
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(last == 0 && Find<Button>(session.View, "RealizedFirst") != null, "Offscreen original rows were eagerly initialized or the realized action was not projected.");
            ScrollViewer scroll = Descendants(session.View).OfType<ScrollViewer>().First();
            scroll.ScrollToBottom(); window.UpdateLayout();
            VisualAssetChecks.Pump(() => last == 1, "Scrolling the new viewport did not realize the trailing original row.");
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(first == 1 && last == 1 && Find<Button>(session.View, "RealizedLast") != null, "Semantic virtualization initialized a row more than once or lost its actual new action.");
            NoSharedVisuals(source, session.View);
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void DownloadVersionActions()
    {
        var entry = new JObject {
            ["id"] = "1.21.5", ["type"] = "release",
            ["url"] = "https://example.invalid/version.json",
            ["releaseTime"] = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)
        };
        int selected = 0, built = 0, saved = 0, logs = 0, custom = 0;
        object? selectedRow = null, selectedTag = null;
        MyListItem row = ModDownloadLib.McDownloadListItem(entry, (sender, _) => {
            selected++; selectedRow = sender; selectedTag = ((MyListItem)sender).Tag;
        }, false).Init();
        row.Name = "DownloadVersionRow";
        Action<MyListItem, EventArgs> deferred = row.ContentHandler ?? throw new InvalidOperationException("The real download builder no longer exposes its deferred source actions.");
        row.ContentHandler = (sender, args) => { built++; deferred(sender, args); };
        var source = new StackPanel(); source.Children.Add(row);
        var file = new MyListItem { Name = "DownloadFileAction", Title = "实际资源文件", Type = MyListItem.CheckType.Clickable, Tag = new ResourceVersion() };
        var summary = new MyListItem { Name = "DownloadFileSummary", Title = "版本摘要", Type = MyListItem.CheckType.Clickable, Tag = new ResourceVersion() };
        int files = 0; file.Click += (_, _) => files++;
        source.Children.Add(file); source.Children.Add(summary);
        object menus = typeof(ModDownloadLib).GetField("Menus", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object menuHost = Native.Get(menus, "host")!;
        object previousSave = Native.Get(menuHost, "SaveMinecraft")!;
        object previousOpen = Native.Get(menuHost, "OpenWebsite")!;
        ControlServiceHost services = ControlServices.Current;
        Action<DependencyObject> previousCustom = services.RaiseCustomEvent;
        GlassPresentationSession? session = null; Window? window = null;
        MyIconButton? originalSave = null;
        try
        {
            Native.Set(menuHost, "SaveMinecraft", (Action<Func<(string Title, JToken Json)>, bool>)((read, server) => {
                (string title, JToken json) = read();
                Native.Check(!server && title == row.Title && ReferenceEquals(json, entry),
                    "The actual save callback lost its original row title/Tag through the action's parent chain.");
                saved++;
            }));
            Native.Set(menuHost, "OpenWebsite", (Action<string>)(url => {
                Native.Check(url == "https://zh.minecraft.wiki/w/Java版1.21.5", "The actual update-log callback lost its version Tag through the original parent chain.");
                logs++;
            }));
            services.RaiseCustomEvent = sender => { if (ReferenceEquals(sender, originalSave)) custom++; };
            session = GlassPresentation.Create(source);
            Native.Check(built == 1 && row.ContentHandler is null && !row.IsMouseOver,
                "Projecting an unhovered download row did not initialize its real source actions exactly once.");
            originalSave = row.Buttons.Single(button => Equals(button.ToolTip, "另存为"));
            RoutedEventArgs? payload = null;
            originalSave.Click += (sender, args) => {
                Native.Check(ReferenceEquals(sender, originalSave), "The projected save replaced the real source sender.");
                payload = args as RoutedEventArgs;
                Native.Check(payload is not null && payload.RoutedEvent == UIElement.MouseLeftButtonUpEvent && ReferenceEquals(payload.Source, originalSave),
                    "The semantic icon action did not retain the real download callback's routed-event contract.");
            };
            window = Host(source, session.View);
            NoSharedVisuals(source, session.View);
            FrameworkElement projectedRow = Find<FrameworkElement>(session.View, row.Name);
            FrameworkElement projectedFile = Find<FrameworkElement>(session.View, file.Name), projectedSummary = Find<FrameworkElement>(session.View, summary.Name);
            Native.Check(row.HasClickAction && file.HasClickAction && !summary.HasClickAction &&
                Descendants(projectedFile).OfType<TextBlock>().Any(text => text.Text == "下载") &&
                !Descendants(projectedSummary).OfType<TextBlock>().Any(text => text.Text is "下载" or "安装") &&
                !Descendants(projectedRow).OfType<TextBlock>().Any(text => text.Text == "安装"),
                "A real version action lost its download label, or a summary/out-of-context Tag invented an install action.");
            Invoke(Descendants(projectedFile).OfType<Button>().Single(button => AutomationProperties.GetName(button) == file.Title));
            Native.Check(files == 1, "The visible download label did not retain the original row action exactly once.");
            Button primary = Descendants(projectedRow).OfType<Button>().Single(button => AutomationProperties.GetName(button) == row.Title);
            Button save = Descendants(projectedRow).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "另存为");
            Button log = Descendants(projectedRow).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更新日志");
            Native.Check(!Descendants(projectedRow).OfType<Button>().Any(button => AutomationProperties.GetName(button) is "详情" or "下载服务端"),
                "Version actions changed their actual download/update-log semantics or retained a server action.");
            Invoke(primary);
            Native.Check(selected == 1 && ReferenceEquals(selectedRow, row) && ReferenceEquals(selectedTag, entry),
                "The main download/install row no longer invokes its actual version callback exactly once.");
            Invoke(save);
            Native.Check(saved == 1 && selected == 1 && custom == 1 && payload?.Handled == true,
                "The real save callback/custom event was lost, duplicated, or bubbled into the row's installation action.");
            Invoke(log);
            Native.Check(logs == 1 && selected == 1, "The actual update-log callback was lost, duplicated, or replaced the main installation action.");
            session.Refresh(); session.Refresh();
            Native.Check(built == 1 && ReferenceEquals(save, Descendants(projectedRow).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "另存为")),
                "Repeated projection rebuilt the original actions or replaced the live save control.");
            IInvokeProvider retained = InvokePattern(save);
            originalSave.CanClick = false; session.Refresh(); TryRetiredInvoke(retained);
            session.Dispose(); TryRetiredInvoke(retained);
            Native.Check(saved == 1 && selected == 1 && custom == 1, "A disabled/retired native version action still reached the download backend.");
        }
        finally
        {
            session?.Dispose();
            if (window != null) { window.Content = null; window.Close(); }
            services.RaiseCustomEvent = previousCustom;
            Native.Set(menuHost, "SaveMinecraft", previousSave);
            Native.Set(menuHost, "OpenWebsite", previousOpen);
            VisualAssetChecks.Drain();
        }
    }

    // Keep the download page ancestry without starting the network loader.
    private sealed class PageDownloadResourceFixture : StackPanel { }

    private static void ResourceRows()
    {
        var source = new PageDownloadResourceFixture();
        var search = new MySearchBox { Name = "ResourceSearch", HintText = "搜索资源", Text = "实际关键词" };
        var file = new ResourceVersion {
            Display = "实际文件",
            FileName = new string('a', 100) + ".jar",
            ReleaseDate = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Local),
            GameVersions = ["1.21.1"],
            ReleaseType = ResourceVersion.ReleaseTypes.Release
        };
        MyListItem version = file.ToListItem((_, _) => { }).Init();
        version.Name = "ResourceVersionRow";
        var resource = new MyResourceItem { Name = "ResourceProjectRow", Title = "实际资源项目", Description = "实际资源说明" };
        ((TextBlock)resource.FindName("LabTime")).Text = "2026-07-20";
        object controller = Native.Property(typeof(MyResourceItem), "Service").GetValue(resource)!;
        object host = Native.Get(controller, "host")!;
        int opened = 0, clicks = 0;
        Native.Set(host, "CurrentPage", (Func<int>)(() => 0));
        Native.Set(host, "CurrentInstance", (Func<ModMinecraft.McInstance>)(() => null!));
        Native.Set(host, "CurrentArguments", (Func<object[]>)(() => null!));
        Native.Set(host, "OpenDetail", (Action<object[]>)(_ => opened++));
        resource.Click += (_, _) => clicks++;
        source.Children.Add(search); source.Children.Add(version); source.Children.Add(resource);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try
        {
            window = Host(source, session.View);
            window.Width = 400;
            session.Refresh(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(Find<FrameworkElement>(session.View, "ResourceSearch").ActualHeight == 32, "Search is not a compact32px native field.");
            FrameworkElement fileRow = Find<FrameworkElement>(session.View, "ResourceVersionRow");
            Native.Check(fileRow.ActualHeight >= 48 && fileRow.ActualHeight <= 56, "Version row no longer uses the reference-scale flat list height.");
            var releaseDate = Descendants(fileRow).OfType<TextBlock>().Single(text => text.Text == "2026-07-20");
            Native.Check(releaseDate.IsVisible && releaseDate.ActualWidth > 0 &&
                releaseDate.TranslatePoint(new Point(releaseDate.ActualWidth, 0), session.View).X <= session.View.ActualWidth,
                "The resource detail page hid or clipped the file's release date.");
            Native.Check(!Descendants(fileRow).OfType<TextBlock>().Any(text => text.Text.Contains("更新于 ", StringComparison.Ordinal)),
                "The resource file description duplicated its separate release date.");
            Button detail = Descendants(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == resource.Title);
            Invoke(detail); Native.Check(opened == 1 && clicks == 1, "New resource row did not execute the genuine detail/Click path exactly once.");
            resource.CanInteraction = false; session.Refresh(); resource.PerformClick();
            Native.Check(!detail.IsEnabled && opened == 1 && clicks == 1, "A noninteractive resource row bypassed the original guard.");
            Button clear = Descendants(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "清空搜索");
            Invoke(clear); Native.Check(search.Text == string.Empty, "The compact clear action did not use the actual search backend.");
            NoSharedVisuals(source, session.View);
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void SettingsChoiceLayout()
    {
        var page = new PageSetupUI();
        using var session = GlassPresentation.Create((FrameworkElement)page.FindName("CardCustom"));
        var window = new Window { Content = session.View, Width = 820, Height = 900, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show();
            foreach (int width in new[] { 820, 440 }) {
                window.Width = width; window.UpdateLayout(); session.Refresh(); VisualAssetChecks.Drain(); window.UpdateLayout();
                var choices = Descendants(session.View).OfType<RadioButton>().Where(item => item.Name.StartsWith("RadioCustomType", StringComparison.Ordinal)).ToArray();
                var originals = ((Grid)((MyRadioBox)page.FindName("RadioCustomType0")).Parent).Children.OfType<MyRadioBox>().ToArray();
                Native.Check(choices.Length == 4 && choices[0].Parent is Grid, "The home modes no longer use their original grid.");
                foreach (RadioButton choice in choices) {
                    MyRadioBox original = originals.Single(item => item.Name == choice.Name);
                    Native.Check(ReferenceEquals(choice.Parent, choices[0].Parent) && Grid.GetRow(choice) == 0 && Grid.GetColumn(choice) == Grid.GetColumn(original),
                        "A home mode moved out of its original single-row order: " + choice.Name);
                    var label = (TextBlock)choice.Content;
                    Native.Check(label.ActualHeight < 25, $"A home mode label wrapped within the original four-option row at {width}: {label.Text} ({label.ActualWidth} x {label.ActualHeight}).");
                }
            }
            Native.Check(!page.IsLoaded, "Layout verification ran original settings load workflows.");
        } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void ModNoticeColors()
    {
        using var session = GlassPresentation.Create(new PageInstanceModDisabled());
        var view = session.View;
        var size = new Size(900, 480);
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var line = Descendants(view).OfType<System.Windows.Shapes.Rectangle>().Single(shape => Equals(shape.Tag, "NoticeSeparator"));
        var label = Descendants(view).OfType<TextBlock>().Single(text => text.Text == "该版本不可使用 Mod");
        var grid = (Grid)label.Parent;
        foreach (Brush palette in new[] { Brushes.DarkSlateGray, Brushes.LightGray }) {
            grid.Resources["GlassPresentationSeparator"] = palette;
            Native.Check(ReferenceEquals(line.Fill, palette), "The mod notice divider retained its old fill after the palette changed.");
        }
    }

    private static void InstanceMemoryOptimizationLayout()
    {
        var page = new PageInstanceSetup();
        var source = (FrameworkElement)page.FindName("ComboRamOptimize");
        var row = (Grid)source.Parent;
        var card = (MyCard)((FrameworkElement)row.Parent).Parent;
        using var view = GlassPresentation.Create(card);
        var window = new Window { Content = view.View, Height = 480, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show();
            foreach (double width in new[] { 820d, 440d }) {
                window.Width = width; window.UpdateLayout(); view.Refresh(); VisualAssetChecks.Drain(); window.UpdateLayout();
                var editor = Find<ComboBox>(view.View, "ComboRamOptimize");
                var meter = Find<FrameworkElement>(view.View, "PanRamDisplay");
                Rect slot = LayoutInformation.GetLayoutSlot(editor);
                Native.Check(editor.ActualHeight >= 34 && slot.Height >= editor.ActualHeight - .5 &&
                    editor.TranslatePoint(new Point(0, editor.ActualHeight), meter).Y <= .5,
                    $"The instance memory optimization dropdown was clipped or overlapped the meter at width {width}.");
            }
        } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void ResourceEntryActivation(string fixtureRoot)
    {
        object Entry(string name, bool folder) {
            object entry = Activator.CreateInstance(Native.Type("PCL.InstanceResourceEntry"), true)!;
            Native.Set(entry, "Name", name); Native.Set(entry, "FullPath", Path.Combine(fixtureRoot, name)); Native.Set(entry, "IsDirectory", folder);
            return entry;
        }
        var source = new StackPanel();
        var folder = new MyLocalModItem { Name = "ProjectedFolder", Title = "目录", ResourceFolderRequested = () => { } };
        Native.Set(folder, "ResourceEntry", Entry("folder", true));
        int opened = 0; folder.ResourceFolderRequested = () => opened++;
        var screenshot = new MyScreenshotItem { Name = "ProjectedScreenshot", Width = 280 };
        Native.Call(typeof(MyScreenshotItem), "Bind", screenshot, Entry("screenshot.png", false));
        var thumbnail = (PCLCS.ResourceThumbnailController)Native.Get(screenshot, "Controller")!;
        var clicks = new List<int>(); thumbnail.ClickRequested += clicks.Add;
        source.Children.Add(folder); source.Children.Add(screenshot);
        using GlassPresentationSession session = GlassPresentation.Create(source);
        Window? window = null;
        try {
            window = Host(source, session.View);
            PresentationSource input = PresentationSource.FromVisual(window)!;
            void Enter(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, input, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Button directory = Descendants(session.View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "目录");
            Enter(directory); VisualAssetChecks.Drain();
            Native.Check(opened == 1 && !folder.Checked, "Opening a projected resource directory lost its original command or selected it instead.");
            Grid tile = Find<Grid>(session.View, screenshot.Name);
            tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Enter(tile); VisualAssetChecks.Drain();
            Native.Check(clicks.SequenceEqual(new[] { 1, 2 }), "Projected screenshot selection/preview lost the real thumbnail controller's click count.");
            CheckBox selection = Descendants(tile).OfType<CheckBox>().Single();
            selection.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Native.Check(clicks.Count == 2, "Selecting the screenshot checkbox also opened its image.");
            session.Dispose(); Enter(tile); Enter(directory);
            Native.Check(clicks.Count == 2 && opened == 1, "A retired resource projection still executed its original commands.");
        }
        finally { if (window != null) { window.Content = null; window.Close(); } VisualAssetChecks.Drain(); }
    }

    private static void RenderSettings(FrameworkElement presentation, string fixtureRoot, int width)
    {
        string evidence = Environment.GetEnvironmentVariable("PCLLF_GLASS_EVIDENCE_DIR") ?? fixtureRoot;
        if (evidence != fixtureRoot)
        {
            string full = Path.GetFullPath(evidence);
            DirectoryInfo? directory = new(full); bool allowed = false;
            for (; directory != null; directory = directory.Parent)
                if (directory.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent &&
                    (File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")))) { allowed = true; break; }
            Native.Check(allowed, "Presentation evidence must stay under a Git workspace's artifacts directory.");
            evidence = full;
        }
        Directory.CreateDirectory(evidence);
        var wallpaper = new DrawingImage(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(158, 179, 182), Color.FromRgb(217, 222, 209), 35), null, new RectangleGeometry(new Rect(0, 0, width, 720)))); wallpaper.Freeze();
        var host = new Grid { Width = width, Height = 720 };
        var background = new Image { Source = wallpaper, Stretch = Stretch.Fill, Clip = new RectangleGeometry(new Rect(24, 24, width - 48, 672), 44, 44) };
        host.Children.Add(background);
        var surface = new Border { Margin = new Thickness(24), Padding = new Thickness(24), CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(223, 245, 246, 248)), Child = presentation };
        host.Children.Add(surface);
        presentation.Width = double.NaN; presentation.Height = double.NaN;
        host.Measure(new Size(width, 720)); host.Arrange(new Rect(0, 0, width, 720)); host.UpdateLayout();
        VisualAssetChecks.Drain(); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
        string path = Path.Combine(evidence, $"settings-new-{width}.png"); VisualAssetChecks.Save(bitmap, path);
        Console.WriteLine("Actual settings presentation render (synthetic wallpaper; not native backdrop proof): " + path);
        surface.Child = null;
    }

    private static Window Host(FrameworkElement source, FrameworkElement presentation)
    {
        var root = new Grid();
        root.Children.Add(new ContentControl { Content = source, Visibility = Visibility.Hidden, IsHitTestVisible = false });
        root.Children.Add(presentation);
        var window = new Window { Content = root, Width = 850, Height = 650, Left = -30000, Top = -30000, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
        Native.Check(source.IsLoaded && !source.IsVisible && presentation.IsLoaded && presentation.IsVisible, "The semantic backend and new presentation did not have the intended independent lifecycles.");
        return window;
    }
    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().Single(item => item.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void NoSharedVisuals(FrameworkElement source, FrameworkElement presentation)
    {
        var originals = Descendants(source).ToHashSet(ReferenceEqualityComparer.Instance);
        Native.Check(Descendants(presentation).All(item => !originals.Contains(item)), "The new presentation reused an original source UIElement.");
        Native.Check(!Descendants(presentation).OfType<FrameworkElement>().Any(item => item.GetType().Assembly == typeof(MyButton).Assembly && item is not GlassAccountPanel), "An original PCL visual control remains in the new render tree.");
    }
    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke(); VisualAssetChecks.Drain();
    }
    private static void Toggle(CheckBox checkbox)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(checkbox) ?? new CheckBoxAutomationPeer(checkbox);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
        VisualAssetChecks.Drain();
    }
}
