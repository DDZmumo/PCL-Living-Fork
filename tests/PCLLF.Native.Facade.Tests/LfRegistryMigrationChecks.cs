using Microsoft.Win32;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class LfRegistryMigrationChecks {
    internal static void Run() {
        string token=Guid.NewGuid().ToString("N"),parent=@"Software\PCLLF.Native.LfMigration-"+token;
        string[] folders={"PCL","PCLDebug",LfRegistryStorage.Folder};
        using(var exists=Registry.CurrentUser.OpenSubKey(parent,false))Native.Check(exists is null,"Refusing an existing Registry migration fixture.");
        using(var root=Registry.CurrentUser.CreateSubKey(parent,true)) {
            Native.Check(root is not null && root.GetValueNames().Length==0 && root.GetSubKeyNames().Length==0,"Refusing an occupied Registry migration fixture.");
            root!.SetValue("FixtureOwner",token,RegistryValueKind.String);
            foreach(string folder in folders)using(var child=root.CreateSubKey(folder,true)) { }
        }
        ILegacySettingsStore? Open(string folder,bool writable,bool create) {
            Native.Check(folders.Contains(folder,StringComparer.Ordinal),"Registry migration attempted an unowned folder.");
            string path=parent+"\\"+folder;
            var key=create?Registry.CurrentUser.CreateSubKey(path,true):Registry.CurrentUser.OpenSubKey(path,writable);
            return key is null?null:new LegacySettingsRegistryStore(key,true);
        }
        try {
            using(var official=Open("PCL",true,false)!) {
                official.SetValue("LoginMsJson","official-account-sentinel",RegistryValueKind.String);
                official.SetValue("CacheMsV2OAuthRefresh","official-session-sentinel",RegistryValueKind.String);
                official.SetValue("Identify","shared-seed",RegistryValueKind.String);
                official.SetValue("CacheMsV2OAuthRefresh_LF","opaque-lf-session",RegistryValueKind.String);
                official.SetValue("LaunchPersonalOptionsSync","True",RegistryValueKind.String);
                official.SetValue("UiLaunchMoreSettings","True",RegistryValueKind.String);
                official.SetValue("UnrelatedFutureKey",new byte[]{1,2,3},RegistryValueKind.Binary);
            }
            using(var legacy=Open("PCLDebug",true,false)!) {
                legacy.SetValue("ToolLauncherUpdateIgnoredTag","v1.0.0",RegistryValueKind.String);
                legacy.SetValue("LivingPclSharedPreferencesMigrated","True",RegistryValueKind.String);
            }
            using(var lf=Open(LfRegistryStorage.Folder,true,false)!) {
                lf.SetValue("UiLaunchMoreSettings","False",RegistryValueKind.String);
                lf.SetValue("BackupStorageRoot",@"D:\ExampleBackup",RegistryValueKind.String);
            }
            var router=new LfRegistryStorage(Open);router.EnsureMigrated();
            using(var official=Open("PCL",false,false)!)using(var lf=Open(LfRegistryStorage.Folder,false,false)!) {
                Native.Check((string?)official.GetValue("LoginMsJson")=="official-account-sentinel" && (string?)official.GetValue("CacheMsV2OAuthRefresh")=="official-session-sentinel" && (string?)official.GetValue("Identify")=="shared-seed", "Migration changed official credentials or the shared identity.");
                Native.Check(official.GetValue("CacheMsV2OAuthRefresh_LF") is null && official.GetValue("LaunchPersonalOptionsSync") is null && official.GetValue("UiLaunchMoreSettings") is null,"Verified LF source values were not removed.");
                Native.Check((string?)lf.GetValue("CacheMsV2OAuthRefresh_LF")=="opaque-lf-session" && (string?)lf.GetValue("UiLaunchMoreSettings")=="False" && (string?)lf.GetValue("BackupStorageRoot")==@"D:\ExampleBackup", "LF accounts, authoritative preferences or unrelated new records were overwritten.");
                Native.Check(official.GetValueKind("UnrelatedFutureKey")==RegistryValueKind.Binary,"Migration touched an unrelated official value.");
            }
            using(var account=router.OpenRegistry("PCL",true,true)!)account.SetValue("LoginMsJson_LF","updated-private-session",RegistryValueKind.String);
            using(var lf=Open(LfRegistryStorage.Folder,false,false)!)Native.Check((string?)lf.GetValue("LoginMsJson_LF")=="updated-private-session","Account transaction routing did not use the dedicated node.");
            var backend=Native.Field(Native.Type("PCL.Settings"),"Backend").GetValue(null)!;
            var host=Native.Get(backend,"host")!;
            Native.Check(Native.Field(Native.Type("PCL.Settings"),"LfRegistry").GetValue(null) is LfRegistryStorage,"Actual Main has no dedicated LF routing/migration service.");
            // The facade's usual platform primitives are synthetic. Rebind only
            // this existence lookup to the owned stores for a real Main call.
            var has=Native.Field(host.GetType(),"HasRegistry");object? previous=has.GetValue(host);
            try {
                has.SetValue(host,new Func<string,string,bool>(router.HasRegistry));
                Native.Check((bool)Native.Static("PCL.Settings","HasSaved","UiLaunchMoreSettings",null)!,"Actual Main did not recognize the routed private preference.");
            } finally {has.SetValue(host,previous);}
            new LfRegistryStorage(Open).EnsureMigrated();
        } finally {
            using(var root=Registry.CurrentUser.OpenSubKey(parent,true)) {
                if(root is null || (string?)root.GetValue("FixtureOwner")!=token)throw new InvalidOperationException("Owned Registry migration fixture changed; refusing cleanup.");
                foreach(string folder in folders) {
                    using(var child=root.OpenSubKey(folder,true)) {
                        if(child is null || child.GetSubKeyNames().Length!=0)throw new InvalidOperationException("Unexpected Registry fixture descendants.");
                        foreach(string name in child.GetValueNames())child.DeleteValue(name,false);
                    }
                    root.DeleteSubKey(folder,false);
                }
                root.DeleteValue("FixtureOwner",false);
            }
            Registry.CurrentUser.DeleteSubKey(parent,false);
        }
        Console.WriteLine("PASS LF Registry automatic migration: real GUID-owned stores preserve official accounts/identity/types, prefer new LF values, clean only the whitelist, route account writes and bind actual Main without touching a user namespace.");
    }
}
