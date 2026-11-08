using PCLCS;

internal static class DailyLuckTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("daily luck matches PCL 2.99 CIL golden vectors including leap days Unicode zero and full scores", Vectors);
        yield return ("daily luck remains stable through a local day and preserves every description boundary", Stability);
    }
    private static void Vectors() {
        // Independently replayed from the supplied artifact's Jrrp CIL; no product code ran.
        foreach (var (day, identify, expected) in new[] {
            ("2026-11-08", "fixture-machine", 26), ("2026-11-09", "fixture-machine", 30),
            ("2024-02-29", "fixture-machine", 51), ("2025-03-01", "fixture-machine", 71),
            ("2026-11-08", "other-machine", 7), ("2026-11-08", "", 22),
            ("2000-01-01", "A", 8), ("2000-12-31", "A", 25),
            ("2026-11-08", "\U0001f3b2-fixture", 100), ("2026-11-08", "boundary-79", 0),
            ("2026-11-08", "boundary-84", 50), ("2026-11-08", "boundary-55", 100)
        }) Equal(expected, LegacyDailyLuck.Score(DateTime.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), identify));
    }
    private static void Stability() {
        DateTime day = new(2026, 11, 8);
        foreach (int hour in Enumerable.Range(0, 24)) Equal(26, LegacyDailyLuck.Score(day.AddHours(hour).AddMinutes(59), "fixture-machine"));
        foreach (var (score, suffix) in new[] { (0, "？！"), (1, "……（是百分制哦）"), (10, "……（是百分制哦）"), (11, "？！不会吧……"),
            (19, "？！不会吧……"), (20, "！呜……"), (39, "！呜……"), (40, "！勉强还行吧……？"), (49, "！勉强还行吧……？"),
            (50, "！五五开……"), (51, "！还行啦，还行啦。"), (64, "！还行啦，还行啦。"), (65, "！今天运气不错呢！"),
            (89, "！今天运气不错呢！"), (90, "！好评如潮！"), (97, "！好评如潮！"), (98, "！差点就到 100 了呢……"),
            (99, "！差点就到 100 了呢……"), (100, "！100！100！！！！！") }) Equal(suffix, LegacyDailyLuck.Description(score));
    }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
}
