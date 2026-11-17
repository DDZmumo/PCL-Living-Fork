using System.Runtime.InteropServices;
using System.Text;

namespace PCLCS;

internal sealed class MmcLifecycleAccess:IDisposable {
    internal readonly Dictionary<string,MmcConfigurationQuiescence> Quiet=new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string,MmcAdmissionLease> Leases=new(StringComparer.OrdinalIgnoreCase);
    internal MmcLifecycleAccess(string source,string target,Guid id,CancellationToken token=default) {
        try {foreach(string root in new[]{source,target}.Order(StringComparer.OrdinalIgnoreCase))Quiet[root]=MmcConfigurationCoordinator.Quiesce(root,token);
            foreach(string root in new[]{source,target}.Order(StringComparer.OrdinalIgnoreCase))Leases[root]=MmcAdmissionGate.Acquire(root,id,TimeSpan.Zero,token);
        }catch{Dispose();throw;}
    }
    public void Dispose(){foreach(var value in Leases.Values.Reverse())value.Dispose();Leases.Clear();foreach(var value in Quiet.Values.Reverse())value.Dispose();Quiet.Clear();}
    internal static void SameVolume(string source,string target) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Lifecycle directory moves currently require the verified Windows volume boundary.");
        static string Volume(string path){while(!Directory.Exists(path)){path=Path.GetDirectoryName(path)??throw new IOException("No existing volume ancestor.");}MmcInstancePaths.EnsureNoLinks(path);
            var mount=new StringBuilder(32768);var volume=new StringBuilder(128);
            if(!GetVolumePathNameW(path,mount,(uint)mount.Capacity)||!GetVolumeNameForVolumeMountPointW(mount.ToString(),volume,(uint)volume.Capacity))throw new IOException("Cannot establish a stable local volume identity.");return volume.ToString();}
        if(!Volume(source).Equals(Volume(target),StringComparison.OrdinalIgnoreCase))throw new IOException("Cross-volume MMC moves are not supported.");
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetVolumePathNameW(string path,StringBuilder volumePath,uint length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetVolumeNameForVolumeMountPointW(string mount,StringBuilder name,uint length);
}
internal sealed class MmcLifecycleCancellationFence:IDisposable {
    private readonly object sync=new();private readonly CancellationTokenSource source=new();private readonly CancellationTokenRegistration registration;private bool entered;
    internal CancellationToken Token=>source.Token;
    internal MmcLifecycleCancellationFence(CancellationToken external)=>registration=external.Register(()=>{lock(sync){if(!entered)source.Cancel();}});
    internal void EnterDecision(){lock(sync){source.Token.ThrowIfCancellationRequested();entered=true;}}
    public void Dispose(){registration.Dispose();source.Dispose();}
}
