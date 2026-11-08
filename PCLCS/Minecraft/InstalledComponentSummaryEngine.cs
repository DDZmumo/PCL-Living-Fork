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
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF-only installed-component read model, MPL-2.0 for PCLLF-original code. PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8
// and CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 do not replace this LF saved-isolation/manifest policy.
public class InstalledComponentSummaryItemData {
    public readonly string Name, Version, Icon;
    public InstalledComponentSummaryItemData(string name, string? version, string icon) { Name = name; Version = string.IsNullOrWhiteSpace(version) ? "未知版本" : version!; Icon = icon; }
}
public sealed class InstalledComponentSummaryEngine<TItem> where TItem : InstalledComponentSummaryItemData {
    private readonly Func<string, string?, string, TItem> create;
    public InstalledComponentSummaryEngine(Func<string, string?, string, TItem> create) { this.create = create; }
    public List<TItem> Describe(McVersion version, McComponents? components = null, IDictionary<string, string?>? localMods = null) {
        List<TItem> result = new() { create("Minecraft", version.VanillaName, "Grass") };
        foreach (ModLoaders loader in new[] { ModLoaders.Forge, ModLoaders.NeoForge, ModLoaders.Fabric, ModLoaders.LiteLoader }) {
            if (!version.ModLoaderVersions.TryGetValue(loader, out string? value)) continue;
            if (components is not null && components.ModLoaderVersions.TryGetValue(loader, out string? recorded) && !string.IsNullOrWhiteSpace(recorded)) value = recorded;
            string icon = loader == ModLoaders.Forge ? "Anvil" : loader == ModLoaders.NeoForge ? "NeoForge" : loader == ModLoaders.Fabric ? "Fabric" : "Egg";
            result.Add(create(loader.ToString(), value, icon));
        }
        string? fabricApi = null, optiFabric = null, optiFine = version.OptiFine;
        if (localMods is not null) {
            localMods.TryGetValue("fabric-api", out fabricApi); localMods.TryGetValue("optifabric", out optiFabric);
            if (localMods.TryGetValue("optifine", out string? localOptiFine)) optiFine = localOptiFine ?? components?.OptiFineVersion ?? optiFine ?? "未知版本";
        } else if (components is not null) { fabricApi = components.FabricApiVersion; optiFabric = components.OptiFabricVersion; optiFine ??= components.OptiFineVersion; }
        if (fabricApi is not null) result.Add(create("Fabric API", fabricApi, "Fabric"));
        if (optiFine is not null) result.Add(create("OptiFine", optiFine.Replace("HD_U_", ""), "GrassPath"));
        if (optiFabric is not null) result.Add(create("OptiFabric", optiFabric, "OptiFabric"));
        return result;
    }
    public static Dictionary<string, string?> ReadLocalMods(string modsFolder) {
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        if (!Directory.Exists(modsFolder)) return result;
        string[] files;
        try { files = Directory.EnumerateFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly).Take(1024).ToArray(); }
        catch (IOException) { return result; } catch (UnauthorizedAccessException) { return result; }
        foreach (string path in files) {
            try {
                if (new FileInfo(path).Length > 256L * 1024 * 1024) continue;
                using (ZipArchive archive = ZipFile.OpenRead(path)) {
                    ZipArchiveEntry? metadata = archive.GetEntry("fabric.mod.json");
                    if (metadata is not null && metadata.Length <= 1024 * 1024) {
                        using (Stream stream = metadata.Open()) using (StreamReader reader = new(stream)) {
                            JObject json = JObject.Parse(reader.ReadToEnd()); string? id = json["id"]?.ToString();
                            if (id == "fabric-api" || id == "optifabric") result[id] = json["version"]?.ToString() ?? "未知版本";
                        }
                    }
                    if (archive.GetEntry("optifine/Config.class") is not null || archive.GetEntry("net/optifine/Config.class") is not null) {
                        Match match = Regex.Match(Path.GetFileNameWithoutExtension(path), "HD_U_(.+)$", RegexOptions.IgnoreCase); result["optifine"] = match.Success ? match.Groups[1].Value : null;
                    }
                }
            } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException) { }
        }
        return result;
    }
}
