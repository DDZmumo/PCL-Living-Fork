// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Wpf.Motion;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassFeedbackChecks
{
    internal static void Toast(FixtureEnvironment environment)
    {
        const string message = "下载或验证主页失败：程序权限不足。请尝试右键程序，选择以管理员身份运行，或将文件移动到其他文件夹。";
        using var host = new GlassToastHost();
        var frame = new Grid { Children = { host } };
        var window = Host(frame);
        try
        {
            host.Show(message, GlassToastKind.Error);
            host.Show("另一条提示", GlassToastKind.Info);
            window.UpdateLayout();
            var card = (Border)host.Children[0];
            var other = host.Children[1];
            var text = Visuals(card).OfType<TextBlock>().Single(block => block.Text == message);
            var fullText = new TextBlock { Text = message, FontFamily = text.FontFamily, FontSize = text.FontSize,
                TextWrapping = TextWrapping.Wrap };
            fullText.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
            Native.Check(text.TextTrimming == TextTrimming.None && fullText.DesiredSize.Height > 38 &&
                text.ActualHeight + .5 >= fullText.DesiredSize.Height,
                $"Homepage error layout: {text.ActualWidth:F1} × {text.ActualHeight:F1} DIP, full height {fullText.DesiredSize.Height:F1}, trimming {text.TextTrimming}.");
            var close = Visuals(card).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "关闭提示");
            Native.Check(close.IsHitTestVisible && close.IsEnabled, "The toast close command cannot receive input.");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            VisualAssetChecks.Pump(() => !host.Children.Contains(card), "manually dismiss one toast");
            Native.Check(host.Children.Contains(other), "Closing one toast dismissed a different notification.");

            string longMessage = string.Join(" ", Enumerable.Repeat(message, 20));
            host.Show(longMessage, GlassToastKind.Error);
            window.UpdateLayout();
            var longCard = (Border)host.Children[^1];
            var scroll = Visuals(longCard).OfType<ScrollViewer>().Single();
            Native.Check(scroll.ScrollableHeight > 0 && ((TextBlock)scroll.Content).Text == longMessage,
                "Long notifications cannot scroll to the complete message.");
            var playlist = new Rect(frame.ActualWidth - 240, 20, 220, frame.ActualHeight - 40);
            host.AvoidOverlays(frame.RenderSize, playlist);
            window.UpdateLayout();
            Rect placed = host.TransformToVisual(frame).TransformBounds(new Rect(host.RenderSize));
            Native.Check(placed.Right <= playlist.Left - 10 && placed.Left >= 0, "Expanded toasts covered the open playlist.");
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
        environment.AssertNoForbiddenIo();
    }

    internal static void Note(FixtureEnvironment environment)
    {
        // Keep the actual favorites viewport and renderer. The owner is used only for detail navigation.
        using var page = new GlassResourceBrowserPage(null!, null);
        var project = new ResourceProject(new JObject {
            ["Platform"] = (int)ResourcePlatforms.Modrinth, ["Types"] = (int)ResourceTypes.Mod, ["Id"] = "NoteFixture",
            ["Slug"] = "note-fixture", ["RawName"] = "模组菜单", ["Description"] = "Fixture", ["DownloadCount"] = 0,
            ["LastUpdate"] = new DateTime(2025, 1, 1), ["Tags"] = new JArray()
        });
        Native.Property(typeof(ResourceProjectModelData), "WikiEntry").SetValue(project, null);
        string notesPath = (string)Native.Field(typeof(ResourceNotes), "FilePath").GetValue(null)!;
        Native.Check(Native.Full(notesPath).StartsWith(Native.Full(environment.Root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "The notes file is outside the isolated facade fixture.");
        Native.Check(ResourceNotes.Set(project, "111"), "Could not seed an isolated favorite note.");
        var type = typeof(GlassResourceBrowserPage).GetNestedType("Rows", Native.Flags)!;
        object next = Activator.CreateInstance(type, Native.Flags, null,
            [new object(), 0, new[] { project }, null, true, true], null)!;
        var rows = (StackPanel)Native.Get(page, "_rows")!;
        var window = Host(page);
        try
        {
            VisualAssetChecks.Pump(() => page.IsLoaded && page.Scroll.ViewportHeight > 0, "load the favorites viewport");
            page.Open(null, null, loadData: false);
            Native.Call(typeof(GlassResourceBrowserPage), "Present", page, next);
            var entry = (FrameworkElement)rows.Children[0];
            var note = FindNote("111");
            Native.Check(note.Visibility == Visibility.Hidden, "A favorite note appeared while its resource row was still a placeholder.");
            window.UpdateLayout();
            Native.Call(typeof(GlassResourceBrowserPage), "PrepareViewport", page);
            window.UpdateLayout();
            var row = Visuals(entry).OfType<GlassResourceProjectRow>().SingleOrDefault()
                ?? throw new InvalidOperationException($"The viewport did not realize its first resource row at offset {page.Scroll.VerticalOffset:F1}.");
            note = FindNote("111");
            Native.Check(note.Visibility == Visibility.Visible && note.TranslatePoint(new Point(), entry).Y + .1 >=
                row.TranslatePoint(new Point(0, row.ActualHeight), entry).Y, "The favorite note overlapped its resource row.");
            if (MotionPreferences.AllowsAnimation)
                Native.Check(!entry.RenderTransform.Value.IsIdentity && row.RenderTransform.Value.IsIdentity &&
                    note.RenderTransform.Value.IsIdentity, "The resource row and note did not share an entrance transform.");
            var bubble = (GlassFavoriteNote)note;
            VisualAssetChecks.Pump(() => bubble.IsLoaded && bubble.IsVisible, "load the note before toggling it");
            var glyph = bubble.Children.OfType<System.Windows.Shapes.Path>().Single();
            var title = bubble.Children.OfType<TextBlock>().Single(label => label.Text == "“111”");
            double iconCenter = glyph.TranslatePoint(new Point(0, glyph.ActualHeight / 2), bubble).Y;
            double textCenter = title.TranslatePoint(new Point(0, title.LineHeight / 2), bubble).Y;
            Native.Check(Math.Abs(iconCenter - textCenter) < .5, "The note icon was not centered on the first line.");
            var motion = (SpringMotion)Native.Get(bubble, "_motion")!;
            var toggle = (Button)Native.Get(page, "_notesToggle")!;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Native.Check(!bubble.IsExpanded && motion.Target == 0 && (!MotionPreferences.AllowsAnimation || motion.IsRunning) &&
                ReferenceEquals(FindNote("111"), bubble),
                $"Note collapse: expanded={bubble.IsExpanded}, target={motion.Target}, running={motion.IsRunning}, loaded={bubble.IsLoaded}.");
            bubble.SetExpanded(true);
            VisualAssetChecks.Pump(() => !motion.IsRunning && motion.Value == 1, "reverse the note transition");
            Native.Check(ResourceNotes.Set(project, null) && ReferenceEquals(rows.Children[0], entry) &&
                Visuals(entry).Contains(row), "Removing a note reparented its animated row.");
            Native.Check(ResourceNotes.Set(project, "更新后的备注") && ReferenceEquals(rows.Children[0], entry) &&
                FindNote("更新后的备注").Visibility == Visibility.Visible, "Editing notes replaced the animated row container.");
        }
        finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
        environment.AssertNoForbiddenIo();

        FrameworkElement FindNote(string text) => Visuals((DependencyObject)rows.Children[0]).OfType<FrameworkElement>()
            .Single(element => AutomationProperties.GetName(element) == "备注：" + text);
    }

    internal static void ChoiceMenu(FixtureEnvironment environment)
    {
        var anchor = new Button { Content = "排序", Width = 130, Height = 36 };
        var window = Host(new Grid { Children = { anchor } });
        try {
            VisualAssetChecks.Pump(() => anchor.IsLoaded, "load the menu anchor");
            var popup = GlassChoicePopup.Show(anchor,
                [new("添加时间", () => { }), new("重命名", () => { }, ModBase.Logo.IconButtonEdit)], selected: 0);
            try {
                Native.Check(popup.PopupAnimation == (MotionPreferences.AllowsAnimation ? PopupAnimation.Fade : PopupAnimation.None),
                    "The shared native menu ignored the current animation preference.");
                Native.Check(Visuals(popup.Child).OfType<System.Windows.Shapes.Path>().Any(),
                    "Folder commands lost their icons in the shared menu.");
                var same = GlassChoicePopup.Show(anchor, [new("再次点击", () => { })]);
                Native.Check(ReferenceEquals(same, popup) && !same.IsOpen, "A second press left two menus open.");
            } finally { popup.IsOpen = false; }
        } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
        environment.AssertNoForbiddenIo();
    }

    private static Window Host(UIElement content)
    {
        var window = new Window { Content = content, Width = 640, Height = 470, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    internal static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
