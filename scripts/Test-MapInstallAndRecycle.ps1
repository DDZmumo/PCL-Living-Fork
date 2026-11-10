param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$PreviewDirectory)
# Synthetic ZIPs/worlds and offscreen native controls only; never start a game or access real profiles.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($p in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $p))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
# Only this page's private state may move from a field to a forwarding property.
# Keep DTO/control field contracts and the behavioral assertions below unchanged.
function Get-ResourcePageStateMember([type]$pageType,[string]$name){
 if($pageType.FullName -cne 'PCL.PageInstanceResources'){throw 'State hooks are limited to PageInstanceResources'}
 $member=$pageType.GetField($name,$flags)
 if($null -eq $member){$member=$pageType.GetProperty($name,$flags)}
 if($null -eq $member){throw [MissingMemberException]::new($pageType.FullName,$name)}
 return $member
}
function Get-ResourcePageStateType([type]$pageType,[string]$name){
 $member=Get-ResourcePageStateMember $pageType $name
 if($member -is [Reflection.FieldInfo]){return $member.FieldType}
 return $member.PropertyType
}
function Get-ResourcePageState($page,[string]$name){
 $target=$page.PSObject.BaseObject;$member=Get-ResourcePageStateMember ($target.GetType()) $name
 if($member -is [Reflection.FieldInfo]){return ,$member.GetValue($target)}
 return ,$member.GetValue($target,$null)
}
function Set-ResourcePageState($page,[string]$name,$value){
 $target=$page.PSObject.BaseObject;$member=Get-ResourcePageStateMember ($target.GetType()) $name
 if($null -ne $value){$value=$value.PSObject.BaseObject}
 if($member -is [Reflection.FieldInfo]){$member.SetValue($target,$value)}else{$member.SetValue($target,$value,$null)}
}
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true);$app.InitializeComponent()
# The dispatcher pump below must not follow Application.xaml's real StartupUri.
[Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_Startup',$flags)))
$app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$flags)))
[Windows.Application]::ResourceAssembly=$asm
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='map-recycle-fixture'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclMapRecycle-'+[Guid]::NewGuid().ToString('N'))
$none=[Threading.CancellationToken]::None
$store=$asm.GetType('PCL.InstanceResourceStore');$installer=$asm.GetType('PCL.MapDownloadInstaller')
$kind=[Enum]::Parse($asm.GetType('PCL.InstanceResourceKind'),'Saves')
function Call($type,[string]$method,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 try{return ,$type.GetMethod($method,$flags).Invoke($null,$values)}catch{throw ($method+': '+$_.Exception.ToString())}
}
function Put([string]$p,[string]$s){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null;[IO.File]::WriteAllText($p,$s)}
function Zip([string]$p,[hashtable]$entries){$z=[IO.Compression.ZipFile]::Open($p,[IO.Compression.ZipArchiveMode]::Create);try{foreach($name in $entries.Keys){$w=[IO.StreamWriter]::new($z.CreateEntry($name).Open());try{$w.Write($entries[$name])}finally{$w.Dispose()}}}finally{$z.Dispose()}}
function Reject([scriptblock]$work,[string]$message){$failed=$false;try{& $work|Out-Null}catch{$failed=$true};if(-not $failed){throw $message}}
function Text([string]$p,[string]$expected){if(-not [IO.File]::Exists($p) -or [IO.File]::ReadAllText($p) -cne $expected){throw ('Wrong world contents: '+$p)}}
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null;$saves=Join-Path $fixture 'game/saves'
 $archives=Call $installer 'GetDownloadFolder' @($saves)
 if($archives -ne (Join-Path $saves 'PCLLFWorldDownload') -or -not [IO.Directory]::Exists($archives)){throw 'Map archive directory was not created under saves'}
 if((Call $installer 'GetDownloadFolder' @($saves)) -ne $archives){throw 'Existing archive directory was not reused'}
 $flat=Join-Path $archives 'Flat map.zip';Zip $flat @{'level.dat'='flat-world';'region/data.txt'='region-data'}
 $hash=(Get-FileHash -LiteralPath $flat).Hash;$world=Call $installer 'Install' @($flat,$saves,$none)
 if($world -ne (Join-Path $saves 'Flat map') -or [IO.Directory]::Exists((Join-Path $archives 'Flat map'))){throw 'Map was extracted inside the ZIP download directory'}
 Text (Join-Path $world 'level.dat') 'flat-world';Text (Join-Path $world 'region/data.txt') 'region-data'
 if((Get-FileHash -LiteralPath $flat).Hash -cne $hash){throw 'Auto install changed the original downloaded ZIP'}
 $second=Call $installer 'Install' @($flat,$saves,$none)
 if($world -ceq $second){throw 'Repeat map download reused an existing world'};Text (Join-Path $world 'level.dat') 'flat-world'
 $wrapped=Join-Path $archives 'Wrapped.zip';Zip $wrapped @{'outer/world/level.dat'='wrapped-world';'outer/world/region/data.txt'='wrapped-data';'README.txt'='readme'}
 $step=$asm.GetType('PCL.PageDownloadResourceDetail').GetMethod('CreateMapInstallLoader',$flags).Invoke($null,@([string]$wrapped,[string]$saves))
 $step.WaitForExit(1,$null,$false,$none,$null,$true)
 Text (Join-Path $saves 'Wrapped/level.dat') 'wrapped-world'
 $bad=Join-Path $archives 'Bad.zip';Zip $bad @{'../outside.txt'='bad';'level.dat'='bad'};Reject {Call $installer 'Install' @($bad,$saves,$none)} 'Map traversal accepted'
 if(Test-Path -LiteralPath (Join-Path $fixture 'outside.txt')){throw 'Map wrote outside staging'}
 $multiple=Join-Path $archives 'Multiple.zip';Zip $multiple @{'one/level.dat'='one';'two/level.dat'='two'};Reject {Call $installer 'Install' @($multiple,$saves,$none)} 'Multi-world archive silently selected a world'
 $missing=Join-Path $archives 'Not a world.zip';Zip $missing @{'README.txt'='not a world'};Reject {Call $installer 'Install' @($missing,$saves,$none)} 'Map without level.dat was installed'
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel();try{Reject {Call $installer 'Install' @($flat,$saves,$cancel.Token)} 'Canceled map installed'}finally{$cancel.Dispose()}
 if(@(Get-ChildItem -LiteralPath (Join-Path $fixture 'game') -Directory -Filter '.pcl-import-*').Count){throw 'Map left incomplete staging folders'}
 Write-Output 'PASS: map download completion installs flat/wrapped worlds, preserves ZIP/existing worlds, rejects traversal/ambiguous archives and cleans canceled staging.'

 $trash=Join-Path $fixture 'game/PCL/ResourceRecycle/Saves'
 $items=Call $store 'Scan' @($saves,$saves,$kind,$none)
 if(@($items|Where-Object Name -eq 'PCLLFWorldDownload').Count){throw 'ZIP download directory is displayed as a world'}
 $matches=@($items|Where-Object {$_.IsDirectory -and $_.Name -in @('Flat map','Flat map (2)','Wrapped')})
 $targets=[Array]::CreateInstance($asm.GetType('PCL.InstanceResourceEntry'),$matches.Count)
 for($i=0;$i -lt $matches.Count;$i++){$targets.SetValue($matches[$i].PSObject.BaseObject,$i)}
 Call $store 'DeleteMany' @($saves,$targets,$none)|Out-Null
 foreach($item in $targets){if([IO.Directory]::Exists($item.FullPath)){throw 'Selected world survived direct deletion'}}
 if([IO.Directory]::Exists($trash)){throw 'Direct deletion created a resource recycle bin'}
 Write-Output 'PASS: map installation, archive preservation and direct world deletion.'
}finally{
 $app.Shutdown();$full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclMapRecycle-[a-f0-9]{32}\z'){throw 'Unsafe map/recycle fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
