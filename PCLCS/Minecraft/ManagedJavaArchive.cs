using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PCLCS;

// Original LF implementation. See the repository LICENCE.
internal static class ManagedJavaArchive
{
    // JetBrains Runtime SDKs with JCEF can exceed 512 MiB as compressed archives.
    internal const long MaximumBytes = 768L * 1024 * 1024;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 32_768;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);

    internal static async Task DownloadAsync(HttpClient client, JavaRuntimePackage package,
        JavaRuntimeArchive source, string stage, Action<double>? progress, CancellationToken token)
    {
        if (!source.DownloadUri.IsAbsoluteUri || source.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            source.DownloadUri.UserInfo.Length != 0 || !source.HasValidChecksum || package.Size is < 0 or > MaximumBytes)
            throw new InvalidDataException("Java 下载地址、校验值或安装包大小无效。");

        FileTaskProgress.BeginDownload();
        try
        {
            using HttpResponseMessage response = await client.GetAsync(source.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Java 安装包重定向到了非 HTTPS 地址。");

            long expected = response.Content.Headers.ContentLength ?? package.Size;
            if (expected is < 0 or > MaximumBytes || package.Size > 0 && expected != package.Size)
                throw new InvalidDataException("Java 安装包的大小与目录记录不符。");

            MmcInstancePaths.EnsureNoLinks(stage);
            string file = Path.Combine(stage, "runtime.zip");
            await using var archive = new FileStream(file, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = new byte[131_072];
            long count = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(), token).AsTask()
                    .WaitAsync(ReadTimeout, token).ConfigureAwait(false);
                if (read == 0) break;
                FileTaskProgress.RecordDownload(read);
                count += read;
                if (count > MaximumBytes || expected > 0 && count > expected)
                    throw new InvalidDataException("Java 安装包超出了大小限制。");
                await archive.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (expected > 0) progress?.Invoke((double)count / expected);
            }
            if (count == 0 || expected > 0 && count != expected)
                throw new InvalidDataException("Java 安装包下载不完整。");
            await archive.FlushAsync(token).ConfigureAwait(false);
        }
        finally { FileTaskProgress.EndDownload(); }
        progress?.Invoke(1);
    }

    internal static async Task VerifyAndExtractAsync(string stage, string payload, JavaRuntimePackage package,
        JavaRuntimeArchive source, Action<double>? progress, CancellationToken token)
    {
        string file = Path.Combine(stage, "runtime.zip");
        MmcInstancePaths.EnsureNoLinks(file);
        await using var archive = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.None, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archive.Length is <= 0 or > MaximumBytes || package.Size > 0 && archive.Length != package.Size)
            throw new InvalidDataException("Java 安装包的大小与目录记录不符。");

        using IncrementalHash hash = IncrementalHash.CreateHash(source.Sha512 is null ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512);
        byte[] buffer = new byte[131_072];
        long checkedBytes = 0;
        int read;
        while ((read = await archive.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            FileTaskProgress.RecordRead(read);
            checkedBytes += read;
            progress?.Invoke(.15 * checkedBytes / archive.Length);
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), source.Sha512 ?? source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Java 安装包的校验值不匹配。");

        token.ThrowIfCancellationRequested();
        archive.Position = 0;
        await ExtractAsync(archive, payload, buffer, progress, token).ConfigureAwait(false);
    }

    private static async Task ExtractAsync(FileStream file, string payload, byte[] buffer, Action<double>? progress, CancellationToken token)
    {
        int entries = MmcArtifactMaterializer.PreflightArchive(file, MaximumEntries, MaximumExpandedBytes, Encoding.UTF8,
            allowZip64EntrySizes: true, maximumEntryBytes: MaximumExpandedBytes);
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        if (zip.Entries.Count != entries) throw new InvalidDataException("Java 安装包目录记录不一致。");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>(entries);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            bool directory = name.EndsWith('/');
            if (directory) name = name[..^1];
            int mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                mode != 0 && mode != (directory ? 0x4000 : 0x8000) || directory && entry.Length != 0)
                throw new InvalidDataException("Java 安装包包含链接或非预期文件。");
            string target = ResolveInside(payload, name);
            if (!paths.Add(target) || entry.Length < 0 || entry.Length > MaximumExpandedBytes - total)
                throw new InvalidDataException("Java 安装包包含重复路径或超出解压大小限制。");
            total += entry.Length;
            targets.Add((entry, target, directory));
        }

        long done = 0;
        foreach (var (entry, target, directory) in targets)
        {
            token.ThrowIfCancellationRequested();
            MmcInstancePaths.EnsureNoLinks(target);
            if (directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using Stream data = entry.Open();
            long remaining = entry.Length;
            int read;
            while ((read = await data.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                remaining -= read;
                if (remaining < 0) throw new InvalidDataException("Java 安装包中的文件超出声明大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                FileTaskProgress.RecordRead(read);
                FileTaskProgress.RecordWrite(read);
                done += read;
                if (total > 0) progress?.Invoke(.15 + .8 * done / total);
            }
            if (remaining != 0) throw new InvalidDataException("Java 安装包中的文件不完整。");
        }
    }

    internal static string ResolveInside(string directory, string relative)
    {
        if (relative.Length is 0 or > 4096 || Path.IsPathRooted(relative) || relative[0] is '/' or '\\')
            throw new InvalidDataException("Java 安装包中包含无效路径。");
        MmcInstancePaths.ValidateSegments(relative);
        string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Java 安装包中文件路径超出目标目录。");
        return path;
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool IsSha512(string? value) => value is { Length: 128 } && value.All(Uri.IsHexDigit);
}
