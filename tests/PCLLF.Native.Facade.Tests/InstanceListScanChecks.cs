// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class InstanceListScanChecks {
    internal static void Run(FixtureEnvironment environment) {
        string root = Path.Combine(environment.Data, "instance-list/minecraft");
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        string[] names = Enumerable.Range(1, 13).Select(index => "fixture-" + index).ToArray();
        foreach (string name in names) {
            string directory = Path.Combine(root, "versions", name); Directory.CreateDirectory(Path.Combine(directory, "PCL"));
            File.WriteAllText(Path.Combine(directory, name + ".json"), "{\"id\":\"" + name + "\",\"mainClass\":\"fixture.Main\",\"type\":\"release\",\"libraries\":[],\"arguments\":{\"game\":[],\"jvm\":[]}}");
            File.WriteAllText(Path.Combine(directory, "PCL/Setup.ini"), "State:1\nInfo:fixture\nLogo:pack://application:,,,/images/Blocks/Grass.png\nVersionVanillaName:1.20.1\nReleaseTime:2026-01-01 00:00\nVersionArgumentIndieV2:True\n");
        }
        File.WriteAllText(Path.Combine(root, "PCL.ini"), "CardCount:1\nCardKey1:3\nCardValue1:" + string.Join(':', names) + ":\n");
        var times = new List<double>();
        for (int pass = 0; pass < 3; pass++) {
            var elapsed = Stopwatch.StartNew();
            var cards = (IDictionary)Native.Static("PCL.ModMinecraft", "InitMcInstanceListWithCache", root + Path.DirectorySeparatorChar)!;
            int count = cards.Values.Cast<IEnumerable>().Sum(items => items.Cast<object>().Count());
            Native.Check(count == 13, "Actual cached version-list scan lost ordinary versions.");
            Native.Check(cards.Values.Cast<IEnumerable>().SelectMany(items => items.Cast<ModMinecraft.McInstance>()).All(instance => !instance.UsesMmcBackend && instance.Version.VanillaName == "1.20.1"),
                "Ordinary cache scan claimed MMC ownership or changed the cached game version.");
            times.Add(elapsed.Elapsed.TotalMilliseconds);
        }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS instance-list-scan: 13 ordinary versions through actual Main cache/model/config adapters; elapsedMs=" + JsonSerializer.Serialize(times));
    }
}
