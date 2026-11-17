using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PCLCS;

// Original LF host mechanism implementation under repository LICENCE; no upstream code is copied.
public sealed record MmcManifestInputAbsence(string ArtifactKey, string JarPath, string JarSha256,
    string OwnedRoot, string Reference, string TargetPath);

/// <summary>Conservative local-file URI subset of JDK manifest Class-Path, with explicit absence facts.</summary>
internal static class MmcManifestClasspath {
    internal static ImmutableArray<MmcManifestInputAbsence> ObserveAbsent(string key, string jar, string sha, string? root, string value, CancellationToken token) {
        var result = ImmutableArray.CreateBuilder<MmcManifestInputAbsence>(); long characters = 0;
        for (int cursor = 0; cursor < value.Length;) {
            while (cursor < value.Length && Delimiter(value[cursor])) cursor++;
            if (cursor == value.Length) break;
            int start = cursor; while (cursor < value.Length && !Delimiter(value[cursor])) cursor++;
            if (result.Count >= 4096 || cursor - start > 8192) throw Fail("Manifest Class-Path token budget exceeded.", true);
            token.ThrowIfCancellationRequested();
            if (root is null) throw Fail("Implicit Class-Path on host support JARs is unsupported.");
            string reference = value[start..cursor], target = Resolve(jar, root, reference);
            characters = checked(characters + reference.Length + target.Length);
            if (characters > 16 * 1024 * 1024) throw Fail("Manifest Class-Path path budget exceeded.", true);
            RequireAbsent(root, target);
            result.Add(new(key, Path.GetFullPath(jar), sha, root, reference, target));
        }
        return result.ToImmutable();
    }
    // StringTokenizer's default delimiters, not .NET's larger Unicode whitespace set.
    private static bool Delimiter(char value) => value is ' ' or '\t' or '\n' or '\r' or '\f';
    internal static string Resolve(string jar, string root, string reference) {
        RejectNetwork(root); RejectNetwork(jar);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); jar = Path.GetFullPath(jar);
        if (!Within(jar, root) || reference.Length is 0 or > 8192 || reference[0] is '/' or '\\' || reference.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#') || reference.Any(Delimiter))
            throw Fail("Manifest Class-Path requires an ordinary relative local-file URI inside the verified owned root.");
        ValidatePercentEscapes(reference);
        if (!Uri.TryCreate(new Uri(new Uri(jar, UriKind.Absolute).AbsoluteUri, UriKind.Absolute), AsciiReference(reference), out Uri? resolved) || !resolved.IsFile || resolved.IsUnc || resolved.Host.Length != 0 || resolved.Query.Length != 0 || resolved.Fragment.Length != 0)
            throw Fail("Manifest Class-Path URI scheme, authority or query/fragment is unsupported.");
        // LocalPath deliberately preserves some escaped reserved characters on .NET
        // (notably %2B/%23). JDK file URL mapping decodes them once before file lookup.
        string decodedPath = Uri.UnescapeDataString(resolved.AbsolutePath);
        if (OperatingSystem.IsWindows() && decodedPath.Length >= 3 && decodedPath[0] == '/' && decodedPath[2] == ':') decodedPath = decodedPath[1..];
        string target = Path.GetFullPath(decodedPath); RejectNetwork(target);
        if (!Within(target, root) || target.Length > 32767) throw Fail("Manifest Class-Path escapes its verified immutable root.");
        string relative = Path.GetRelativePath(root, target);
        string[] segments = relative.Replace('\\', '/').Split('/');
        if (segments.Length > 128) throw Fail("Manifest Class-Path depth budget exceeded.", true);
        foreach (string segment in segments) PhysicalSegment(segment);
        return target;
    }
    private static string AsciiReference(string value) {
        var result = new StringBuilder();
        foreach (byte item in new UTF8Encoding(false,true).GetBytes(value)) {
            if (item < 128) result.Append((char)item);
            else result.Append('%').Append(item.ToString("X2",System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
    private static void ValidatePercentEscapes(string value) {
        for (int cursor = 0; cursor < value.Length;) {
            if (value[cursor] != '%') { cursor++; continue; }
            var bytes = new List<byte>();
            while (cursor < value.Length && value[cursor] == '%') {
                if (cursor + 2 >= value.Length || !Uri.IsHexDigit(value[cursor+1]) || !Uri.IsHexDigit(value[cursor+2])) throw Fail("Invalid manifest URI percent escape.");
                bytes.Add(Convert.ToByte(value.Substring(cursor+1,2),16)); cursor += 3;
            }
            string decoded;
            try { decoded = new UTF8Encoding(false,true).GetString(bytes.ToArray()); }
            catch (DecoderFallbackException) { throw Fail("Manifest URI percent escapes are not valid UTF-8."); }
            if (decoded.Any(c => c is '\0' or '/' or '\\')) throw Fail("Encoded manifest URI separators/NUL are outside the supported profile.");
        }
    }
    internal static void RequireAbsent(string root, string target) {
        RejectNetwork(root); RejectNetwork(target);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); target = Path.GetFullPath(target);
        if (!Within(target,root)) throw Fail("Manifest target escaped its owned role.");
        // Walk from the volume root towards the leaf; never probe below a reparse point.
        string volume = Path.GetPathRoot(root)!; string current = volume;
        RequireDirectory(current);
        foreach (string part in root[volume.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries)) { current=Path.Combine(current,part); RequireDirectory(current); }
        string[] segments = Path.GetRelativePath(root,target).Split(Path.DirectorySeparatorChar);
        for (int index=0; index<segments.Length; index++) {
            current=Path.Combine(current,segments[index]); FileAttributes? attributes=Attributes(current);
            if (attributes is null) return;
            if ((attributes.Value & FileAttributes.ReparsePoint)!=0) throw Fail("Manifest target ancestry contains a symbolic link/reparse point.");
            if (index==segments.Length-1) throw Fail("An implicit manifest input exists; its participation/loading order has not been bound and is unsupported.");
            if ((attributes.Value & FileAttributes.Directory)==0) throw Fail("Manifest target ancestry has a file/directory conflict.");
        }
    }
    private static void RequireDirectory(string path) {
        var attributes=Attributes(path);
        if (attributes is null || (attributes.Value & FileAttributes.Directory)==0 || (attributes.Value & FileAttributes.ReparsePoint)!=0) throw Fail("Verified manifest owned-root ancestry changed or contains a link.");
    }
    private static FileAttributes? Attributes(string path) {
        try{return File.GetAttributes(path);}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}
    }
    private static void RejectNetwork(string path) {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\",StringComparison.Ordinal) || path.StartsWith("//",StringComparison.Ordinal)) throw Fail("Network/UNC/device manifest file paths are unsupported.");
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) throw Fail("Mapped network manifest file volumes are unsupported.");
    }
    private static bool Within(string target,string root) => target.StartsWith(root+Path.DirectorySeparatorChar,MmcRuntimeFiles.PathComparison);
    private static void PhysicalSegment(string value) {
        if (value.Length is 0 or > 255 || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.') || value.Any(c=>c<32 || "<>:\"|?*".Contains(c))) throw Fail("Manifest file URI has an unsupported physical path segment.");
        string stem=value.Split('.')[0];
        if (new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$"}.Contains(stem,StringComparer.OrdinalIgnoreCase) || stem.Length==4 && (stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')) throw Fail("Manifest file URI names a Windows reserved device.");
    }
    private static MmcRuntimeLaunchException Fail(string message,bool budget=false) => new(budget?"MMC_CLASSPATH_MANIFEST_BUDGET":"MMC_CLASSPATH_MANIFEST_INPUT",message);
}
