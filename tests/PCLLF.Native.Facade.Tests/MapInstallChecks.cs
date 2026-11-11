using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;

namespace PCLLF.Native.Facade.Tests;
internal static class MapInstallChecks
{
    private static void C(int line,bool value)=>MapInstallSourceChecks.Check("map",line,value);
    private static object Kind(string name)=>Native.Enum("PCL.InstanceResourceKind",name)!;
    private static object? Store(string method,params object?[] args)=>Native.Static("PCL.InstanceResourceStore",method,args);
    private static object? Installer(string method,params object?[] args)=>Native.Static("PCL.MapDownloadInstaller",method,args);
    private static object? Value(object item,string name)=>item.GetType().GetField(name,Native.Flags) is { } field?field.GetValue(item):Native.Property(item.GetType(),name).GetValue(item);
    private static void Put(string file,string text){Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,text);}
    private static void Zip(string file,params(string Name,string Text)[] entries){Directory.CreateDirectory(Path.GetDirectoryName(file)!);using ZipArchive zip=ZipFile.Open(file,ZipArchiveMode.Create);foreach(var item in entries){using StreamWriter writer=new(zip.CreateEntry(item.Name).Open());writer.Write(item.Text);}}
    private static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static Exception Rejected(Action work){try{work();}catch(Exception error){return error;}throw new InvalidOperationException("Actual map/recycle unexpectedly accepted invalid work.");}
    private static void Text(int line,string file,string expected)=>C(line,File.Exists(file)&&File.ReadAllText(file)==expected);
    private static T Part<T>(FrameworkElement owner,string name)where T:class=>owner.FindName(name)as T??throw new MissingMemberException(owner.GetType().FullName,name);
    private static void Set(object page,string name,object? value)=>Native.Property(page.GetType(),name).SetValue(page,value);
    private static IList Scan(string folder,object kind)=>(IList)Store("Scan",folder,folder,kind,CancellationToken.None)!;
    internal static void Run(FixtureEnvironment environment)
    {
        MapInstallSourceChecks.Reset("map");using MapInstallResourceScope scope=new(environment,"map-install-recycle");
        string saves=Path.Combine(scope.Root,"game","saves"),archives=(string)Installer("GetDownloadFolder",saves)!;object kind=Kind("Saves");
        C(32,archives==Path.Combine(saves,"PCLLFWorldDownload")&&Directory.Exists(archives));C(33,(string)Installer("GetDownloadFolder",saves)! ==archives);
        string flat=Path.Combine(archives,"Flat map.zip");Zip(flat,("level.dat","flat-world"),("region/data.txt","region-data"));string hash=Hash(flat),world=(string)Installer("Install",flat,saves,CancellationToken.None)!;
        C(36,world==Path.Combine(saves,"Flat map")&&!Directory.Exists(Path.Combine(archives,"Flat map")));Text(37,Path.Combine(world,"level.dat"),"flat-world");Text(37,Path.Combine(world,"region","data.txt"),"region-data");C(38,Hash(flat)==hash);
        string second=(string)Installer("Install",flat,saves,CancellationToken.None)!;C(40,world!=second);Text(40,Path.Combine(world,"level.dat"),"flat-world");
        string wrapped=Path.Combine(archives,"Wrapped.zip");Zip(wrapped,("outer/world/level.dat","wrapped-world"),("outer/world/region/data.txt","wrapped-data"),("README.txt","readme"));
        ModLoader.LoaderBase step=(ModLoader.LoaderBase)Native.Static("PCL.PageDownloadResourceDetail","CreateMapInstallLoader",wrapped,saves)!;Native.Check(step.GetType().Assembly==Native.Main,"Map install loader is not the genuine Main adapter.");scope.Track(step);
        using(CancellationTokenSource deadline=new(TimeSpan.FromSeconds(10)))step.WaitForExit(1,null,false,deadline.Token,null,true);
        Text(44,Path.Combine(saves,"Wrapped","level.dat"),"wrapped-world");
        string bad=Path.Combine(archives,"Bad.zip");Zip(bad,("../outside.txt","bad"),("level.dat","bad"));C(45,Rejected(()=>Installer("Install",bad,saves,CancellationToken.None))is not null);C(46,!File.Exists(Path.Combine(scope.Root,"outside.txt")));
        string multiple=Path.Combine(archives,"Multiple.zip");Zip(multiple,("one/level.dat","one"),("two/level.dat","two"));C(47,Rejected(()=>Installer("Install",multiple,saves,CancellationToken.None))is not null);
        string missing=Path.Combine(archives,"Not a world.zip");Zip(missing,("README.txt","not a world"));C(48,Rejected(()=>Installer("Install",missing,saves,CancellationToken.None))is not null);
        using(CancellationTokenSource canceled=new()){canceled.Cancel();C(49,Rejected(()=>Installer("Install",flat,saves,canceled.Token))is OperationCanceledException);}
        C(50,!Directory.EnumerateDirectories(Path.Combine(scope.Root,"game"),".pcl-import-*").Any());
        string trash=Path.Combine(scope.Root,"game","PCL","ResourceRecycle","Saves");IList items=Scan(saves,kind);C(55,!items.Cast<object>().Any(item=>(string)Value(item,"Name")! =="PCLLFWorldDownload"));
        Type entryType=Native.Type("PCL.InstanceResourceEntry");IList targets=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
        foreach(object item in items)if((bool)Value(item,"IsDirectory")!&&new[]{"Flat map","Flat map (2)","Wrapped"}.Contains((string)Value(item,"Name")!))targets.Add(item);
        Store("DeleteMany",saves,targets,CancellationToken.None);
        Native.Check(targets.Cast<object>().All(item=>!Directory.Exists((string)Value(item,"FullPath")!))&&!Directory.Exists(trash)&&File.Exists(flat), "Direct world deletion must preserve downloaded archives and leave no recycle copies.");
        DirectResourceDeletionChecks.Run(scope);
        MapInstallSourceChecks.Complete("map");environment.AssertNoForbiddenIo();Console.WriteLine("PASS native map-install-recycle: real Main loader/install, permanent world deletion and native resource page; retained source IDs plus current deletion policy.");
    }
}
