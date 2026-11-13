using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;

internal static class BackupStorageLocationTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("backup storage default preserves legacy stores without creating config", Legacy);
        yield return ("backup storage moves both stores verifies bytes and keeps other personal data", RoundTrip);
        yield return ("backup storage refuses occupied nested relative and unavailable locations", Guards);
        yield return ("backup destination preflight rejects drive roots and occupied folders without writes", DestinationPreflight);
        yield return ("backup storage cancellation preserves source and empties owned staging", Cancel);
        yield return ("backup storage detects source edits before commit", SourceChanged);
        yield return ("backup storage config failure rolls back published copies", PersistFailure);
        yield return ("backup storage rejects malformed and stale config without falling back", BadConfig);
        yield return ("backup storage imports legacy JSON once and gives dedicated preferences priority", DedicatedPreference);
        yield return ("backup storage preference write failure preserves source and rolls back copies", PreferenceFailure);
        yield return ("backup storage uncertain preference commits keep both complete data copies", UncertainPreference);
    }
    private static void DestinationPreflight() {
        using Fixture f = new(); f.Seed(); var store = new BackupStorageLocation(f.Profile);
        Equal(f.Target, store.ValidateDestination(f.Target));
        string occupied = f.Directory("occupied"); f.Put("occupied/keep.txt", "foreign");
        try { store.ValidateDestination(occupied); throw new Exception("Occupied destination accepted."); }
        catch (IOException error) { Equal(true, error.Message.Contains("空文件夹", StringComparison.Ordinal)); }
        try { store.ValidateDestination(Path.GetPathRoot(f.Root)!); throw new Exception("Drive root accepted."); }
        catch (IOException error) { Equal(true, error.Message.Contains("根目录", StringComparison.Ordinal) && error.Message.Contains("PCLLF-Backups", StringComparison.Ordinal)); }
        Equal(f.Profile, store.Root); Equal(false, File.Exists(Path.Combine(f.Profile, "backup-storage.json")));
        Equal("foreign", File.ReadAllText(Path.Combine(occupied, "keep.txt")));
        Equal(0, Directory.GetFileSystemEntries(f.Target).Length);
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
    private static void DedicatedPreference() {
        using Fixture f=new();f.Seed();
        string json=new JObject(new JProperty("schema",1),new JProperty("root",f.Profile)).ToString();
        f.Put("profile/backup-storage.json",json);var preference=new Preference();
        var store=new BackupStorageLocation(f.Profile,preference);
        Equal(f.Profile,preference.Value);Equal(1,preference.Writes);
        store.MoveTo(f.Target,CancellationToken.None);
        Equal(f.Target,preference.Value);Equal(json,File.ReadAllText(Path.Combine(f.Profile,"backup-storage.json")));
        f.Put("profile/backup-storage.json","obsolete invalid JSON");
        Equal(f.Target,new BackupStorageLocation(f.Profile,preference).Root);
    }
    private static void PreferenceFailure() {
        using Fixture f=new();f.Seed();var preference=new Preference { Fail=true };
        var store=new BackupStorageLocation(f.Profile,preference);
        Throws(()=>store.MoveTo(f.Target,CancellationToken.None));
        Equal(f.Profile,store.Root);Equal<string?>(null,preference.Value);Equal(0,Directory.EnumerateFileSystemEntries(f.Target).Count());
        Equal(true,File.Exists(Path.Combine(f.Profile,"world-backups/game/points/one/world.zip")));
    }
    private static void UncertainPreference() {
        using Fixture f=new();f.Seed();var preference=new Preference { Uncertain=true };
        var store=new BackupStorageLocation(f.Profile,preference);
        Throws(()=>store.MoveTo(f.Target,CancellationToken.None));
        Equal(true,File.Exists(Path.Combine(f.Profile,"world-backups/game/points/one/world.zip")));
        Equal(true,File.Exists(Path.Combine(f.Target,"world-backups/game/points/one/world.zip")));
        Equal(f.Target,new BackupStorageLocation(f.Profile,preference).Root);
    }
    private sealed class Preference:IBackupStoragePreference {
        internal string? Value;internal int Writes;internal bool Fail,Uncertain;
        public string? Read()=>Value;
        public void Write(string value,string? expected) {
            if(Value!=expected||Fail)throw new IOException("simulated preference failure");
            Value=value;Writes++;
            if(Uncertain)throw new BackupStoragePreferenceUncertainException(new IOException("simulated uncertain write"));
        }
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
