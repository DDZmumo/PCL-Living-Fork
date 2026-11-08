// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>A platform project, never a filename, hash, slug or version ID.</summary>
public readonly record struct ResourceFavoriteIdentity(ResourcePlatforms Platform, string ProjectId) {
    public static bool TryCreate(ResourcePlatforms platform, string? projectId, out ResourceFavoriteIdentity identity) {
        identity = default;
        if (string.IsNullOrEmpty(projectId) || projectId != projectId!.Trim()) return false;
        if (platform == ResourcePlatforms.CurseForge) {
            if (!long.TryParse(projectId, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0) return false;
            projectId = number.ToString(CultureInfo.InvariantCulture);
        } else if (platform == ResourcePlatforms.Modrinth) {
            // Modrinth IDs use case-sensitive base62. Do not normalize their case.
            if (projectId.Length > 64 || projectId.Any(c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9'))) return false;
        } else return false;
        identity = new ResourceFavoriteIdentity(platform, projectId);
        return true;
    }
    public override string ToString() => ResourceProjectKey.Get(Platform, ProjectId);
}

/// <summary>Offline project metadata copied from an authoritative platform response/cache.</summary>
public sealed class ResourceFavorite {
    private readonly JObject project;
    public ResourceFavoriteIdentity Identity { get; }
    public ResourceTypes Types { get; }
    public string Name => (string?)project["FavoriteDisplayName"] ?? (string?)project["RawName"] ?? Identity.ProjectId;
    public string Description => (string?)project["Description"] ?? "";
    public DateTime SavedAt { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> KnownGameVersions { get; }
    public ModLoaders ModLoaders { get; }
    public JObject ProjectJson => (JObject)project.DeepClone();
    public const ResourceTypes SupportedTypes = ResourceTypes.Mod | ResourceTypes.ModPack | ResourceTypes.Map | ResourceTypes.DataPack | ResourceTypes.ResourcePack | ResourceTypes.Shader;

    private ResourceFavorite(ResourceFavoriteIdentity identity, ResourceTypes types, JObject metadata, DateTime savedAt) {
        Identity = identity; Types = types; project = (JObject)metadata.DeepClone(); SavedAt = savedAt;
        project["Id"] = identity.ProjectId;
        Tags = Array.AsReadOnly(((JArray)project["Tags"]!).Values<string>().Select(value => value!).ToArray());
        KnownGameVersions = Array.AsReadOnly(((JArray?)project["KnownGameVersions"] ?? new JArray()).Values<string>()
            .Where(value => value is not null && ResourceFavoriteFilter.IsGameVersionLabel(value)).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray());
        ModLoaders = (ModLoaders?)project["ModLoaders"]?.ToObject<int>() ?? PCLCS.ModLoaders.None;
    }
    public static ResourceFavorite FromMetadata(JObject metadata, DateTime savedAt) {
        if (metadata["Platform"]?.Type != JTokenType.Integer || metadata["Types"]?.Type != JTokenType.Integer || metadata["Id"]?.Type != JTokenType.String ||
            !ResourceFavoriteIdentity.TryCreate((ResourcePlatforms)(int)metadata["Platform"]!, (string?)metadata["Id"], out var identity))
            throw new InvalidDataException("收藏缺少有效的平台项目身份。");
        var types = (ResourceTypes)(int)metadata["Types"]!;
        if ((types & SupportedTypes) == 0 || (types & ~SupportedTypes) != 0 || metadata["Tags"] is not JArray || metadata["Drops"] is not (null or JArray) ||
            metadata["LastUpdate"] is null || metadata["DownloadCount"]?.Type != JTokenType.Integer || metadata["RawName"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)metadata["RawName"]) ||
            new[] { "Slug", "Description", "Website", "LogoUrl", "FavoriteDisplayName" }.Any(key => metadata[key] is { Type: not (JTokenType.String or JTokenType.Null) }) ||
            ((JArray)metadata["Tags"]!).Any(value => value.Type != JTokenType.String) ||
            metadata["ModLoaders"] is { Type: not JTokenType.Integer } || metadata["CurseForgeFileIds"] is not (null or JArray) ||
            metadata["KnownGameVersions"] is not (null or JArray) || metadata["KnownGameVersions"] is JArray known && known.Any(value => value.Type != JTokenType.String))
            throw new InvalidDataException("收藏项目的资源信息不完整。");
        _ = metadata["LastUpdate"]!.ToObject<DateTime>();
        _ = metadata["DownloadCount"]!.ToObject<int>();
        if (metadata["ModLoaders"] is not null) _ = metadata["ModLoaders"]!.ToObject<int>();
        foreach (string field in new[] { "Drops", "CurseForgeFileIds" }) {
            if (metadata[field] is not JArray numbers) continue;
            foreach (JToken number in numbers) {
                if (number.Type != JTokenType.Integer) throw new InvalidDataException("收藏项目的版本信息损坏。");
                _ = number.ToObject<int>();
            }
        }
        return new ResourceFavorite(identity, types, metadata, savedAt);
    }
    public bool Matches(ResourceTypes? type, string? search) => (!type.HasValue || (Types & type.Value) != 0) &&
        (string.IsNullOrWhiteSpace(search) || (Name + "\n" + (string?)project["RawName"] + "\n" + Description + "\n" + (string?)project["Slug"] + "\n" + Identity.Platform)
            .IndexOf(search!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
}

/// <summary>Project metadata filters; unknown compatibility never becomes an exact match.</summary>
public sealed class ResourceFavoriteFilter {
    public ResourceTypes? Category { get; init; }
    public string? Name { get; init; }
    public ResourcePlatforms Source { get; init; } = ResourcePlatforms.Any;
    public string? Tag { get; init; }
    public string? GameVersion { get; init; }
    public ModLoaders Loader { get; init; }
    public bool Matches(ResourceFavorite entry, bool ignoreCategory = false) => entry.Matches(ignoreCategory ? null : Category, Name) &&
        (Source & entry.Identity.Platform) != 0 && (string.IsNullOrEmpty(Tag) || entry.Tags.Contains(Tag!, StringComparer.Ordinal)) &&
        (string.IsNullOrWhiteSpace(GameVersion) || entry.KnownGameVersions.Contains(GameVersion!.Trim(), StringComparer.Ordinal)) &&
        (Loader == ModLoaders.None || (entry.Types & ResourceTypes.Mod) != 0 && (entry.ModLoaders & Loader) == Loader);
    // Dotted release/beta labels and weekly snapshots. Base62 version IDs lack either shape.
    public static bool IsGameVersionLabel(string value) => !string.IsNullOrWhiteSpace(value) &&
        (System.Text.RegularExpressions.Regex.IsMatch(value, @"^(?:[a-z]?\d+\.)\d+(?:[.a-z0-9_+\-]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
         System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{2}w\d{2}[a-z]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
}

/// <summary>
/// One launcher-wide JSON store. Reads are cached; a mutation re-reads under a process/file
/// lock and only publishes its snapshot after an atomic save. A corrupt file is never replaced.
/// </summary>
public sealed class ResourceFavoriteStore {
    public const int MaxCount = 2000;
    public const int MaxBytes = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path;
    private readonly object gate;
    private readonly object cacheGate = new();
    private readonly Action<string, string> write;
    private IReadOnlyList<ResourceFavorite> snapshot = Array.Empty<ResourceFavorite>();
    private HashSet<ResourceFavoriteIdentity> identities = new();
    private bool loaded;
    public Exception? LoadError { get; private set; }
    public Exception? LastOperationError { get; private set; }
    public event EventHandler<EventArgs>? Changed;

    public ResourceFavoriteStore(string path, Action<string, string>? atomicWrite = null) {
        this.path = Path.GetFullPath(path); gate = Gates.GetOrAdd(this.path, _ => new object());
        write = atomicWrite ?? AtomicWrite;
    }
    public IReadOnlyList<ResourceFavorite> Snapshot { get { lock (cacheGate) { EnsureLoaded(); return snapshot; } } }
    public bool Contains(ResourceFavoriteIdentity identity) { lock (cacheGate) { EnsureLoaded(); return identities.Contains(identity); } }
    public bool Reload() {
        lock (gate) {
            try {
                var entries = Read();
                lock (cacheGate) { Publish(entries); loaded = true; LoadError = null; LastOperationError = null; }
                return true;
            } catch (Exception error) {
                lock (cacheGate) { loaded = true; LoadError = error; }
                return false;
            }
        }
    }
    private void EnsureLoaded() {
        if (loaded) return;
        try { Publish(Read()); LoadError = null; }
        catch (Exception error) { LoadError = error; } // Preserve the last good in-memory view too.
        loaded = true;
    }
    public bool TryToggle(ResourceFavorite project, out bool favorited, out Exception? error) {
        bool changed = false;
        lock (gate) {
            lock (cacheGate) { EnsureLoaded(); favorited = identities.Contains(project.Identity); }
            error = null;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using FileStream lease = AcquireFileLock();
                // Never save over unrecognized/malformed data, including external edits.
                var current = Read().ToList();
                int index = current.FindIndex(value => value.Identity == project.Identity);
                bool next = index < 0;
                if (next) {
                    if (current.Count >= MaxCount) throw new InvalidDataException("收藏数量已达上限。");
                    current.Insert(0, project);
                } else current.RemoveAt(index);
                string serialized = Serialize(current);
                if (Encoding.UTF8.GetByteCount(serialized) > MaxBytes) throw new InvalidDataException("收藏数据已达上限。");
                write(path, serialized);
                lock (cacheGate) { Publish(current); loaded = true; LoadError = null; LastOperationError = null; }
                favorited = next; changed = true;
            } catch (Exception failure) {
                error = failure;
                lock (cacheGate) { LastOperationError = failure; favorited = identities.Contains(project.Identity); }
                return false;
            }
        }
        // A stale/detached view must not turn an already committed save into failure.
        if (changed && Changed is { } callbacks)
            foreach (EventHandler<EventArgs> callback in callbacks.GetInvocationList())
                try { callback(this, EventArgs.Empty); } catch { }
        return true;
    }
    private void Publish(IEnumerable<ResourceFavorite> entries) {
        ResourceFavorite[] items = entries.ToArray();
        snapshot = new ReadOnlyCollection<ResourceFavorite>(items);
        identities = new HashSet<ResourceFavoriteIdentity>(items.Select(value => value.Identity));
    }
    private IReadOnlyList<ResourceFavorite> Read() {
        if (!File.Exists(path)) return Array.Empty<ResourceFavorite>();
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("收藏文件过大。");
        JObject root;
        using (var reader = new JsonTextReader(new StreamReader(path, Encoding.UTF8)) { DateParseHandling = DateParseHandling.DateTime }) {
            root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("收藏文件包含多余内容。");
        }
        if (root["Schema"]?.Type != JTokenType.Integer || (int)root["Schema"]! != 1 || root["Projects"] is not JArray projects || projects.Count > MaxCount)
            throw new InvalidDataException("无法识别收藏文件格式。");
        List<ResourceFavorite> result = new(); HashSet<ResourceFavoriteIdentity> seen = new();
        foreach (JToken item in projects) {
            if (item is not JObject value || value["Project"] is not JObject project || value["SavedAt"] is null) throw new InvalidDataException("收藏项目数据损坏。");
            ResourceFavorite entry = ResourceFavorite.FromMetadata(project, value["SavedAt"]!.ToObject<DateTime>());
            if (!seen.Add(entry.Identity)) throw new InvalidDataException("收藏文件包含重复项目。");
            result.Add(entry);
        }
        return result;
    }
    private static string Serialize(IEnumerable<ResourceFavorite> entries) => new JObject {
        ["Schema"] = 1,
        ["Projects"] = new JArray(entries.Select(entry => new JObject { ["SavedAt"] = entry.SavedAt, ["Project"] = entry.ProjectJson }))
    }.ToString(Formatting.Indented);
    private FileStream AcquireFileLock() {
        for (int attempt = 0; ; attempt++) {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { Thread.Sleep(20); }
        }
    }
    private static void AtomicWrite(string path, string serialized) {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] bytes = new UTF8Encoding(false).GetBytes(serialized); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
