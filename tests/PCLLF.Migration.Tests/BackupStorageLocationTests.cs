using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;

internal static class BackupStorageLocationTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("backup storage default preserves legacy stores without creating config", Legacy);
        yield return ("backup storage moves both stores verifies bytes and keeps other personal data", RoundTrip);
        yield return ("backup storage refuses occupied nested relative and unavailable locations", Guards);
        yield return ("backup storage cancellation preserves source and empties owned staging", Cancel);
        yield return ("backup storage detects source edits before commit", SourceChanged);
        yield return ("backup storage config failure rolls back published copies", PersistFailure);
        yield return ("backup storage rejects malformed and stale config without falling back", BadConfig);
    }
    private static void Legacy() {
        using Fixture f = new(); var store = new BackupStorageLocation(f.Profile);
        Equal(f.Profile, store.Root); Equal(false, File.Exists(Path.Combine(f.Profile,"backup-storage.json")));
    }
    private static void RoundTrip() {
        using Fixture f = new(); f.Seed(); string personal=f.Put("profile/personal-options.json","personal");
        var store=new BackupStorageLocation(f.Profile); List<double> progress=new();
        var result=store.MoveTo(f.Target,CancellationToken.None,progress.Add);
        Equal(2,result.Files); Equal(true,result.CleanupComplete); Equal(f.Target,store.Root);
        Equal(f.Target,new BackupStorageLocation(f.Profile).Root);
        Equal("world",File.ReadAllText(Path.Combine(f.Target,"world-backups/game/points/one/world.zip")));
        Equal("object",File.ReadAllText(Path.Combine(f.Target,"environment-history/version/objects/hash")));
        Equal(true,Directory.Exists(Path.Combine(f.Target,"world-backups/game/empty")));
        Equal(false,Directory.Exists(Path.Combine(f.Profile,"world-backups")));
        Equal(false,Directory.Exists(Path.Combine(f.Profile,"environment-history")));
        Equal("personal",File.ReadAllText(personal)); Equal(true,progress.Count>0);
        string again=f.Directory("again");store.MoveTo(again,CancellationToken.None);
        Equal(again,new BackupStorageLocation(f.Profile).Root);
        Equal(false,Directory.Exists(Path.Combine(f.Target,"world-backups")));
    }
    private static void Guards() {
        using Fixture f=new();f.Seed();var store=new BackupStorageLocation(f.Profile);
        f.Put("target/foreign.txt","keep");Throws(()=>store.MoveTo(f.Target,CancellationToken.None));
        Equal("keep",File.ReadAllText(Path.Combine(f.Target,"foreign.txt")));
        Throws(()=>store.MoveTo(f.Directory("profile/inside"),CancellationToken.None));
        Throws(()=>store.MoveTo(f.Root,CancellationToken.None));Throws(()=>store.MoveTo("relative",CancellationToken.None));
        Equal(0,store.MoveTo(f.Profile,CancellationToken.None).Files);Equal(f.Profile,store.Root);
    }
    private static void Cancel() {
        using Fixture f=new();f.Seed();f.Put("profile/world-backups/game/large.bin",new string('x',400000));
        var store=new BackupStorageLocation(f.Profile);using CancellationTokenSource cancel=new();
        try {store.MoveTo(f.Target,cancel.Token,_=>cancel.Cancel());throw new Exception("Expected cancellation.");}
        catch(OperationCanceledException) { }
        Equal(f.Profile,store.Root);Equal(true,File.Exists(Path.Combine(f.Profile,"world-backups/game/large.bin")));
        Equal(0,Directory.EnumerateFileSystemEntries(f.Target).Count());
    }
    private static void SourceChanged() {
        using Fixture f=new();f.Seed();var store=new BackupStorageLocation(f.Profile);
        Throws(()=>store.MoveTo(f.Target,CancellationToken.None,beforeCommit:()=>f.Put("profile/world-backups/game/points/one/world.zip","new progress")));
        Equal(f.Profile,store.Root);Equal("new progress",File.ReadAllText(Path.Combine(f.Profile,"world-backups/game/points/one/world.zip")));
        Equal(0,Directory.EnumerateFileSystemEntries(f.Target).Count());
    }
    private static void PersistFailure() {
        using Fixture f=new();f.Seed();var store=new BackupStorageLocation(f.Profile);
        Throws(()=>store.MoveTo(f.Target,CancellationToken.None,beforeCommit:()=>f.Directory("profile/backup-storage.json")));
        Equal(f.Profile,store.Root);Equal(true,File.Exists(Path.Combine(f.Profile,"world-backups/game/points/one/world.zip")));
        Equal(0,Directory.EnumerateFileSystemEntries(f.Target).Count());
    }
    private static void BadConfig() {
        using Fixture f=new();f.Put("profile/backup-storage.json","bad-json");Throws(()=>new BackupStorageLocation(f.Profile));
        f.Put("profile/backup-storage.json",new JObject(new JProperty("schema",1),new JProperty("root",f.Target)).ToString());
        var store=new BackupStorageLocation(f.Profile);
        f.Put("profile/backup-storage.json","external edit");Throws(()=>store.MoveTo(f.Directory("other"),CancellationToken.None));
        Equal(f.Target,store.Root);Equal("external edit",File.ReadAllText(Path.Combine(f.Profile,"backup-storage.json")));
    }
    private static void Equal<T>(T expected,T actual) {if(!Equals(expected,actual))throw new Exception($"Expected [{expected}], got [{actual}].");}
    private static void Throws(Action work) {try{work();}catch(IOException){return;}throw new Exception("Expected guarded failure.");}
    private sealed class Fixture:IDisposable {
        internal string Root {get;}=Path.Combine(Path.GetTempPath(),"PCLLF-BackupStorage-"+Guid.NewGuid().ToString("N"));
        internal string Profile=>Path.Combine(Root,"profile");internal string Target=>Path.Combine(Root,"target");
        internal Fixture(){Directory("profile");Directory("target");}
        internal string Directory(string relative){string path=Owned(relative);System.IO.Directory.CreateDirectory(path);return path;}
        internal string Put(string relative,string text){string path=Owned(relative);System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text,new UTF8Encoding(false));return path;}
        internal void Seed(){Put("profile/world-backups/game/points/one/world.zip","world");Directory("profile/world-backups/game/empty");Put("profile/environment-history/version/objects/hash","object");}
        private string Owned(string relative){string path=Path.GetFullPath(Path.Combine(Root,relative));if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new Exception("Unsafe fixture path.");return path;}
        public void Dispose(){string prefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Root.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Root),@"\APCLLF-BackupStorage-[a-f0-9]{32}\z"))throw new Exception("Unsafe fixture cleanup.");System.IO.Directory.Delete(Root,true);}
    }
}
