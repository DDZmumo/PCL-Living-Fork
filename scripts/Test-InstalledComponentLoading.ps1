param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Offscreen overview, synthetic settings and GUID temporary mods only.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){
 [Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null
}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true)
$app.InitializeComponent()
# Pumping the Dispatcher must never enter the actual launcher's startup handler.
$eventFlags=[Reflection.BindingFlags]'NonPublic,Instance'
$startup=$asm.GetType('PCL.Application').GetMethod('Application_Startup',$eventFlags)
$app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$startup))
$unhandled=$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$eventFlags)
$app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$unhandled))
[Windows.Application]::ResourceAssembly=$asm
$entries=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $entries.Values){$entry.ValueCache['']=$entry.DefaultValue}
$entries['LivingModpackExported'].ValueCache['']=$true
$entries['LivingModpackMode'].ValueCache['']=$true
$flags=[Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-ComponentLoad-'+[Guid]::NewGuid().ToString('N'))
try{
 $folder=Join-Path $fixture '.minecraft/versions/fabric'
 $mods=Join-Path $folder 'mods'
 [IO.Directory]::CreateDirectory((Join-Path $folder 'PCL'))|Out-Null
 [IO.Directory]::CreateDirectory($mods)|Out-Null
 [IO.File]::WriteAllText((Join-Path $folder 'PCL/Setup.ini'),"VersionArgumentIndieV2:True`r`n")
 $archive=[IO.Compression.ZipFile]::Open((Join-Path $mods 'fabric-api.jar'),[IO.Compression.ZipArchiveMode]::Create)
 try{
  $writer=[IO.StreamWriter]::new($archive.CreateEntry('fabric.mod.json').Open())
  try{$writer.Write('{"id":"fabric-api","version":"0.155.2"}')}finally{$writer.Dispose()}
 }finally{$archive.Dispose()}
 $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance',$true)
 $instance=$instanceType.GetConstructor([type[]]@([string])).Invoke([object[]]@([string]$folder))
 $version=[PCLCS.McVersion]::new('26.2',$true)
 $version.ModLoaderVersions[[PCLCS.ModLoaders]::Fabric]='0.19.3'
 $instanceType.GetProperty('Version').SetValue($instance,$version,$null)
 $asm.GetType('PCL.PageInstanceLeft').GetField('Instance').SetValue($null,$instance)
 $view=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceOverall'))
 $panel=$view.FindName('PanInstalledComponents')
 $content=$view.FindName('PanBack')
 $view.GetType().GetMethod('RefreshInstalledComponents',$flags).Invoke($view,@())|Out-Null
 $initial=$panel.Children.Count
 $initialVisibility=$content.Visibility
 $partialVisible=$initial -gt 0 -and $initial -lt 3 -and $content.Visibility -eq 'Visible'
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while($panel.Children.Count -ne 3 -and $timer.ElapsedMilliseconds -lt 10000){
  $frame=[Windows.Threading.DispatcherFrame]::new()
  [Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::Background,[Action]{$frame.Continue=$false})|Out-Null
  [Windows.Threading.Dispatcher]::PushFrame($frame)
  if($panel.Children.Count -gt 0 -and $panel.Children.Count -lt 3 -and $content.Visibility -eq 'Visible'){$partialVisible=$true}
  [Threading.Thread]::Sleep(5)
 }
 if($panel.Children.Count -ne 3){throw 'Complete installed components were not delivered'}
 if($partialVisible){throw "Overview exposed a partial component list: initial=$initial, final=$($panel.Children.Count), content=$initialVisibility"}
 if($initial -ne 3 -and $initialVisibility -ne 'Collapsed'){throw 'Overview was shown before its complete component snapshot was ready'}
 if(($panel.Children.Title -join ',') -cne 'Minecraft,Fabric,Fabric API'){throw 'Completed overview omitted the installed Fabric API'}
 $loader=$view.GetType().GetField('ComponentsLoader',$flags).GetValue($view)
 $revision=$view.GetType().GetField('ComponentRefreshId',$flags)
 $currentRevision=$revision.GetValue($view)
 $finish=$view.GetType().GetMethod('InstalledComponentsLoaded',$flags)
 $panel.Children.Clear()
 $revision.SetValue($view,($currentRevision+1))
 $finish.Invoke($view,@($loader))|Out-Null
 if($panel.Children.Count){throw 'A result from a previous refresh was published'}
 $revision.SetValue($view,$currentRevision)
 $asm.GetType('PCL.PageInstanceLeft').GetField('Instance').SetValue($null,$null)
 $finish.Invoke($view,@($loader))|Out-Null
 if($panel.Children.Count){throw 'A previous instance result was published'}
 $asm.GetType('PCL.PageInstanceLeft').GetField('Instance').SetValue($null,$instance)
 $finish.Invoke($view,@($loader))|Out-Null
 if($panel.Children.Count -ne 3){throw 'Current complete result was not published'}
 $entries['LivingModpackMode'].ValueCache['']=$false
 $view.GetType().GetMethod('RefreshInstalledComponents',$flags).Invoke($view,@())|Out-Null
 $view.GetType().GetMethod('RefreshModpackVisibility',$flags).Invoke($view,@())|Out-Null
 $view.PageOnEnter()
 if($content.Visibility -ne 'Visible' -or $panel.Visibility -ne 'Collapsed'){throw 'Ordinary overview remained behind the component loader'}
 $view.PageOnForceExit()
 Write-Output 'Installed-component loading passed: complete Fabric API snapshot before content display, background scanning, stale-result guards and ordinary-mode entry.'
}finally{
 $asm.GetType('PCL.PageInstanceLeft').GetField('Instance').SetValue($null,$null)
 $full=[IO.Path]::GetFullPath($fixture)
 $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-ComponentLoad-[a-f0-9]{32}\z'){throw 'Unsafe fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
 $app.Shutdown()
}
