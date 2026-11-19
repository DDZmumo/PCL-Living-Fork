// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS.Applications;

namespace PCLLF.Native.Facade.Tests;

internal static class SkinLibraryChecks {
    internal static void Run(FixtureEnvironment environment) {
        string root = Path.Combine(environment.Data, "SkinLibrary");
        var store = new SkinLibraryStore(root, png => {
            using var input = new MemoryStream(png);
            _ = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        });
        byte[] skin = Png(64, 64), cape = Png(64, 32);
        var first = store.ImportTexture("Explorer", skin, model: SkinModel.Slim);
        var duplicate = store.ImportTexture("Explorer", skin);
        var capeEntry = store.ImportTexture("Cloak", cape, SkinTextureKind.Cape);
        Native.Check(first.Id != duplicate.Id && store.List().Count == 2 && store.List(SkinTextureKind.Cape).Count == 1, "Skin and cape directories/duplicate import.");
        string bad = Path.Combine(environment.Data, "invalid.png");
        File.WriteAllBytes(bad, Png(16, 16));
        Native.Check(store.Import([bad]).Errors.Count == 1, "Invalid skin dimensions were accepted.");
        var preview = (SkinLibraryPreview)Native.Call(typeof(SkinLibraryApplicationService), "Preview", null, store.ReadTexture(first.Id), SkinTextureKind.Skin)!;
        Native.Check(preview.Texture.IsFrozen && preview.Avatar is { IsFrozen: true } && preview.Model == SkinModel.Slim, "WPF preview did not reuse the head renderer and model.");

        store.SetOfflineTexture("Explorer", SkinTextureKind.Skin, first.Id);
        store.SetOfflineTexture("Explorer", SkinTextureKind.Cape, capeEntry.Id);
        store.SetOfflineTexture("Second", SkinTextureKind.Skin, duplicate.Id);
        var renamed = store.Rename(first.Id, "Explorer renamed");
        store.Rename(duplicate.Id, "explorer (2)");
        var reopened = new SkinLibraryStore(root, _ => { });
        Native.Check(reopened.GetOfflineAppearance("Explorer").SkinId == renamed.Id && reopened.GetOfflineAppearance("Second").SkinId == "explorer (2).png" &&
            reopened.ReadTexture(renamed.Id).Model == SkinModel.Slim, "Rename and restart lost player-specific assignments or model.");
        var appearance = new LocalSkinLaunch("Explorer", Guid.NewGuid(), reopened.ReadOfflineTextures("Explorer")!);
        store.Remove(renamed.Id);
        Native.Check(store.GetOfflineAppearance("Explorer") == new OfflineSkinAppearance(null, capeEntry.Id) && store.GetOfflineAppearance("Second").SkinId is not null,
            "Deleting a skin disturbed unrelated assignments.");
        Task.Run(() => CheckWorkerAsync(appearance, skin, cape)).GetAwaiter().GetResult();
        CheckMicrosoftCapeRefresh();
    }

    private static void CheckMicrosoftCapeRefresh() {
        var target = new SkinLibraryTarget(AccountProvider.Microsoft, "Explorer", Guid.NewGuid().ToString("N"), "");
        IReadOnlyList<MicrosoftCape> cached = [new("old", "Old cape", false, null)];
        IReadOnlyList<MicrosoftCape> latest = [new("new", "New cape", true, null)];
        int requests = 0;
        Func<SkinLibraryTarget, CancellationToken, Task<IReadOnlyList<MicrosoftCape>>> fetch = (actual, token) => {
            Native.Check(actual == target && !token.IsCancellationRequested, "Cape refresh used the wrong account or canceled request.");
            requests++;
            return Task.FromResult(latest);
        };
        Type type = Native.Type("PCL.GlassSkinLibrary");
        using var library = (IDisposable)Activator.CreateInstance(type, Native.Flags, null, [(Func<bool>)(() => false), (Action)(() => { }), fetch], null)!;
        Native.Set(library, "_account", new SkinLibraryAccount(target, null, false, new(), null));
        Native.Set(library, "_kind", SkinTextureKind.Cape);
        Native.Set(library, "_microsoftCapes", cached);
        var scene = (FrameworkElement)Native.Property(type, "Scene").GetValue(library)!;
        Button refresh = Buttons(scene).Single(button => AutomationProperties.GetName(button) == "刷新");
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Native.Check(requests == 1 && ReferenceEquals(Native.Get(library, "_microsoftCapes"), latest), "Refresh retained the cached Microsoft cape list.");
    }

    private static IEnumerable<Button> Buttons(DependencyObject root) {
        if (root is Button button) yield return button;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (Button child in Buttons(VisualTreeHelper.GetChild(root, index))) yield return child;
        foreach (FrameworkElement logical in LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>())
            if (VisualTreeHelper.GetParent(logical) is null)
                foreach (Button child in Buttons(logical)) yield return child;
    }

    private static async Task CheckWorkerAsync(LocalSkinLaunch appearance, byte[] skin, byte[] cape) {
        string executable = Path.Combine(AppContext.BaseDirectory, "LivingPCL.exe");
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q") {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true
        };
        using var game = Process.Start(start)!;
        Uri endpoint;
        try {
            using (var service = await LocalSkinServiceProcess.StartAsync(executable, appearance, token.Token)) {
                endpoint = service.Endpoint.Address;
                service.Attach(game, token.Token);
            }
            using var metadata = JsonDocument.Parse(await http.GetStringAsync(endpoint, token.Token));
            using var profile = JsonDocument.Parse(await http.GetStringAsync(new Uri(endpoint,
                $"sessionserver/session/minecraft/profile/{appearance.ProfileId:N}?unsigned=false"), token.Token));
            var property = profile.RootElement.GetProperty("properties")[0];
            string value = property.GetProperty("value").GetString()!;
            using var key = RSA.Create();
            key.ImportFromPem(metadata.RootElement.GetProperty("signaturePublickey").GetString()!);
            Native.Check(key.VerifyData(Encoding.ASCII.GetBytes(value), Convert.FromBase64String(property.GetProperty("signature").GetString()!),
                HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1), "Yggdrasil texture signature is invalid.");
            using var decoded = JsonDocument.Parse(Convert.FromBase64String(value));
            var textures = decoded.RootElement.GetProperty("textures");
            Native.Check(decoded.RootElement.GetProperty("profileId").GetString() == appearance.ProfileId.ToString("N") &&
                textures.GetProperty("SKIN").GetProperty("metadata").GetProperty("model").GetString() == "slim", "Game profile identity or model changed.");
            foreach (var (type, png) in new[] { ("SKIN", skin), ("CAPE", cape) }) {
                var address = new Uri(textures.GetProperty(type).GetProperty("url").GetString()!);
                using var response = await http.GetAsync(address, token.Token);
                Native.Check(endpoint.IsBaseOf(address) && address.Segments.Last() == Convert.ToHexStringLower(SHA256.HashData(png)) &&
                    response.Content.Headers.ContentType?.MediaType == "image/png" && (await response.Content.ReadAsByteArrayAsync(token.Token)).SequenceEqual(png),
                    "Game did not receive the selected texture bytes.");
            }
            using var other = await http.GetAsync(new Uri(endpoint, $"sessionserver/session/minecraft/profile/{Guid.NewGuid():N}"), token.Token);
            using var outside = await http.GetAsync(new Uri(endpoint, "/"), token.Token);
            Native.Check(other.StatusCode == HttpStatusCode.NoContent && outside.StatusCode == HttpStatusCode.NotFound, "Loopback service exposed an unassigned profile or route.");
        } finally {
            game.StandardInput.Close();
            if (!game.WaitForExit(4000)) game.Kill();
            await game.WaitForExitAsync(token.Token);
        }

        for (int attempt = 0; attempt < 20; attempt++) {
            try { using var result = await http.GetAsync(endpoint, token.Token); }
            catch (HttpRequestException) { return; }
            await Task.Delay(100, token.Token);
        }
        throw new IOException("Skin service continued to listen after its game process exited.");
    }

    private static byte[] Png(int width, int height) {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
