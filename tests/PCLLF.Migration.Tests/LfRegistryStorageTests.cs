using Microsoft.Win32;
using PCLCS;

internal static class LfRegistryStorageTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("LF Registry migration moves only reviewed values and preserves official accounts and preferences", Migrate);
        yield return ("LF Registry migration preserves new namespace preferences and retries idempotently", NewWins);
        yield return ("LF Registry destination failure leaves all source records intact", WriteFailure);
        yield return ("LF Registry source cleanup failure restores deleted source values", DeleteFailure);
        yield return ("LF Registry settings account and compatibility routes stay in the dedicated namespace", Routing);
    }
    private static void Migrate() {
        var f=new Fixture();f.Seed();
        f.Gateway.EnsureMigrated();
        Equal("lf-refresh",f.Lf["CacheMsV2OAuthRefresh_LF"].Value);Equal("personal",f.Lf["LaunchPersonalOptionsExcluded"].Value);
        Equal("ignored",f.Lf["ToolLauncherUpdateIgnoredTag"].Value);Equal("True",f.Lf["LivingPclSharedPreferencesMigrated"].Value);
        Equal(false,f.Pcl.ContainsKey("CacheMsV2OAuthRefresh_LF"));Equal(false,f.Pcl.ContainsKey("LaunchPersonalOptionsExcluded"));
        Equal("official-refresh",f.Pcl["CacheMsV2OAuthRefresh"].Value);Equal("official-list",f.Pcl["LoginMsJson"].Value);
        Equal("shared-seed",f.Pcl["Identify"].Value);Equal("shared-folders",f.Pcl["LaunchFolders"].Value);Equal("future",f.Pcl["FuturePlugin_LF"].Value);
        Equal(RegistryValueKind.String,f.Lf["CacheMsV2OAuthRefresh_LF"].Kind);
        Equal("path",f.Lf["BackupStorageRoot"].Value);
    }
    private static void NewWins() {
        var f=new Fixture();f.Seed();f.Lf["CacheMsV2OAuthRefresh_LF"]=("newer-session",RegistryValueKind.String);
        f.Gateway.EnsureMigrated();Equal("newer-session",f.Lf["CacheMsV2OAuthRefresh_LF"].Value);
        int writes=f.Writes;f.Gateway.EnsureMigrated();new LfRegistryStorage(f.Open).EnsureMigrated();Equal(writes,f.Writes);
    }
    private static void WriteFailure() {
        var f=new Fixture();f.Seed();f.FailSet="CacheMsV2OAuthRefresh_LF";
        Throws(f.Gateway.EnsureMigrated);Equal("lf-refresh",f.Pcl["CacheMsV2OAuthRefresh_LF"].Value);Equal("personal",f.Pcl["LaunchPersonalOptionsExcluded"].Value);
        Equal(1,f.Lf.Count);f.FailSet=null;f.Gateway.EnsureMigrated();Equal(false,f.Pcl.ContainsKey("CacheMsV2OAuthRefresh_LF"));
    }
    private static void DeleteFailure() {
        var f=new Fixture();f.Seed();f.FailDelete="CacheMsV2OAuthRefresh_LF";
        Throws(f.Gateway.EnsureMigrated);Equal("personal",f.Pcl["LaunchPersonalOptionsExcluded"].Value);Equal("lf-refresh",f.Pcl["CacheMsV2OAuthRefresh_LF"].Value);
        Equal(1,f.Lf.Count);f.FailDelete=null;f.Gateway.EnsureMigrated();Equal(false,f.Pcl.ContainsKey("CacheMsV2OAuthRefresh_LF"));
    }
    private static void Routing() {
        var f=new Fixture();f.Seed();string prefix="HKEY_CURRENT_USER\\Software\\";
        Equal("lf-refresh",f.Gateway.ReadRegistry(prefix+"PCL","CacheMsV2OAuthRefresh_LF",null));
        f.Gateway.WriteRegistry(prefix+"PCL","UiLaunchMoreSettings","False");Equal("False",f.Lf["UiLaunchMoreSettings"].Value);Equal(false,f.Pcl.ContainsKey("UiLaunchMoreSettings"));
        f.Gateway.WriteRegistry(prefix+"PCL","LaunchFolders","changed-shared");Equal("changed-shared",f.Pcl["LaunchFolders"].Value);
        using var account=f.Gateway.OpenRegistry("PCL",true,true)!;
        account.SetValue("LoginMsJson_LF","updated-lf",RegistryValueKind.String);Equal("updated-lf",f.Lf["LoginMsJson_LF"].Value);
        Equal("official-list",account.GetValue("LoginMsJson"));account.DeleteValue("CacheMsV2OAuthRefresh_LF",false);Equal(false,f.Lf.ContainsKey("CacheMsV2OAuthRefresh_LF"));
        using var legacy=f.Gateway.OpenRegistry("PCLDebug",true,false)!;legacy.SetValue("LivingPclSharedPreferencesMigrated","True",RegistryValueKind.String);
        Equal(false,f.Legacy.ContainsKey("LivingPclSharedPreferencesMigrated"));Equal("True",f.Lf["LivingPclSharedPreferencesMigrated"].Value);
    }
    private static void Equal<T>(T expected,T actual){if(!Equals(expected,actual))throw new Exception($"Expected [{expected}], got [{actual}].");}
    private static void Throws(Action action){try{action();}catch(IOException){return;}throw new Exception("Expected migration refusal.");}
    private sealed class Fixture {
        internal readonly Dictionary<string,(object Value,RegistryValueKind Kind)> Pcl=new(),Legacy=new(),Lf=new();
        internal int Writes;internal string? FailSet,FailDelete;internal readonly LfRegistryStorage Gateway;
        internal Fixture()=>Gateway=new LfRegistryStorage(Open);
        internal void Seed(){foreach(var v in new[]{("CacheMsV2OAuthRefresh_LF","lf-refresh"),("LaunchPersonalOptionsExcluded","personal"),("CacheMsV2OAuthRefresh","official-refresh"),("LoginMsJson","official-list"),("Identify","shared-seed"),("LaunchFolders","shared-folders"),("FuturePlugin_LF","future")})Pcl[v.Item1]=(v.Item2,RegistryValueKind.String);Legacy["ToolLauncherUpdateIgnoredTag"]=("ignored",RegistryValueKind.String);Legacy["LivingPclSharedPreferencesMigrated"]=("True",RegistryValueKind.String);Lf["BackupStorageRoot"]=("path",RegistryValueKind.String);}
        internal ILegacySettingsStore? Open(string folder,bool writable,bool create)=>new Store(this,folder switch {"PCL"=>Pcl,"PCLDebug"=>Legacy,LfRegistryStorage.Folder=>Lf,_=>throw new Exception("Unreviewed fixture folder.")});
        private sealed class Store:ILegacySettingsStore {
            private readonly Fixture owner;private readonly Dictionary<string,(object Value,RegistryValueKind Kind)> values;
            internal Store(Fixture owner,Dictionary<string,(object Value,RegistryValueKind Kind)> values){this.owner=owner;this.values=values;}
            public object? GetValue(string name,object? fallback=null,bool doNotExpandEnvironmentNames=false)=>values.TryGetValue(name,out var v)?v.Value:fallback;
            public RegistryValueKind GetValueKind(string name)=>values[name].Kind;
            public void SetValue(string name,object value,RegistryValueKind kind){if(ReferenceEquals(values,owner.Lf)&&name==owner.FailSet)throw new IOException("simulated destination failure");values[name]=(value,kind);owner.Writes++;}
            public void DeleteValue(string name,bool throwOnMissingValue){if(!ReferenceEquals(values,owner.Lf)&&name==owner.FailDelete)throw new IOException("simulated source cleanup failure");values.Remove(name);}
            public void Dispose(){}
        }
    }
}
