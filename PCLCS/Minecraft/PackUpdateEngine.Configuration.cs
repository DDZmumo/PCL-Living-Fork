// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    internal const string OriginalConfiguration = "originalConfiguration";
    private const int ConfigurationStorageBudget = 2 * 1024 * 1024;

    private static bool ConfigurationPath(string path) => PackConfigMerge.Supports(path) && !IsPersonal(path) && !IsPrivate(path);
    private static bool ConfigurationCandidate(TPlan plan, TChange change) =>
        change is { AllowAuthor: true, DisplayOnly: false, ConfigurationMerge: null, Ownership: PackUpdateOwnership.PersonalModified, Incoming: not null,
            BaselineRelative: not null, LocalRelative: not null, Relative: not null } &&
        ConfigurationPath(change.Relative) && string.Equals(change.BaselineRelative, change.Relative, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(change.LocalRelative, change.Relative, StringComparison.OrdinalIgnoreCase) &&
        plan.Baseline?["files"]?[change.BaselineRelative] is JObject record &&
        !Same(record["hash"]?.ToString(), change.Incoming.Hash);

    internal bool NeedsOriginalConfigurations(TPlan plan, CancellationToken token) {
        foreach (TChange change in plan.Changes.Where(change => ConfigurationCandidate(plan, change))) {
            token.ThrowIfCancellationRequested();
            if (ReadCachedConfiguration((JObject)plan.Baseline!["files"]![change.BaselineRelative!]!) is not null ||
                change.Incoming!.Payload is not { } payload) continue;
            byte[]? local = ReadConfiguration(Target(plan.Root!, change.LocalRelative!));
            byte[]? author = ReadConfiguration(payload);
            if (local is not null && author is not null && PackConfigMerge.TryCreate(change.Relative!, local, local, author) is not null) return true;
        }
        return false;
    }

    internal static void RecoverOriginalConfigurations(TPlan plan, TPackage oldPackage, CancellationToken token) {
        int remaining = ConfigurationStorageBudget;
        foreach (TChange change in plan.Changes.Where(change => ConfigurationCandidate(plan, change))) {
            token.ThrowIfCancellationRequested();
            var record = (JObject)plan.Baseline!["files"]![change.BaselineRelative!]!;
            if (ReadCachedConfiguration(record) is not null || !oldPackage.Files.TryGetValue(change.BaselineRelative!, out var original)) continue;
            CaptureOriginalConfiguration(record, original.Payload, ref remaining);
        }
    }

    private static void CaptureOriginalConfigurations(TPackage package, JObject files, Func<TFile, string?>? installed = null) {
        int remaining = ConfigurationStorageBudget;
        foreach (TFile file in package.Files.Values.Where(file => file.Relative is not null && ConfigurationPath(file.Relative)))
            CaptureOriginalConfiguration((JObject)files[RelativePath(file.Relative!)]!, file.Payload ?? installed?.Invoke(file), ref remaining);
    }

    private static void CarryOriginalConfigurations(TPlan plan, JObject files) {
        if (plan.Baseline?["files"] is not JObject baseline) return;
        int remaining = ConfigurationStorageBudget;
        foreach (var entry in files.Properties())
            if (entry.Value[OriginalConfiguration]?.Value<string>() is { } captured) remaining -= Math.Max(1, captured.Length / 4 * 3);
        foreach (var entry in files.Properties()) {
            if (remaining <= 0) break;
            if (entry.Value[OriginalConfiguration] is not null || !ConfigurationPath(entry.Name) || baseline[entry.Name] is not JObject original ||
                !Same(entry.Value["hash"]?.ToString(), original["hash"]?.ToString())) continue;
            if (ReadCachedConfiguration(original) is not { } bytes || bytes.Length > remaining) continue;
            entry.Value[OriginalConfiguration] = original[OriginalConfiguration]!.DeepClone();
            remaining -= Math.Max(1, bytes.Length);
        }
    }

    private static void CaptureOriginalConfiguration(JObject record, string? source, ref int remaining) {
        if (source is null || remaining <= 0) return;
        byte[]? bytes = ReadConfiguration(source);
        if (bytes is null || bytes.Length > remaining || !MatchesConfiguration(bytes, record["hash"]?.ToString())) return;
        record[OriginalConfiguration] = Convert.ToBase64String(bytes);
        remaining -= Math.Max(1, bytes.Length);
    }

    internal void PrepareConfigurationMerges(TPlan plan, string stage, CancellationToken token) {
        foreach (TChange change in plan.Changes.Where(change => ConfigurationCandidate(plan, change))) {
            token.ThrowIfCancellationRequested();
            var record = (JObject)plan.Baseline!["files"]![change.BaselineRelative!]!;
            byte[]? baseline = ReadCachedConfiguration(record);
            byte[]? local = ReadConfiguration(Target(plan.Root!, change.LocalRelative!));
            byte[]? author = change.Incoming!.Payload is { } payload ? ReadConfiguration(payload) : null;
            if (baseline is null || local is null || author is null ||
                !MatchesConfiguration(local, change.Before) || !MatchesConfiguration(author, change.Incoming.Hash)) continue;
            PackConfigMerge? merge = PackConfigMerge.TryCreate(change.Relative!, baseline, local, author);
            if (merge is null) continue;
            change.ConfigurationMerge = merge;
            change.AuthorConfigurationKeys = new(StringComparer.Ordinal);
            change.Conflict = false;
            change.Choice = PackUpdateChoice.UseAuthor;
            change.Reason = merge.Conflicts.Count == 0 ? "合并作者更新与个人配置" : "已合并其余配置项，请核对下方的冲突键";
            plan.ConfigurationStage = stage;
        }
    }

    private static byte[]? ReadCachedConfiguration(JObject record) {
        if (record[OriginalConfiguration] is not JValue { Type: JTokenType.String, Value: string text } ||
            text.Length > (PackConfigMerge.MaximumFileBytes + 2) / 3 * 4) return null;
        try {
            byte[] bytes = Convert.FromBase64String(text);
            return bytes.Length <= PackConfigMerge.MaximumFileBytes && MatchesConfiguration(bytes, record["hash"]?.ToString()) ? bytes : null;
        } catch (FormatException) {
            return null;
        }
    }

    private static byte[]? ReadConfiguration(string path) {
        try {
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
            using var stream = File.OpenRead(path);
            if (stream.Length > PackConfigMerge.MaximumFileBytes) return null;
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            FileTaskProgress.RecordRead(bytes.Length);
            return stream.ReadByte() == -1 ? bytes : null;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private static bool MatchesConfiguration(byte[] content, string? expected) {
        if (expected is null) return false;
        string hash = NormalizeHash(expected);
        string algorithm = hash.Split(':')[0];
        return Same(ConfigurationHash(content, algorithm), hash);
    }

    private static string ConfigurationHash(byte[] content, string algorithm = "sha1") {
        using HashAlgorithm hasher = CreateHashAlgorithm(algorithm);
        return algorithm + ":" + BitConverter.ToString(hasher.ComputeHash(content)).Replace("-", "").ToLowerInvariant();
    }

    private static byte[] RenderConfiguration(TChange change) {
        var merge = change.ConfigurationMerge!;
        var keys = change.AuthorConfigurationKeys ?? throw new IOException("配置合并缺少键值选择。");
        if (keys.Any(key => !merge.Conflicts.Any(conflict => conflict.Key == key)))
            throw new IOException("配置冲突选择已变化，请重新检查更新。");
        return merge.Render(keys);
    }

    private string PrepareConfigurationPayload(TPlan plan, TChange change) {
        string stage = plan.ConfigurationStage ?? throw new IOException("配置合并缺少暂存位置。");
        byte[] bytes = RenderConfiguration(change);
        string hash = ConfigurationHash(bytes, "sha256");
        string file = Target(stage, "merged-config/" + hash["sha256:".Length..]);
        if (File.Exists(file)) {
            if (!Matches(file, hash)) throw new IOException("暂存的配置合并结果已被修改。");
            return file;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes);
        output.Flush(true);
        FileTaskProgress.RecordWrite(bytes.Length);
        return file;
    }
}
