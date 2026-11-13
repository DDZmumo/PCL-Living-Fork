using System.IO;
using System.IO.Compression;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JsonSerializer=System.Text.Json.JsonSerializer;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

var selected=args is ["--case",var requested]?requested:null;
var chosen=ExportChecks.Cases().Where(test=>selected is null||test.Name.Contains(selected,StringComparison.OrdinalIgnoreCase)).ToArray();
if(chosen.Length==0){Console.Error.WriteLine("No managed export check matches the requested case.");return 2;}
foreach(var test in chosen) {
    try { test.Run(); Console.WriteLine("PASS "+test.Name); }
    catch(Exception error) {Console.Error.WriteLine("FAIL "+test.Name+": "+error);return 1;}
}
Console.WriteLine("Controlled managed import/export/reimport evidence; no Minecraft/JVM/game or external network.");return 0;

internal static class ExportChecks {
    internal static IEnumerable<(string Name,Action Run)> Cases() {
        yield return ("managed MMC current environment and fixed remote/local sources round-trip for Java8/21",RoundTrip);
        yield return ("explicit player options, separate roots, privacy and unadopted library refusal",OptionsAndRoles);
        yield return ("export cancellation drains writer; stale/mutated/colliding outputs preserve existing data",Failures);
        yield return ("portable preferences preserve empty JVM override and disclose machine-dependent conversions",Preferences);
        yield return ("active/pending receipts, stale previews and a held real ZIP writer remain protected",AdmissionAndDrain);
        yield return ("published user component patch survives portable export and fresh reimport",UserPatch);
        yield return ("extra game and fullscreen become native component arguments without host identity",ExtraGameAndFullscreen);
        yield return ("literal account data is rejected while ordinary version query URLs remain portable",AccountAndUrls);
        yield return ("hardlink originals and adopted-CAS changes remain protected across output volumes",OutputAndOriginals);
        yield return ("published ZIP outcome survives a late cancelled progress observer",PublishedObserver);
        yield return ("final export argument policy rejects account root and equals-server bypasses",ArgumentReviewRegression);
        yield return ("server inheritance respects disabled package joins and explicit empty instance values",ServerReviewRegression);
        yield return ("unportable console and PermGen requirements are disclosed as adjustments, never substituted",DifferenceDisclosure);
    }
    private static void RoundTrip() {
        foreach(int major in new[]{8,21}) using(var fixture=new Fixture(major)) {
            var imported=fixture.Import("source");var paths=imported.Paths!;
            using var provider=new InstanceConfigProvider(paths.InstanceRoot);
            MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["MmcJvmArgsMode"]="Custom",["VersionAdvanceJvm"]=""});
            fixture.Put(paths.GameRoot,"config/personal.cfg","edited current configuration");
            fixture.Put(paths.GameRoot,"libraries/same.jar","game-root library bytes");
            fixture.Put(paths.GameRoot,"saves/world/level.dat","private world");fixture.Put(paths.GameRoot,"screenshots/private.png","private screenshot");fixture.Put(paths.GameRoot,"servers.dat","private server list");
            fixture.Put(paths.GameRoot,"accounts.json","DO NOT EXPORT SECRET");fixture.Put(paths.GameRoot,"config/launcher_accounts.json","DO NOT EXPORT SECRET");
            string global=Path.Combine(fixture.Root,"global-libraries");Directory.CreateDirectory(global);File.WriteAllText(Path.Combine(global,"poison.jar"),"mutable global cache is not an export source");
            var request=fixture.Request(imported,"roundtrip.zip");var exporter=new MmcInstanceExportBackend();
            var preview=exporter.PreviewAsync(request).GetAwaiter().GetResult();
            var result=exporter.ExportAsync(request with {ExpectedPreviewFingerprint=preview.Fingerprint}).GetAwaiter().GetResult();Success(result);
            using(var zip=ZipFile.OpenRead(result.OutputZipPath!)) {
                string[] names=zip.Entries.Select(e=>e.FullName).ToArray();
                Check(!names.Any(n=>n.StartsWith("PCL/",StringComparison.OrdinalIgnoreCase)||n.Contains("/saves/",StringComparison.OrdinalIgnoreCase)||n.Contains("screenshots",StringComparison.OrdinalIgnoreCase)||n.EndsWith("servers.dat",StringComparison.OrdinalIgnoreCase)||n.EndsWith("accounts.json",StringComparison.OrdinalIgnoreCase)),"Private/host data escaped export.");
                Check(Read(zip,"minecraft/config/personal.cfg")=="edited current configuration","Current game config was not exported.");
                Check(zip.GetEntry("libraries/same.jar") is not null&&Read(zip,"minecraft/libraries/same.jar")=="game-root library bytes","Three-root library roles were merged.");
                using(var local=zip.GetEntry("libraries/same.jar")!.Open()){using var bytes=new MemoryStream();local.CopyTo(bytes);Check(bytes.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(paths.LocalLibraryRoot,"same.jar"))),"Exported adopted local bytes changed.");}
                Check(names.Contains("patches/fixture.remote.json"),"Adopted remote component was not frozen locally.");
                string cfg=Read(zip,"instance.cfg");Check(cfg.Contains("OverrideJavaArgs=\"true\"")&&cfg.Contains("JvmArgs=\"\""),"Explicit empty JVM override became inheritance.");
                Check(!cfg.Contains("JavaPath")&&!cfg.Contains(paths.InstanceRoot),"Host Java/root identity leaked into portable cfg.");
            }
            var reimported=fixture.Import("target",result.OutputZipPath!);Check(reimported.Definition!.InstanceId!=imported.Definition!.InstanceId&&reimported.Definition.ParentInstanceId is null,"Export/reimport reused the LF identity.");
            var a=imported.BoundPlan!.CompiledProfile;var b=reimported.BoundPlan!.CompiledProfile;
            Check(a.MainClass==b.MainClass&&a.JvmArguments.Select(x=>x.Value).SequenceEqual(b.JvmArguments.Select(x=>x.Value))&&a.GameArguments.Select(x=>x.Value).SequenceEqual(b.GameArguments.Select(x=>x.Value))&&a.Tweakers.SequenceEqual(b.Tweakers),"Launch entry/args/tweakers changed.");
            Check(a.Classpath.Select(x=>x.Coordinate!.Name).SequenceEqual(b.Classpath.Select(x=>x.Coordinate!.Name)),"Classpath order changed.");
            Check(a.JavaPolicy.AllowedMajors.SequenceEqual(b.JavaPolicy.AllowedMajors),"Java declaration changed.");
            using var reProvider=new InstanceConfigProvider(reimported.Paths!.InstanceRoot);
            Check(MmcIniConfiguration.CapturePreferences(reProvider).Ini.GetValueOrDefault("MmcJvmArgsMode")=="UsePackage","Empty portable package override did not retain its local role after reimport.");
        }
    }
    private static void OptionsAndRoles() {
        using var fixture=new Fixture(8);var source=fixture.Import("source");
        fixture.Put(source.Paths!.GameRoot,"saves/world/level.dat","world");fixture.Put(source.Paths.GameRoot,"screenshots/p.png","shot");fixture.Put(source.Paths.GameRoot,"servers.dat","servers");
        var all=new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"with-player.zip",new(true,true,true))).GetAwaiter().GetResult();Success(all);
        using(var zip=ZipFile.OpenRead(all.OutputZipPath!))Check(zip.GetEntry("minecraft/saves/world/level.dat") is not null&&zip.GetEntry("minecraft/screenshots/p.png") is not null&&zip.GetEntry("minecraft/servers.dat") is not null,"Explicit player options were ignored.");
        fixture.Put(source.Paths.InstanceRoot,".minecraft/config/inactive.cfg","separate inactive role");
        Check(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"dual.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Inactive data silently merged or disappeared.");
        var excluded=new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"active-only.zip",new(){InactiveGameTree=MmcExportInactiveTree.Exclude})).GetAwaiter().GetResult();Success(excluded);
        fixture.Put(source.Paths.GameRoot,"personal-unrecognized.bin","personal unknown data");
        Check(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"unknown-game.zip",new(){InactiveGameTree=MmcExportInactiveTree.Exclude})).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Unknown personal game data was silently exported.");
        Success(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"explicit-unknown-game.zip",new(){InactiveGameTree=MmcExportInactiveTree.Exclude,IncludeUnrecognizedGameFiles=true})).GetAwaiter().GetResult());
        fixture.Put(source.Paths.InstanceRoot,"libraries/unadopted.jar","unadopted");
        Check(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"unknown-library.zip",new(){InactiveGameTree=MmcExportInactiveTree.Exclude})).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Unadopted private library was promoted into a pack.");
    }
    private static void Failures() {
        using var fixture=new Fixture(8);var source=fixture.Import("source");
        foreach(string mode in new[]{"cancel","source-change","collision","stale","inside","existing","original","bad-extension","missing-parent"}) {
            var request=fixture.Request(source,mode+".zip");using var cancel=new CancellationTokenSource();
            if(mode=="stale")request=request with {ExpectedSourceGeneration=source.Definition!.Generation+1};
            if(mode=="inside")request=request with {OutputZipPath=Path.Combine(source.Paths!.GameRoot,"nested.zip")};
            if(mode=="existing")File.WriteAllText(request.OutputZipPath,"keep existing");
            if(mode=="original")request=request with {OutputZipPath=fixture.Archive};
            if(mode=="bad-extension")request=request with {OutputZipPath=Path.Combine(fixture.Root,"not-a-zip.dat")};
            if(mode=="missing-parent")request=request with {OutputZipPath=Path.Combine(fixture.Root,"missing-parent","output.zip")};
            request=request with {Checkpoint=(phase,_)=>{
                if(mode=="cancel"&&phase==MmcInstanceExportCheckpoint.EntryWritten)cancel.Cancel();
                if(mode=="source-change"&&phase==MmcInstanceExportCheckpoint.BeforePublication)fixture.Put(source.Paths!.GameRoot,"config/personal.cfg","changed at publication boundary");
                if(mode=="collision"&&phase==MmcInstanceExportCheckpoint.BeforePublication)File.WriteAllText(request.OutputZipPath,"competing creator");
            }};
            var result=new MmcInstanceExportBackend().ExportAsync(request,cancel.Token).GetAwaiter().GetResult();
            Check(result.Status!=MmcInstanceExportStatus.Completed,"Unsafe/cancelled export was published: "+mode);
            if(mode=="cancel") {Check(result.Status==MmcInstanceExportStatus.Cancelled,"Cancellation status lost.");if(result.RetainedPartialFile is not null)using(new FileStream(result.RetainedPartialFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){} }
            if(mode=="collision")Check(File.ReadAllText(request.OutputZipPath)=="competing creator","Competing output was overwritten.");
            if(mode=="existing")Check(File.ReadAllText(request.OutputZipPath)=="keep existing","Existing ZIP was overwritten.");
            if(mode=="original")Check(File.Exists(fixture.Archive),"Original archive was removed.");
        }
    }
    private static void Preferences() {
        using var fixture=new Fixture(8);var source=fixture.Import("source");using var provider=new InstanceConfigProvider(source.Paths!.InstanceRoot);
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["MmcJavaLocationMode"]="Custom",["VersionArgumentJavaV2"]="1",["VersionArgumentJavaRange"]="[8,8]",["MmcMemoryMode"]="Custom",["VersionRamType"]="0"});
        var exporter=new MmcInstanceExportBackend();Check(exporter.ExportAsync(fixture.Request(source,"machine.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Machine-dependent choices were silently exported.");
        var options=new MmcInstanceExportOptions(){DiscardMachineJavaSelection=true,PortableMinimumMemoryMiB=512,PortableMaximumMemoryMiB=2048};
        var portable=exporter.ExportAsync(fixture.Request(source,"portable.zip",options)).GetAwaiter().GetResult();Success(portable);Check(portable.Preview!.Transformations.Any(x=>x.Contains("Java selection"))&&portable.Preview.Transformations.Any(x=>x.Contains("memory policy")),"Portable losses were not recorded in preview.");
    }
    private static void ArgumentReviewRegression() {
        const string macros="--username ${auth_player_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --session ${auth_session} --userProperties ${user_properties} --clientId ${clientid} --xuid ${auth_xuid}";
        using var fixture=new Fixture(21,declaredGame:macros);var source=fixture.Import("source");using var provider=new InstanceConfigProvider(source.Paths!.InstanceRoot);var exporter=new MmcInstanceExportBackend();
        string[] protectedFields={"--username","--uuid","--accessToken","--session","--userProperties","--clientId","--clientToken","--xuid","--userType","--gameDir","--assetsDir","--assetIndex"};
        int caseId=0;
        foreach(string field in protectedFields)foreach(string separator in new[]{" ","="}) {
            MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionAdvanceGame"]=field+separator+"PRIVATE-IDENTITY"});
            var request=fixture.Request(source,"identity-"+(caseId++)+".zip");
            Check(exporter.ExportAsync(request).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected&&!File.Exists(request.OutputZipPath),"Supplemental protected binding escaped: "+field+separator);
        }
        foreach(string field in new[]{"--server","--port","--quickPlayMultiplayer"})foreach(string separator in new[]{" ","="}) {
            MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionAdvanceGame"]=field+separator+"private.example"});
            Check(exporter.ExportAsync(fixture.Request(source,"server-arg-"+(caseId++)+".zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Default no-server option was bypassed by "+field+separator);
        }
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionAdvanceGame"]="--custom value"});
        var legal=exporter.ExportAsync(fixture.Request(source,"legal-macros-and-extra.zip")).GetAwaiter().GetResult();Success(legal);
        using(var zip=ZipFile.OpenRead(legal.OutputZipPath!))Check(zip.Entries.Where(entry=>entry.FullName.StartsWith("patches/",StringComparison.Ordinal)).Select(entry=>Read(zip,entry.FullName)).Any(text=>text.Contains("${clientid}")&&text.Contains("--custom value")),"Valid declared macros/custom extra were changed.");
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionAdvanceGame"]="--server=explicit.example"});
        Success(exporter.ExportAsync(fixture.Request(source,"explicit-server-arg.zip",new(IncludeServers:true))).GetAwaiter().GetResult());
    }
    private static void ServerReviewRegression() {
        foreach(bool enabled in new[]{false,true})using(var fixture=new Fixture(8,packageJoin:enabled,packageAddress:"package.example")) {
            var source=fixture.Import("source");using var provider=new InstanceConfigProvider(source.Paths!.InstanceRoot);var exporter=new MmcInstanceExportBackend();
            MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionServerEnter"]=null});
            var inherited=exporter.ExportAsync(fixture.Request(source,"package-join.zip",new(IncludeServers:true))).GetAwaiter().GetResult();Success(inherited);
            using(var archive=MmcArchiveSourceReader.ReadAsync(inherited.OutputZipPath!).GetAwaiter().GetResult()) {
                var values=archive.Package.Configuration.Values;Check(values.GetValueOrDefault("JoinServerOnLaunch")==enabled.ToString().ToLowerInvariant(),"A disabled package join was enabled by a stale address.");
                Check(enabled?values.GetValueOrDefault("JoinServerOnLaunchAddress")=="package.example":!values.ContainsKey("JoinServerOnLaunchAddress"),"Inactive/fallback server address changed.");
            }
            MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionServerEnter"]=""});
            var empty=exporter.ExportAsync(fixture.Request(source,"empty-instance-join.zip",new(IncludeServers:true))).GetAwaiter().GetResult();Success(empty);
            using(var archive=MmcArchiveSourceReader.ReadAsync(empty.OutputZipPath!).GetAwaiter().GetResult())Check(archive.Package.Configuration.Values.GetValueOrDefault("JoinServerOnLaunch")=="false"&&!archive.Package.Configuration.Values.ContainsKey("JoinServerOnLaunchAddress"),"Explicit empty instance server fell back to package address.");
            MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionServerEnter"]="instance.example"});
            var own=exporter.ExportAsync(fixture.Request(source,"own-instance-join.zip",new(IncludeServers:true))).GetAwaiter().GetResult();Success(own);
            using(var archive=MmcArchiveSourceReader.ReadAsync(own.OutputZipPath!).GetAwaiter().GetResult())Check(archive.Package.Configuration.Values.GetValueOrDefault("JoinServerOnLaunch")=="true"&&archive.Package.Configuration.Values.GetValueOrDefault("JoinServerOnLaunchAddress")=="instance.example","Explicit instance server did not override package state.");
        }
    }
    private static void OutputAndOriginals() {
        using var fixture=new Fixture(8);var source=fixture.Import("source");var exporter=new MmcInstanceExportBackend();
        byte[] original=File.ReadAllBytes(fixture.Archive);string linked=Path.Combine(fixture.Root,"outputs","original-hardlink.zip");
        Check(NativeExportLinks.CreateHardLink(linked,fixture.Archive,IntPtr.Zero),"Owned hardlink could not be created.");
        Check(exporter.ExportAsync(fixture.Request(source,"original-hardlink.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected&&File.ReadAllBytes(linked).SequenceEqual(original),"Hardlink source archive was overwritten.");
        string outputRoot=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","mmc-export-volume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outputRoot);
        string target=Path.Combine(outputRoot,"other-volume.zip");bool observed=false;
        var cross=fixture.Request(source,"unused.zip") with {OutputZipPath=target,Checkpoint=(phase,_)=>{if(phase==MmcInstanceExportCheckpoint.EntryWritten){string partial=Directory.EnumerateFiles(outputRoot,"*.partial").Single();Check(Path.GetDirectoryName(partial)==Path.GetDirectoryName(target),"Partial and final paths are on different volume/parent.");observed=true;}}};
        Success(exporter.ExportAsync(cross).GetAwaiter().GetResult());Check(observed,"Same-output-directory writer was not observed.");
        Console.WriteLine("Output source volume="+Path.GetPathRoot(fixture.Root)+" destination volume="+Path.GetPathRoot(target)+"; staging/final same parent verified.");
        var lockRecord=source.Definition!.ArtifactLocks.First();string cas=source.Paths!.ResolveRelative(MmcPathRole.Instance,lockRecord.RecoveryObjectRelativePath);byte[] saved=File.ReadAllBytes(cas);
        var tamper=fixture.Request(source,"changed-cas.zip") with {Checkpoint=(phase,_)=>{if(phase==MmcInstanceExportCheckpoint.BeforePublication)File.WriteAllText(cas,"tampered adopted CAS");}};
        Check(exporter.ExportAsync(tamper).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Failed&&!File.Exists(tamper.OutputZipPath),"Changed CAS was published.");
        File.WriteAllBytes(cas,saved);Check(File.ReadAllBytes(fixture.Archive).SequenceEqual(original),"Export changed its original source ZIP.");
    }
    private static void AccountAndUrls() {
        using(var fixture=new Fixture(8,literalAccount:true)){var source=fixture.Import("source");Check(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"private-token.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Literal account token was exported.");}
        using(var fixture=new Fixture(8,versionQuery:true)){var source=fixture.Import("source");Success(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"query-version.zip")).GetAwaiter().GetResult());}
    }
    private static void ExtraGameAndFullscreen() {
        foreach(bool tweaks in new[]{false,true}) {
        using var fixture=new Fixture(21,tweaks:tweaks);var source=fixture.Import("source");using var provider=new InstanceConfigProvider(source.Paths!.InstanceRoot);
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionAdvanceGame"]="--custom value",["MmcWindowMode"]="Custom",["MmcWindowWidth"]="1024",["MmcWindowHeight"]="768",["MmcWindowFullscreen"]="True",["MmcWindowMaximized"]="False"});
        var result=new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"extra-game.zip")).GetAwaiter().GetResult();Success(result);
        var imported=fixture.Import("extra-game-target",result.OutputZipPath!);string[] values=imported.BoundPlan!.CompiledProfile.GameArguments.Select(a=>a.Value).ToArray();
        Check(values.SequenceEqual(source.BoundPlan!.CompiledProfile.GameArguments.Select(argument=>argument.Value).Concat(new[]{"--custom","value","--fullscreen"})),"Portable extra/fullscreen changed native argument sequence.");
        Check(imported.BoundPlan.CompiledProfile.MainClass==source.BoundPlan!.CompiledProfile.MainClass,"Preference conversion changed main entry.");
        var required=MmcLaunchClassRequirements.Collect(imported.BoundPlan.CompiledProfile.MainClass,imported.BoundPlan.CompiledProfile.JvmArguments.Select(argument=>argument.Value),values,imported.BoundPlan.CompiledProfile.Tweakers);
        Check(!tweaks||required.Contains("fixture.Tweak"),"Raw tweak class escaped the shared class-requirement proof.");
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["VersionAdvanceGame"]="--title \"literal space\""});
        Check(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"cannot-encode-legacy-token.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Native legacy whitespace token was exported with wrong quote semantics.");
        }
    }
    private static void PublishedObserver() {
        using var fixture=new Fixture(8);var source=fixture.Import("source");var request=fixture.Request(source,"observer.zip");
        var result=new MmcInstanceExportBackend().ExportAsync(request,progress:new ThrowAfterPublication()).GetAwaiter().GetResult();Success(result);
        Check(File.Exists(result.OutputZipPath!),"Published ZIP disappeared after observer cancellation.");
        using var provider=new InstanceConfigProvider(source.Paths!.InstanceRoot);
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["MmcJvmArgsMode"]="Custom",["VersionAdvanceJvm"]="'unterminated"});
        Check(new MmcInstanceExportBackend().ExportAsync(fixture.Request(source,"malformed-preference.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Malformed preference faulted instead of a clear capability refusal.");
    }
    private sealed class ThrowAfterPublication:IProgress<MmcInstanceExportProgress> {
        public void Report(MmcInstanceExportProgress value){if(value.Phase=="export-completed")throw new OperationCanceledException("late owned observer cancellation");}
    }
    private static void UserPatch() {
        using var fixture=new Fixture(21,userComponent:true);var source=fixture.Import("source");
        string user="""{"uid":"fixture.user","version":"1","mainClass":"fixture.UserGame","+jvmArgs":["-Duser=changed"],"+tweakers":["fixture.Tweak"]}""";
        var updated=fixture.PublishUserPatch(source,user);var request=fixture.Request(updated,"user.zip");
        var result=new MmcInstanceExportBackend().ExportAsync(request).GetAwaiter().GetResult();Success(result);
        var imported=fixture.Import("from-user-export",result.OutputZipPath!);var actual=imported.BoundPlan!.CompiledProfile;
        Check(actual.MainClass=="fixture.UserGame"&&actual.JvmArguments.Any(a=>a.Value=="-Duser=changed")&&actual.Tweakers.SequenceEqual(updated.BoundPlan!.CompiledProfile.Tweakers),"Effective user patch was lost or overwritten.");
        Check(imported.Definition!.ComponentOverrides.IsEmpty&&imported.Definition.InstanceId!=updated.Definition!.InstanceId,"Portable effective patch retained LF override/identity authority.");
        using var zip=ZipFile.OpenRead(result.OutputZipPath!);Check(Read(zip,"patches/fixture.user.json").Contains("-Duser=changed")&&!zip.Entries.Any(e=>e.FullName.Contains("MmcOverrides",StringComparison.OrdinalIgnoreCase)),"LF private override store was exported instead of the effective MMC patch.");
    }
    private static void AdmissionAndDrain() {
        using var fixture=new Fixture(8);var source=fixture.Import("source");var exporter=new MmcInstanceExportBackend();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var cancel=new CancellationTokenSource();
        var request=fixture.Request(source,"held.zip") with {Checkpoint=(phase,_)=>{if(phase==MmcInstanceExportCheckpoint.EntryWritten&&!entered.IsSet){entered.Set();release.Wait();}}};
        var work=exporter.ExportAsync(request,cancel.Token);Check(entered.Wait(10000),"ZIP writer did not reach the owned barrier.");
        var watch=System.Diagnostics.Stopwatch.StartNew();cancel.Cancel();watch.Stop();Check(watch.ElapsedMilliseconds<500&&!work.IsCompleted,"Cancel synchronously blocked or returned before the actual ZIP writer stopped.");
        string partial=Directory.EnumerateFiles(Path.GetDirectoryName(request.OutputZipPath)!,"*.partial").Single();
        bool held=false;try{using var exclusive=new FileStream(partial,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}catch(IOException){held=true;}Check(held,"Actual ZIP file was not held by the blocked writer.");
        release.Set();var cancelled=work.GetAwaiter().GetResult();Check(cancelled.Status==MmcInstanceExportStatus.Cancelled,"Held writer cancellation was not observed.");using(new FileStream(partial,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        var originalRequest=fixture.Request(source,"preview.zip");var preview=exporter.PreviewAsync(originalRequest).GetAwaiter().GetResult();using var provider=new InstanceConfigProvider(source.Paths!.InstanceRoot);
        MmcIniConfiguration.QueuePreferenceBatch(provider,new Dictionary<string,string?>{["MmcJvmArgsMode"]="Custom",["VersionAdvanceJvm"]="-Dchanged=yes"});
        Check(exporter.ExportAsync(originalRequest with {ExpectedPreviewFingerprint=preview.Fingerprint}).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"A stale preference preview was accepted.");
        fixture.Put(source.Paths.InstanceRoot,"PCL/MmcSessions/unknown.record","uncertain receipt");
        Check(exporter.ExportAsync(fixture.Request(source,"active.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Unknown/active session receipt was ignored.");
        Check(File.ReadAllText(Path.Combine(source.Paths.InstanceRoot,"PCL","MmcSessions","unknown.record"))=="uncertain receipt","Export changed an uncertain receipt.");
        File.Delete(Path.Combine(source.Paths.InstanceRoot,"PCL","MmcSessions","unknown.record"));
        fixture.Put(source.Paths.InstanceRoot,"PCL/MmcTransactions/unknown.record","pending transaction");
        Check(exporter.ExportAsync(fixture.Request(source,"pending.zip")).GetAwaiter().GetResult().Status==MmcInstanceExportStatus.Rejected,"Pending transaction was ignored.");
        Check(File.ReadAllText(Path.Combine(source.Paths.InstanceRoot,"PCL","MmcTransactions","unknown.record"))=="pending transaction","Export changed pending transaction data.");
        // Only remove this fixture's deliberately injected blocker after proving it was kept.
        // Queued preference writers must be allowed to finish before provider teardown.
        File.Delete(Path.Combine(source.Paths.InstanceRoot,"PCL","MmcTransactions","unknown.record"));
    }
    private static string Read(ZipArchive zip,string path){using var reader=new StreamReader(zip.GetEntry(path)!.Open());return reader.ReadToEnd();}
    private static void Success(MmcInstanceExportResult result){if(result.Status!=MmcInstanceExportStatus.Completed)throw new Exception(result.Status+": "+result.Failure);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}

    // The console/PermGen groups have no equivalent here, so an export cannot carry them. The requirement
    // must be disclosed as an adjustment rather than vanish, and the notice must never read as a conversion
    // into a capability this launcher does not have.
    private static void DifferenceDisclosure() {
        using var fixture=new Fixture(8,extraCfg:"OverrideConsole=true\nShowConsole=true\nPermGen=256\n");
        var source=fixture.Import("source");var exporter=new MmcInstanceExportBackend();
        var exported=exporter.ExportAsync(fixture.Request(source,"differences.zip")).GetAwaiter().GetResult();Success(exported);
        var adjustments=exported.Preview!.Transformations;
        Check(adjustments.Any(value=>value.Contains("console preference")&&value.Contains("not a launcher-visibility setting")),"An unportable console requirement disappeared without disclosure.");
        Check(adjustments.Any(value=>value.Contains("PermGen")&&value.Contains("not converted into a Metaspace")),"An unportable PermGen requirement disappeared or was reported as a conversion.");
        // The declared fields themselves are not silently re-expressed as another option in the exported cfg.
        using(var zip=ZipFile.OpenRead(exported.OutputZipPath!)) {
            string cfg=Read(zip,"instance.cfg");
            Check(!cfg.Contains("Metaspace",StringComparison.OrdinalIgnoreCase)&&!cfg.Contains("PermSize",StringComparison.OrdinalIgnoreCase),"A PermGen value became a substitute in the portable cfg.");
            Check(!cfg.Contains("ConsoleWindow")&&!cfg.Contains("LaunchArgumentVisible"),"A console preference was rewritten as a launcher-visibility setting.");
        }
        // Adisclosed export still stands alone as a portable MMC archive.
        Success(exporter.ExportAsync(fixture.Request(source,"differences-again.zip")).GetAwaiter().GetResult());
    }
    private sealed class Fixture:IDisposable {
        internal string Root{get;}=Path.Combine(Path.GetTempPath(),"PCLLF-MmcExportTests-"+Guid.NewGuid().ToString("N"));
        internal string Archive=>Path.Combine(Root,"original.zip");private readonly HttpClient client;private readonly int major;
        private readonly byte[] index=Encoding.UTF8.GetBytes("{\"objects\":{}}");private readonly byte[] shared=JarResource("remote-resource.txt","self-authored remote bytes");
        internal Fixture(int major,bool userComponent=false,bool tweaks=true,bool literalAccount=false,bool versionQuery=false,string? declaredGame=null,bool packageJoin=false,string? packageAddress=null,string extraCfg="") {
            this.major=major;Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"export-test-owner.json"),"PCLLF owned export fixture / "+Path.GetFileName(Root));Directory.CreateDirectory(Path.Combine(Root,"outputs"));
            string indexSha=Convert.ToHexStringLower(SHA1.HashData(index));string sharedSha=Convert.ToHexStringLower(SHA1.HashData(shared));
            string remote=new JObject{["formatVersion"]=1,["uid"]="fixture.remote",["version"]="1",["+jvmArgs"]=new JArray("-Dremote=yes"),["libraries"]=new JArray(new JObject{["name"]="fixture:shared:1",["downloads"]=new JObject{["artifact"]=new JObject{["url"]=versionQuery?"https://download.invalid/shared.jar?version=1":"https://download.invalid/shared.jar",["sha1"]=sharedSha,["size"]=shared.Length}}})}.ToString(Newtonsoft.Json.Formatting.None);
            client=new HttpClient(new Metadata(remote));
            using var zip=ZipFile.Open(Archive,ZipArchiveMode.Create);
            var declarations=new JArray(new JObject{["uid"]="net.minecraft",["version"]="1.7.10"},new JObject{["uid"]="fixture.remote",["version"]="1"});if(userComponent)declarations.Add(new JObject{["uid"]="fixture.user",["version"]="1"});Add("mmc-pack.json",new JObject{["formatVersion"]=1,["components"]=declarations}.ToString(Newtonsoft.Json.Formatting.None));
            if(userComponent)Add("patches/fixture.user.json","""{"uid":"fixture.user","version":"1","+jvmArgs":["-Duser=author"]}""");
            Add("instance.cfg","name=Source\nOverrideJavaArgs=true\nJvmArgs=-Dpackage=yes\nOverrideMemory=true\nMinMemAlloc=512\nMaxMemAlloc=2048\n"+(packageAddress is null?"":"JoinServerOnLaunch="+packageJoin.ToString().ToLowerInvariant()+"\nJoinServerOnLaunchAddress="+packageAddress+"\n")+extraCfg);
            Add("patches/net.minecraft.json",new JObject{["uid"]="net.minecraft",["version"]="1.7.10",["mainClass"]="fixture.Game",["minecraftArguments"]=declaredGame??(literalAccount?"--accessToken PRIVATE-LAUNCHER-TOKEN":"--sample value"),["+jvmArgs"]=new JArray("-Dcustom=yes"),["+tweakers"]=tweaks?new JArray("fixture.Tweak"):new JArray(),["compatibleJavaMajors"]=major==8?new JArray(8):new JArray(17,21),["mainJar"]=new JObject{["name"]="fixture:client:1",["MMC-hint"]="local",["MMC-filename"]="client.jar"},["libraries"]=new JArray(new JObject{["name"]="fixture:local:1",["MMC-hint"]="local",["MMC-filename"]="same.jar"}),["assetIndex"]=new JObject{["id"]="fixture",["url"]="https://download.invalid/index.json",["sha1"]=indexSha,["size"]=index.Length}}.ToString(Newtonsoft.Json.Formatting.None));
            AddBytes("libraries/client.jar",JarBytes());AddBytes("libraries/same.jar",JarResource("local-resource.txt","private adopted library"));AddBytes("libraries/orphan.jar",JarResource("orphan-resource.txt","preserved inactive library"));Add("minecraft/config/personal.cfg","author configuration");
            void Add(string path,string value)=>AddBytes(path,Encoding.UTF8.GetBytes(value));
            void AddBytes(string path,byte[] value){using var output=zip.CreateEntry(path).Open();output.Write(value);}
        }
        internal MmcInstanceBackendResult Import(string name,string? archive=null) {
            string root=Path.Combine(Root,name+"-cache");
            var backend=new MmcInstanceBackend<MmcArtifactDownloadSpec>(new MmcMetadataStore(Path.Combine(root,"metadata"),new Uri("https://metadata.invalid/v1/"),client),new(Path.Combine(root,"libraries"),Path.Combine(root,"assets"),Path.Combine(root,"sources")),spec=>spec,
                async(files,token)=>{foreach(var file in files){Directory.CreateDirectory(Path.GetDirectoryName(file.DownloadPath)!);await File.WriteAllBytesAsync(file.DownloadPath,file.Artifact.Role==MmcArtifactRole.AssetIndex?index:shared,token);}});
            var result=backend.ImportAsync(new(archive??Archive,Path.Combine(Root,"game","versions",name),name,new("windows","x86_64",major))).GetAwaiter().GetResult();
            if(result.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception("Controlled import failed: "+result.Failure);return result;
        }
        internal MmcInstanceBackendResult PublishUserPatch(MmcInstanceBackendResult source,string text) {
            var paths=source.Paths!;var prior=source.Definition!;var saved=MmcSavedSourceStore.LoadAsync(paths,prior).GetAwaiter().GetResult();
            string hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),relative="PCL/MmcOverrides/"+hash+".json";
            var resolution=MmcComponentResolver.Resolve(saved.Package,saved.Resolved.Snapshots,[new MmcComponentOverride("fixture.user",PatchContentUtf8:text)]);
            Check(resolution.IsComplete,"User source resolution failed.");var compiled=MmcProfileCompiler.Compile(resolution.Profile!,source.BoundPlan!.CompiledProfile.Environment).Profile!;
            var targets=prior.ArtifactLocks.ToDictionary(item=>item.ArtifactKey,item=>item.TargetRelativePath,StringComparer.Ordinal);
            var oldJson=JsonNode.Parse(File.ReadAllText(Path.Combine(paths.InstanceRoot,"source.json")))!;string assets=oldJson["pcllfRuntime"]!["assetsRoot"]!.GetValue<string>();
            byte[] projection=MmcVersionProjection.Render("source",prior.InstanceId,2,compiled,targets,null,assets);string projectionHash=Convert.ToHexStringLower(SHA256.HashData(projection));
            var candidate=prior with {InstalledProjection=null,SourceBundle=prior.SourceBundle with {ResolutionFingerprint=resolution.Profile!.SourceFingerprint},ComponentOverrides=[new("fixture.user",PatchReference:new(hash,relative))]};
            var facts=prior.ArtifactLocks.Select(item=>new MmcMaterializedArtifactFact(item.ArtifactKey,item.SourceRevision,item.AdoptedSha256,item.SizeBytes,item.RecoveryObjectRelativePath,item.TargetRelativePath,item.ObservedSha1)).ToArray();
            var asset=source.BoundPlan.CompiledProfile.Assets.Artifact!;var assetLock=prior.ArtifactLocks.Single(item=>item.ArtifactKey==asset.Key);
            var bound=MmcPlanBinder.Bind(candidate,compiled,facts,new(1,2,MmcBindingIntent.UpdateDefinition,projectionHash,2),assetIndexes:[new(asset.Key,File.ReadAllBytes(paths.ResolveRelative(MmcPathRole.Instance,assetLock.RecoveryObjectRelativePath)))],publishedBaseline:prior);
            Check(!bound.HasErrors,"User binding failed: "+string.Join(";",bound.Diagnostics.Select(d=>d.Message)));
            byte[] primary=Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition!));string primaryHash=Convert.ToHexStringLower(SHA256.HashData(primary));
            using var quiet=MmcConfigurationCoordinator.Quiesce(paths.InstanceRoot);using var gate=MmcAdmissionGate.Acquire(paths.InstanceRoot,prior.InstanceId,TimeSpan.Zero);using var scope=quiet.OpenStructuralWrite(gate,1);
            var manifest=(JObject)scope.ConfigSnapshot["MinecraftInstallManifest"]!.DeepClone();manifest["pcllfManagement"]=JObject.Parse(MmcVersionProjection.ManagementHint(prior.InstanceId,2,compiled).ToJsonString());scope.SetConfig("MinecraftInstallManifest",manifest);
            var result=scope.Execute(new(prior.InstanceId,1,2,[new(relative,null,Encoding.UTF8.GetBytes(text),hash),new("source.json",prior.InstalledProjection!.OutputJsonSha256,projection,projectionHash),new("PCL/InstanceDefinition.json",MmcSavedSourceStore.ReadPrimary(paths).Sha256,primary,primaryHash)]));
            Check(result.Outcome==MmcTransactionOutcome.Committed,"User patch transaction did not commit.");return source with {Definition=bound.Definition,BoundPlan=bound.Plan};
        }
        internal MmcInstanceExportRequest Request(MmcInstanceBackendResult source,string file,MmcInstanceExportOptions? options=null)=>new(source.Paths!.InstanceRoot,Path.Combine(Root,"outputs",file),"Portable Instance",source.Definition!.InstanceId,source.Definition.Generation,MmcSavedSourceStore.ReadPrimary(source.Paths).Sha256,options??new());
        internal void Put(string root,string relative,string value){string path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,value);}
        private static byte[] JarResource(string name,string value){using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){using var stream=zip.CreateEntry(name).Open();stream.Write(Encoding.UTF8.GetBytes(value));}return memory.ToArray();}
        private static byte[] JarBytes(){using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){foreach(string name in new[]{"fixture/Game","fixture/UserGame","fixture/Tweak"}){using var stream=zip.CreateEntry(name+".class").Open();stream.Write(Class(name));}}return memory.ToArray();}
        private static byte[] Class(string name){
            using var m=new MemoryStream();void U1(int n)=>m.WriteByte((byte)n);void U2(int n){U1(n>>8);U1(n);}void U4(int n){U2(n>>16);U2(n);}void Utf(string text){U1(1);byte[] b=Encoding.UTF8.GetBytes(text);U2(b.Length);m.Write(b);}
            U4(unchecked((int)0xcafebabe));U2(0);U2(52);U2(12);Utf(name);U1(7);U2(1);Utf("java/lang/Object");U1(7);U2(3);Utf("<init>");Utf("()V");Utf("Code");U1(12);U2(5);U2(6);U1(10);U2(4);U2(8);Utf("main");Utf("([Ljava/lang/String;)V");
            U2(0x21);U2(2);U2(4);U2(0);U2(0);U2(2);U2(1);U2(5);U2(6);U2(1);U2(7);U4(17);U2(1);U2(1);U4(5);m.Write(new byte[]{0x2a,0xb7,0,9,0xb1});U2(0);U2(0);
            U2(9);U2(10);U2(11);U2(1);U2(7);U4(13);U2(0);U2(1);U4(1);U1(0xb1);U2(0);U2(0);U2(0);return m.ToArray();
        }

        public void Dispose(){client.Dispose();InstanceConfigProviders.FlushAndDisposeWithin(Root);Console.WriteLine("RETAINED owned export fixture: "+Root);}

    }
    private sealed class Metadata(string json):HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});
    }
}

internal static class NativeExportLinks {
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string newName,string existingName,IntPtr security);
}
