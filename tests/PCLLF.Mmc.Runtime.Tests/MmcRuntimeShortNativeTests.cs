using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCLCS;

internal static partial class MmcRuntimeLaunchTests {
    private static MmcMutableRuntimeAuthority FixtureAuthority(string root)=>(MmcMutableRuntimeAuthority)typeof(MmcMutableRuntimeAuthority)
        .GetMethod("ForOwnedFixture",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[root])!;
    private static void DeleteMutableFixture(string root){
        string full=Path.GetFullPath(root);Require(Path.GetDirectoryName(full)==Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))&&Guid.TryParseExact(Path.GetFileName(full),"N",out _),"Mutable cleanup scope is not the owned direct GUID fixture.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
    private static void ShortNativePaths(){
        string planFile=Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_NATIVE_PLAN")??throw new Exception("Explicit readonly locked native plan input is required.");
        using JsonDocument plan=JsonDocument.Parse(File.ReadAllText(planFile));
        var inputs=plan.RootElement.GetProperty("Inputs").EnumerateArray().ToArray();
        (string Path,string Hash) Input(string file){
            var input=inputs.Single(value=>System.IO.Path.GetFileName(value.GetProperty("ActualPath").GetString())==file);
            string path=input.GetProperty("ActualPath").GetString()!,hash=input.GetProperty("Sha256").GetString()!;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Require(Convert.ToHexStringLower(SHA256.HashData(stream))==hash,"Locked native fixture source digest changed.");return(path,hash);
        }
        var sdl=Input("lwjgl-sdl-natives-windows-3.4.3.jar");var lwjgl=Input("lwjgl-3.4.3.jar");var native=Input("lwjgl-natives-windows-3.4.3.jar");
        string root=Path.Combine(Path.GetTempPath(),"PCLLF-MmcNativePath-"+Guid.NewGuid().ToString("N")),scope=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);Guid instanceId=Guid.NewGuid(),sid=Guid.NewGuid();
        string instance=Path.Combine(root,new string('d',75),new string('e',75),"versions","native-probe");Directory.CreateDirectory(Path.Combine(instance,"minecraft"));
        var paths=MmcInstancePaths.Resolve(instance);var authority=FixtureAuthority(scope);
        try{
            var mutable=MmcSessionRuntimeDirectories.Prepare(paths,instanceId,sid,new string('a',64),authority);
            Require(mutable.RuntimeBinding.MutableLayout==MmcMutableRuntimeLayout.LocalAppDataV2,"Deep native fixture did not choose the production short layout.");
            string longDirectory=Path.Combine(paths.RuntimeRoot,"sessions",Guid.NewGuid().ToString("N"),"extract"),bootstrap=Path.Combine(root,"bootstrap");Directory.CreateDirectory(longDirectory);Directory.CreateDirectory(bootstrap);
            byte[] Extract(string jar,string leaf){using var file=new FileStream(jar,FileMode.Open,FileAccess.Read,FileShare.Read);using var archive=new ZipArchive(file,ZipArchiveMode.Read);var entry=archive.Entries.Single(value=>value.Name.Equals(leaf,StringComparison.OrdinalIgnoreCase));Require(entry.Length>0&&entry.Length<64*1024*1024,"Controlled DLL exceeds its fixture budget.");using var source=entry.Open();using MemoryStream output=new();source.CopyTo(output);return output.ToArray();}
            byte[] dll=Extract(sdl.Path,"SDL3.dll");string dllHash=Hash(dll);string longDll=Path.Combine(longDirectory,"SDL3.dll"),shortDll=Path.Combine(mutable.ExtractDirectory,"SDL3.dll");
            File.WriteAllBytes(longDll,dll);File.WriteAllBytes(shortDll,dll);File.WriteAllBytes(Path.Combine(bootstrap,"lwjgl.dll"),Extract(native.Path,"lwjgl.dll"));
            string sourceFile=Path.Combine(root,"NativePathProbe.java");File.WriteAllText(sourceFile,"""
public final class NativePathProbe {
  public static void main(String[] args) {
    try { org.lwjgl.system.windows.WindowsLibrary library = new org.lwjgl.system.windows.WindowsLibrary(args[0]);
      System.out.println("NATIVE_LOADED"); library.free();
    } catch (Throwable failure) { System.out.println("NATIVE_LOAD_FAILED:" + failure.toString()); System.exit(73); }
  }
}
""",new UTF8Encoding(false));
            string javac=Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVAC")??Path.Combine(Path.GetDirectoryName(Java21)!,"javac.exe");RunTool(javac,["--release","8","-cp",lwjgl.Path,sourceFile]);
            (int Exit,string Output) Load(string dllPath){
                var start=new ProcessStartInfo(Java21){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
                foreach(string arg in new[]{"-Dorg.lwjgl.librarypath="+bootstrap,"-Dorg.lwjgl.system.SharedLibraryExtractPath="+bootstrap,"-cp",string.Join(Path.PathSeparator,new[]{root,lwjgl.Path,native.Path}),"NativePathProbe",dllPath})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)??throw new Exception("Actual Java21 native probe did not start.");var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(20000)){process.Kill(true);throw new Exception("Owned native probe timed out.");}Task.WaitAll(output,errors);return(process.ExitCode,output.Result+errors.Result);
            }
            var before=Load(longDll);Require(before.Exit==73&&before.Output.Contains("error code = 206",StringComparison.Ordinal),"Long actual LWJGL native load did not reproduce Win32 error206: "+before.Output);
            var after=Load(shortDll);Require(after.Exit==0&&after.Output.Contains("NATIVE_LOADED",StringComparison.Ordinal),"Short production-owned native scope failed actual LWJGL load: "+after.Output);
            Require(Hash(File.ReadAllBytes(longDll))==dllHash&&Hash(File.ReadAllBytes(shortDll))==dllHash,"Native payload changed between controlled paths.");
            using var lease=MmcAdmissionGate.Acquire(instance,instanceId,TimeSpan.Zero);MmcSessionRuntimeDirectories.AbandonUnlaunched(lease,sid,mutable.RuntimeBinding,authority);
            Require(!Directory.Exists(mutable.SessionRoot),"Verified unlaunched native probe left live mutable bytes.");
            Console.WriteLine("  Actual Java21/LWJGL 3.4.3: same SDL3 SHA256 "+dllHash+", long path "+longDll.Length+" -> error206; owned short path "+shortDll.Length+" -> loaded. Locked jar "+sdl.Hash);
        }finally{DeleteMutableFixture(scope);string full=Path.GetFullPath(root);Require(Path.GetDirectoryName(full)==Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))&&Path.GetFileName(full).StartsWith("PCLLF-MmcNativePath-",StringComparison.Ordinal),"Native test cleanup escaped its owned scope.");if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
