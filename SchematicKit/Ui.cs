using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace SchematicKit;

/// <summary>
///     用代码搭建界面用的小工具。库不依赖任何 XAML 资源字典，宿主不需要合并主题文件，
///     颜色统一从 <see cref="SchematicTheme" /> 取，所以换肤只需要换主题。
/// </summary>
internal static class Ui
{
    public static TextBlock Text(string text, double size = 12.5, bool bold = false, Brush? foreground = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = foreground ?? SchematicTheme.TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static TextBlock WrappedText(string text, double size = 12.5, Brush? foreground = null)
    {
        var block = Text(text, size, false, foreground);
        block.TextWrapping = TextWrapping.Wrap;
        block.VerticalAlignment = VerticalAlignment.Top;
        return block;
    }

    public static Border Card(Brush? background = null, Thickness? padding = null, double radius = 8d)
    {
        return new Border
        {
            Background = background ?? SchematicTheme.Surface,
            BorderBrush = SchematicTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(0)
        };
    }

    private const string ButtonTemplate = """
                                         <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                                          TargetType="Button">
                                             <Border x:Name="Bd" Background="__BG__" BorderBrush="__BORDER__" BorderThickness="1"
                                                     CornerRadius="6" SnapsToDevicePixels="True">
                                                 <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center"
                                                                   VerticalAlignment="Center" RecognizesAccessKey="True" />
                                             </Border>
                                             <ControlTemplate.Triggers>
                                                 <Trigger Property="IsMouseOver" Value="True">
                                                     <Setter TargetName="Bd" Property="Background" Value="__HOVER__" />
                                                 </Trigger>
                                                 <Trigger Property="IsPressed" Value="True">
                                                     <Setter TargetName="Bd" Property="Opacity" Value="0.86" />
                                                 </Trigger>
                                                 <Trigger Property="IsEnabled" Value="False">
                                                     <Setter TargetName="Bd" Property="Opacity" Value="0.45" />
                                                 </Trigger>
                                             </ControlTemplate.Triggers>
                                         </ControlTemplate>
                                         """;

    public static Button Button(string text, bool accent = false, double height = 30d, double minWidth = 0d,
        double fontSize = 12.5)
    {
        var background = accent ? SchematicTheme.Accent : SchematicTheme.Surface;
        var hover = accent ? SchematicTheme.AccentHover : SchematicTheme.SurfaceHover;
        var button = new Button
        {
            Content = text,
            Height = height,
            MinWidth = minWidth,
            FontSize = fontSize,
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = Cursors.Hand,
            Foreground = accent ? SchematicTheme.AccentForeground : SchematicTheme.TextPrimary,
            Background = background,
            BorderBrush = accent ? SchematicTheme.Accent : SchematicTheme.Border,
            FocusVisualStyle = null
        };
        button.Template = BuildButtonTemplate(background, hover, accent ? SchematicTheme.Accent : SchematicTheme.Border);
        return button;
    }

    private static ControlTemplate BuildButtonTemplate(Brush background, Brush hover, Brush border)
    {
        var xaml = ButtonTemplate
            .Replace("__BG__", ToHex(background))
            .Replace("__HOVER__", ToHex(hover))
            .Replace("__BORDER__", ToHex(border));
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    public static string ToHex(Brush brush)
    {
        if (brush is not SolidColorBrush { Color: var color }) return "#00000000";
        return color.A == 0xFF
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static CheckBox CheckBox(string text, bool isChecked = false)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            FontSize = 12,
            Foreground = SchematicTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    public static ScrollViewer Scroll(UIElement content, bool vertical = true)
    {
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false
        };
    }

    /// <summary>带占位提示的搜索框。</summary>
    public static TextBox SearchBox(string placeholder, out Grid host)
    {
        var box = new TextBox
        {
            Height = 32,
            FontSize = 12.5,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = SchematicTheme.Surface,
            Foreground = SchematicTheme.TextPrimary,
            BorderBrush = SchematicTheme.Border,
            BorderThickness = new Thickness(1),
            CaretBrush = SchematicTheme.TextPrimary
        };

        var hint = Text(placeholder, 12.5, false, SchematicTheme.TextMuted);
        hint.Margin = new Thickness(13, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;

        host = new Grid();
        host.Children.Add(box);
        host.Children.Add(hint);
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        return box;
    }

    public static Separator Divider()
    {
        return new Separator
        {
            Height = 1,
            Margin = new Thickness(0),
            Background = SchematicTheme.Border
        };
    }

    /// <summary>把多个控件排成一行，间距统一。</summary>
    public static StackPanel Row(double spacing = 8d)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
    }

    public static void Spacing(UIElement element, double margin)
    {
        if (element is FrameworkElement framework) framework.Margin = new Thickness(margin);
    }
}
