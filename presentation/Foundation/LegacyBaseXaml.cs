using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xaml;
using System.Xml.Linq;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLLF.Presentation.Foundation;

/// <summary>LF legacy-home/help compatibility and the same XAML type/value/member guard before actual WPF loading.</summary>
public static class LegacyBaseXaml {
    public static object Load(XElement text, string assemblyName, Func<string, string> normalizeResourceUri, Type settingServiceType) =>
        Load(text.ToString(), assemblyName, normalizeResourceUri, settingServiceType);
    public static object Load(string text, string assemblyName, Func<string, string> normalizeResourceUri, Type settingServiceType) =>
        LoadCore(text, assemblyName, normalizeResourceUri, settingServiceType, null);
    public static object LoadRestricted(XElement text, string assemblyName, Func<string, string> normalizeResourceUri,
        Type settingServiceType, Func<Type, bool> allowType) => LoadCore(text.ToString(), assemblyName, normalizeResourceUri, settingServiceType, allowType);
    private static object LoadCore(string text, string assemblyName, Func<string, string> normalizeResourceUri, Type settingServiceType,
        Func<Type, bool>? allowType) {
        text = Regex.Replace(text, "(?<=;)assembly=(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL)(?=[\"';])",
            "assembly=" + assemblyName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, "(?<=[\"'])(?:pack://application:,,,/|/)(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL);component/",
            match => normalizeResourceUri(match.Value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
#if !NETFRAMEWORK
        // Actual historical LF home/help declarations retain their CLR namespace;
        // the fixed used WPF services and converter aliases now live in modern PF.
        text = Regex.Replace(text, "(?<=;)assembly=MeloongCore\\.Wpf(?=[\"';])",
            "assembly=PCLLF.Presentation", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
#endif
        if (allowType is null) text = text.Replace("EventType=\"", "local:CustomEventService.EventType=\"").Replace("EventData=\"", "local:CustomEventService.EventData=\"")
            .Replace("EventType='", "local:CustomEventService.EventType='").Replace("EventData='", "local:CustomEventService.EventData='")
            .Replace("Property=\"EventType\"", "Property=\"local:CustomEventService.EventType\"").Replace("Property=\"EventData\"", "Property=\"local:CustomEventService.EventData\"")
            .Replace("Property='EventType'", "Property='local:CustomEventService.EventType'").Replace("Property='EventData'", "Property='local:CustomEventService.EventData'");
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        using (XamlXmlReader reader = allowType is null ? new(stream) : new(stream, System.Windows.Markup.XamlReader.GetWpfSchemaContext())) {
            XamlNodeList? verified = allowType is null ? null : new XamlNodeList(reader.SchemaContext);
            using XamlWriter? writer = verified?.Writer;
            while (reader.Read()) {
                if (allowType is not null && ((reader.NodeType == XamlNodeType.StartObject &&
                    (reader.Type?.UnderlyingType is not { } actual || !allowType(actual))) || reader.Member?.IsEvent == true))
                    throw new InvalidDataException("自定义页面不允许使用此类型或事件：" + (reader.Type?.Name ?? reader.Member?.Name));
                foreach (Type type in new[] { typeof(WebBrowser), typeof(Frame), typeof(MediaElement), typeof(ObjectDataProvider), typeof(System.Xaml.XamlReader), typeof(Window), typeof(XmlDataProvider), settingServiceType }) {
                    if (reader.Type is not null && type.IsAssignableFrom(reader.Type.UnderlyingType)) throw new UnauthorizedAccessException($"基于安全考虑，不允许使用 {type.Name} 类型。");
                    if (reader.Value is not null && Conversions.ToBoolean(Operators.ConditionalCompareObjectEqual(reader.Value, type.Name, false))) throw new UnauthorizedAccessException($"基于安全考虑，不允许使用 {type.Name} 值。");
                }
                foreach (string member in new[] { "Code", "FactoryMethod", "Static" }) {
                    if (reader.Member is not null && reader.Member.Name == member) throw new UnauthorizedAccessException($"基于安全考虑，不允许使用 {member} 成员。");
                }
                writer?.WriteNode(reader);
            }
            if (verified is not null) {
                writer!.Close();
                using var validated = verified.GetReader();
                return System.Windows.Markup.XamlReader.Load(validated);
            }
        }
        stream.Position = 0;
        return System.Windows.Markup.XamlReader.Load(stream);
    }
}
