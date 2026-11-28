using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLLF.Presentation.Windowing;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

internal static class LauncherUpdatePageChecks
{
    private const string AutoCheckKey = "ToolLauncherUpdateAutoCheck";

    internal static void Run(FixtureEnvironment environment)
    {
        StartupReleaseNotesChecks.Run();
        Native.Check(ModBase.ReleaseBaselineIsPrerelease == ModBase.ReleaseBaselineVersion.Contains('-', StringComparison.Ordinal), "Release baseline and channel label disagree.");
        string expectedBuildLabel = ModBase.BuildType.ToString() switch { "Debug" => "开发版", "Snapshot" => "快照版", _ => ModBase.VersionBaseName == "dev_edition" ? "开发版" : ModBase.ReleaseBaselineIsPrerelease ? "预发布版" : "正式版" };
        Native.Check(ModBase.BuildTypeDisplay == expectedBuildLabel, "Release/development build label does not match its actual version.");
        object stable = Native.Enum("PCL.ModLauncherUpdate+UpdateChannel", "Stable")!;
        object preview = Native.Enum("PCL.ModLauncherUpdate+UpdateChannel", "StableAndPrerelease")!;
        int futureMajor = Version.Parse(ModBase.ReleaseBaselineVersion.Split('-')[0]).Major + 1;
        string stableTag = $"v{futureMajor}.0.0", newestTag = $"v{futureMajor + 1}.0.0";
        string previewTag = stableTag + "-pre-10", olderPreviewTag = stableTag + "-pre-2";
        const string oldTag = "v0.0.0";
        bool Newer(string tag, object channel) => (bool)Native.Static("PCL.ModLauncherUpdate", "NewerReleaseForChannel", tag, "dev_edition", channel)!;
        Native.Check(!Newer(oldTag, stable) && !Newer("v" + ModBase.ReleaseBaselineVersion, preview) && Newer(stableTag, stable) && Newer(previewTag, preview) && !Newer(previewTag, stable), "dev_edition update baseline/channel compatibility changed.");
        Native.Check((int)Native.Static("PCL.ModLauncherUpdate", "CompareReleaseVersions", "dev_edition", ModBase.ReleaseBaselineVersion)! > 0 &&
            (int)Native.Static("PCL.ModLauncherUpdate", "CompareReleaseVersions", "dev_edition", "dev_edition")! == 0 && !Newer("dev_edition", preview), "Development editions leaked into public release ordering.");
        Native.Check(Native.Main.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion == ModBase.VersionBaseName, "Displayed and executable product versions disagree.");

        bool previousAutoCheck = Settings.Get<bool>(AutoCheckKey);
        Native.Check(bool.TryParse(Settings.GetDefault(AutoCheckKey), out bool enabled) && enabled,
            "Existing installations should check for updates by default.");
        try {
            Native.Cache(AutoCheckKey, false);
            Native.Cache("ToolLauncherUpdateChannel", 0);
            var releases = new[] { Release(newestTag), Release(stableTag), Release(previewTag), Release(oldTag) };
            CheckNativeLatestRelease(releases, newestTag, stableTag, oldTag);
            CheckNativeScrollEdges(Enumerable.Range(0, 48).Select(index => Release($"v0.0.{index}")));
            environment.AssertNoForbiddenIo();
        } finally { Native.Cache(AutoCheckKey, previousAutoCheck); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static ContentControl CreateNativeUpdatePage(IEnumerable<JObject>? entries = null)
    {
        Type type = Native.Type("PCL.GlassLauncherUpdatePage");
        var view = (ContentControl)Activator.CreateInstance(type, Native.Flags, null, [], null)!;
        Native.Call(type, "Open", view);
        if (entries is not null)
        {
            object catalog = Native.Property(type, "Catalog").GetValue(view)!;
            Native.Call(catalog.GetType(), "SetReleases", catalog, entries, null);
        }
        return view;
    }

    private static void CheckNativeLatestRelease(IEnumerable<JObject> entries, string newestTag, string olderUpdate, string historicalTag)
    {
        ContentControl view = CreateNativeUpdatePage(entries);
        using var lifetime = (IDisposable)view;
        object catalog = Native.Property(view.GetType(), "Catalog").GetValue(view)!;
        List<string?> requested = new();
        Native.Property(catalog.GetType(), "RequestUpdate").SetValue(catalog, (Action<JObject>)(release => requested.Add((string?)release["tag_name"])));
        var elements = Descendants(view).ToArray();
        Button install = elements.OfType<Button>().Single(button => Equals(button.Content, "下载并更新"));
        TextBox search = elements.OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "搜索版本");
        foreach (string query in new[] { olderUpdate, historicalTag, "no-matching-release" })
        {
            search.Text = query;
            Native.Check(install.Visibility == Visibility.Visible && Equals(install.ToolTip, $"下载 {newestTag} 并更新启动器") &&
                elements.OfType<TextBlock>().Any(text => text.Text == "发现新版本") &&
                elements.OfType<TextBlock>().Any(text => text.Text == newestTag + " 更新内容"),
                "Filtering history changed the update headline, download action or release notes.");
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, install));
        }
        Native.Check(requested.SequenceEqual(Enumerable.Repeat(newestTag, 3)), "The update action downloaded a filtered history row instead of the newest eligible release.");
        Console.WriteLine("PASS native updates retain the newest eligible headline, notes and download target across filtered and empty history.");
    }

    private static void CheckNativeScrollEdges(IEnumerable<JObject> entries)
    {
        ContentControl view = CreateNativeUpdatePage(entries);
        using var lifetime = (IDisposable)view;
        var fade = view.Content as PCLLF.Wpf.Glass.ScrollEdgeFade;
        Native.Check(fade is not null, "The OTA page did not use the shared scroll-edge fade.");
        if (SystemParameters.HighContrast) return;
        var window = new Window { Content = view, Width = 750, Height = 310, Left = -24000, Top = -24000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try {
            window.Show(); window.UpdateLayout();
            var scroll = fade!.ScrollViewer;
            VisualAssetChecks.Pump(() => scroll.ScrollableHeight > 160 &&
                scroll.Template.FindName("PART_ScrollContentPresenter", scroll) is FrameworkElement { OpacityMask: LinearGradientBrush },
                "The projected update page has no scrollable content or mask.");
            var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            byte Leading() => ((LinearGradientBrush)viewport.OpacityMask).GradientStops[0].Color.A;
            byte Trailing() => ((LinearGradientBrush)viewport.OpacityMask).GradientStops[^1].Color.A;
            Native.Check(Leading() == 255 && Trailing() < 255, "The update page faded the wrong edge at the top.");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            VisualAssetChecks.Pump(() => Leading() < 255 && Trailing() < 255, "The update page did not fade both edges while scrolled.");
            scroll.ScrollToEnd();
            VisualAssetChecks.Pump(() => Leading() < 255 && Trailing() == 255, "The update page faded the wrong edge at the bottom.");
        } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static JObject Release(string tag)
    {
        string file = "PCL-Living-Fork-" + tag.TrimStart('v') + ".exe";
        return new JObject { ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = tag.Contains('-'), ["body"] = "Synthetic release notes", ["assets"] = new JArray(
            new JObject { ["name"] = file, ["size"] = 1, ["browser_download_url"] = "https://github.com/DDZmumo/PCL-Living-Fork/releases/download/" + tag + "/" + file },
            new JObject { ["name"] = "SHA256SUMS.txt", ["size"] = 1, ["browser_download_url"] = "https://github.com/DDZmumo/PCL-Living-Fork/releases/download/" + tag + "/SHA256SUMS.txt" }) };
    }
}
