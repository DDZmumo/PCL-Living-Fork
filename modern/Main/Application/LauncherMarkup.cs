// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCL;

internal static class LauncherMarkup
{
    private static readonly XNamespace Launcher = "clr-namespace:PCL;assembly=Plain Craft Launcher 2";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XName ResourceKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
    private static readonly IReadOnlyDictionary<string, string> Adapted = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["MyCard"] = nameof(LauncherMarkupCard), ["MyImage"] = nameof(LauncherMarkupImage),
        ["MyHint"] = nameof(LauncherMarkupNotice), ["MyListItem"] = nameof(LauncherMarkupListItem),
        ["MyLoading"] = nameof(LauncherMarkupLoading),
        ["MyButton"] = nameof(LauncherMarkupButton), ["MyTextButton"] = nameof(LauncherMarkupTextButton),
        ["MyIconButton"] = nameof(LauncherMarkupButton), ["MyIconTextButton"] = nameof(LauncherMarkupIconTextButton),
        ["MyExtraTextButton"] = nameof(LauncherMarkupButton)
    };
    private static readonly IReadOnlyDictionary<string, (string Name, string? Style)> Standard =
        new Dictionary<string, (string, string?)>(StringComparer.Ordinal) {
            ["MyCheckBox"] = ("CheckBox", "GlassPresentationCheckBox"),
            ["MyRadioBox"] = ("RadioButton", "GlassPresentationRadioBox"),
            ["MyRadioButton"] = ("RadioButton", "GlassPresentationRadioButton"),
            ["MyTextBox"] = ("TextBox", "GlassPresentationTextBox"),
            ["MyComboBox"] = ("ComboBox", "GlassPresentationComboBox"),
            ["MyComboBoxItem"] = ("ComboBoxItem", null),
            ["MySlider"] = ("Slider", "GlassPresentationSlider"),
            ["MyMenuItem"] = ("MenuItem", null),
            ["MyScrollViewer"] = ("ScrollViewer", "GlassPresentationScrollViewer"),
            ["MyScrollBar"] = ("ScrollBar", null)
        };

    internal static UIElement ParseFragment(string content) => LoadFragment(PrepareFragment(content));

    internal static XElement PrepareFragment(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > LauncherContentDownload.MarkupBytes)
            throw new InvalidDataException("自定义页面超过允许的大小。");
        content = ExpandPublicTokens(content, StringUtils.XmlEscape);
        while (content.Contains("xmlns", StringComparison.Ordinal))
            content = content.RegexReplace("xmlns[^\"']*(\"|')[^\"']*(\"|')", "").Replace("xmlns", "");
        using var xml = XmlReader.Create(new StringReader("""
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:sys="clr-namespace:System;assembly=mscorlib" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:PCL;assembly=Plain Craft Launcher 2"
                xmlns:core="clr-namespace:MeloongCore;assembly=MeloongCore"
                xmlns:corewpf="clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf">
            """ + content + "</StackPanel>"), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = LauncherContentDownload.MarkupBytes + 1024
            });
        var root = XElement.Load(xml, LoadOptions.PreserveWhitespace);
        AdaptThumbnailBrushes(root);
        var authoredKeys = root.Descendants().Attributes(ResourceKey).Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        foreach (XElement element in root.Descendants()) {
            Adapt(element);
            MoveAttribute(element, "EventType", Launcher + "CustomEventService.EventType");
            MoveAttribute(element, "EventData", Launcher + "CustomEventService.EventData");
            if (element.Name == Wpf + "Setter" && element.Attribute("Property") is { Value: "EventType" or "EventData" } property)
                property.Value = "local:CustomEventService." + property.Value;
        }
        LauncherMarkupPolicy.Validate(root, authoredKeys);
        return root;
    }

    internal static UIElement LoadFragment(XElement root)
    {
        var authoredKeys = root.Descendants().Attributes(ResourceKey).Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        foreach (XElement element in root.Descendants()) BindThemeBrushes(element, authoredKeys);
        var view = (UIElement)PCLLF.Presentation.Foundation.LegacyBaseXaml.LoadRestricted(root, typeof(MyBitmap).Assembly.GetName().Name!,
            MyBitmap.NormalizeResourceUri, typeof(SettingService), LauncherMarkupPolicy.AllowsRuntimeType);
        AttachCommands(view);
        return view;
    }

    internal static string ExpandPublicTokens(string value, Func<string, string> escape) => Regex.Replace(value,
        @"\{(?:hint|cave|date|time|pcl_version(?:_code|_branch)?|pcl_build_type|pcl_branch|varia?ble:[\w-]{1,64}(?::[^{}]{0,128})?)\}",
        match => escape(ModMain.ArgumentReplace(match.Value)), RegexOptions.CultureInvariant);

    // Homepages commonly use a rounded empty Border as a thumbnail. Keep the
    // existing image transport instead of letting the native XAML reader fetch a brush URI.
    private static void AdaptThumbnailBrushes(XElement root)
    {
        foreach (XElement brush in root.Descendants(Wpf + "ImageBrush").ToArray()) {
            if (brush.Parent is not { } background || background.Name != Wpf + "Border.Background" ||
                background.Parent is not { } border || border.Name != Wpf + "Border" ||
                border.Elements().Count() != 1 || background.HasAttributes || background.Elements().Count() != 1 ||
                brush.HasElements || !string.IsNullOrWhiteSpace(background.Value) ||
                brush.Attributes().Any(attribute => attribute.Name.Namespace != XNamespace.None ||
                    attribute.Name.LocalName is not ("ImageSource" or "Stretch"))) continue;
            string? source = (string?)brush.Attribute("ImageSource");
            string stretch = (string?)brush.Attribute("Stretch") ?? "Fill";
            if (source is null || !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || !LauncherContentDownload.IsHttps(uri) ||
                stretch is not ("None" or "Fill" or "Uniform" or "UniformToFill")) continue;
            var image = new XElement(Launcher + nameof(LauncherMarkupImage), new XAttribute("Source", source),
                new XAttribute("Stretch", stretch), new XAttribute("IsHitTestVisible", "False"));
            if (double.TryParse((string?)border.Attribute("CornerRadius"), NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) &&
                double.IsFinite(radius) && radius > 0)
                image.SetAttributeValue("CornerRadius", radius);
            background.ReplaceWith(image);
        }
    }

    // XamlReader parses the fragment before it is attached to a Glass resource scope. A static
    // reference to an application brush would otherwise capture the classic palette at parse time.
    private static void BindThemeBrushes(XElement element, HashSet<string> authoredKeys)
    {
        const string prefix = "{StaticResource ";
        foreach (var attribute in element.Attributes()) {
            string value = attribute.Value;
            if (!value.StartsWith("{StaticResource ColorBrush", StringComparison.Ordinal) || !value.EndsWith('}')) continue;
            string key = value[prefix.Length..^1];
            if (authoredKeys.Contains(key) || Application.Current?.TryFindResource(key) is not System.Windows.Media.SolidColorBrush) continue;
            string? property = attribute.Name.LocalName;
            if (property == "Value" && element.Name == Wpf + "Setter") property = (string?)element.Attribute("Property");
            if (property is "Foreground" or "Background" or "BorderBrush" or "Fill" or "Stroke")
                attribute.Value = "{DynamicResource " + key + "}";
        }
    }

    private static void Adapt(XElement element)
    {
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) {
            if (attribute.Name.LocalName.EndsWith("Type", StringComparison.Ordinal) || attribute.Value.StartsWith('{'))
                attribute.Value = Regex.Replace(attribute.Value, @"\blocal:(My\w+)", match =>
                    Adapted.TryGetValue(match.Groups[1].Value, out var alias) ? "local:" + alias :
                    Standard.TryGetValue(match.Groups[1].Value, out var standard) ? "wpf:" + standard.Name : match.Value);
        }
        if (element.Name.Namespace == Wpf) {
            if (element.Name.LocalName is "Image" or "Image.Source" or "Image.Stretch")
                element.Name = Launcher + (nameof(LauncherMarkupImage) + element.Name.LocalName[5..]);
            else if (element.Attribute("TargetType") is { } targetType && targetType.Value is "Image" or "{x:Type Image}" or "wpf:Image" or "{x:Type wpf:Image}")
                targetType.Value = "local:" + nameof(LauncherMarkupImage);
            return;
        }
        if (element.Name.Namespace != Launcher) return;

        string[] components = element.Name.LocalName.Split('.', 2);
        string original = components[0];
        string suffix = components.Length == 2 ? "." + components[1] : "";
        if (Adapted.TryGetValue(original, out var replacement)) {
            if (original == "MyCard") {
                if (components.Length == 1) MoveAttribute(element, "IsSwaped", "IsSwapped");
                else if (components[1] == "IsSwaped") suffix = ".IsSwapped";
            }
            element.Name = Launcher + (replacement + suffix);
            return;
        }
        if (!Standard.TryGetValue(original, out var target)) return;
        element.Name = Wpf + (target.Name + suffix);
        if (components.Length != 1) return;
        if (element.Attribute("Style") is null && target.Style is { } style) element.SetAttributeValue("Style", "{DynamicResource " + style + "}");
        if (target.Name is "CheckBox" or "RadioButton" or "MenuItem") {
            MoveAttribute(element, "Text", target.Name == "MenuItem" ? "Header" : "Content");
            MoveAttribute(element, "Checked", "IsChecked");
        }
        if (target.Name == "Slider") {
            MoveAttribute(element, "MinValue", "Minimum");
            MoveAttribute(element, "MaxValue", "Maximum");
        }
    }

    private static void MoveAttribute(XElement element, string source, XName target)
    {
        if (element.Attribute(source) is not { } value) return;
        if (element.Attribute(target) is null) element.SetAttributeValue(target, value.Value);
        value.Remove();
    }

    private static void AttachCommands(DependencyObject element)
    {
        if (element is ButtonBase button && element is not LauncherMarkupButton &&
            (CustomEventService.GetEventType(button) != CustomEvent.EventType.None || CustomEventService.GetEvents(button).Any()))
            button.Click += (_, _) => button.RaiseCustomEvent();
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) AttachCommands(child);
    }
}
