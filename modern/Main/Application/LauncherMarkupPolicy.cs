// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Effects;

namespace PCL;

internal static class LauncherMarkupPolicy
{
    private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Launcher = "clr-namespace:PCL;assembly=Plain Craft Launcher 2";
    private const string SystemNamespace = "clr-namespace:System;assembly=mscorlib";

    private static readonly HashSet<string> VisualTypes = Names("""
        Grid RowDefinition ColumnDefinition StackPanel DockPanel WrapPanel Canvas UniformGrid Border Viewbox
        ScrollViewer TextBlock TextBox Label AccessText Button ToggleButton RepeatButton CheckBox RadioButton
        ContentControl ContentPresenter Expander GroupBox ComboBox ComboBoxItem ListBox ListBoxItem ItemsControl
        ItemsPresenter ContextMenu MenuItem Separator Slider ScrollBar ProgressBar Thumb Track
        FlowDocument FlowDocumentScrollViewer Paragraph Run Span Bold Italic Underline LineBreak
        Section List ListItem Table TableColumn TableRowGroup TableRow TableCell BlockUIContainer InlineUIContainer
        Rectangle Ellipse Path Line Polygon Polyline GeometryGroup PathGeometry PathFigure LineSegment ArcSegment
        BezierSegment PolyBezierSegment QuadraticBezierSegment PolyLineSegment RectangleGeometry EllipseGeometry
        SolidColorBrush LinearGradientBrush RadialGradientBrush GradientStop
        ScaleTransform TranslateTransform RotateTransform SkewTransform MatrixTransform TransformGroup
        ResourceDictionary Style Setter Trigger MultiTrigger Condition ControlTemplate DataTemplate ItemsPanelTemplate
        """);

    private static readonly HashSet<string> LauncherTypes = Names("""
        LauncherMarkupCard LauncherMarkupImage LauncherMarkupNotice LauncherMarkupListItem LauncherMarkupLoading
        LauncherMarkupButton LauncherMarkupTextButton LauncherMarkupIconTextButton CustomEvent CustomEventCollection CustomEventService
        """);

    private static readonly HashSet<string> Members = Names("""
        Name Key Resources MergedDictionaries Style BasedOn TargetType Template ContentTemplate ItemsPanel ItemTemplate
        Setters Triggers Conditions Property Value TargetName Child Children Content Header HeaderTemplate Items
        Width Height MinWidth MinHeight MaxWidth MaxHeight Margin Padding HorizontalAlignment VerticalAlignment
        HorizontalContentAlignment VerticalContentAlignment Visibility Opacity IsEnabled IsHitTestVisible
        Focusable IsTabStop TabIndex ClipToBounds UseLayoutRounding SnapsToDevicePixels
        Background Foreground BorderBrush BorderThickness CornerRadius OpacityMask Orientation FlowDirection
        Text Inlines Blocks Document ToolTip TextWrapping TextTrimming TextAlignment TextDecorations
        FontFamily FontSize FontStyle FontStretch FontWeight LineHeight LineStackingStrategy TextIndent
        AcceptsReturn AcceptsTab IsReadOnly IsSelectionEnabled IsChecked IsThreeState IsSelected
        HorizontalScrollBarVisibility VerticalScrollBarVisibility CanContentScroll
        RowDefinitions ColumnDefinitions Row Column RowSpan ColumnSpan Left Top Right Bottom Dock ZIndex
        Columns Rows RowGroups Cells MarkerStyle MarkerOffset StartIndex
        Fill Stroke StrokeThickness StrokeDashArray StrokeDashOffset StrokeDashCap StrokeStartLineCap StrokeEndLineCap
        StrokeLineJoin Data Points X1 X2 Y1 Y2 RadiusX RadiusY Rect Geometry Figures Segments StartPoint
        Point Point1 Point2 Point3 Size IsClosed IsFilled IsLargeArc SweepDirection RotationAngle
        Color GradientStops Offset MappingMode StartPoint EndPoint Center GradientOrigin SpreadMethod
        RenderTransform LayoutTransform RenderTransformOrigin Transform RelativeTransform
        X Y ScaleX ScaleY CenterX CenterY Angle AngleX AngleY Matrix Stretch StretchDirection
        Logo LogoScale LogoOpacity ColorType Title Info Type IsWarn Theme CanSwap IsSwapped Buttons
        Source FallbackSource LoadingSource EnableCache IsScaleAnimationEnabled EventType EventData Events
        IsExpanded IsDirectionReversed Minimum Maximum Value TickFrequency SmallChange LargeChange
        """);

    private static readonly Regex Resource = new(@"\A\{(?:StaticResource|DynamicResource)\s+([^{}]+)\}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TypeReference = new(@"\A\{x:Type\s+([\w:]+)\}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Template = new(@"\A\{TemplateBinding\s+([\w.]+)\}\z", RegexOptions.CultureInvariant);

    internal static void Validate(XElement root, HashSet<string> resourceKeys)
    {
        int elements = 0;
        ValidateElement(root, 0);

        void ValidateElement(XElement element, int depth) {
            if (++elements > 12000 || depth > 64) throw new InvalidDataException("自定义页面的控件数量或嵌套层数超出上限。");
            if (element.Name == XName.Get("TextBlock.Effect", Wpf)) {
                if (depth == 64 || ++elements > 12000 || !AllowsTextEffect(element)) throw Rejected(element.Name.LocalName);
                return;
            }
            string[] name = element.Name.LocalName.Split('.', 2);
            if (!AllowsType(element.Name.NamespaceName, name[0]) || name.Length == 2 &&
                (!Members.Contains(name[1]) || name[1] == "Source" && name[0] != "LauncherMarkupImage"))
                throw Rejected(element.Name.LocalName);
            if (name.Length == 2 && !element.HasElements) ValidateValue(element, name[1], element.Value, resourceKeys);
            foreach (XAttribute attribute in element.Attributes()) {
                if (attribute.IsNamespaceDeclaration || attribute.Name == XNamespace.Xml + "space") continue;
                string member = attribute.Name.LocalName;
                if (attribute.Name.NamespaceName == Xaml) {
                    if (member is "Name" or "Key" && attribute.Value.IndexOfAny(['{', '}']) < 0) continue;
                    throw Rejected(member);
                }
                if (attribute.Name.NamespaceName is not ("" or Launcher or Wpf) || !AllowsMember(element, member)) throw Rejected(member);
                ValidateValue(element, member[(member.LastIndexOf('.') + 1)..], attribute.Value, resourceKeys);
            }
            foreach (XElement child in element.Elements()) ValidateElement(child, depth + 1);
        }
    }

    // A text decoration may only use a literal, bounded WPF effect at this exact property.
    private static bool AllowsTextEffect(XElement property)
    {
        if (property.Parent?.Name != XName.Get("TextBlock", Wpf) || property.HasAttributes ||
            !string.IsNullOrWhiteSpace(property.Value)) return false;
        XElement? effect = property.Elements().FirstOrDefault();
        if (effect is null || property.Elements().Skip(1).Any() ||
            effect.Name.NamespaceName != Wpf || effect.HasElements) return false;
        string type = effect.Name.LocalName;
        if (type is not ("DropShadowEffect" or "BlurEffect")) return false;

        foreach (XAttribute attribute in effect.Attributes()) {
            if (attribute.Name.Namespace != XNamespace.None) return false;
            string value = attribute.Value.Trim();
            bool allowed = attribute.Name.LocalName switch {
                "Radius" when type == "BlurEffect" => NumberInRange(value, 0, 32),
                "KernelType" when type == "BlurEffect" => value is "Box" or "Gaussian",
                "BlurRadius" when type == "DropShadowEffect" => NumberInRange(value, 0, 32),
                "ShadowDepth" when type == "DropShadowEffect" => NumberInRange(value, 0, 16),
                "Direction" when type == "DropShadowEffect" => NumberInRange(value, 0, 360),
                "Opacity" when type == "DropShadowEffect" => NumberInRange(value, 0, 1),
                "Color" when type == "DropShadowEffect" => IsLiteralColor(value),
                "RenderingBias" => value is "Performance" or "Quality",
                _ => false
            };
            if (!allowed) return false;
        }
        return true;
    }

    private static bool NumberInRange(string value, double min, double max) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number >= min && number <= max;

    private static bool IsLiteralColor(string value) => value.Length is > 0 and <= 32 &&
        (value[0] == '#' ? value.Length is 4 or 5 or 7 or 9 && value.Skip(1).All(Uri.IsHexDigit) : value.All(char.IsAsciiLetter));

    private static bool AllowsMember(XElement element, string qualifiedName)
    {
        string[] parts = qualifiedName.Split('.', 2);
        if (parts.Length == 2 && (!VisualTypes.Contains(parts[0]) && parts[0] is not ("Panel" or "CustomEventService"))) return false;
        string member = parts[^1];
        return Members.Contains(member) && (member != "Source" || element.Name.LocalName == "LauncherMarkupImage");
    }

    private static void ValidateValue(XElement element, string member, string value, HashSet<string> resourceKeys)
    {
        value = value.Trim();
        if (member == "Property") {
            if (value == "Source" && element.Ancestors().FirstOrDefault(node => node.Name.LocalName == "Style") is { } style &&
                ((string?)style.Attribute("TargetType") is "local:LauncherMarkupImage" or "{x:Type local:LauncherMarkupImage}")) return;
            if (!AllowsMember(element, value.Replace("local:", "", StringComparison.Ordinal))) throw Rejected(value);
        }
        if (member == "TargetType") {
            var reference = TypeReference.Match(value);
            if (!AllowsTypeName(element, reference.Success ? reference.Groups[1].Value : value)) throw Rejected(value);
            return;
        }
        string actual = member == "Value" ? ((string?)(element.Attribute("Property") ?? element.Parent?.Attribute("Property")) ?? "").Split('.')[^1] : member;
        if (actual == "FontFamily" && value.IndexOfAny(['\\', '/', ':', '#', '{', '}']) >= 0) throw Rejected(actual);
        if (!value.StartsWith('{') || value.StartsWith("{}", StringComparison.Ordinal) || value == "{x:Null}") return;
        var resource = Resource.Match(value);
        if (resource.Success) {
            string key = resource.Groups[1].Value.Trim();
            if (resourceKeys.Contains(key) || key.StartsWith("ColorBrush", StringComparison.Ordinal) || key.StartsWith("GlassPresentation", StringComparison.Ordinal)) return;
        }
        var binding = Template.Match(value);
        if (binding.Success && AllowsMember(element, binding.Groups[1].Value)) return;
        throw Rejected("标记扩展");
    }

    private static bool AllowsTypeName(XElement element, string qualifiedName)
    {
        string[] name = qualifiedName.Split(':', 2);
        return AllowsType(name.Length == 1 ? element.GetDefaultNamespace().NamespaceName : element.GetNamespaceOfPrefix(name[0])?.NamespaceName, name[^1]);
    }

    private static bool AllowsType(string? scope, string type) => scope switch {
        Wpf => VisualTypes.Contains(type), Launcher => LauncherTypes.Contains(type), SystemNamespace => type == "String", _ => false
    };

    internal static bool AllowsRuntimeType(Type type) => type == typeof(string) || type == typeof(TypeExtension) ||
        type == typeof(DropShadowEffect) || type == typeof(BlurEffect) ||
        type == typeof(NullExtension) || type == typeof(StaticResourceExtension) || type == typeof(DynamicResourceExtension) ||
        type == typeof(TemplateBindingExtension) ||
        type.Assembly == typeof(LauncherMarkupCard).Assembly && type.Namespace == typeof(LauncherMarkupCard).Namespace && LauncherTypes.Contains(type.Name) ||
        (type.Assembly == typeof(FrameworkElement).Assembly || type.Assembly == typeof(UIElement).Assembly || type.Assembly == typeof(DependencyObject).Assembly) &&
            VisualTypes.Contains(type.Name);

    private static HashSet<string> Names(string names) => names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    private static InvalidDataException Rejected(string name) => new("自定义页面不允许使用此控件或属性：" + name);
}
