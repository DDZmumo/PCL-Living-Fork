param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Exercises native page transitions and animation ticks without showing a window or starting a game.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($file in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $file))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true);$app.InitializeComponent()
$app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_Startup',$flags)))
$app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$flags)))
[Windows.Application]::ResourceAssembly=$asm
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='recent-card-lifecycle-fixture'
try{
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh',$flags).Invoke($null,@(0))|Out-Null
 $page=[Activator]::CreateInstance($asm.GetType('PCL.PageLaunchRight'))
 $worldType=$asm.GetType('PCL.RecentWorld');$world=[Activator]::CreateInstance($worldType)
 foreach($pair in @(@('VersionPath','D:\Fixture\versions\example'),@('GameFolder','D:\Fixture'),@('Identifier','World'),@('Name','World'),@('PlayedUtc',[datetime]::UtcNow))){$worldType.GetField($pair[0],$flags).SetValue($world,$pair[1])}
 $tupleType=[type]'System.Tuple`2';$tupleType=$tupleType.MakeGenericType(@($worldType,[Windows.Media.ImageSource]));$record=[Activator]::CreateInstance($tupleType,[object[]]@($world,$null))
 $page.GetType().GetMethod('ShowRecentWorld',$flags).Invoke($page,[object[]](,$record))|Out-Null
 $card=$page.FindName('CardRecentWorlds');$transform=$page.FindName('TransRecentWorld')
 $page.PageOnEnter()
 if(-not [object]::ReferenceEquals($card.RenderTransform,$transform)){throw 'Native page entry replaced the recent-world slide transform'}
 $animation=$asm.GetType('PCL.ModAnimation');$tick=$animation.GetMethod('AniTimer',$flags);$last=$animation.GetField('AniLastTick',$flags)
 function Step-Animations([int]$frames){for($i=0;$i -lt $frames;$i++){$last.SetValue($null,[long]::MaxValue);$tick.Invoke($null,@([double]16))|Out-Null}}
 Step-Animations 50
 $apply=$page.GetType().GetMethod('ApplyRecentWorldState',$flags)
 if(-not $apply){throw 'Recent-world activity state handler missing'}
 $apply.Invoke($page,@($true,$false))|Out-Null
 $apply.Invoke($page,@($false,$true))|Out-Null
 if($card.Visibility -ne 'Visible' -or $transform.X -ge 0 -or $card.Opacity -gt 0.1){throw 'Card did not start its left-to-right entrance'}
 Step-Animations 5
 if($transform.X -le -24 -or $card.Opacity -le 0){throw 'Entrance animation did not progress'}
 Step-Animations 35
 if([Math]::Abs($transform.X) -gt 0.05 -or $card.Opacity -lt 0.99){throw 'Entrance animation did not finish at its anchored position'}
 $apply.Invoke($page,@($true,$true))|Out-Null
 if([Windows.Controls.ToolTipService]::GetIsEnabled($page.FindName('ItemRecentWorld'))){throw 'Moving card still allows its tooltip to open and follow the animation'}
 Step-Animations 30
 if($card.Visibility -ne 'Collapsed'){throw 'Card stayed visible while the game was active'}
 $apply.Invoke($page,@($false,$true))|Out-Null;Step-Animations 35
 if($card.Visibility -ne 'Visible' -or $card.Opacity -lt 0.99){throw 'Card did not return after game exit/cancellation'}
 $page.GetType().GetMethod('ReleaseRecentWorlds',$flags).Invoke($page,@())|Out-Null
 $page.GetType().GetMethod('ShowRecentWorld',$flags).Invoke($page,[object[]](,$record))|Out-Null
 $apply.Invoke($page,@($false,$true))|Out-Null;Step-Animations 35
 if($card.Visibility -ne 'Visible'){throw 'Reloading the page lost the card'}
 if($card.Width -gt 260 -or $card.Height -gt 80){throw 'Recent-world card is not compact'}
 $icon=$page.FindName('ItemRecentWorld').Children|Where-Object {$_ -is [Windows.Controls.Canvas] -and $_.Background -is [Windows.Media.ImageBrush]}|Select-Object -First 1
 if(-not $icon -or $icon.Clip -isnot [Windows.Media.RectangleGeometry] -or $icon.Clip.RadiusX -le 0){throw 'World icon is not rounded'}
 $begin=$page.GetType().GetMethod('BeginRecentWorldLaunch',$flags)
 $script:launchCalls=0;$script:wasHidden=$false
 $start=[Func[bool]]{ $script:launchCalls++;$script:wasHidden=($card.Visibility -eq 'Collapsed');return $false }
 $begin.Invoke($page,@($start,$true))|Out-Null
 $begin.Invoke($page,@($start,$true))|Out-Null
 $apply.Invoke($page,@($false,$true))|Out-Null
 Step-Animations 5
 if($script:launchCalls -ne 0 -or $card.Opacity -ge 1 -or [Windows.Controls.ToolTipService]::GetIsEnabled($page.FindName('ItemRecentWorld'))){throw 'Click did not finish exit animation before dispatching launch'}
 Step-Animations 55
 if($script:launchCalls -ne 1 -or -not $script:wasHidden){throw 'Launch was duplicated or dispatched before the card disappeared'}
 if($card.Visibility -ne 'Visible' -or $card.Opacity -lt 0.99 -or -not [Windows.Controls.ToolTipService]::GetIsEnabled($page.FindName('ItemRecentWorld'))){throw 'Rejected launch did not restore a stable card/tooltip'}
 $begin.Invoke($page,@($start,$true))|Out-Null
 $page.GetType().GetMethod('ReleaseRecentWorlds',$flags).Invoke($page,@())|Out-Null
 Step-Animations 40
 if($script:launchCalls -ne 1){throw 'Unloading the page left a pending launch callback'}
 Write-Output 'Recent-world card passed native page entry, real animation ticks, running/exit/cancel/reload visibility, compact sizing and rounded icon checks. No application window or game was started.'
}finally{$app.Shutdown()}
