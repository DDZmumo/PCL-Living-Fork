param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$ProjectFixturePath,[string]$FilesFixturePath)
# Exercises the real detail-page filter with isolated loader output; no app entry point or real profile.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='map-details-fixture'
if($ProjectFixturePath){$project=[PCL.ResourceProject]::new([Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText([IO.Path]::GetFullPath($ProjectFixturePath))))}
else{$project=[PCL.ResourceProject]::new([Newtonsoft.Json.Linq.JObject]::Parse('{"Tags":[],"Platform":1,"Types":64,"Slug":"fixture-map","Id":"684667","RawName":"Fixture map","Description":"Map fixture","Website":"https://www.curseforge.com/minecraft/worlds/fixture-map","LastUpdate":"2026-10-25T00:00:00Z","DownloadCount":1}'))}
$items=[Collections.Generic.List[PCL.ResourceVersion]]::new()
if($FilesFixturePath){foreach($data in ([Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText([IO.Path]::GetFullPath($FilesFixturePath))))['data']){$items.Add([PCL.ResourceVersion]::FromPlatformJson($data,[PCLCS.ResourceTypes]::Map))}}
else{
 foreach($version in @('1.20.2','1.20.1')){$file=[PCL.ResourceVersion]::new();$file.ResourceType=[PCLCS.ResourceTypes]::Map;$file.Id=$version;$file.FileName='map.zip';$file.GameVersions=[Collections.Generic.List[string]]::new();$file.GameVersions.Add($version);$file.DownloadUrls=[Collections.Generic.List[string]]::new();$file.DownloadUrls.Add('https://example.invalid/map.zip');$items.Add($file)}
}
if($items.Count -eq 0){throw 'Fixture has no map versions'}
$detailType=$asm.GetType('PCL.PageDownloadResourceDetail');$page=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($detailType)
$loaderType=$detailType.GetField('ResourceFileLoader',$flags).FieldType;$loader=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($loaderType)
$loaderType.GetField('Output',$flags).SetValue($loader,$items.PSObject.BaseObject)
$detailType.GetField('ResourceFileLoader',$flags).SetValue($page,$loader)
$detailType.GetField('Project',$flags).SetValue($page,$project)
# 1.2.0's map navigation omitted its type and passed enum value zero. This reproduced an empty list.
$detailType.GetField('TargetResourceType',$flags).SetValue($page,[Enum]::ToObject([PCLCS.ResourceTypes],0))
$visible=$detailType.GetMethod('GetResults',$flags).Invoke($page,@())
if($visible.Count -ne $items.Count){throw ('Map detail filtered '+$items.Count+' available versions to '+$visible.Count+' after map navigation')}
$detailType.GetField('TargetResourceType',$flags).SetValue($page,[PCLCS.ResourceTypes]::Map)
$visible=$detailType.GetMethod('GetResults',$flags).Invoke($page,@())
if($visible.Count -ne $items.Count){throw 'Explicit map details lost map files'}
$detailType.GetField('TargetResourceType',$flags).SetValue($page,[PCLCS.ResourceTypes]::ResourcePack)
if($detailType.GetMethod('GetResults',$flags).Invoke($page,@()).Count -ne $items.Count){throw 'Resource-pack filter behavior changed'}
Write-Output ('Map detail filter passed: '+$items.Count+' map files retained for map navigation and explicit type, preserving resource-pack behavior.')