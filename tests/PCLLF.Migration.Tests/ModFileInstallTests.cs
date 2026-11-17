// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using PCLCS;

internal static class ModFileInstallTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("mod download replaces the chosen identity regardless of version order or file age", ReplaceChosen);
        yield return ("mod download preserves unknown bundles other IDs disabled state and target conflicts", IdentityAndDisabled);
        yield return ("mod download cancellation restores files already moved into its private transaction", Rollback);
        yield return ("duplicate mods use platform-qualified projects and root IDs without names or nested aliases", Duplicates);
        yield return ("verified project identity supports renamed IDs while unrecognized incoming files retain originals", VerifiedProject);
        yield return ("concurrent mod downloads serialize commits and retain one selected version", ConcurrentCommits);
        yield return ("external file edits before commit stop replacement without losing those edits", ChangedFile);
        yield return ("explicit required dependency installation enables its matching disabled file", EnableDependency);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void EnableDependency() {
        using Fixture f = new(); string disabled = f.File("library.jar.disabled", "library|1");
        string incoming = f.File("incoming.jar", "library|2", false);
        var result = f.Install.Install(incoming, Path.Combine(f.Mods, "library.jar"), enable: true);
        Check(result.Destination.EndsWith("library.jar", StringComparison.Ordinal) && !File.Exists(disabled) &&
            File.ReadAllText(result.Destination) == "library|2", "Confirmed required dependency remained disabled.");
    }
    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pcllf-mod-install-" + Guid.NewGuid().ToString("N"));
        internal readonly string Mods;
        internal readonly ModFileInstall Install = new((path, token) => {
            token.ThrowIfCancellationRequested(); string[] parts = System.IO.File.ReadAllText(path).Split('|');
            return parts[0] == "unknown" ? null : parts[0].Split(',');
        });
        internal Fixture() { Mods = Path.Combine(Root,"mods"); Directory.CreateDirectory(Mods); }
        internal string File(string name,string data,bool mod=true) {
            string path=Path.Combine(mod?Mods:Root,name); System.IO.File.WriteAllText(path,data);return path;
        }
        public void Dispose() { Directory.Delete(Root,true); }
    }
    private static void ReplaceChosen() {
        using Fixture f=new(); string old=f.File("library-v9.jar","library|9");
        f.File("library-v1.jar","library|1"); string other=f.File("unrelated.jar","other|1");
        File.SetCreationTimeUtc(old,DateTime.UtcNow.AddDays(1));
        string download=f.File("incoming.jar","library|2",false);
        var result=f.Install.Install(download,Path.Combine(f.Mods,"chosen-v2.jar"));
        Check(result.ReplacedFiles==2&&File.ReadAllText(result.Destination)=="library|2"&&File.ReadAllText(other)=="other|1",
            "Explicit downgrade kept an old/new version based on timestamps or removed another mod.");
        Check(Directory.GetFiles(f.Mods).Length==2&&Directory.GetDirectories(f.Mods).Length==0,"Successful install left stale mod versions or transaction backups.");
    }
    private static void IdentityAndDisabled() {
        using Fixture f=new(); f.File("same.jar.disabled","same|1"); f.File("bundle.jar","same,extra|1");
        string unknown=f.File("unknown.jar","unknown|same"); string download=f.File("incoming.jar","same|2",false);
        var result=f.Install.Install(download,Path.Combine(f.Mods,"same-v2.jar"));
        Check(result.Destination.EndsWith(".disabled")&&result.ReplacedFiles==1&&File.Exists(unknown)&&File.Exists(Path.Combine(f.Mods,"bundle.jar")),
            "Disabled state, unknown metadata or a larger multi-mod bundle was discarded.");
        string occupied=f.File("occupied.jar","different|1");
        bool refused=false;try{f.Install.Install(download,occupied);}catch(IOException){refused=true;}
        Check(refused&&File.ReadAllText(occupied)=="different|1"&&File.Exists(result.Destination),"An unrelated same-name target was overwritten.");
    }
    private static void Rollback() {
        using Fixture f=new(); string first=f.File("first.jar","same|1"),second=f.File("second.jar","same|2");
        string download=f.File("incoming.jar","same|3",false);using var cancel=new CancellationTokenSource();
        bool stopped=false;
        try { f.Install.Install(download,Path.Combine(f.Mods,"new.jar"),cancel.Token,(done,total)=> {
            if(Directory.GetDirectories(f.Mods,".pcl-mod-install-*").Length>0&&done==1)cancel.Cancel();
        }); } catch(OperationCanceledException){stopped=true;}
        Check(stopped&&File.ReadAllText(first)=="same|1"&&File.ReadAllText(second)=="same|2"&&Directory.GetFiles(f.Mods).Length==2&&Directory.GetDirectories(f.Mods).Length==0,
            "Canceled replacement lost original files or published the incoming version.");
    }
    private static void Duplicates() {
        string root=Path.GetTempPath(); string P(string name)=>Path.Combine(root,name);
        var files=new[]{
            new ModFileIdentity{Path=P("a.jar"),ProjectKey="CurseForge:123"},
            new ModFileIdentity{Path=P("b.jar"),ProjectKey="CurseForge:123"},
            new ModFileIdentity{Path=P("c.jar"),ProjectKey="Modrinth:123"},
            new ModFileIdentity{Path=P("d.jar"),RootIds=new[]{"library"}},
            new ModFileIdentity{Path=P("e.jar.disabled"),RootIds=new[]{"library"}},
            new ModFileIdentity{Path=P("unknown.jar")}
        };
        var duplicate=ModDuplicateRules.Find(files);
        Check(duplicate.SetEquals(new[]{P("a.jar"),P("b.jar"),P("d.jar"),P("e.jar.disabled")}),
            "Duplicate detection mixed platform IDs or inferred identity for unknown files.");
    }
    private static void VerifiedProject() {
        using Fixture f=new();string old=f.File("old.jar","old_id|1"),download=f.File("incoming.jar","new_id|2",false);
        var install=new ModFileInstall((path,_)=>File.ReadAllText(path).Split('|')[0].Split(','),(path,_)=>path==old);
        var result=install.Install(download,Path.Combine(f.Mods,"new.jar"));
        Check(result.ReplacedFiles==1&&!File.Exists(old),"An exact project proof could not bridge a root-ID rename.");
        string unknown=f.File("unknown-source.jar","unknown|3",false);
        var conservative=new ModFileInstall((path,_)=>path==unknown?null:new[]{"new_id"},(_,_)=>true);
        var saved=conservative.Install(unknown,Path.Combine(f.Mods,"unknown.jar"));
        Check(saved.ReplacedFiles==0&&File.Exists(result.Destination),"An unrecognized download deleted the known good version.");
    }
    private static void ConcurrentCommits() {
        using Fixture f=new();f.File("original.jar","same|1");string a=f.File("a.jar","same|2",false),b=f.File("b.jar","same|3",false);
        var first=Task.Run(()=>f.Install.Install(a,Path.Combine(f.Mods,"selected-a.jar")));
        var second=Task.Run(()=>f.Install.Install(b,Path.Combine(f.Mods,"selected-b.jar")));
        Task.WaitAll(first,second);
        Check(Directory.GetFiles(f.Mods).Length==1&&Directory.GetDirectories(f.Mods).Length==0,"Concurrent commits raced or left two active copies.");
    }
    private static void ChangedFile() {
        using Fixture f=new();string old=f.File("original.jar","same|1"),download=f.File("incoming.jar","same|2",false);bool edited=false,stopped=false;
        try { f.Install.Install(download,Path.Combine(f.Mods,"new.jar"),default,(done,total)=>{
            if(!edited){edited=true;File.WriteAllText(old,"other|external edit");}
        }); } catch(IOException){stopped=true;}
        Check(stopped&&File.ReadAllText(old)=="other|external edit"&&Directory.GetFiles(f.Mods).Length==1,"External edits were overwritten using an old identity check.");
    }
}
