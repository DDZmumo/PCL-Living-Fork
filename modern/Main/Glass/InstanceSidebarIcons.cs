// SPDX-License-Identifier: MPL-2.0
using System;
using System.Linq;

namespace PCL;

public static class InstanceSidebarIcons
{
    // iconmonstr, Alexander Kahlkopf; https://iconmonstr.com/license/ .
    // The native About page retains the original iconmonstr credit/license.
    // Exact original paths: https://iconmonstr.com/copy-2-svg/ and
    // https://iconmonstr.com/time-17-svg/ . Only app UI glyphs are included.
    // Pinned mirrors used to read those two originals (no icon-pack download):
    // github.com/adeojoemmanuel/gcwalletV2/blob/665790f43228983435b6715b41cde36a041dbea2/src/assets/img/iconmonstr-copy-2.svg
    // github.com/maximefondu/Pickmo/blob/c9a5ca0460158d0e2030c6d57223cc15de59ce0a/src/assets/images/iconmonstr-time-17.svg
    public const string Copy = "M22 6v16h-16v-16h16zm2-2h-20v20h20v-20zm-24 17v-21h21v2h-19v19h-2z";
    public const string History = "M24 12c0 6.6-5.4 12-12 12S0 18.6 0 12h2c0 5.5 4.5 10 10 10s10-4.5 10-10S17.5 2 12 2C9.2 2 6.7 3.1 4.9 5L7 7 0 8.4l1.3-7 2.2 2.2C5.7 1.4 8.7 0 12 0c6.6 0 12 5.4 12 12zM11 6v8h7v-2h-5V6h-2z";

    // Two supplemental Tabler icons, not claimed to be original PCL art.
    // Paweł Kuna, MIT; full notice is embedded as Resources/Tabler-Icons-LICENSE.txt.
    // https://github.com/tabler/tabler-icons/tree/ed177a085b652fe04ec4f37ced867c94621e7840/icons/outline
    // SVG paths and their 24px, stroke=2, round cap/join are unchanged.
    public static readonly string Keyboard = Outline(
        "M2 8a2 2 0 0 1 2 -2h16a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2l0 -8",
        "M6 10l0 .01", "M10 10l0 .01", "M14 10l0 .01", "M18 10l0 .01", "M6 14l0 .01", "M18 14l0 .01", "M10 14l4 .01");
    public static readonly string Export = Outline(
        "M14 3v4a1 1 0 0 0 1 1h4",
        "M11.5 21h-4.5a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2h7l5 5v5m-5 6h7m-3 -3l3 3l-3 3");

    private static string Outline(params string[] paths)
    {
        var pen = new Pen(Brushes.Black, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (string path in paths) geometry.AddGeometry(Geometry.Parse(path).GetWidenedPathGeometry(pen));
        // MyListItem accepts filled Path geometry; standard WPF widening
        // preserves the stock SVG strokes without changing shared controls.
        return geometry.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
