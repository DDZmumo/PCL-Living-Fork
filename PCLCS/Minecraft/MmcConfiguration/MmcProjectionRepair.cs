using System;
using System.IO;
using System.Linq;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies. Core remains MPL-2.0.
public sealed partial class MmcConfigurationQuiescence {
    /// <summary>
    /// Rebuild missing/damaged compatibility projections from a separately verified primary definition.
    /// This does not authorize reconstructing source facts from markers or bypass pending/session gates.
    /// </summary>
    public MmcConfigurationWriteScope OpenProjectionRepair(MmcAdmissionLease lease, long expectedGeneration, string expectedPrimarySha256) {
        ObjectDisposedException.ThrowIf(disposed, this); lease.AssertHeld();
        if (expectedGeneration < 1 || !MmcDurableFiles.IsHash(expectedPrimarySha256)) throw new IOException("投影修复必须提供主定义代数及 SHA256。");
        MmcInstancePaths.Resolve(logicalRoot);
        if (!string.Equals(lease.VersionRoot, State.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("投影修复目录与准入租约不一致。");
        return new(State, lease, expectedGeneration, false, expectedPrimarySha256);
    }
}

public sealed partial class MmcConfigurationWriteScope {
    private static void ValidateRepairPrimary(string path, string hash, Guid instanceId, long generation) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("投影修复主定义超过读取预算。");
        using MemoryStream memory = new(); byte[] buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
            if (memory.Length + count > MmcManagementReader.MaximumDocumentBytes) throw new IOException("投影修复主定义读取期间超过预算。");
            memory.Write(buffer, 0, count);
        }
        byte[] bytes = memory.ToArray();
        if (MmcDurableFiles.Hash(bytes) != hash) throw new IOException("主定义已变化，拒绝以其他字节作为修复来源。");
        ValidatePublishedDefinition(bytes, instanceId, generation);
    }
    private static void ValidatePublishedDefinition(byte[] bytes, Guid instanceId, long generation) {
        MmcDefinitionReadResult read;
        try { read = MmcDefinitionCodec.Read(new UTF8Encoding(false, true).GetString(bytes)); }
        catch (DecoderFallbackException error) { throw new IOException("主定义编码无效。", error); }
        if (read.Status != MmcDefinitionReadStatus.Valid || read.Definition is null || read.Definition.InstanceId != instanceId || read.Definition.Generation != generation)
            throw new IOException("主定义未通过 Core 发布态校验，不能从投影提示推导来源。");
    }
    private void ValidateRepairPublication(MmcFileTransactionRequest request) {
        if (repairPrimaryHash is null) return;
        MmcFileMutation? primary = request.Mutations.SingleOrDefault(item => item.RelativePath == "PCL/InstanceDefinition.json");
        if (primary?.NewContent is null || primary.ExpectedOldSha256 != repairPrimaryHash)
            throw new IOException("投影修复提交必须继续绑定已验证主定义的旧摘要。");
        ValidatePublishedDefinition(primary.NewContent, lease.InstanceId, request.NewGeneration);
    }
}
