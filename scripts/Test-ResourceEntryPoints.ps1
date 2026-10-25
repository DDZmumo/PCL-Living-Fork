param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Exercises the real detail-page filter with isolated loader output; no app entry point or real profile.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='map-details-fixture'
$module=$asm.GetType('PCL.ModMain');$message=$module.GetMethod('MyMsgBox',$flags)
$result=$message.Invoke($null,[object[]]@('fixture local-world details','详情','确定',$null,$null,$false,$true,$false,$null,$null,$null))
if($result -ne 1){throw 'Single-button details did not queue normally'}
$queued=$module.GetField('WaitingMyMsgBox',$flags).GetValue($null).ToArray()|Select-Object -Last 1
if($queued.Button2 -cne '' -or $queued.Button3 -cne ''){throw 'Null button captions were not normalized'}
$project=[PCL.ResourceProject]::new([Newtonsoft.Json.Linq.JObject]::Parse('{"Tags":[],"Platform":1,"Types":8,"Slug":"fixture-shader","Id":"fixture","RawName":"Fixture shader","Description":"Fixture","Website":"https://example.invalid/fixture","LastUpdate":"2026-10-25T00:00:00Z","DownloadCount":1}'))
$build=$asm.GetType('PCL.MyResourceItem').GetMethod('BuildDetailArguments',$flags)
$enum=$asm.GetType('PCL.FormMain+PageType')
$download=[Enum]::Parse($enum,'Download');$detail=[Enum]::Parse($enum,'ResourceDetail');$instancePage=[Enum]::Parse($enum,'InstanceSetup')
$instanceType=$asm.GetType('PCL.ModMinecraft+McInstance');$instance=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]'D:\Fixture\versions\example'))
$instanceType.GetProperty('Version').SetValue($instance,[PCLCS.McVersion]::new('1.20.1',$true),$null)
$args=$build.Invoke($null,[object[]]@($project,$instancePage,$null,$null,$instance))
if($args[2] -cne '1.20.1' -or $args[4].ToString() -cne 'Shader' -or $args[3].ToString() -cne 'None'){throw 'Version-page resource entry lost type/version or added an invalid loader filter'}
foreach($empty in @($null,[object[]]@(),[object[]]@('incomplete'))){$args=$build.Invoke($null,[object[]]@($project,$detail,$null,$empty,$null));if($args[4].ToString() -cne 'Any'){throw 'Dependency route changed'}}
$request=[PCL.ResourceSearcher+SearchRequest]::new([PCLCS.ResourceTypes]::Map,[PCL.ResourceSearcher+SearchResult]::new(),40);$request.GameVersion='1.20.2';$request.ModLoaders=[PCLCS.ModLoaders]::Fabric
$args=$build.Invoke($null,[object[]]@($project,$download,$request,$null,$null))
if($args[4].ToString() -cne 'Map' -or $args[2] -cne '1.20.2' -or $args[3].ToString() -cne 'None'){throw 'Download map route did not preserve search context'}
$args=$build.Invoke($null,[object[]]@($project,$download,$null,$null,$null))
if($args[4].ToString() -cne 'Shader'){throw 'Missing search input did not fall back to project type'}
Write-Output 'Resource entry regressions passed: null-caption local details, version-page context, missing dependency arguments, download search context and missing search-input fallback. No windows or profiles accessed.'