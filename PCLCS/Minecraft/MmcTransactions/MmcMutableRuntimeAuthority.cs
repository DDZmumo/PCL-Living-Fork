using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>A host capability, never reconstructed from receipt paths. The default scope is fixed to this user's LocalAppData.</summary>
public sealed class MmcMutableRuntimeAuthority {
    public static MmcMutableRuntimeAuthority Default { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PCLLF","m"));
    internal string Root { get; }
    internal string Fingerprint=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows()?Root.ToUpperInvariant():Root)));
    private MmcMutableRuntimeAuthority(string root){
        if(!Path.IsPathFullyQualified(root)||root.StartsWith("\\\\",StringComparison.Ordinal))throw new IOException("Mutable runtime authority must be a local absolute host path.");
        Root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    // Only explicitly owned direct GUID fixture roots may mint a non-default authority. No public
    // caller or serialized receipt can supply an arbitrary deletion base.
    internal static MmcMutableRuntimeAuthority ForOwnedFixture(string fixtureRoot){
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureRoot));string leaf=Path.GetFileName(root);
        bool guid=Guid.TryParseExact(leaf,"N",out _)||leaf.StartsWith("PclNativeFacade-",StringComparison.Ordinal)&&Guid.TryParseExact(leaf[16..],"N",out _);
        if(!guid||!Path.GetDirectoryName(root)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),Comparison))throw new IOException("Mutable fixture authority requires an explicit direct owned GUID temp root.");
        MmcInstancePaths.EnsureNoLinks(root);return new(Path.Combine(root,"m"));
    }
    internal static StringComparison Comparison=>OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
    internal string InstanceRoot(Guid id){if(id==Guid.Empty)throw new IOException("Mutable instance ID is missing.");return Path.Combine(Root,id.ToString("N"));}
    internal string Live(Guid instanceId,Guid sessionId)=>Path.Combine(InstanceRoot(instanceId),sessionId.ToString("N"));
    internal string Retired(Guid instanceId,Guid sessionId)=>Path.Combine(InstanceRoot(instanceId),".h",sessionId.ToString("N"));
    internal void EnsureParents(Guid instanceId,bool retired=false){
        MmcInstancePaths.EnsureNoLinks(Root);Directory.CreateDirectory(Path.GetDirectoryName(Root)!);MmcInstancePaths.EnsureNoLinks(Root);
        OwnedParent(Root,"PCLLF.MmcMutableAuthority/v1\n"+Root);
        string instance=InstanceRoot(instanceId);OwnedParent(instance,"PCLLF.MmcMutableInstance/v1\n"+instanceId.ToString("N"));
        if(retired)OwnedParent(Path.Combine(instance,".h"),"PCLLF.MmcMutableHistory/v1\n"+instanceId.ToString("N"));
    }
    internal void ValidateParents(Guid instanceId){
        VerifyParent(Root,"PCLLF.MmcMutableAuthority/v1\n"+Root);
        VerifyParent(InstanceRoot(instanceId),"PCLLF.MmcMutableInstance/v1\n"+instanceId.ToString("N"));
    }
    private static void OwnedParent(string path,string owner){
        MmcInstancePaths.EnsureNoLinks(path);
        if(MmcInstancePaths.Attributes(path)is null){CreateExclusive(path);MmcDurableFiles.WriteNew(Path.Combine(path,"authority.owner"),Encoding.UTF8.GetBytes(owner));}
        VerifyParent(path,owner);
    }
    private static void VerifyParent(string path,string owner){
        MmcInstancePaths.CheckDirectoryRole(path);string marker=Path.Combine(path,"authority.owner");MmcInstancePaths.EnsureNoLinks(marker);
        if(!File.Exists(marker)||new FileInfo(marker).Length>4096||File.ReadAllText(marker)!=owner)throw new IOException("Mutable host scope contains an unknown directory or owner.");
    }
    internal static void CreateExclusive(string path){
        MmcInstancePaths.EnsureNoLinks(path);bool created=OperatingSystem.IsWindows()?CreateDirectoryW(path,IntPtr.Zero):Mkdir(path,448)==0;
        if(!created)throw new IOException("Mutable session directory is not fresh/exclusively owned.",new Win32Exception(Marshal.GetLastWin32Error()));
        MmcInstancePaths.EnsureNoLinks(path);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateDirectoryW(string path,IntPtr security);
    [DllImport("libc",EntryPoint="mkdir",SetLastError=true)]private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)]string path,uint mode);
}
