param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Exercises the real detail-page filter with isolated loader output; no app entry point or real profile.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='map-details-fixture'
Add-Type -AssemblyName System.IO.Compression
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclWorldDetails-'+[Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($fixture)|Out-Null
function Name($stream,[string]$value){$bytes=[Text.Encoding]::UTF8.GetBytes($value);$stream.WriteByte([byte]($bytes.Length -shr 8));$stream.WriteByte([byte]($bytes.Length -band 255));$stream.Write($bytes,0,$bytes.Length)}
function Header($stream,[int]$kind,[string]$name){$stream.WriteByte([byte]$kind);Name $stream $name}
function Number($stream,[int]$kind,[string]$name,$value){Header $stream $kind $name;if($kind -eq 1){$stream.WriteByte([byte]$value);return};$bytes=if($kind -eq 3){[BitConverter]::GetBytes([int]$value)}else{[BitConverter]::GetBytes([long]$value)};if([BitConverter]::IsLittleEndian){[Array]::Reverse($bytes)};$stream.Write($bytes,0,$bytes.Length)}
function Text($stream,[string]$name,[string]$value){Header $stream 8 $name;Name $stream $value}
$type=$asm.GetType('PCL.WorldMetadata');$path=Join-Path $fixture 'level.dat'
try {
 $output=[IO.File]::Create($path);$zip=[IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionMode]::Compress)
 try {
  Header $zip 10 '';Header $zip 10 'Data';Text $zip 'LevelName' 'Fixture world';Number $zip 4 'LastPlayed' ([long]1730000000000)
  # Important fields follow the two basic fields; detail reading must not stop early.
  Number $zip 3 'GameType' 1;Number $zip 1 'Difficulty' 3;Number $zip 1 'hardcore' 1;Number $zip 1 'allowCommands' 0;Number $zip 1 'WasModded' 1
  Number $zip 3 'DataVersion' 3465;Number $zip 3 'SpawnX' -32;Number $zip 3 'SpawnY' 70;Number $zip 3 'SpawnZ' 256;Number $zip 4 'DayTime' 48000;Number $zip 4 'Time' 92000
  Header $zip 10 'Version';Text $zip 'Name' '1.20.1';Number $zip 1 'Snapshot' 0;$zip.WriteByte(0)
  Header $zip 10 'Unknown';Number $zip 3 'Ignored' 42;$zip.WriteByte(0)
  Header $zip 10 'WorldGenSettings';Number $zip 4 'seed' ([long]-9876543210123);$zip.WriteByte(0)
  $zip.WriteByte(0);$zip.WriteByte(0)
 }finally{$zip.Dispose();$output.Dispose()}
 $before=(Get-FileHash -LiteralPath $path).Hash
 $result=$type.GetMethod('ReadDetails',$flags).Invoke($null,@([string]$fixture))
 foreach($pair in @(@('Name','Fixture world'),@('VersionName','1.20.1'),@('GameMode',1),@('Difficulty',3),@('Seed',[long]-9876543210123),@('SpawnX',-32),@('Hardcore',$true),@('AllowCommands',$false))){if($type.GetField($pair[0],$flags).GetValue($result) -ne $pair[1]){throw ('World detail field mismatch: '+$pair[0])}}
 $caption=$type.GetMethod('Describe',$flags).Invoke($null,@([string]$fixture))
 foreach($text in @('Fixture world','1.20.1','创造','困难','极限模式：开启','允许作弊：关闭','-9876543210123','-32, 70, 256','第 2 天',$fixture)){if(-not $caption.Contains($text)){throw ('World caption missing: '+$text)}}
 $basic=$type.GetMethod('Read',$flags).Invoke($null,@([string]$fixture));if($null -eq $basic -or $type.GetField('Name',$flags).GetValue($basic) -ne 'Fixture world'){throw 'Recent-world metadata reading regressed'}
 if((Get-FileHash -LiteralPath $path).Hash -cne $before){throw 'Reading details modified the world'}
 $legacy=Join-Path $fixture 'legacy';[IO.Directory]::CreateDirectory($legacy)|Out-Null;$output=[IO.File]::Create((Join-Path $legacy 'level.dat'));$zip=[IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionMode]::Compress)
 try{Header $zip 10 '';Header $zip 10 'Data';Text $zip 'LevelName' 'Old world';Number $zip 4 'RandomSeed' -42;$zip.WriteByte(0);$zip.WriteByte(0)}finally{$zip.Dispose();$output.Dispose()}
 $old=$type.GetMethod('Describe',$flags).Invoke($null,@([string]$legacy));if(-not $old.Contains('Old world') -or -not $old.Contains('-42') -or $old.Contains('游戏模式：')){throw 'Legacy/absent field handling is incorrect'}
 [IO.File]::WriteAllText($path,'invalid gzip')
 $bad=$type.GetMethod('Describe',$flags).Invoke($null,@([string]$fixture));if(-not $bad.Contains('无法读取') -or -not $bad.Contains($fixture)){throw 'Corrupt metadata did not produce local folder details'}
 $missing=$type.GetMethod('Describe',$flags).Invoke($null,@([string](Join-Path $fixture 'missing')));if(-not $missing.Contains('未找到')){throw 'Missing metadata was not explained'}
 Write-Output 'World details passed: modern/legacy NBT, fields after basic metadata, negative seed/spawn, PCL caption content, missing/corrupt files, recent-world compatibility and read-only preservation.'
}finally{
 $full=[IO.Path]::GetFullPath($fixture);if($full.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('PclWorldDetails-')){Remove-Item -LiteralPath $full -Recurse -Force}
}