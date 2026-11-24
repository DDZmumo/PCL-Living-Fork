namespace PCLCS;

/// <summary>
/// 单个 Java 的实例。
/// </summary>
[Serializable] public class Java(string folder, Version? version = null) : IEquatable<Java> {

    /// <summary>
    /// java.exe 文件所在的文件夹路径，这通常是 bin 文件夹。以 \ 结尾。
    /// </summary>
    public readonly string Folder = PathUtils.AddSlashSuffix(PathUtils.ForCompare(folder));
    /// <summary>
    /// java.exe 文件的完整路径。
    /// </summary>
    [JsonIgnore] public string JavaExePath => Path.Combine(Folder, "java.exe");

    /// <summary>
    /// Java 的版本号（例如 16.0.1），保证 <see cref="Version.Major"/> 为其主版本。
    /// <para/> 若为 null，则还需要调用 <see cref="CheckAsync"/> 以获取版本号。
    /// </summary>
    [JsonConverter(typeof(JavaCacheVersionConverter))]
    public Version? Version = version;
    /// <summary>Java 的默认字符编码。</summary>
    [JsonIgnore] public string? FileEncodingName;
    /// <summary>
    /// Java 根据系统环境确定的本地字符编码，仅 Java 17+ 可用。
    /// <para/>若系统开启了 UTF-8，部分 Java 会错误地返回 JDK，因此不能使用 <see cref="Encoding.Default"/>。
    /// </summary>
    [JsonIgnore] public string? NativeEncodingName;

    [JsonIgnore] public string? Distribution => probeCache?.Distribution;
    [JsonIgnore] public string? Architecture => probeCache?.Architecture;
    [JsonIgnore] public string? VirtualMachine => probeCache?.VirtualMachine;
    [JsonIgnore] public string? RuntimeVersion => probeCache?.RuntimeVersion;
    [JsonIgnore] public string? PackageType => probeCache?.PackageType;
    [JsonIgnore] public bool Is32Bit => Architecture == "x86";
    public const int X86MaxHeapMiB = 1024;

    internal static bool HeapOptionExceeds(string argument, int limitMiB) {
        string option = argument.Trim(' ', '"');
        if (!option.StartsWith("-Xmx", StringComparison.Ordinal) && !option.StartsWith("-Xms", StringComparison.Ordinal)) return false;
        ReadOnlySpan<char> size = option.AsSpan(4);
        if (size.IsEmpty) return false;
        int shift = char.ToLowerInvariant(size[^1]) switch { 'k' => 10, 'm' => 20, 'g' => 30, 't' => 40, _ => 0 };
        if (shift != 0) size = size[..^1];
        return ulong.TryParse(size, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong value) &&
            value > ((ulong)limitMiB << 20) >> shift;
    }

    private sealed record JavaProbeCache(string? Stamp, string Version, string? Distribution, string? Architecture, string? VirtualMachine,
        string? FileEncodingName, string? NativeEncodingName, string? RuntimeVersion, string? PackageType);
    [JsonProperty("ProbeCache", NullValueHandling = NullValueHandling.Ignore)] private JavaProbeCache? probeCache;
    [JsonIgnore] private readonly SemaphoreSlim probeGate = new(1, 1);

    /// <summary>
    /// 检查该 Java 是否存在问题，并获取其版本号。
    /// 返回其是否通过了检查。
    /// </summary>
    public Task<bool> CheckAsync(CancellationToken cancellationToken = default) => CheckCoreAsync(cancellationToken, null);

    /// <summary>Explicit host runner for isolated MMC probes. Use a fresh Java object; ordinary checks retain their existing cache/environment contract.</summary>
    public Task<bool> CheckAsync(CancellationToken cancellationToken, Func<string, CancellationToken, Task<string>> runProbe) {
        ArgumentNullException.ThrowIfNull(runProbe);
        return CheckCoreAsync(cancellationToken, runProbe);
    }

    internal Task<bool> CheckForDiscoveryAsync(CancellationToken cancellationToken, Func<string, CancellationToken, Task<string>>? runProbe = null) =>
        CheckCoreAsync(cancellationToken, runProbe, forDiscovery: true);

    private async Task<bool> CheckCoreAsync(CancellationToken cancellationToken, Func<string, CancellationToken, Task<string>>? runProbe,
        bool forDiscovery = false) {
        await probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? output = null;
        try {
            if (!FileUtils.Exists(JavaExePath)) throw new FileNotFoundException("未找到 java.exe 文件", JavaExePath);
            string? stamp = forDiscovery || runProbe is null ? WindowsJavaDiscovery.ProbeStamp(JavaExePath) : null;
            if (stamp is not null && probeCache is { } cached && cached.Stamp == stamp &&
                Version?.Major is > 4 and < 100 && cached.Version == Version.ToString() &&
                (forDiscovery || available == true)) {
                FileEncodingName = cached.FileEncodingName;
                NativeEncodingName = cached.NativeEncodingName;
                return true;
            }

            output = await (runProbe ?? WindowsJavaDiscovery.RunVersionProbeAsync)(JavaExePath, cancellationToken).ConfigureAwait(false);
            if (output == "") throw new InvalidOperationException("尝试运行该 Java 失败");
            Logger.Trace($"Java 检查输出：{JavaExePath}{Environment.NewLine}{output}");
            string lowered = output.Lower();
            if (lowered.Contains("/lib/ext exists")) throw new InvalidOperationException("无法运行该 Java，请在删除 Java 文件夹中的 /lib/ext 文件夹后再试");
            if (lowered.Contains("a fatal error") || lowered.Contains("error: ")) throw new InvalidOperationException("无法运行该 Java，该 Java 或系统存在问题");
           
            // 获取版本号
            var versionString = (lowered.RegexSeek(@"(?<=version "")[^""]+") ?? lowered.RegexSeek("(?<=openjdk )[0-9]+"))?.Replace("_", ".").Replace("+", ".").BeforeFirst("-");
            if (string.IsNullOrEmpty(versionString)) throw new InvalidOperationException($"未找到该 Java 的版本号{(output.Length < 500 ? $"\n原始输出为：\n{output}" : "")}");
            if (versionString.StartsWithF("1.")) versionString = versionString!.AfterFirst("."); // 去除开头的 1.
            var segments = versionString!.Split(".", true).ToList();
            while (segments.Count < 4) segments.Add("0");
            var detectedVersion = new Version(segments.Take(4).Join("."));
            if (detectedVersion.Major is <= 4 or >= 100) throw new InvalidOperationException($"分析详细信息失败，获取的版本为 {detectedVersion}{(output.Length < 500 ? $"\n原始输出为：\n{output}" : "")}");

            // 获取 Java 的相关属性
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split('\n')) {
                int separator = line.IndexOf(" = ", StringComparison.Ordinal);
                if (separator < 0) continue;
                properties.TryAdd(line[..separator].Trim(), line[(separator + 3)..].Trim());
            }
            string? GetProperty(string name) => properties.GetValueOrDefault(name);
            string? architecture = WindowsJavaDiscovery.Architecture(GetProperty("os.arch"));
            string? bitness = GetProperty("sun.arch.data.model");
            if (bitness == "32" && architecture is null) architecture = "x86";
            if (architecture == "x86" && bitness == "64" || bitness == "32" && architecture != "x86")
                throw new InvalidOperationException("Java 报告的位数与架构不一致");
            if (architecture is not ("x86" or "x64" or "ARM64") && bitness != "64" && !lowered.Contains("64-bit"))
                throw new InvalidOperationException("无法确认该 Java 的位数");
            if (stamp is not null && stamp != WindowsJavaDiscovery.ProbeStamp(JavaExePath))
                throw new IOException("Java 文件在检查过程中发生变化，请重新搜索");

            Version = detectedVersion;
            FileEncodingName = GetProperty("file.encoding")?.Lower();
            NativeEncodingName = GetProperty("native.encoding")?.Lower() ?? FileEncodingName;
            probeCache = new(stamp, detectedVersion.ToString(),
                WindowsJavaDiscovery.Distribution(GetProperty("java.vendor"), GetProperty("java.vendor.version"), GetProperty("java.runtime.name")),
                architecture, WindowsJavaDiscovery.VirtualMachine(GetProperty("java.vm.name"), GetProperty("java.vm.version")),
                FileEncodingName, NativeEncodingName, GetProperty("java.runtime.version") ?? GetProperty("java.version"),
                File.Exists(Path.Combine(Folder, "javac.exe")) ? "jdk" : "jre");

            Logger.Info($"检查 Java 成功：{this}");
            available = forDiscovery ? null : true;
            return true;
        } catch (Exception ex) {
            if (ex.IsCanceled()) throw;
            Logger.Warn($"检查 Java 失败（{JavaExePath}），其输出为：\n{output ?? "无程序输出"}");
            probeCache = null;
            available = false;
            return false;
        } finally {
            probeGate.Release();
        }
    }

    public void InvalidateChecked() => available = null;

    internal Java RelocateDiscoveryResult(string folder) {
        var relocated = new Java(folder, Version) { FileEncodingName = FileEncodingName, NativeEncodingName = NativeEncodingName };
        if (probeCache is { } cached)
            relocated.probeCache = cached with { Stamp = WindowsJavaDiscovery.ProbeStamp(relocated.JavaExePath) };
        return relocated;
    }

    /// <summary>若已进行过检查，表示该检查是否成功。若未检查，则为 null。</summary>
    [JsonIgnore] internal bool? available = null;

    // 相同检查
    public bool Equals(Java? other)
        => other is not null && PathUtils.IsSame(Folder, other.Folder);
    public override bool Equals(object? obj) => obj is Java other && Equals(other);
    public override int GetHashCode() => StringComparer.CurrentCultureIgnoreCase.GetHashCode(PathUtils.ForCompare(Folder));

    /// <summary>用户友好的描述文本。</summary>
    public override string ToString()
        => $"Java {Version?.Major.ToString() ?? "尚未检查"} ({Version})：{Folder}";
}

// Scoped to the persisted Java.Version member. Framework LF wrote Version as
// an object; modern System.Version has a string conversion contract. Reads must
// accept the historical cache without changing global JSON or current writes.
internal sealed class JavaCacheVersionConverter : JsonConverter {
    public override bool CanWrite => false;
    public override bool CanConvert(Type objectType) => objectType == typeof(Version);
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) {
        if (reader.TokenType == JsonToken.Null) return null;
        try {
            if (reader.TokenType == JsonToken.String) {
                string? text = (string?)reader.Value;
                return string.IsNullOrEmpty(text) ? null : new Version(text);
            }
            if (reader.TokenType == JsonToken.StartObject) {
                JObject value = JObject.Load(reader);
                int ReadComponent(string name) {
                    JToken? component = value.GetValue(name, StringComparison.Ordinal);
                    if (component?.Type != JTokenType.Integer)
                        throw new JsonSerializationException("Cached Java Version requires an integer " + name + ".");
                    return checked((int)component.Value<long>());
                }
                int major = ReadComponent("Major"), minor = ReadComponent("Minor");
                int build = ReadComponent("Build"), revision = ReadComponent("Revision");
                if (major < 0 || minor < 0 || build < -1 || revision < -1 || (build == -1 && revision != -1))
                    throw new JsonSerializationException("Cached Java Version components are invalid.");
                if (build == -1) return new Version(major, minor);
                return revision == -1 ? new Version(major, minor, build) : new Version(major, minor, build, revision);
            }
        } catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or InvalidCastException) {
            throw new JsonSerializationException("Cached Java Version is invalid.", error);
        }
        throw new JsonSerializationException("Cached Java Version must be a string, a historical component object, or null.");
    }
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => throw new NotSupportedException("Java cache version writes use the existing serializer contract.");
}

public static class JavaUtils {

    /// <summary>
    /// 检查所有指定的 Java 是否存在问题，并获取其版本号。
    /// </summary>
    /// <returns>所有通过检查的 Java。</returns>
    public static async Task<List<Java>> CheckAllAsync(IEnumerable<Java> javas, CancellationToken cancellationToken = default, ProgressProvider? progress = null) {
        await TaskUtils.WhenAll(javas.Where(j => j.available == null).Select(
            async java => await java.CheckAsync(cancellationToken).NoCapture()
        ), progress).NoCapture();
        return javas.Where(j => j.available == true).ToList();
    }

    /// <summary>
    /// 将 Java 列表进行排序。
    /// 这同时会检查所有未检查的 Java。
    /// </summary>
    public static async Task<List<Java>> SortAsync(List<Java> javas, CancellationToken cancellationToken = default, ProgressProvider? progress = null) {
        javas = await JavaUtils.CheckAllAsync(javas, cancellationToken, progress).NoCapture();
        SortInstalled(javas);
        return javas;
    }

    private static void SortInstalled(List<Java> javas) {
        if (javas.Count < 2) return;
        var preferredFolders = CandidateFolders();
        javas.Sort((left, right) => {
            // 优先使用带完整 Java 的文件夹列表中的 Java
            var isLeftInCandidateFolder = preferredFolders.Any(folder => PathUtils.IsParentOf(folder, left.Folder));
            var isRightInCandidateFolder = preferredFolders.Any(folder => PathUtils.IsParentOf(folder, right.Folder));
            if (isLeftInCandidateFolder != isRightInCandidateFolder) return isLeftInCandidateFolder ? -1 : 1;
            // 其次优先使用主版本号接近 21 的 Java
            var leftDistance = left.Version is null ? int.MaxValue : Math.Abs(left.Version.Major - 21);
            var rightDistance = right.Version is null ? int.MaxValue : Math.Abs(right.Version.Major - 21);
            return leftDistance.CompareTo(rightDistance);
        });
    }

    #region 搜索

    /// <summary>
    /// 存在完整 Java 的文件夹列表。
    /// </summary>
    private static List<string> CandidateFolders() {
        List<string> folders = [
            Paths.AppData + @".minecraft\runtime\", // 这也是 PCL 下载 Java 的路径
            Paths.AppData + @".hmcl\java\",
            Paths.AppData + @"ATLauncher\runtimes\minecraft\",
            Paths.AppData + @"ModrinthApp\meta\java_versions\",
            Paths.AppData + @"PrismLauncher\java\",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\curseforge\minecraft\Install\runtime\",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\.jdks\",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\.sdkman\candidates\java\",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\.ftba\bin\runtime\",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Minecraft Launcher\runtime\",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Minecraft\runtime\",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\Curse\Minecraft\Install\runtime\"
        ];
        string[] programFiles = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") ?? ""];
        foreach (string root in programFiles.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase)) {
            folders.AddRange(WindowsJavaDiscovery.VendorDirectories.Select(vendor => Path.Combine(root, vendor)));
            folders.AddRange(DirectoryUtils.EnumerateDirectories(Path.Combine(root, "Microsoft"), searchPattern: "jdk-*"));
        }
        return folders.Select(f => {
            try {
                return PathUtils.ForCompare(f);
            } catch (Exception ex) {
                Logger.Warn(ex, $"Java 候选文件夹路径无效（{f}）");
                return null;
            }
        }).Where(f => f is not null).Cast<string>().ToList();
    }
    

    /// <summary>
    /// 从常见文件夹和环境变量中搜索 Java，并将搜索结果写入设置。
    /// </summary>
    public static async Task RefreshListAsync(CancellationToken c = default, ProgressProvider? p = null) {
        var folders = await FindJavaFoldersAsync(true, CandidateFolders(), c, p?.SplitTo(0.35), globalScan: true).NoCapture();
        IEnumerable<string?> homes = WindowsJavaDiscovery.RegisteredHomes(c).Concat(
            [Environment.GetEnvironmentVariable("JAVA_HOME"), Environment.GetEnvironmentVariable("JDK_HOME")]);
        var direct = WindowsJavaDiscovery.DirectFolders(Environment.GetEnvironmentVariable("PATH"), homes);
        var managed = ManagedJavaRuntimeStore.InstalledFolders(ManagedJavaRuntimeStore.DefaultRoot, c);
        await RefreshCandidatesAsync(folders.Concat(direct).Concat(managed), c, p?.SplitTo(1)).NoCapture();
    }

    internal static async Task RefreshCandidatesAsync(IEnumerable<string> folders, CancellationToken token,
        ProgressProvider? progress = null, Func<string, CancellationToken, Task<string>>? runProbe = null) {
        Java[] existing;
        lock (Configs.JavaList) existing = Configs.JavaList.Get()!.Distinct().ToArray();
        var existingObjects = new HashSet<Java>(existing, ReferenceEqualityComparer.Instance);
        var existingPaths = existing.ToHashSet();
        var discovered = folders.Select(folder => new Java(folder)).Where(java => !existingPaths.Contains(java)).Distinct().ToArray();
        var byPath = existing.Concat(discovered).GroupBy(Identity, StringComparer.OrdinalIgnoreCase).ToArray();
        var verified = new ConcurrentBag<Java>();
        await TaskUtils.ForEachAsync(byPath, 4, async (aliases, _) => {
            bool retained = false;
            foreach (Java java in aliases.Where(existingObjects.Contains)) {
                if (!await java.CheckForDiscoveryAsync(token, runProbe).NoCapture()) continue;
                verified.Add(java);
                retained = true;
            }
            if (retained) return;
            foreach (Java java in aliases.Where(java => !existingObjects.Contains(java))
                .OrderBy(java => !PathUtils.IsSame(java.JavaExePath, aliases.Key))) {
                if (!await java.CheckForDiscoveryAsync(token, runProbe).NoCapture()) continue;
                verified.Add(java);
                break;
            }
        }, token, progress).NoCapture();
        token.ThrowIfCancellationRequested();
        var valid = new HashSet<Java>(verified, ReferenceEqualityComparer.Instance);
        var additions = verified.Where(java => !existingObjects.Contains(java)).ToList();
        SortInstalled(additions);

        lock (Configs.JavaList) {
            token.ThrowIfCancellationRequested();
            var kept = Configs.JavaList.Get()!.Where(java => !existingObjects.Contains(java) || valid.Contains(java)).Distinct().ToList();
            var identities = kept.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var disabled = Configs.JavaRemovedList.Get()!.ToArray();
            var disabledIdentities = disabled.Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(folder => WindowsJavaDiscovery.ActualExecutable(Path.Combine(folder, "java.exe")))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Preserve the selected paths and ordering of existing entries, including manually added aliases.
            kept.RemoveAll(java => disabled.Contains(java.Folder, StringComparer.OrdinalIgnoreCase));
            ConcurrentList<Java> catalogue = [..additions.Where(java => {
                string identity = Identity(java);
                return File.Exists(java.JavaExePath) && !disabledIdentities.Contains(identity) && identities.Add(identity);
            }), ..kept];
            Configs.JavaList.Set(catalogue);
            Logger.Info($"Java 搜索完成，发现 {catalogue.Count} 个 Java");
        }

        static string Identity(Java java) => WindowsJavaDiscovery.ActualExecutable(java.JavaExePath);
    }

    /// <summary>
    /// 并行搜索所有指定的文件夹及其子文件夹中的 Java，并检查其有效性。
    /// </summary>
    public static async Task<List<Java>> SearchFoldersAsync(bool includeSubDirectories, IEnumerable<string> folders, CancellationToken cancellationToken = default, ProgressProvider? progress = null) {
        List<string> directories = await FindJavaFoldersAsync(includeSubDirectories, folders, cancellationToken, progress?.SplitTo(0.2)).NoCapture();
        var javaList = directories.Select(folder => new Java(folder)).ToList();
        javaList = await SortAsync(javaList, cancellationToken, progress?.SplitTo(1)).NoCapture();
        Logger.Info($"快速查找 Java 完成，共有 {javaList.Count} 个候选：{javaList.Join('、')}");
        return javaList;
    }

    private static async Task<List<string>> FindJavaFoldersAsync(bool includeSubDirectories, IEnumerable<string> folders,
        CancellationToken cancellationToken, ProgressProvider? progress, bool globalScan = false) {
        // 并行搜索
        ConcurrentBag<(string Folder, bool HasReparsePoint, bool IsSpecialPath)> results = [];
        await TaskUtils.ForEachAsync(folders.Distinct(StringComparer.OrdinalIgnoreCase), 4, async (folder, cp) => {
            await Task.Run(() => {
                try {
                    if (!DirectoryUtils.Exists(folder)) return;
                    var files = globalScan
                        ? Directory.EnumerateFiles(PathUtils.ForApi(folder), "java.exe", new EnumerationOptions {
                            RecurseSubdirectories = includeSubDirectories, IgnoreInaccessible = true, MaxRecursionDepth = 6
                        }).Select(PathUtils.RemoveExtendedPrefix)
                        : DirectoryUtils.EnumerateFiles(folder, includeSubDirectories, "java.exe");
                    foreach (var file in files) {
                        cancellationToken.ThrowIfCancellationRequested();
                        string targetFolder = PathUtils.AddSlashSuffix(PathUtils.ForCompare(PathUtils.RemoveLastPart(file)));
                        // 判断文件夹是否包含重解析点（例如符号链接）
                        static bool HasReparsePoint(FileSystemInfo info) {
                            if (PathUtils.IsParentOf(Paths.AppData + @".minecraft\runtime\", info.FullName)) return false; // 不筛查 PCL 的下载路径（#8928）
                            FileSystemInfo? current = info;
                            do {
                                if (current.Attributes.HasFlagF(FileAttributes.ReparsePoint)) return true;
                                current = current is FileInfo fileInfo ? fileInfo.Directory : ((DirectoryInfo) current).Parent;
                            } while (current is not null);
                            return false;
                        }
                        // 结果
                        var hasReparsePoint = !globalScan && HasReparsePoint(FileUtils.GetInfo(Path.Combine(targetFolder, "java.exe")));
                        var isSpecialPath = targetFolder.ContainsIgnoreCase("java8path_target_") || targetFolder.ContainsIgnoreCase("javapath_target_") || targetFolder.ContainsIgnoreCase("javatmp") || targetFolder.ContainsIgnoreCase("system32");
                        results.Add((targetFolder, hasReparsePoint, isSpecialPath));
                    }
                } catch (UnauthorizedAccessException) {
                    Logger.Info($"快速查找 Java 时没有权限（{folder}）");
                } catch (Exception ex) {
                    ex.ThrowIfCanceled();
                    Logger.Warn(ex, $"快速查找 Java 时出错（{folder}）");
                }
            }, cancellationToken).NoCapture();
        }, cancellationToken, progress).NoCapture();
        if (!results.Any()) return [];

        // 筛除重解析点
        if (!globalScan && results.All(r => r.HasReparsePoint)) {
            Logger.Warn("找到的所有 Java 都包含重解析点，保留全部结果");
        } else if (results.Any(r => r.HasReparsePoint)) {
            Logger.Warn($"找到的以下 Java 包含重解析点，将它们从列表中排除：{results.Where(r => r.HasReparsePoint).Select(r => r.Folder).Join('、')}");
            results = [..results.Where(r => !r.HasReparsePoint)];
        }
        // 筛除特殊路径
        if (results.All(r => r.IsSpecialPath)) {
            Logger.Warn("找到的所有 Java 都包含特殊路径，保留全部结果");
        } else if (results.Any(r => r.IsSpecialPath)) {
            Logger.Warn($"找到的以下 Java 包含特殊路径，将它们从列表中排除：{results.Where(r => r.IsSpecialPath).Select(r => r.Folder).Join('、')}");
            results = [..results.Where(r => !r.IsSpecialPath)];
        }

        return results.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    #endregion

}
