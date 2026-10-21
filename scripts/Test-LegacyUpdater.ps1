param([Parameter(Mandatory=$true)][string]$ExecutablePath,[Parameter(Mandatory=$true)][string]$LegacyExecutablePath)
# Invoke only the published updater's pure validation/replacement methods; no application entry point or restart.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')){
 [Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null
}
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$LegacyExecutablePath=[IO.Path]::GetFullPath($LegacyExecutablePath)
$legacy=[Reflection.Assembly]::LoadFrom($LegacyExecutablePath)
$updater=$legacy.GetType('PCL.ModLauncherUpdate',$true)
$flags=[Reflection.BindingFlags]'NonPublic,Public,Static'
$current=[Diagnostics.FileVersionInfo]::GetVersionInfo($LegacyExecutablePath).ProductVersion
$latest=[Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).ProductVersion
if(-not $updater.GetMethod('NewerRelease',$flags).Invoke($null,@(('v'+$latest),$current))){throw 'Published updater did not detect the newer version'}
$name='LivingPCL2-Re-'+$latest+'.exe'
$url='https://github.com/DDZmumo/LivingPCL2-Re/releases/download/v'+$latest+'/'+$name
$json=[Newtonsoft.Json.Linq.JObject]::Parse((@{assets=@(@{name=$name;browser_download_url=$url})}|ConvertTo-Json -Depth 4))
$selected=$updater.GetMethod('ReleaseExecutable',$flags).Invoke($null,[object[]](,$json))
if($selected['name'].ToString() -cne $name){throw 'Published updater selected the wrong asset'}
$hash=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest=$hash+'  '+$name
$parsed=$updater.GetMethod('ManifestHash',$flags).Invoke($null,@($manifest,$name))
$updater.GetMethod('ValidateExecutable',$flags).Invoke($null,@($ExecutablePath,$parsed))|Out-Null
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-LegacyUpdate-'+[Guid]::NewGuid().ToString('N'))
try{
 [IO.Directory]::CreateDirectory((Join-Path $fixture 'PCL'))|Out-Null
 [IO.Directory]::CreateDirectory((Join-Path $fixture '.minecraft'))|Out-Null
 $target=Join-Path $fixture 'launcher.exe'
 [IO.File]::Copy($LegacyExecutablePath,$target)
 $settings=Join-Path $fixture 'PCL/Setup.ini'
 $game=Join-Path $fixture '.minecraft/fixture.txt'
 [IO.File]::WriteAllText($settings,'synthetic-settings-preserve')
 [IO.File]::WriteAllText($game,'synthetic-game-preserve')
 $before=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
 $replace=$updater.GetMethod('ReplaceExecutable',$flags)
 $rejected=$false
 try{$replace.Invoke($null,@($ExecutablePath,[string]$target,('0'*64),$false))|Out-Null}catch{$rejected=$true}
 if(-not $rejected -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before){throw 'Published updater damaged its target after invalid checksum'}
 $replace.Invoke($null,@($ExecutablePath,[string]$target,$hash,$false))|Out-Null
 if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash){throw 'Published updater failed to install the new executable'}
 if([IO.File]::ReadAllText($settings) -cne 'synthetic-settings-preserve' -or [IO.File]::ReadAllText($game) -cne 'synthetic-game-preserve'){throw 'Legacy update changed settings or game data'}
 if(@([IO.Directory]::EnumerateFiles($fixture,'*',[IO.SearchOption]::AllDirectories)).Count -ne 3){throw 'Legacy update left temporary files'}
 Write-Output "Published $current updater passed: detects $latest, selects asset, validates SHA256/assembly, replaces in a temporary directory and preserves settings/game files."
}finally{
 $full=[IO.Path]::GetFullPath($fixture)
 $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-LegacyUpdate-[a-f0-9]{32}\z'){throw 'Unsafe update fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
