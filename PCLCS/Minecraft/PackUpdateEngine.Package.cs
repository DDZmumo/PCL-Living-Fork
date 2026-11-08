// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    public const string ManifestName = "PCL/ModpackOrigin.json";
    private readonly PackUpdateHost<TFile, TPackage, TChange, TPlan, TNetFile> host;
    public PackUpdateEngine(PackUpdateHost<TFile, TPackage, TChange, TPlan, TNetFile> host) { this.host = host; }
    private static bool Same(string? left, string? right) => (left ?? "") == (right ?? "");
    public static string RelativePath(string value) {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new IOException("整合包包含无效路径。");
        value = value.Replace("\\", "/");
        foreach (string part in value.Split('/')) {
            if (part == "" || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase)) throw new IOException("整合包包含无效路径：" + value);
        }
        return value;
    }
    public string Target(string root, string relative) {
        string result = Path.Combine(root, RelativePath(relative).Replace('/', Path.DirectorySeparatorChar));
        host.EnsureWithin(root, result);
        if (Directory.Exists(result)) throw new IOException("文件位置已被文件夹占用：" + relative);
        return result;
    }
    public static string? Digest(string fileName, string algorithm = "sha1", CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        algorithm = HashAlgorithmName(algorithm);
        if (!File.Exists(fileName)) return null;
        using (Stream input = File.OpenRead(fileName))
        using (HashAlgorithm hasher = algorithm switch { "md5" => MD5.Create(), "sha1" => SHA1.Create(), "sha256" => SHA256.Create(), "sha512" => SHA512.Create(), _ => throw new IOException("不支持的整合包校验算法。") }) {
            byte[] buffer = new byte[64 * 1024];
            while (true) {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                FileTaskProgress.RecordRead(read);
                token.ThrowIfCancellationRequested();
                if (read == 0) break;
                hasher.TransformBlock(buffer, 0, read, buffer, 0);
            }
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            token.ThrowIfCancellationRequested();
            return algorithm + ":" + BitConverter.ToString(hasher.Hash!).Replace("-", "").ToLowerInvariant();
        }
    }
    private static string HashAlgorithmName(string algorithm) {
        string value = algorithm.ToLowerInvariant();
        if (!new[] { "md5", "sha1", "sha256", "sha512" }.Contains(value)) throw new IOException("不支持的整合包校验算法：" + algorithm);
        return value;
    }
    public static string NormalizeHash(string expected) {
        string[] parts = expected.Split(':');
        if (parts.Length != 2) throw new IOException("整合包文件缺少有效校验值。");
        return ValidHash(parts[0], parts[1]);
    }
    public static bool Matches(string fileName, string? expected, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        if (expected is null) return false;
        string normalized = NormalizeHash(expected);
        return Same(Digest(fileName, normalized.Split(':')[0], token), normalized);
    }
    public static string ValidHash(string algorithm, string? value) {
        algorithm = HashAlgorithmName(algorithm);
        int length = algorithm switch { "md5" => 32, "sha1" => 40, "sha256" => 64, "sha512" => 128, _ => throw new IOException("不支持的整合包校验算法。") };
        if (!Regex.IsMatch(value ?? "", @"\A[0-9a-fA-F]{" + length + @"}\z")) throw new IOException("整合包文件缺少有效校验值。");
        return algorithm + ":" + value!.ToLowerInvariant();
    }
    public static bool IsPrivate(string relative) {
        string lower = relative.ToLowerInvariant();
        return lower.StartsWith("pcl/") || lower.StartsWith(".pcl-") || lower == ".pclignore";
    }
    public static bool IsRuntimeData(string relative) {
        string lower = relative.ToLowerInvariant();
        // Litematica's per-world/server/dimension state is not a global author setting.
        return lower.StartsWith("config/litematica/litematica_", StringComparison.Ordinal) && lower.EndsWith(".json", StringComparison.Ordinal) &&
            lower.Substring("config/litematica/".Length).IndexOf('/') < 0;
    }
    public static bool IsPersonal(string relative) {
        string lower = relative.ToLowerInvariant();
        return IsRuntimeData(relative) || lower == "options.txt" || lower.StartsWith("optionsof") || lower.StartsWith("optionsshaders") || lower == "servers.dat" || lower == "servers.dat_old" ||
            new[] { "saves/", "screenshots/", "xaerowaypoints/", "xaeroworldmap/", "journeymap/", "voxelmap/", "schematics/" }.Any(prefix => lower.StartsWith(prefix)) ||
            lower.Contains("waypoint") || lower.StartsWith("config/xaero") || lower.StartsWith("config/journeymap") || lower.StartsWith("config/voxelmap");
    }
    public bool PersonalAreaExists(string root, string relative) {
        string[] parts = relative.Split('/');
        if (parts.Length < 2) return false;
        string prefix = parts[0].ToLowerInvariant();
        string area = prefix == "saves" && parts.Length > 2 ? parts[0] + "/" + parts[1] : parts[0];
        string folder = Path.Combine(root, area.Replace('/', Path.DirectorySeparatorChar));
        host.EnsureWithin(root, folder);
        return IsPersonal(relative) && Directory.Exists(folder);
    }
    public static JObject JsonEntry(ZipArchiveEntry entry) {
        if (entry.Length > 16 * 1024 * 1024) throw new IOException("整合包清单过大。");
        using (StreamReader reader = new(entry.Open(), Encoding.UTF8)) return JObject.Parse(reader.ReadToEnd());
    }
    public TPackage ReadPackage(string archivePath, string stage, CancellationToken token, Func<string, HttpMethod?, string?, JToken>? request = null) {
        if (request is null) request = host.Request;
        TPackage result = host.CreatePackage();
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Read, InstanceResourcePackageRules.EntryNameEncoding())) {
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item => item.FullName == "modrinth.index.json" || item.FullName == "manifest.json");
            if (entry is null) {
                var entries = archive.Entries.Where(item => item.FullName.Split('/').Length == 2 && new[] { "modrinth.index.json", "manifest.json" }.Contains(item.Name)).ToList();
                if (entries.Count != 1) throw new IOException("请选择 CurseForge 或 Modrinth 格式的整合包文件。");
                entry = entries[0];
            }
            string prefix = entry.FullName.Substring(0, entry.FullName.Length - entry.Name.Length);
            JObject json = JsonEntry(entry);
            result.Name = json["name"]?.ToString() ?? "整合包";
            result.Version = json["versionId"]?.ToString() ?? json["version"]?.ToString() ?? "";
            List<string> overrideFolders = new();
            if (entry.Name == "modrinth.index.json") {
                result.Minecraft = json["dependencies"]?["minecraft"]?.ToString();
                foreach (var pair in new Dictionary<string, ModLoaders> { { "fabric-loader", ModLoaders.Fabric }, { "forge", ModLoaders.Forge }, { "neoforge", ModLoaders.NeoForge } })
                    if (json["dependencies"]?[pair.Key] is not null) result.Loaders[pair.Value] = json["dependencies"]![pair.Key]!.ToString();
                foreach (JProperty dependency in ((JObject)json["dependencies"]!).Properties())
                    if (!new[] { "minecraft", "fabric-loader", "forge", "neoforge" }.Contains(dependency.Name)) throw new IOException("暂不支持此整合包加载器：" + dependency.Name);
                foreach (JToken item in (json["files"] as JArray) ?? new JArray()) {
                    token.ThrowIfCancellationRequested();
                    if (item["env"]?["client"]?.ToString() == "unsupported") continue;
                    TFile value = host.CreateFile();
                    value.Relative = RelativePath(item["path"]!.ToString());
                    value.Hash = item["hashes"]?["sha1"] is not null ? ValidHash("sha1", item["hashes"]!["sha1"]!.ToString()) : ValidHash("sha512", item["hashes"]?["sha512"]?.ToString());
                    value.OptionalFile = item["env"]?["client"]?.ToString() == "optional";
                    value.Urls = ((JArray)item["downloads"]!).Select(url => url.ToString()).ToList();
                    foreach (string url in value.Urls) {
                        Match project = Regex.Match(url, @"\Ahttps://cdn\.modrinth\.com/data/([A-Za-z0-9]+)/versions/");
                        if (!project.Success) continue;
                        value.Identity = "modrinth:" + project.Groups[1].Value;
                        // Historical URLs also use version names containing dots, plus signs or escapes.
                        // Project ownership remains known even when no exact platform version ID is available.
                        Match version = Regex.Match(url, @"\Ahttps://cdn\.modrinth\.com/data/[A-Za-z0-9]+/versions/([A-Za-z0-9]+)/");
                        if (version.Success) value.FileIdentity = "modrinth:" + version.Groups[1].Value;
                        break;
                    }
                    AddFile(result, value);
                }
                overrideFolders.Add(prefix + "overrides/"); overrideFolders.Add(prefix + "client-overrides/");
            } else {
                result.Minecraft = json["minecraft"]?["version"]?.ToString();
                foreach (JToken item in (json["minecraft"]?["modLoaders"] as JArray) ?? new JArray()) {
                    string id = item["id"]!.ToString(); int split = id.IndexOf('-');
                    if (split < 1) throw new IOException("无效的整合包加载器。");
                    ModLoaders kind = id.Substring(0, split) switch { "forge" => ModLoaders.Forge, "neoforge" => ModLoaders.NeoForge, "fabric" => ModLoaders.Fabric, _ => throw new IOException("暂不支持此整合包加载器：" + id) };
                    result.Loaders[kind] = id.Substring(split + 1);
                }
                var files = ((json["files"] as JArray) ?? new JArray()).ToList();
                if (files.Any()) {
                    JArray response = (JArray)request("https://api.curseforge.com/v1/mods/files", HttpMethod.Post,
                        new JObject(new JProperty("fileIds", new JArray(files.Select(item => (int)item["fileID"]!)))).ToString())["data"]!;
                    foreach (JToken item in files) {
                        token.ThrowIfCancellationRequested();
                        JToken? data = response.FirstOrDefault(value => value["id"]!.ToString() == item["fileID"]!.ToString());
                        if (data is null || data["modId"]!.ToString() != item["projectID"]!.ToString()) throw new IOException("整合包所需文件不可用，未修改当前版本。");
                        ResourceVersionModelData version = host.ParseVersion((JObject)data, ResourceTypes.Mod);
                        if (!version.DownloadAvailable) throw new IOException("整合包所需文件不允许下载：" + version.FileName);
                        var modules = ((data["modules"] as JArray) ?? new JArray()).Select(module => module["name"]!.ToString()).ToList();
                        string folder = version.FileName!.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : modules.Contains("shaders") ? "shaderpacks" : modules.Contains("pack.mcmeta") ? "resourcepacks" : "mods";
                        TFile value = host.CreateFile();
                        value.Relative = RelativePath(folder + "/" + version.FileName);
                        value.Hash = ValidHash(version.Hash?.Length == 32 ? "md5" : "sha1", version.Hash);
                        value.Identity = "curseforge:" + item["projectID"]!.ToString();
                        value.FileIdentity = "curseforge:" + item["fileID"]!.ToString();
                        value.OptionalFile = item["required"] is not null && !(bool)item["required"]!;
                        value.Urls = version.DownloadUrls!.ToList();
                        AddFile(result, value);
                    }
                }
                string overridePath = (json["overrides"]?.ToString() ?? "overrides").TrimEnd('/');
                overrideFolders.Add(prefix + RelativePath(overridePath) + "/");
            }
            if (string.IsNullOrWhiteSpace(result.Minecraft)) throw new IOException("整合包缺少 Minecraft 版本。");
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (string folder in overrideFolders) {
                seen.Clear();
                foreach (ZipArchiveEntry item in archive.Entries.Where(value => value.FullName.StartsWith(folder, StringComparison.Ordinal) && !value.FullName.EndsWith("/"))) {
                    token.ThrowIfCancellationRequested();
                    string relative = RelativePath(item.FullName.Substring(folder.Length));
                    if (IsPrivate(relative)) continue;
                    if (!seen.Add(relative)) throw new IOException("整合包包含重复路径：" + relative);
                    total += item.Length;
                    if (total > 20L * 1024 * 1024 * 1024 || item.Length > 2L * 1024 * 1024 * 1024) throw new IOException("整合包覆写文件过大。");
                    string payload = Target(stage, "payload/" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                    using (Stream input = item.Open()) using (Stream output = File.Create(payload)) FileTaskProgress.CopyStream(input, output, token);
                    TFile value = host.CreateFile(); value.Relative = relative; value.Payload = payload; value.Hash = Digest(payload, token: token);
                    result.Files[relative] = value;
                }
            }
        }
        return result;
    }
    public void AddFile(TPackage package, TFile value) {
        if (IsPrivate(value.Relative!)) return;
        foreach (string url in value.Urls) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme != "https" || address.UserInfo != "") throw new IOException("整合包包含无效下载地址。");
        }
        if (package.Files.ContainsKey(value.Relative!)) throw new IOException("整合包包含重复路径：" + value.Relative);
        package.Files.Add(value.Relative!, value);
    }
    public static JObject Manifest(TPackage package) {
        JObject files = new();
        foreach (TFile item in package.Files.Values) files[RelativePath(item.Relative!)] = new JObject(new JProperty("hash", NormalizeHash(item.Hash ?? "")), new JProperty("identity", item.Identity),
            new JProperty("fileIdentity", item.FileIdentity), new JProperty("optional", item.OptionalFile), new JProperty("state", "declared"));
        return new JObject(new JProperty("schema", 2), new JProperty("complete", true), new JProperty("associationPolicyVersion", 2), new JProperty("name", package.Name), new JProperty("version", package.Version), new JProperty("minecraft", package.Minecraft),
            new JProperty("loaders", JObject.FromObject(package.Loaders)), new JProperty("files", files), new JProperty("source", package.Source));
    }
    public JObject? ReadManifest(string versionFolder) {
        string fileName = Target(versionFolder, ManifestName);
        if (!File.Exists(fileName)) return null;
        if (new FileInfo(fileName).Length > 16 * 1024 * 1024) throw new IOException("整合包作者清单过大。");
        JObject result = JObject.Parse(File.ReadAllText(fileName));
        string? schema = result["schema"]?.Type == JTokenType.Integer ? result["schema"]!.ToString() : null;
        if ((schema != "1" && schema != "2") || result["files"] is not JObject files) throw new IOException("不支持此版本的整合包作者清单。");
        ValidateOptionalManifestType(result, "complete", JTokenType.Boolean);
        ValidateOptionalManifestType(result, "baselineNeedsRepair", JTokenType.Boolean);
        if (schema == "2" && result["complete"]?.Type != JTokenType.Boolean) throw InvalidManifest();
        ValidateOptionalManifestType(result, "source", JTokenType.Object);
        ValidateOptionalManifestType(result, "minecraft", JTokenType.String);
        ValidateOptionalManifestType(result, "loaders", JTokenType.Object);
        if (result["loaders"] is JObject loaders) foreach (JProperty loader in loaders.Properties())
            if (loader.Value.Type != JTokenType.String) throw InvalidManifest();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JProperty file in files.Properties()) {
            ManifestPath(file.Name);
            if (!seen.Add(file.Name)) throw new IOException("整合包作者清单包含重复路径，请重新提供正确的旧整合包。");
            if (file.Value is not JObject value || value["hash"]?.Type != JTokenType.String) throw new IOException("整合包作者清单缺少原始校验值，请重新提供正确的旧整合包。");
            value["hash"] = NormalizeHash(value["hash"]!.ToString());
            foreach (string field in new[] { "identity", "fileIdentity", "state" }) ValidateOptionalManifestType(value, field, JTokenType.String);
            ValidateOptionalManifestType(value, "optional", JTokenType.Boolean);
            ValidateOptionalManifestType(value, "baselineRelative", JTokenType.String);
            if (value["baselineRelative"]?.Type == JTokenType.String) ManifestPath(value["baselineRelative"]!.ToString());
            ValidateOptionalManifestType(value, "originalHash", JTokenType.String);
            if (value["originalHash"]?.Type == JTokenType.String) value["originalHash"] = NormalizeHash(value["originalHash"]!.ToString());
            ValidateOptionalManifestType(value, "local", JTokenType.Object);
            if (value["local"] is JObject local) {
                if (local["relative"]?.Type != JTokenType.String || local["hash"]?.Type != JTokenType.String) throw InvalidManifest();
                ManifestPath(local["relative"]!.ToString());
                local["hash"] = NormalizeHash(local["hash"]!.ToString());
                ValidateOptionalManifestType(local, "ownership", JTokenType.String);
                if (local["ownership"]?.Type == JTokenType.String && local["ownership"]!.ToString() is not ("author" or "personal-added" or "ambiguous" or "unknown")) throw InvalidManifest();
                ValidateOptionalManifestType(local, "association", JTokenType.String);
                ValidateOptionalManifestType(local, "personalComponentMatched", JTokenType.Boolean);
            }
            ValidateOptionalManifestType(value, "localCandidates", JTokenType.Array);
            if (value["localCandidates"] is JArray candidates) foreach (JToken candidate in candidates) {
                if (candidate.Type != JTokenType.String) throw InvalidManifest();
                ManifestPath(candidate.ToString());
            }
        }
        ValidateOptionalManifestType(result, "retained", JTokenType.Object);
        if (result["retained"] is JObject retained) foreach (JProperty file in retained.Properties()) {
            ManifestPath(file.Name);
            if (file.Value is not JObject value) throw new IOException("整合包保留文件记录无效，请重新提供正确的旧整合包。");
            if (value["authorRelative"]?.Type != JTokenType.String || value["hash"]?.Type != JTokenType.String || value["observedHash"]?.Type != JTokenType.String) throw InvalidManifest();
            ManifestPath(value["authorRelative"]!.ToString());
            value["hash"] = NormalizeHash(value["hash"]!.ToString());
            value["observedHash"] = NormalizeHash(value["observedHash"]!.ToString());
            foreach (string field in new[] { "identity", "fileIdentity", "state", "ownership" }) ValidateOptionalManifestType(value, field, JTokenType.String);
            if (value["ownership"]?.Type == JTokenType.String && value["ownership"]!.ToString() != "personal-retained") throw InvalidManifest();
        }
        if (schema == "1") {
            // Historical RecordInstalled pruned absent/modified files. Do not silently
            // promote that filtered inventory to a complete author declaration.
            result["complete"] = false;
            result["baselineNeedsRepair"] = true;
        }
        return result;
    }
    private static IOException InvalidManifest() => new("整合包作者或本地状态记录无效，请重新提供正确的旧整合包。");
    private static void ValidateOptionalManifestType(JObject value, string field, JTokenType type) {
        if (value[field] is JToken token && token.Type != JTokenType.Null && token.Type != type) throw InvalidManifest();
    }
    private static void ManifestPath(string relative) {
        RelativePath(relative);
        if (IsPrivate(relative)) throw InvalidManifest();
    }
    public JObject? Detect(string versionFolder) {
        JObject? recorded = ReadManifest(versionFolder);
        if (recorded is not null) return recorded;
        foreach (string name in new[] { "modrinth.index.json", "manifest.json" }) {
            string address = Target(versionFolder, name);
            if (!File.Exists(address)) continue;
            if (new FileInfo(address).Length > 16 * 1024 * 1024) throw new IOException("整合包清单过大。");
            JObject data = JObject.Parse(File.ReadAllText(address));
            string? mc = name == "modrinth.index.json" ? data["dependencies"]?["minecraft"]?.ToString() : data["minecraft"]?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(mc) || data["files"] is not JArray) continue;
            return new JObject(new JProperty("name", data["name"]?.ToString() ?? "整合包"), new JProperty("version", data["versionId"]?.ToString() ?? data["version"]?.ToString()), new JProperty("minecraft", mc), new JProperty("legacy", true));
        }
        return null;
    }
    public void RecordInstalled(string archivePath, string versionFolder, JObject? source = null) {
        string stage = host.TempFolder();
        try {
            TPackage package = ReadPackage(archivePath, stage, CancellationToken.None); package.Source = source;
            string root = host.InstanceGameRoot(versionFolder);
            JObject manifest = Manifest(package);
            foreach (JProperty entry in ((JObject)manifest["files"]!).Properties()) ObserveInstalledFile(root, entry.Name, (JObject)entry.Value);
            host.AtomicWrite(Target(versionFolder, ManifestName), Encoding.UTF8.GetBytes(manifest.ToString()));
        } catch (Exception ex) { host.Warn(ex, "整合包已安装，但记录作者文件清单失败；更新时可重新提供原始整合包"); }
    }
}
