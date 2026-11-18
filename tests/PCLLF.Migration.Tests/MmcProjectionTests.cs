using System.Text;
using System.Text.Json;
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcProjectionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC version projection is deterministic portable and keeps ordered private targets", Projection);
    }
    private static void Projection() {
        const string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}";
        const string patch = """
            {"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"example.Game",
             "mainJar":{"name":"com.mojang:minecraft:1.7.10:client"},
             "libraries":[{"name":"example:library:1.0"}],
             "assetIndex":{"id":"1.7.10","url":"https://example.invalid/assets.json","sha1":"0000000000000000000000000000000000000000","size":1},
             "minecraftArguments":"--username ${auth_player_name}","compatibleJavaMajors":[8]}
            """;
        Dictionary<string, string> metadata = new() { ["mmc-pack.json"] = manifest, ["patches/net.minecraft.json"] = patch };
        var parsed = MmcPackageReader.Parse(new(metadata.Select(p => new MmcArchiveEntry(p.Key, Encoding.UTF8.GetByteCount(p.Value))).ToArray(), metadata));
        var resolved = MmcComponentResolver.Resolve(parsed, []);
        if (!resolved.IsComplete) throw new Exception(string.Join(";", resolved.Diagnostics));
        var compilation = MmcProfileCompiler.Compile(resolved.Profile!, new("windows", "x86_64", 8));
        if (compilation.HasErrors || compilation.Profile is null) throw new Exception(string.Join(";", compilation.Diagnostics));
        var plan = compilation.Profile;
        string prefix = "PCL/MmcRuntime/" + new string('a', 64) + "/";
        Dictionary<string, string> targets = plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts)
            .Concat(plan.AuxiliaryArtifacts).GroupBy(a => a.Key).Select(g => g.First()).ToDictionary(a => a.Key,
                a => prefix + (a.Role == MmcArtifactRole.AssetIndex ? "assets/" : "libraries/") + a.RelativePath);
        Guid id = Guid.NewGuid();
        byte[] first = MmcVersionProjection.Render("test-instance", id, 1, plan, targets, prefix + "natives", prefix + "assets");
        byte[] second = MmcVersionProjection.Render("test-instance", id, 1, plan, targets, prefix + "natives", prefix + "assets");
        if (!first.SequenceEqual(second)) throw new Exception("Projection contains unstable clock or filesystem data.");
        using JsonDocument json = JsonDocument.Parse(first);
        var root = json.RootElement;
        if (root.GetProperty("mainClass").GetString() != plan.MainClass ||
            root.GetProperty("pcllfManagement").GetProperty("instanceId").GetString() != id.ToString("D") ||
            root.GetProperty("pcllfRuntime").GetProperty("classpath").EnumerateArray().Select(x => x.GetString()).SequenceEqual(plan.Classpath.Select(a => targets[a.Key])) == false)
            throw new Exception("Projection lost the compiled entry, identity or classpath order.");
        if (root.GetProperty("libraries").GetArrayLength() != plan.Classpath.Length - 1 ||
            root.GetProperty("pcllfRuntime").GetProperty("mainJar").GetString() != targets[plan.MainJar.Key])
            throw new Exception("Main JAR was conflated with an ordinary runtime library.");
        targets.Remove(plan.MainJar.Key);
        try { MmcVersionProjection.Render("test-instance", id, 1, plan, targets, null, prefix + "assets"); }
        catch (InvalidDataException) { return; }
        throw new Exception("A missing bound target was silently projected as installed.");
    }
}
