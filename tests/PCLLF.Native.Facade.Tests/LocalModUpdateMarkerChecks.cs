// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class LocalModUpdateMarkerChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "mod-update-marker");
        var previous = LauncherInstanceContext.Instance;
        try {
            var owner = new ModMinecraft.McInstance(scope.Root);
            LauncherInstanceContext.Instance = owner;
            Native.Cache("UiHiddenFunctionModUpdate", false);
            Settings.Entries["VersionAdvanceDisableModUpdate"].ValueCache[owner.PathVersion] = false;
            var entry = new LocalResourceFile(new FileInfo(Path.Combine(scope.Root, "fixture.jar"))) {
                ProjectVersion = new ResourceVersion { Version = "1.2.3", ReleaseDate = new DateTime(2024, 1, 1) },
                UpdateFile = new ResourceVersion { Version = "1.2.4", ReleaseDate = new DateTime(2024, 2, 1) }
            };
            var row = new GlassLocalModRow(new PageInstanceMod(), entry, false);
            row.Refresh();
            var arrow = (Button)Native.Get(row, "_update")!;
            var name = (TextBlock)Native.Get(row, "_title")!;
            var tags = (Panel)Native.Get(row, "_chips")!;
            var glyph = (FrameworkElement)arrow.Content;
            Native.Check(arrow.Visibility == Visibility.Visible && tags.Children.Count >= 2,
                "An updatable mod did not display its marker, version and update label.");

            foreach (string title in new[] { "钠 · 扩展", "Sodium Extra" }) {
                name.Text = title;
                row.Measure(new Size(600, 54));
                row.Arrange(new Rect(0, 0, 600, 54));
                row.UpdateLayout();
                double nameCenter = name.TranslatePoint(new Point(0, name.DesiredSize.Height / 2), row).Y;
                Native.Check(glyph.ActualHeight > 0 && Math.Abs(CenterY(glyph, row) - nameCenter) <= 1 &&
                    Math.Abs(CenterY((FrameworkElement)tags.Children[0], row) - nameCenter) <= 1,
                    "The upgrade arrow, mod name and version chip did not share a vertical center.");
            }
        } finally { LauncherInstanceContext.Instance = previous; }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS local mod upgrade marker: title, arrow and version chip alignment in Chinese and English.");
    }

    private static double CenterY(FrameworkElement item, FrameworkElement row) =>
        item.TranslatePoint(new Point(0, item.ActualHeight / 2), row).Y;
}
