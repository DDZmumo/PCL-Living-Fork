using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using PCLCS;

internal static class SymbioLauncherSwitchTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("toolbox surprise reuses same-directory local programs without any network", LocalProgram);
        yield return ("toolbox surprise verifies the release before starting and closes only after startup", DownloadAndReuse);
        yield return ("toolbox surprise rejects unsafe incomplete or mismatched releases", InvalidReleases);
        yield return ("toolbox surprise preserves LF and existing files on download hash start or race failures", FailureBoundaries);
    }

    private static byte[] ProgramBytes() { byte[] data = new byte[2048]; data[0] = (byte)'M'; data[1] = (byte)'Z'; return data; }
    private static JObject Release(byte[] data) => new() {
        ["draft"] = false, ["prerelease"] = false, ["tag_name"] = "v2.17.14",
        ["assets"] = new JArray(new JObject {
            ["name"] = "PCL-Symbio-Edition-2.17.14-x64.exe", ["state"] = "uploaded", ["size"] = data.Length,
            ["browser_download_url"] = SymbioLauncherSwitch.Repository + "/releases/download/v2.17.14/PCL-Symbio-Edition-2.17.14-x64.exe",
            ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
        })
    };

    private static void LocalProgram() {
        foreach (string name in new[] { "pclse.exe", "PCL-Symbio-Edition-2.17.14-x64.exe" }) {
            using Fixture fixture = new();
            string local = Path.Combine(fixture.Root, name); File.WriteAllBytes(local, ProgramBytes());
            List<string> events = new();
            var host = new SymbioLauncherSwitchHost {
                Release = () => throw new Exception("Local launch must not read GitHub."), Download = (_, _) => throw new Exception("Local launch must not download."),
                Hint = text => { if (text.Contains("PCL", StringComparison.OrdinalIgnoreCase) || text.Contains("Symbio", StringComparison.OrdinalIgnoreCase)) throw new Exception("Surprise hint revealed its name."); },
                Start = file => { Equal(local, file); events.Add("start"); }, Close = () => events.Add("close")
            };
            new SymbioLauncherSwitch(host).Run(fixture.Root, fixture.Launcher);
            Equal("start,close", string.Join(',', events));
            Equal<string?>(null, SymbioLauncherSwitch.FindLocal(fixture.Root, local));
        }
    }

    private static void DownloadAndReuse() {
        using Fixture fixture = new();
        byte[] data = ProgramBytes(); List<string> events = new();
        var host = new SymbioLauncherSwitchHost {
            Release = () => { events.Add("release"); return Release(data); },
            Download = (asset, target) => { Equal((long)data.Length, asset.Size); events.Add("download"); File.WriteAllBytes(target, data); },
            Hint = _ => { }, Start = path => { Equal("PCLSE.exe", Path.GetFileName(path)); Equal(true, File.ReadAllBytes(path).SequenceEqual(data)); events.Add("start"); },
            Close = () => events.Add("close")
        };
        new SymbioLauncherSwitch(host).Run(fixture.Root, fixture.Launcher);
        Equal("release,download,start,close", string.Join(',', events));
        Equal(false, Directory.EnumerateFiles(fixture.Root, ".pcl-*").Any());
        Equal("existing LF executable", File.ReadAllText(fixture.Launcher));
        events.Clear(); new SymbioLauncherSwitch(host).Run(fixture.Root, fixture.Launcher);
        Equal("start,close", string.Join(',', events));
    }

    private static void InvalidReleases() {
        Action<JObject>[] changes = [
            release => release["draft"] = true, release => release["prerelease"] = true, release => release["tag_name"] = "../outside",
            release => release["assets"]![0]!["digest"] = "", release => release["assets"]![0]!["size"] = 1023,
            release => release["assets"]![0]!["size"] = 257L * 1024 * 1024,
            release => release["assets"]![0]!["browser_download_url"] = "https://evil.example/payload.exe",
            release => release["assets"]![0]!["name"] = "../PCLSE.exe", release => release["assets"]![0]!["state"] = "new",
            release => ((JArray)release["assets"]!).Add(release["assets"]![0]!.DeepClone())
        ];
        foreach (Action<JObject> change in changes) { JObject release = Release(ProgramBytes()); change(release); Throws(() => SymbioLauncherSwitch.SelectAsset(release)); }
    }

    private static void FailureBoundaries() {
        foreach (string failure in new[] { "download", "hash", "size", "header", "start", "race" }) {
            using Fixture fixture = new(); byte[] bytes = ProgramBytes(); int starts = 0, closes = 0;
            byte[] payload = (byte[])bytes.Clone(); if (failure == "hash") payload[10] = 7;
            if (failure == "header") { payload[0] = 0; bytes = payload; }
            var host = new SymbioLauncherSwitchHost {
                Release = () => Release(bytes), Hint = _ => { },
                Download = (_, file) => {
                    File.WriteAllBytes(file, failure == "size" ? payload[..1024] : payload);
                    if (failure == "download") throw new IOException("fixture download failure");
                    if (failure == "race") File.WriteAllText(Path.Combine(fixture.Root, "PCLSE.exe"), "concurrent writer");
                },
                Start = _ => { starts++; if (failure == "start") throw new IOException("fixture startup failure"); }, Close = () => closes++
            };
            Throws(() => new SymbioLauncherSwitch(host).Run(fixture.Root, fixture.Launcher));
            Equal(failure == "start" ? 1 : 0, starts); Equal(0, closes);
            Equal(false, Directory.EnumerateFiles(fixture.Root, ".pcl-*").Any());
            Equal("existing LF executable", File.ReadAllText(fixture.Launcher));
            if (failure == "race") Equal("concurrent writer", File.ReadAllText(Path.Combine(fixture.Root, "PCLSE.exe")));
        }
    }

    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static void Throws(Action run) { try { run(); } catch (IOException) { return; } throw new Exception("Expected guarded failure."); }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-Surprise-" + Guid.NewGuid().ToString("N"));
        internal string Launcher => Path.Combine(Root, "PCLLF.exe");
        internal Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(Launcher, "existing LF executable"); }
        public void Dispose() {
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(Root), @"\APCLLF-Surprise-[a-f0-9]{32}\z")) throw new Exception("Unsafe fixture cleanup.");
            Directory.Delete(Root, true);
        }
    }
}
