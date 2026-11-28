using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCLLF.Presentation.Controls;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

// Original fixed Windows/path/JSON/UTF16 golds from Test-InstanceResources, not a second implementation.
internal static class BackupResourceContractChecks
{
    private static void Error(string method, Type type, string? message, params object?[] arguments)
    {
        Exception observed = Reject(() => Store(method, arguments), method);
        Native.Check(observed.GetType() == type && (message is null || observed.Message == message), "Original resource exception contract changed for " + method + ": " + observed);
    }
    internal static void Run(string root)
    {
        var clipboard = Native.Field(Native.Type("PCL.Controls.Behaviors.ClipboardInterceptor"), "EnableSafeClipboardProperty").GetValue(null);
        Native.Check(ReferenceEquals(clipboard, ClipboardInterceptorView.EnableSafeClipboardProperty), "Original BAML clipboard owner created a second DP.");
        DependencyProperty property = (DependencyProperty)clipboard!;
        foreach (var sample in new (FrameworkElement Control, int Added)[] { (new TextBox(), 3), (new RichTextBox(), 3), (new DataGrid(), 1) }) {
            int before = sample.Control.CommandBindings.Count;
            Native.Check(!(bool)sample.Control.GetValue(property), "Clipboard attached-property default changed."); sample.Control.SetValue(property, true);
            Native.Check(sample.Control.CommandBindings.Count == before + sample.Added, "Clipboard callback lost original command bindings."); sample.Control.SetValue(property, true);
            Native.Check(sample.Control.CommandBindings.Count == before + sample.Added, "Same clipboard value repeated the change callback."); sample.Control.SetValue(property, false); sample.Control.SetValue(property, true);
            Native.Check(sample.Control.CommandBindings.Count == before + sample.Added * 2, "Clipboard false/true lost original accumulation."); sample.Control.CommandBindings.Clear();
        }
        PackDetails(root, "测试补充信息");
        Console.WriteLine("PASS backup-resources contracts: actual attached DP/page state, enum/field maps, independent Windows names/extensions/messages, cultures and JSON/UTF16 edge cases.");
    }

    private static void PackDetails(string root, string supplementary)
    {
        string folder = Path.Combine(root, "metadata-detail"), file = Path.Combine(folder, "pack.mcmeta"); object entry = New("PCL.InstanceResourceEntry"); Native.Set(entry, "FullPath", folder); Native.Set(entry, "IsDirectory", true);
        string Read() { object?[] values = [entry, "seed"]; Store("ReadPackInfo", values); return (string)values[1]!; }
        foreach (var sample in new[] {
            ("{\"pack\":{\"description\":\"first\\r\\nsecond\\u2028third\",\"pack_format\":46}}", "first second third · 资源格式 46"),
            ("{\"pack\":{\"description\":{\"text\":\"\\nX\\u0085Y\"},\"pack_format\":null}}", " X Y · 资源格式 "),
            ("{\"pack\":{\"description\":{\"text\":1234}}}", "1234"),
            ("{}", "seed"), ("{\"pack\":[]}", "seed"), ("{\"pack\":{\"description\":[]}}", "seed"), ("not JSON", "seed") }) {
            Put(file, sample.Item1); Native.Check(Read() == sample.Item2, "Resource pack ref-detail/newline/swallowed-error contract changed.");
        }
        string longDescription = new string('a', 179) + supplementary; Put(file, new JObject { ["pack"] = new JObject { ["description"] = longDescription } }.ToString()); string actual = Read();
        Native.Check(actual == longDescription[..180] && char.IsHighSurrogate(actual[179]), "Resource detail lost its original 180 UTF16-unit boundary.");
        foreach (string json in new[] { "{\"pack\":null}", "{\"pack\":{\"description\":null}}" }) { Put(file, json); Error("ReadPackInfo", typeof(InvalidOperationException), null, entry, "seed"); }
    }
}
