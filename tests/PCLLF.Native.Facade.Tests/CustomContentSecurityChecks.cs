// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MeloongCore.Extensions;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class CustomContentSecurityChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "custom-content-security");
        CheckMarkup();
        CheckActions();
        CheckImageSources();
        CheckImageLayout();
        CheckImageCache(environment.Data);
        CheckResponseLimit().GetAwaiter().GetResult();
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS custom content: shipped help, homepage controls, action and image policies, bounded HTTP responses.");
    }

    private static void CheckMarkup()
    {
        var view = (StackPanel)LauncherMarkup.ParseFragment("""
            <StackPanel>
                <StackPanel.Resources>
                    <Style TargetType="local:MyIconTextButton" x:Key="Link">
                        <Setter Property="EventType" Value="打开网页" />
                        <Setter Property="EventData" Value="https://example.invalid/" />
                    </Style>
                </StackPanel.Resources>
                <local:MyCard Title="Fixture" CanSwap="False" IsSwaped="False" />
                <local:MyIconTextButton Style="{StaticResource Link}" Text="Link" />
                <local:MyLoading Text="Loading" />
                <Image Source="https://example.invalid/image.png" />
            </StackPanel>
            """);
        var panel = (StackPanel)view.Children[0];
        Native.Check(panel.Children[0] is LauncherMarkupCard && panel.Children[1] is LauncherMarkupIconTextButton &&
            panel.Children[2] is LauncherMarkupLoading && panel.Children[3] is LauncherMarkupImage { Source: "https://example.invalid/image.png" },
            "The restricted homepage loader discarded compatible controls or used the native image loader.");
        Native.Check(((Image)panel.Children[3]).Source is null, "A detached homepage image started native URI loading.");
        Native.Check(CustomEventService.GetEventType((DependencyObject)panel.Children[1]) == CustomEvent.EventType.打开网页,
            "An allowed command disappeared from its authored style.");
        try {
            LauncherMarkup.ParseFragment("<UserControl />");
            throw new InvalidOperationException("An unlisted control entered the custom content tree.");
        } catch (InvalidDataException error) {
            Native.Check(!error.GetDisplay(false).Contains("程序权限不足", StringComparison.Ordinal), "A markup rejection was reported as a filesystem access error.");
        }
        var decorated = (StackPanel)LauncherMarkup.ParseFragment("""
            <TextBlock Text="News"><TextBlock.Effect>
                <DropShadowEffect Color="#303030" BlurRadius="8" ShadowDepth="0" Opacity="0.5" />
            </TextBlock.Effect></TextBlock>
            <TextBlock Text="Blurred"><TextBlock.Effect><BlurEffect Radius="4" /></TextBlock.Effect></TextBlock>
            """);
        Native.Check(((TextBlock)decorated.Children[0]).Effect is DropShadowEffect { BlurRadius: 8 } &&
            ((TextBlock)decorated.Children[1]).Effect is BlurEffect { Radius: 4 },
            "The restricted homepage loader did not render supported text effects.");
        try {
            LauncherMarkup.PrepareFragment("""<TextBlock><TextBlock.Effect><BlurEffect Radius="10000" /></TextBlock.Effect></TextBlock>""");
            throw new InvalidOperationException("An out-of-range decoration passed markup validation.");
        } catch (InvalidDataException) { }
        var thumbnail = (StackPanel)LauncherMarkup.ParseFragment("""
            <Border Width="75" Height="75" CornerRadius="6">
                <Border.Background><ImageBrush ImageSource="https://example.invalid/safe.png" Stretch="UniformToFill" /></Border.Background>
            </Border>
            """);
        Native.Check(thumbnail.Children[0] is Border { Child: LauncherMarkupImage { CornerRadius: 6, Stretch: System.Windows.Media.Stretch.UniformToFill } },
            "A rounded homepage thumbnail did not use the controlled image loader.");
        try {
            LauncherMarkup.PrepareFragment("""<Border><Border.Background><ImageBrush ImageSource="unavailable.png" /></Border.Background></Border>""");
            throw new InvalidOperationException("An unchecked brush source passed markup validation.");
        } catch (InvalidDataException) { }

        var manager = (ResourceManager)Native.Property(Native.Type("PCL.My.Resources.Resources"), "ResourceManager").GetValue(null)!;
        using var zip = new ZipArchive(new MemoryStream((byte[])manager.GetObject("Help", CultureInfo.InvariantCulture)!));
        int count = 0;
        foreach (ZipArchiveEntry entry in zip.Entries.Where(item => item.Name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))) {
            using var reader = new StreamReader(entry.Open());
            try { LauncherMarkup.ParseFragment(reader.ReadToEnd()); }
            catch (Exception ex) { throw new InvalidOperationException("Bundled help no longer loads: " + entry.FullName, ex); }
            count++;
        }
        Native.Check(count > 0, "The product help archive had no XAML to verify.");
        if (Environment.GetEnvironmentVariable("PCLLF_HOME_MARKUP_FIXTURE") is { Length: > 0 } sample) {
            var fixture = (StackPanel)LauncherMarkup.ParseFragment(LauncherContentDownload.ReadFile(sample, LauncherContentDownload.MarkupBytes));
            Native.Check(fixture.Children.Count > 0, "The cached homepage rendered no controls.");
            Console.WriteLine("Verified the supplied homepage.");
        }
        Console.WriteLine($"Verified {count} built-in help fragments.");
    }

    private static void CheckActions()
    {
        Native.Check(LauncherContentActions.Allows(CustomEvent.EventType.打开帮助, "https://example.invalid/topic.json?version=1") &&
            LauncherContentActions.Allows(CustomEvent.EventType.打开网页, "https://example.invalid/") &&
            LauncherContentActions.Allows(CustomEvent.EventType.启动游戏, "1.21.11") &&
            LauncherContentActions.Allows(CustomEvent.EventType.修改设置, "UiPanelOpacity|850"), "A supported custom page action was blocked.");
        Native.Check(!LauncherContentActions.Allows(CustomEvent.EventType.写入设置, "UnknownPreference|0") &&
            !LauncherContentActions.Allows(CustomEvent.EventType.写入设置, "UiPanelOpacity|1001") &&
            !LauncherContentActions.Allows(CustomEvent.EventType.打开文件, "notes.txt"), "An external action exceeded its allowlist.");
    }

    private static void CheckImageSources() => Native.Check(
        LauncherMarkupImage.EmbeddedImageAddress(ModBase.PathImage + "Blocks/Grass.png") is not null &&
        LauncherMarkupImage.EmbeddedImageAddress("image.png") is null &&
        LauncherContentDownload.IsHttps(new Uri("https://example.invalid/image.png")) &&
        !LauncherContentDownload.IsHttps(new Uri("http://example.invalid/image.png")), "Custom image origins no longer match the content policy.");

    private static void CheckImageLayout()
    {
        var stripe = new byte[160 * 90 * 4];
        for (int y = 0; y < 90; y++)
        for (int x = 0; x < 160; x++) {
            int pixel = (y * 160 + x) * 4;
            stripe[pixel] = (byte)(x * 255 / 160);
            stripe[pixel + 2] = (byte)(y * 255 / 90);
            stripe[pixel + 3] = 255;
        }
        var image = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, stripe, 160 * 4);
        var authored = (StackPanel)LauncherMarkup.ParseFragment("""
            <StackPanel>
                <StackPanel.Resources><Style x:Key="VersionImage" TargetType="local:MyImage">
                    <Setter Property="Stretch" Value="UniformToFill" />
                    <Setter Property="VerticalAlignment" Value="Center" />
                    <Setter Property="EnableCache" Value="False" />
                </Style></StackPanel.Resources>
                <Border BorderThickness="4" BorderBrush="White" CornerRadius="7" MaxHeight="140">
                    <local:MyImage Style="{StaticResource VersionImage}" />
                </Border>
            </StackPanel>
            """);
        var tested = (Border)((StackPanel)authored.Children[0]).Children[0];
        var control = (LauncherMarkupImage)tested.Child;
        ((Image)control).Source = image;
        Native.Check(!control.EnableCache && control.Stretch == Stretch.UniformToFill &&
            control.LoadingSource?.EndsWith("NoIcon.png", StringComparison.Ordinal) == true,
            "The original MyImage style or loading placeholder was lost.");
        var reference = new Border { BorderThickness = new Thickness(4), BorderBrush = Brushes.White, CornerRadius = new CornerRadius(7),
            MaxHeight = 140, Child = new Image { Source = image, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center } };
        foreach (int width in new[] { 380, 740 })
            Native.Check(Render(tested, width).SequenceEqual(Render(reference, width)), $"News artwork crops differently from a native Image at width {width}.");

        static byte[] Render(Border frame, int width)
        {
            var size = new Size(width, 140);
            frame.Measure(size);
            frame.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap(width, 140, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var pixels = new byte[width * 140 * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }
    }

    private static void CheckImageCache(string root)
    {
        var purePath = Native.Field(typeof(ModBase), "PathPure");
        object? previous = purePath.GetValue(null);
        purePath.SetValue(null, new Lazy<string>(() => Path.Combine(root, "homepage-image-native") + Path.DirectorySeparatorChar));
        try { CheckRemoteImages(root); }
        finally { purePath.SetValue(null, previous); }
    }

    private static void CheckRemoteImages(string root)
    {
        const string primary = "https://example.invalid/news.jpg", fallback = "https://example.invalid/news.webp";
        byte[] webp = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvBcAAAAdQmUrXov+BiOh/AAA=");
        var request = new ImageResponses(webp);
        using var network = new HttpClient(request);
        string cache = LauncherMarkupImages.CachePath(root, primary);
        var image = LauncherMarkupImages.DownloadAsync(primary, fallback, cache, default, network).GetAwaiter().GetResult();
        Native.Check(image is { PixelWidth: 6, PixelHeight: 4, IsFrozen: true } && request.Addresses.SequenceEqual(new[] { primary, fallback }),
            "The original backup source did not decode a WebP image after the main source failed.");
        var hit = LauncherMarkupImages.ReadCacheAsync(cache, default).GetAwaiter().GetResult();
        Native.Check(hit.Fresh && hit.Image is { PixelWidth: 6, PixelHeight: 4 }, "The next instance could not reuse the fresh disk cache.");
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-8));
        var stale = LauncherMarkupImages.ReadCacheAsync(cache, default).GetAwaiter().GetResult();
        Native.Check(!stale.Fresh && stale.Image is { PixelWidth: 6 }, "An expired image could not display during refresh.");
        using var unavailable = new HttpClient(new ImageResponses(null));
        Native.Check(LauncherMarkupImages.DownloadAsync(primary, fallback, cache, default, unavailable).GetAwaiter().GetResult() is null &&
            File.ReadAllBytes(cache).SequenceEqual(webp), "A failed refresh destroyed the previous image.");
    }

    private sealed class ImageResponses(byte[]? fallbackBytes) : HttpMessageHandler
    {
        internal List<string> Addresses { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Addresses.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(fallbackBytes is not null && request.RequestUri.AbsolutePath.EndsWith(".webp", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fallbackBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static async Task CheckResponseLimit()
    {
        const int limit = 4096;
        using var acceptable = new HttpClient(new Response(new ByteArrayContent(new byte[limit])));
        byte[] bytes = await LauncherContentDownload.FetchAsync("https://example.invalid/", limit, transport: acceptable).ConfigureAwait(false);
        Native.Check(bytes.Length == limit, "A response that fit the limit was discarded.");
        using var stream = new ForwardOnlyStream(new byte[2 * limit]);
        using var tooLarge = new HttpClient(new Response(new StreamContent(stream)));
        try {
            await LauncherContentDownload.FetchAsync("https://example.invalid/", limit, transport: tooLarge).ConfigureAwait(false);
            throw new InvalidOperationException("A response without Content-Length exceeded its size limit.");
        } catch (InvalidDataException) { }
        Native.Check(stream.BytesRead == limit + 1, "The reader consumed more data after the decision to reject the response.");
    }

    private sealed class Response(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        internal int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            int count = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += count;
            return count;
        }
    }
}
