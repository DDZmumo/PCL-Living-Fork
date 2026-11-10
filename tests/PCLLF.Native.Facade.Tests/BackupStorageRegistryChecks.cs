using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class BackupStorageRegistryChecks {
    internal static void Run(BackupResourceChecks.Scope scope) {
        string token=Guid.NewGuid().ToString("N"),keyPath=@"Software\PCLLF.Native.BackupStorageTests-"+token;
        string profile=Path.GetFullPath(Path.Combine(scope.Root,"registry-location/profile")),target=Path.GetFullPath(Path.Combine(scope.Root,"registry-location/chosen"));
        Directory.CreateDirectory(profile);Directory.CreateDirectory(target);
        var store=new BackupStorageRegistryStore(keyPath);
        Native.Check(BackupStorageRegistryStore.SubKey==@"Software\PCL Living Fork" && BackupStorageRegistryStore.ValueName=="BackupStorageRoot","Backup storage is not assigned to its dedicated LF Registry namespace.");
        Native.Check(store.Read() is null,"A fresh owned Registry location unexpectedly exists.");
        using(var absent=Registry.CurrentUser.OpenSubKey(keyPath,false))Native.Check(absent is null,"Read-only preference lookup created a Registry key.");
        using(var key=Registry.CurrentUser.CreateSubKey(keyPath,true)) {
            Native.Check(key is not null && key.GetValueNames().Length==0,"Refusing an occupied Registry fixture root.");
            key!.SetValue("FixtureOwner",token,RegistryValueKind.String);key.SetValue("ForeignSentinel",42,RegistryValueKind.DWord);
        }
        try {
            string json=new JObject(new JProperty("schema",1),new JProperty("root",profile)).ToString();
            File.WriteAllText(Path.Combine(profile,"backup-storage.json"),json);
            BackupResourceChecks.Put(Path.Combine(profile,"world-backups/example/points/a/world.zip"),"world-kept");
            BackupResourceChecks.Put(Path.Combine(profile,"environment-history/example/objects/object"),"history-kept");
            var location=new BackupStorageLocation(profile,store);
            Native.Check(store.Read()==profile,"Legacy JSON location was not imported into the dedicated Registry store.");
            location.MoveTo(target,CancellationToken.None);
            Native.Check(store.Read()==target && new BackupStorageLocation(profile,new BackupStorageRegistryStore(keyPath)).Root==target,"Dedicated location is not global across independently created readers.");
            Native.Check(File.ReadAllText(Path.Combine(profile,"backup-storage.json"))==json,"Registry path updates rewrote the compatibility JSON.");
            bool rejected=false;try{store.Write(profile,"stale expected value");}catch(IOException){rejected=true;}
            Native.Check(rejected && store.Read()==target,"A stale preference write replaced the selected path.");
            using(var key=Registry.CurrentUser.OpenSubKey(keyPath,true)) {
                Native.Check(key!.GetValueKind(BackupStorageRegistryStore.ValueName)==RegistryValueKind.String && (int)key.GetValue("ForeignSentinel")! ==42,"Registry kind or unrelated values changed.");
                key.SetValue(BackupStorageRegistryStore.ValueName,1,RegistryValueKind.DWord);
            }
            rejected=false;try{store.Read();}catch(IOException){rejected=true;}Native.Check(rejected,"An invalid Registry value was silently treated as the default.");
        } finally {
            using(var key=Registry.CurrentUser.OpenSubKey(keyPath,true)) {
                if(key is null)throw new InvalidOperationException("Owned Registry fixture disappeared.");
                Native.Check((string?)key.GetValue("FixtureOwner")==token && key.GetSubKeyNames().Length==0,"Owned Registry fixture changed; refusing cleanup.");
                foreach(string name in key.GetValueNames())key.DeleteValue(name,false);
            }
            Registry.CurrentUser.DeleteSubKey(keyPath,false);
        }
        Console.WriteLine("PASS backup storage Registry: dedicated HKCU namespace, noncreating reads, JSON import, persistent global readers, stale guards, value kinds and untouched foreign sentinels; only a GUID-owned test key was accessed.");
    }
}
