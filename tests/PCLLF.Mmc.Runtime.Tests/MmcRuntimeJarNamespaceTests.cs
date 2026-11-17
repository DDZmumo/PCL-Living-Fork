using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using PCLCS;

internal static partial class MmcRuntimeLaunchTests {
    private static byte[] AppendJarEntries(byte[] original, params (string Name, byte[] Bytes)[] extras) {
        using MemoryStream result = new();
        using (var output = new ZipArchive(result, ZipArchiveMode.Create, true)) {
            using var input = new ZipArchive(new MemoryStream(original), ZipArchiveMode.Read);
            foreach (var entry in input.Entries) { using var source = entry.Open(); using var target = output.CreateEntry(entry.FullName).Open(); source.CopyTo(target); }
            foreach (var item in extras) { using var target = output.CreateEntry(item.Name).Open(); target.Write(item.Bytes); }
        }
        return result.ToArray();
    }
    private static object JarPreflight(byte[] bytes, int maximumEntries = 100000, long maximumOutput = 4L*1024*1024*1024, long maximumEntry = 256L*1024*1024) {
        using var input = new MemoryStream(bytes);
        var method = typeof(MmcClasspathInspector).Assembly.GetType("PCLCS.MmcReadOnlyJarPreflight",true)!.GetMethod("Inspect",BindingFlags.Static|BindingFlags.NonPublic)!;
        try { return method.Invoke(null,[input,maximumEntries,maximumOutput,maximumEntry])!; }
        catch(TargetInvocationException error) when(error.InnerException is not null){ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
    }
    private static void RejectJar(Action work) { try {work();throw new Exception("Unsupported read-only ZIP was accepted.");} catch(IOException){} }
    private static void ReadOnlyJarNamespace() {
        byte[] small = Encoding.UTF8.GetBytes("resource");
        (string,byte[])[] resources = [("assets/example.txt",small),("assets/example.txt",Encoding.UTF8.GetBytes("different")),("Assets/Case.png",small),("assets/case.png",small),
            ("CON",small),("ads:name",small),("trailing. ",small),("literal\\resource",small),("../literal",small),("node",small),("node/child",small),("fixture/probe.class",small)];
        using(var fixture = new Fixture(firstJarTransform: bytes=>AppendJarEntries(bytes,resources))) {
            var plan=MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inspected=MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            Require(inspected.EffectiveClasses.Single(c=>c.ClassName=="fixture.Probe").ClasspathPosition==0,"Literal resource entries changed the required Java class lookup.");
        }
        using(var fixture=new Fixture(firstJarTransform:bytes=>AppendJarEntries(bytes,("fixture/Probe.class",small)))) {
            var plan=MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            Expect("MMC_CLASSPATH_CLASS_AMBIGUOUS",()=>MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult());
        }
        foreach(string second in new[]{"META-INF/MANIFEST.MF","meta-inf/manifest.mf"}) using(var fixture=new Fixture(firstJarTransform:bytes=>AppendJarEntries(bytes,("META-INF/MANIFEST.MF",Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\n\r\n")),(second,small)))) {
            var plan=MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            Expect("MMC_CLASSPATH_MANIFEST",()=>MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult());
        }
        byte[] repeated=AppendJarEntries(Jar(new()),("assets/repeat",small),("assets/repeat",small));
        _=JarPreflight(repeated);
        RejectJar(()=>JarPreflight(repeated,maximumEntries:1));
        RejectJar(()=>JarPreflight(repeated,maximumOutput:1));
        RejectJar(()=>JarPreflight(repeated,maximumEntry:1));
        RejectJar(()=>JarPreflight(AppendJarEntries(Jar(new()),(string.Join('/',Enumerable.Repeat("d",129)),[]))));
        var native=typeof(MmcArtifactMaterializer).GetMethod("PreflightArchive",BindingFlags.NonPublic|BindingFlags.Static)!;
        using(var input=new MemoryStream(repeated)) {
            try {native.Invoke(null,[input,100,1024L*1024,null,false,1024L*1024]);throw new Exception("Native extraction admitted duplicate targets.");}
            catch(TargetInvocationException error) when(error.InnerException is IOException){}
        }
        int central=FindSignature(repeated,0x02014b50);
        byte[] unsupported=(byte[])repeated.Clone();BinaryPrimitives.WriteUInt16LittleEndian(unsupported.AsSpan(central+10),99);RejectJar(()=>JarPreflight(unsupported));
        byte[] invalidUtf8=(byte[])repeated.Clone();invalidUtf8[central+46]=0xff;RejectJar(()=>JarPreflight(invalidUtf8));
        byte[] zip64Entry=(byte[])repeated.Clone();BinaryPrimitives.WriteUInt32LittleEndian(zip64Entry.AsSpan(central+24),uint.MaxValue);RejectJar(()=>JarPreflight(zip64Entry));
        byte[] multidisk=(byte[])repeated.Clone();int end=FindSignature(multidisk,0x06054b50);BinaryPrimitives.WriteUInt16LittleEndian(multidisk.AsSpan(end+4),1);RejectJar(()=>JarPreflight(multidisk));
        Console.WriteLine("PASS literal/exact-duplicate resources, case-distinct class names, duplicate class/manifest refusal, bounded UTF8/compression/entry/depth/output, native duplicate rejection.");
    }
    private static int FindSignature(byte[] bytes,uint signature){for(int i=0;i<=bytes.Length-4;i++)if(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i))==signature)return i;throw new Exception("Synthetic ZIP signature absent.");}
    internal static void ReadOnlyJarSourceProof(string path) {
        path=Path.GetFullPath(path);byte[] original=File.ReadAllBytes(path);string before=Hash(original);_=JarPreflight(original);
        string name;
        using(var archive=new ZipArchive(new MemoryStream(original),ZipArchiveMode.Read)) name=archive.Entries.First(e=>e.FullName.EndsWith(".class",StringComparison.Ordinal)&&!e.FullName.StartsWith("META-INF/",StringComparison.Ordinal)).FullName[..^6].Replace('/','.');
        foreach(int major in new[]{8,21}) using(var fixture=new Fixture(major,loader:false,firstJarTransform:_=>original)) {
            var plan=MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inspected=MmcClasspathInspector.ObserveAsync(plan,additionalClassNames:[name]).GetAwaiter().GetResult();
            MmcClasspathInspector.RevalidateManifestInputs(plan,inspected);
            Console.WriteLine("JAR source proof Java="+major+" absent manifest refs="+inspected.ManifestInputs.Length);
            Require(inspected.EffectiveClasses.Single(c=>c.ClassName==name).ClasspathPosition==0,"Original JAR required class did not participate in the real inspector proof.");
        }
        Require(Hash(File.ReadAllBytes(path))==before,"Read-only source changed.");
        Console.WriteLine("PASS actual readonly source JAR + inspector Java8/21 class-header proof SHA256="+before+"; no Minecraft/JAR execution; source bytes unchanged.");
    }
}
