param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Synthetic environment history only; configuration values must never appear in the report.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $repo $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$history=$asm.GetType('PCL.EnvironmentHistory',$true);$changes=$asm.GetType('PCL.RecentEnvironmentChanges',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='recent-environment-fixture'
function Invoke-Engine([type]$type,[string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try{return ,$type.GetMethod($name,$flags).Invoke($null,$values)}catch{throw ($name+': '+$_.Exception.ToString())}
}
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};if(-not $caught){throw $message}}
function Assert-Change($report,[string]$path,[string]$kind){$row=@($report.Changes|Where-Object {$_.Relative -eq $path -and $_.Kind -match $kind});if($row.Count -ne 1){throw ('Missing/duplicate change: '+$path+' '+$kind)}}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclRecentChanges-'+[Guid]::NewGuid().ToString('N'))
$token=[Threading.CancellationToken]::None;$junction=$null
try{
 $history.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'history'),$null)
 $version=Join-Path $fixture 'minecraft/versions/example';$game=$version
 Put (Join-Path $version 'example.json') '{"id":"example","inheritsFrom":"1.20.1"}'
 Put (Join-Path $version 'mods/update-1.0.jar') 'old-update'
 Put (Join-Path $version 'mods/disabled.jar') 'same-disable-content'
 Put (Join-Path $version 'mods/re-enabled.jar.old') 'same-enable-content'
 Put (Join-Path $version 'mods/content.jar') 'old-content'
 Put (Join-Path $version 'config/private.toml') 'api_key=fixture-private-before'
 Put (Join-Path $version 'config/deleted.toml') 'old-config'
 Put (Join-Path $version 'saves/world/level.dat') 'original-world'
 Put (Join-Path $version 'screenshots/fixture.png') 'original-shot'
 Put (Join-Path $version 'config/xaero/waypoints.txt') 'original-markers'
 $empty=Invoke-Engine $changes 'Build' @($version,$game,$token)
 if($empty.HasBaseline -or $empty.ToAnalysisText() -notmatch '尚无正常运行记录'){throw 'No-baseline state is misleading'}
 $healthy=Invoke-Engine $history 'Capture' @($version,$game,'healthy',$token,$null,'fixture baseline')
 Invoke-Engine $history 'MarkHealthy' @($version,$healthy,$true)|Out-Null
 $unchanged=Invoke-Engine $changes 'Build' @($version,$game,$token)
 if(-not $unchanged.HasBaseline -or $unchanged.BaselineId -cne $healthy -or $unchanged.Changes.Count -ne 0){throw 'Unchanged baseline mismatch'}
 $unhealthy=Invoke-Engine $history 'Capture' @($version,$game,'newer unhealthy',$token,$null,'not healthy')
 Move-Item -LiteralPath (Join-Path $version 'mods/disabled.jar') -Destination (Join-Path $version 'mods/disabled.jar.disabled')
 Move-Item -LiteralPath (Join-Path $version 'mods/re-enabled.jar.old') -Destination (Join-Path $version 'mods/re-enabled.jar')
 Remove-Item -LiteralPath (Join-Path $version 'mods/update-1.0.jar')
 Put (Join-Path $version 'mods/update-2.0.jar') 'new-update'
 Put (Join-Path $version 'mods/content.jar') 'new-content'
 Put (Join-Path $version 'mods/added.jar.disabled') 'new-disabled'
 Remove-Item -LiteralPath (Join-Path $version 'config/deleted.toml')
 Put (Join-Path $version 'config/private.toml') 'api_key=fixture-private-after'
 Put (Join-Path $version 'config/new.toml') 'new-config'
 Put (Join-Path $version 'example.json') '{"id":"example","inheritsFrom":"1.21.1"}'
 Put (Join-Path $version 'saves/world/level.dat') 'world-changed'
 Put (Join-Path $version 'screenshots/fixture.png') 'shot-changed'
 Put (Join-Path $version 'config/xaero/waypoints.txt') 'markers-changed'
 $report=Invoke-Engine $changes 'Build' @($version,$game,$token)
 if($report.BaselineId -cne $healthy -or $report.BaselineId -ceq $unhealthy -or -not $report.BaselineCreatedUtc){throw 'Latest healthy baseline was not selected'}
 Assert-Change $report 'g/mods/disabled.jar.disabled' '禁用'
 Assert-Change $report 'g/mods/re-enabled.jar' '^启用'
 Assert-Change $report 'g/mods/update-2.0.jar' '文件版本变化'
 Assert-Change $report 'g/mods/content.jar' '更新内容'
 Assert-Change $report 'g/mods/added.jar.disabled' '新增'
 Assert-Change $report 'g/config/private.toml' '修改'
 Assert-Change $report 'g/config/deleted.toml' '删除'
 Assert-Change $report 'g/config/new.toml' '新增'
 Assert-Change $report 'v/example.json' '修改'
 $text=$report.ToAnalysisText()
 if($text -match 'fixture-private|api_key|original-world|world-changed|shot-changed|waypoints|markers-changed' -or $text.Contains($fixture)){throw 'Report exposed configuration/account/personal values or absolute paths'}
 if($text -notmatch '不能据此确定崩溃原因' -or $text -notmatch '1.0' -or $text -notmatch '2.0'){throw 'Report lacks uncertainty or version information'}
 $appended=Invoke-Engine $changes 'AppendAnalysis' @('fixture crash reason',$report)
 if(-not $appended.StartsWith('fixture crash reason') -or $appended -notmatch '上次正常运行后改了什么'){throw 'Crash analysis appendix failed'}
 $other=Join-Path $fixture 'different-game-root';[IO.Directory]::CreateDirectory($other)|Out-Null
 $mismatch=Invoke-Engine $changes 'Build' @($version,$other,$token)
 if($mismatch.HasBaseline -or $mismatch.ToAnalysisText() -notmatch '不同的游戏目录'){throw 'Isolation mismatch produced a false comparison'}
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 Reject {Invoke-Engine $changes 'Build' @($version,$game,$cancel.Token)} 'Canceled comparison completed'
 $outside=Join-Path $fixture 'outside';Put (Join-Path $outside 'keep.txt') 'keep-outside'
 $junction=Join-Path $version 'config/linked';New-Item -ItemType Junction -Path $junction -Target $outside|Out-Null
 Reject {Invoke-Engine $changes 'Build' @($version,$game,$token)} 'Linked config directory was scanned'
 [IO.Directory]::Delete($junction);$junction=$null
 if([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -cne 'keep-outside'){throw 'Comparison modified external linked data'}
 Write-Output 'Recent changes passed: no baseline, unchanged environment, latest healthy selection, mod update/enable/disable/old status, added/removed config, Minecraft change, relative-only private-value-free analysis, append API, isolation mismatch, cancellation and reparse rejection.'
}finally{
 if($junction -and [IO.Directory]::Exists($junction)){[IO.Directory]::Delete($junction)}
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('PclRecentChanges-')){if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}}
}
