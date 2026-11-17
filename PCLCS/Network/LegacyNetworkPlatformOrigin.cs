using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PCLCS.Loaders;
using PCLLF.MMCC.Core;

namespace PCLCS.Network;

// Original LF host integration; governed by the repository LICENCE.
public sealed partial class LegacyNetworkRuntime<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TSource : LegacyNetSourceData<TThread>, new()
    where TThread : LegacyNetThreadData<TFile, TSource, TThread, TNetState>, new()
    where TFile : LegacyNetFile<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TDownload : TLoader, ILegacyNetworkDownload<TFile>
    where TManager : LegacyNetManager<TSource, TThread, TFile, TDownload, TManager, TLoader, TNetState, TLoadState, TLoadingState>
    where TLoader : LegacyLoaderBase<TLoader, TLoadState, TLoadingState>
    where TNetState : struct, Enum where TLoadState : struct, Enum {
    private HttpClient? platformOriginMetadataClient;

    /// <summary>Same LF header signer/cache infrastructure, with strict system TLS and no redirects or source rewriting.</summary>
    public MmcPlatformOriginCatalog CreateMmcPlatformOriginCatalog() => new(ReadOfficialPlatformMetadataAsync, MmcPlatformApiPolicy.Production);

    private async Task<string> ReadOfficialPlatformMetadataAsync(Uri address, CancellationToken token) {
        if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length != 0 || address.Fragment.Length != 0 ||
            !(address.Host == "api.modrinth.com" && address.AbsolutePath.StartsWith("/v2/", StringComparison.Ordinal) ||
              address.Host == "api.curseforge.com" && address.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal)))
            throw new IOException("Platform identity metadata requires the fixed official API endpoint.");
        HttpClient client;
        lock (requestClientLock) {
            // Intentionally bypass the launcher's permissive global certificate callback and redirect wrapper.
            // This is metadata transport only; all package downloads remain on the existing NetFile batch.
            platformOriginMetadataClient ??= Host.CreateCachedClient(Host.TempFolder() + "Cache/Http/PlatformOrigin/", new HttpClientHandler {
                AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
            client = platformOriginMetadataClient;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpRequestMessage request = new(HttpMethod.Get, address);
        try {
            Host.SignHeaders(address.AbsoluteUri, ref request, false, true);
            if (request.RequestUri?.AbsoluteUri != address.AbsoluteUri || request.Method != HttpMethod.Get) throw new IOException("The official metadata request was rewritten.");
            request.Headers.Accept.Clear(); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MaxAge = TimeSpan.Zero };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.AbsoluteUri != address.AbsoluteUri)
                throw new IOException("The official platform metadata endpoint is unavailable or redirected.");
            if (response.Content.Headers.ContentLength > MmcPlatformOriginCodec.MaximumMetadataBytes) throw new IOException("Platform metadata exceeds its byte budget.");
            using var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false); using MemoryStream output = new();
            byte[] buffer = new byte[8192]; int count;
            while ((count = await content.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0) {
                if (output.Length + count > MmcPlatformOriginCodec.MaximumMetadataBytes) throw new IOException("Platform metadata grew beyond its byte budget.");
                output.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested(); return new UTF8Encoding(false, true).GetString(output.ToArray());
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The official platform metadata request timed out."); }
        catch (HttpRequestException) { throw new IOException("The official platform metadata request failed."); }
        finally { request.Dispose(); }
    }
}
