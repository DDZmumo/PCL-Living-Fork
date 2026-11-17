using System.Text;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>Only a proven exit or a proven pre-launch failure may retire mutable runtime files.</summary>
public static class MmcSessionRuntimeDirectories {
    private const string MarkerName = "owner.txt";
    private const int MaximumEntries = 65_536;
    // Preserve space for native filenames/subfolders used by legacy Win32 loaders. This is
    // intentionally stricter than .NET's extended-path support, which LoadLibraryW may not use.
    public const int WindowsNativeSuffixBudget = 128;
    private const int WindowsLegacyPathLimit = 260;

    public static MmcMutableRuntimePaths Prepare(MmcInstancePaths paths,Guid instanceId,Guid sessionId,string boundFingerprint,MmcMutableRuntimeAuthority? authority=null){
        var described=Describe(paths,instanceId,sessionId,boundFingerprint,authority);CreateOwned(paths,instanceId,sessionId,described.RuntimeBinding,authority);return described;
    }
    internal static MmcMutableRuntimePaths Describe(MmcInstancePaths paths,Guid instanceId,Guid sessionId,string fingerprint,MmcMutableRuntimeAuthority? authority=null,bool containsAuthenticationAgent=false){
        var binding=new MmcSessionRuntimeBinding(fingerprint);ValidateBinding(sessionId,binding);if(instanceId==Guid.Empty)throw new IOException("Mutable instance identity is missing.");
        var inside=Paths(paths,instanceId,sessionId,binding,authority);
        if((OperatingSystem.IsWindows()&&Math.Max(inside.ExtractDirectory.Length,inside.TemporaryDirectory.Length)+1+WindowsNativeSuffixBudget>=WindowsLegacyPathLimit)||
            containsAuthenticationAgent&&(inside.SessionRoot.Contains('=')||OperatingSystem.IsWindows()&&Encoding.UTF8.GetByteCount(Path.Combine(inside.SessionRoot,"a.jar"))+1>=WindowsLegacyPathLimit))
            binding=binding with{MutableLayout=MmcMutableRuntimeLayout.LocalAppDataV2,AuthorityFingerprint=(authority??MmcMutableRuntimeAuthority.Default).Fingerprint};
        var result=Paths(paths,instanceId,sessionId,binding,authority);
        if(OperatingSystem.IsWindows()&&Math.Max(result.ExtractDirectory.Length,result.TemporaryDirectory.Length)+1+WindowsNativeSuffixBudget>=WindowsLegacyPathLimit)
            throw new IOException("Controlled mutable runtime scope is too long for the native-loader path budget.");
        if(OperatingSystem.IsWindows()&&containsAuthenticationAgent&&Encoding.UTF8.GetByteCount(Path.Combine(result.SessionRoot,"a.jar"))+1>=WindowsLegacyPathLimit)
            throw new IOException("Controlled authentication execution scope is too long for the native-agent path budget.");
        // -javaagent uses the first '=' as a path/options separator with no escaping.
        // A host authority rooted under such a directory cannot emit a valid token.
        if(containsAuthenticationAgent&&result.SessionRoot.Contains('='))
            throw new IOException("Controlled authentication execution scope contains the javaagent option separator.");
        return result;
    }
    internal static void CreateOwned(MmcInstancePaths paths,Guid instanceId,Guid sessionId,MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority=null){
        var selected=Paths(paths,instanceId,sessionId,binding,authority);var host=authority??MmcMutableRuntimeAuthority.Default;
        if(binding.MutableLayout is not null)host.EnsureParents(instanceId);
        else{string parent=Path.GetDirectoryName(selected.SessionRoot)!;MmcInstancePaths.EnsureNoLinks(parent);Directory.CreateDirectory(parent);MmcInstancePaths.EnsureNoLinks(parent);}
        if(MmcInstancePaths.Attributes(Retired(paths,instanceId,sessionId,binding,host))is not null)throw new IOException("Mutable session ID was already retired.");
        MmcMutableRuntimeAuthority.CreateExclusive(selected.SessionRoot);
        MmcDurableFiles.WriteNew(Path.Combine(selected.SessionRoot,MarkerName),Encoding.UTF8.GetBytes(Owner(paths,instanceId,sessionId,binding)));
        Directory.CreateDirectory(selected.TemporaryDirectory);Directory.CreateDirectory(selected.ExtractDirectory);
        ValidateLaunch(paths,instanceId,sessionId,binding,host);
    }
    internal static MmcMutableRuntimePaths Paths(MmcInstancePaths paths,Guid instanceId,Guid sessionId,MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority=null){
        ValidateBinding(sessionId,binding);if(instanceId==Guid.Empty)throw new IOException("Mutable instance identity is missing.");
        if(binding.MutableLayout is not null&&binding.AuthorityFingerprint!=(authority??MmcMutableRuntimeAuthority.Default).Fingerprint)throw new IOException("Mutable receipt belongs to a different trusted host authority.");
        string root=binding.MutableLayout is null?Live(paths,sessionId):(authority??MmcMutableRuntimeAuthority.Default).Live(instanceId,sessionId);
        MmcInstancePaths.EnsureNoLinks(root);
        if(binding.MutableLayout is not null&&(Within(root,paths.InstanceRoot)||Within(paths.InstanceRoot,root)))throw new IOException("External mutable scope overlaps the instance tree.");
        return new(root,Path.Combine(root,binding.MutableLayout is null?"temp":"t"),Path.Combine(root,binding.MutableLayout is null?"extract":"x"),binding);
    }
    private static bool Within(string root,string candidate)=>candidate.Equals(root,MmcMutableRuntimeAuthority.Comparison)||candidate.StartsWith(root+Path.DirectorySeparatorChar,MmcMutableRuntimeAuthority.Comparison);

    internal static void ValidateLaunch(MmcInstancePaths paths,Guid instanceId, Guid sessionId, MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority=null) {
        ValidateBinding(sessionId, binding);
        var selected=Paths(paths,instanceId,sessionId,binding,authority);string live=selected.SessionRoot,retired=Retired(paths,instanceId,sessionId,binding,authority);
        if(binding.MutableLayout is not null)(authority??MmcMutableRuntimeAuthority.Default).ValidateParents(instanceId);
        if (MmcInstancePaths.Attributes(retired) is not null) throw new IOException("Mutable session ID has already been retired.");
        ValidateOwner(live,paths,instanceId,sessionId,binding);
        if(!MmcInstancePaths.CheckDirectoryRole(selected.TemporaryDirectory)||!MmcInstancePaths.CheckDirectoryRole(selected.ExtractDirectory))throw new IOException("Mutable child directory is missing.");
    }

    /// <summary>The caller has not written Launching intent or created a process for this ID.</summary>
    public static void AbandonUnlaunched(MmcAdmissionLease lease, Guid sessionId, MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority=null) {
        lease.AssertHeld(); ValidateBinding(sessionId, binding);
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        var paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        if (MmcInstancePaths.Attributes(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcSessionHistory/" + sessionId.ToString("N"))) is not null)
            throw new IOException("A previously launched session cannot be abandoned as unlaunched.");
        Retire(paths,lease.InstanceId,sessionId,binding,authority);
    }

    // Builder-only rollback before returning a launch plan: this caller just created
    // the fresh owned directory, and no process or durable launch intent can use it.
    internal static void AbandonFreshBuild(MmcInstancePaths paths,Guid instanceId,Guid sessionId,MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority=null){
        foreach(string root in new[]{"PCL/MmcSessions/","PCL/MmcSessionHistory/"})
            if(MmcInstancePaths.Attributes(paths.ResolveRelative(MmcPathRole.Instance,root+sessionId.ToString("N")))is not null)
                throw new IOException("A durable launch intent cannot be retired as a fresh failed build.");
        Retire(paths,instanceId,sessionId,binding,authority);
    }

    internal static void CompleteVerifiedExit(MmcInstancePaths paths, MmcSessionReceipt receipt,MmcMutableRuntimeAuthority? authority=null) {
        if (receipt.RuntimeBinding is { } binding) Retire(paths,receipt.InstanceId,receipt.SessionId,binding,authority);
    }

    private static void Retire(MmcInstancePaths paths,Guid instanceId, Guid sessionId, MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority) {
        ValidateBinding(sessionId, binding);
        string live=Paths(paths,instanceId,sessionId,binding,authority).SessionRoot,retired=Retired(paths,instanceId,sessionId,binding,authority);
        bool hasLive = MmcInstancePaths.Attributes(live) is not null, hasRetired = MmcInstancePaths.Attributes(retired) is not null;
        if (hasLive && hasRetired) throw new IOException("Mutable session has conflicting live and retired directories.");
        if (!hasLive && !hasRetired) return;
        if(binding.MutableLayout is not null)(authority??MmcMutableRuntimeAuthority.Default).ValidateParents(instanceId);
        string current = hasLive ? live : retired;
        ValidateOwner(current,paths,instanceId,sessionId,binding);
        // Preflight every descendant before the first move/delete. In particular, never pass
        // an unchecked junction to the legacy recursive directory helper.
        InspectTree(current);
        if (hasLive) {
            if(binding.MutableLayout is not null)(authority??MmcMutableRuntimeAuthority.Default).EnsureParents(instanceId,retired:true);
            string parent = Path.GetDirectoryName(retired)!;MmcInstancePaths.EnsureNoLinks(parent); Directory.CreateDirectory(parent); MmcInstancePaths.EnsureNoLinks(parent);
            Directory.Move(live, retired);
        }
        ValidateOwner(retired,paths,instanceId,sessionId,binding);
        var tree = InspectTree(retired);
        foreach (string file in tree.Files) {
            if (string.Equals(file, Path.Combine(retired, MarkerName), StringComparison.OrdinalIgnoreCase)) continue;
            MmcInstancePaths.EnsureNoLinks(file); File.Delete(file);
        }
        foreach (string directory in tree.Directories.AsEnumerable().Reverse()) {
            MmcInstancePaths.EnsureNoLinks(directory); Directory.Delete(directory, false);
        }
        // Retain this tiny non-sensitive ownership receipt for idempotent recovery. No temporary
        // native files survive successful cleanup, and no active plan refers to this retired path.
        ValidateOwner(retired,paths,instanceId,sessionId,binding);
        if (Directory.EnumerateFileSystemEntries(retired).Any(path => Path.GetFileName(path) != MarkerName))
            throw new IOException("Mutable cleanup encountered an external change; ownership evidence was preserved.");
    }

    private static (List<string> Files, List<string> Directories) InspectTree(string root) {
        List<string> files = [], directories = [];
        Stack<string> pending = new(); pending.Push(root); int count = 0;
        while (pending.TryPop(out string? directory)) {
            MmcInstancePaths.CheckDirectoryRole(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                if (++count > MaximumEntries) throw new IOException("Mutable session exceeds its cleanup entry budget.");
                MmcInstancePaths.EnsureNoLinks(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) { directories.Add(entry); pending.Push(entry); }
                else files.Add(entry);
            }
        }
        return (files, directories);
    }
    private static void ValidateOwner(string root,MmcInstancePaths paths,Guid instanceId, Guid id, MmcSessionRuntimeBinding binding) {
        MmcInstancePaths.CheckDirectoryRole(root);
        string marker = Path.Combine(root, MarkerName); MmcInstancePaths.EnsureNoLinks(marker);
        string expected=Owner(paths,instanceId,id,binding);
        using FileStream input = new(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = Encoding.UTF8.GetBytes(expected);
        if (input.Length != bytes.Length) throw new IOException("Mutable runtime ownership marker differs from the session receipt.");
        byte[] actual = new byte[bytes.Length]; input.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(bytes)) throw new IOException("Mutable runtime ownership marker differs from the session receipt.");
    }
    private static void ValidateBinding(Guid sessionId, MmcSessionRuntimeBinding binding) {
        if (sessionId == Guid.Empty || binding is null || !MmcDurableFiles.IsHash(binding.BoundPlanFingerprint)||binding.MutableLayout is { } layout&&!Enum.IsDefined(layout)||
            binding.MutableLayout is not null&&!MmcDurableFiles.IsHash(binding.AuthorityFingerprint)||binding.MutableLayout is null&&binding.AuthorityFingerprint is not null)
            throw new IOException("Mutable runtime session binding is invalid.");
    }
    private static string Live(MmcInstancePaths paths, Guid id) => paths.ResolveRelative(MmcPathRole.Runtime, "sessions/" + id.ToString("N"));
    private static string Retired(MmcInstancePaths paths,Guid instanceId,Guid id,MmcSessionRuntimeBinding binding,MmcMutableRuntimeAuthority? authority)=>binding.MutableLayout is null?paths.ResolveRelative(MmcPathRole.Runtime,".session-history/"+id.ToString("N")):(authority??MmcMutableRuntimeAuthority.Default).Retired(instanceId,id);
    private static string Owner(MmcInstancePaths paths,Guid instanceId,Guid id,MmcSessionRuntimeBinding binding)=>binding.MutableLayout is null?
        "PCLLF.MmcSessionMutable/v1\n"+id.ToString("N")+"\n"+binding.BoundPlanFingerprint:
        "PCLLF.MmcSessionMutable/v2\n"+MmcCanonicalPaths.ForAdmission(paths.InstanceRoot)+"\n"+instanceId.ToString("N")+"\n"+id.ToString("N")+"\n"+binding.BoundPlanFingerprint+"\n"+binding.MutableLayout+"\n"+binding.AuthorityFingerprint;
}
