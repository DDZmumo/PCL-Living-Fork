// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using MeloongCore;
using PCLLF.Presentation.Accounts;
using PCLLF.Presentation.Launch;

namespace PCL;

internal static class LauncherSkinAvatars {
    private sealed record Head(LegacySkinRequest Request, BitmapSource Image, string? TextureFile);
    private static readonly ConcurrentDictionary<string, Head> heads = new(StringComparer.Ordinal);

    internal static BitmapSource? Read(string kind, LegacySkinRequest request) =>
        heads.TryGetValue(kind, out var head) && head.Request == request ? head.Image : null;

    internal static string? ReadTexture(string kind, LegacySkinRequest request) =>
        heads.TryGetValue(kind, out var head) && head.Request == request ? head.TextureFile : null;

    internal static bool Publish(string kind, LegacySkinRequest request, string path, bool fetched, Func<bool> isCurrent) {
        Render(kind, request, path, fetched, isCurrent);
        return true;
    }

    private static void Render(string kind, LegacySkinRequest request, string path, bool fetched, Func<bool> isCurrent) {
        try {
            bool hasTexture = fetched || kind == "Legacy" && request.Name != "3";
            BitmapSource? image = hasTexture ? null : GlassSkinHead.LastGood(kind, request);
            if (image is null) {
                var (back, fore) = MinecraftSkinHead.Crop(new MyBitmap(path));
                image = GlassSkinHead.Render(back, fore);
            }
            if (image is null || !isCurrent()) return;
            heads[kind] = new Head(request, image, hasTexture ? path : null);
            if (fetched) GlassSkinHead.Remember(kind, request, image);
        } catch (Exception error) {
            Logger.Warn(error, "载入账号头像失败");
        }
    }
}
