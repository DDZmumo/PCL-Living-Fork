using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MeloongCore;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
// A live installer task can prove only its own initially absent target. Historical
// folders and other incomplete observations never acquire this narrow permission.
internal static class OrdinaryInstallCleanupProof {
    private sealed class Proof(string root, string? jsonName) {
        internal readonly string Root = root;
        internal readonly string? JsonName = jsonName;
        internal readonly byte[] Marker = Encoding.UTF8.GetBytes("PCLLF.OrdinaryInstall/v1\n" + Guid.NewGuid().ToString("N") + "\n" + root);
        internal string? DirectoryIdentity;
    }
    private static readonly ConditionalWeakTable<object, Proof> proofs = new();
    internal static void Register(object task, string folder, string instanceName) {
        string root = MmcCanonicalPaths.ForTrustedRoot(folder);
        string leaf = Path.GetFileName(root);
        bool sameName = string.Equals(instanceName, leaf, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (MmcInstancePaths.Attributes(root) is null) proofs.Add(task, new(root, sameName ? leaf + ".json" : null));
    }
    internal static void WriteMarker(object task, string folder) {
        const string original = "用于临时地在 PCL 的版本列表中屏蔽此版本。";
        if (!proofs.TryGetValue(task, out var proof) || proof.JsonName is null || !OperatingSystem.IsWindows()) { FileUtils.Write(Path.Combine(folder, ".pclignore"), original); return; }
        lock (proof) {
            Directory.CreateDirectory(folder);
            string marker = Path.Combine(folder, ".pclignore");
            if (proof.DirectoryIdentity is not null) {
                if (!Matches(proof)) throw new IOException("本次普通安装的目录或标识已变化，已保留现场。");
                return;
            }
            if (File.Exists(marker)) return; // An existing marker never becomes this task's nonce.
            string identity = Identity(proof.Root);
            MmcDurableFiles.WriteNew(marker, proof.Marker);
            proof.DirectoryIdentity = identity;
        }
    }
    internal static bool TryAuthorize(IEnumerable<object> tasks, string folder, out bool hasProof, out string? refusal) {
        hasProof = false; refusal = null;
        try {
            string? root = null;
            foreach (object task in tasks) {
                if (!proofs.TryGetValue(task, out var proof)) continue;
                hasProof = true;
                root ??= MmcCanonicalPaths.ForTrustedRoot(folder);
                if (!string.Equals(root, proof.Root, StringComparison.OrdinalIgnoreCase)) continue;
                if (proof.DirectoryIdentity is null || proof.JsonName is null) throw new IOException("本次普通安装尚未取得同名 JSON、目录身份和 nonce 证明，已保留现场。");
                if (!Matches(proof))
                    throw new IOException("普通安装的目标目录身份或 nonce 标识已变化。");
                if (Directory.Exists(Path.Combine(proof.Root, "saves")) || Directory.Exists(Path.Combine(proof.Root, "versions")))
                    throw new IOException("安装目录含有玩家存档或版本目录，已保留。");
                var snapshot = MmcManagementProbe.Capture(proof.Root);
                if (!snapshot.ObservationComplete || snapshot.HasSourceRecords || snapshot.HasPendingTransaction || snapshot.Definition.Exists || snapshot.Definition.Error is not null ||
                    snapshot.ConfigHint.Exists || snapshot.ConfigHint.Error is not null || snapshot.VersionHint.Exists)
                    throw new IOException("安装目录存在 MMC 证据或其它未确认的观察，不能按普通安装清理。");
                if (snapshot.VersionHint.Error is not null) {
                    string json = Path.Combine(proof.Root, proof.JsonName); MmcInstancePaths.EnsureNoLinks(json);
                    using var input = new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length != 0) throw new IOException("未完成的版本 JSON 不是本次安装的空下载目标。");
                    // Only this live task's known empty JSON is treated as absent here.
                    // The global probe and every other management capability remain strict.
                    snapshot = snapshot with { VersionHint = MmcManagementDocument.Missing };
                }
                if (!MmcManagementReader.Evaluate(snapshot).CanUseLegacyBackend) throw new IOException("普通安装清理仍有未确认的管理状态。");
                return true;
            }
            if (hasProof) refusal = "当前安装任务没有绑定此清理目标，已保留现场。";
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or PlatformNotSupportedException) { refusal = error.Message; }
        return false;
    }
    private static bool Matches(Proof proof) {
        string marker = Path.Combine(proof.Root, ".pclignore"); MmcInstancePaths.EnsureNoLinks(marker);
        if (Identity(proof.Root) != proof.DirectoryIdentity) return false;
        using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != proof.Marker.Length) return false;
        byte[] bytes = new byte[proof.Marker.Length]; int offset = 0;
        while (offset < bytes.Length) { int count = input.Read(bytes, offset, bytes.Length - offset); if (count == 0) return false; offset += count; }
        return input.ReadByte() == -1 && bytes.AsSpan().SequenceEqual(proof.Marker);
    }
    private static string Identity(string root) {
        MmcInstancePaths.EnsureNoLinks(root);
        using var handle = CreateFileW(PathUtils.ForApi(root), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new IOException("无法确认本次普通安装的目录身份。", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & (uint)FileAttributes.Directory) == 0 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("普通安装目标目录角色已变化。");
        return $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}:{info.CreationHigh:x8}{info.CreationLow:x8}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct Information {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
}
