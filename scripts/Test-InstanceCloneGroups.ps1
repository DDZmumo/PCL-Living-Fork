param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# GUID relation fixtures and offscreen controls only. Never calls DeleteInstance or starts Minecraft.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($file in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $repo $file))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$groups=$asm.GetType('PCL.InstanceCloneGroups',$true);$pageType=$asm.GetType('PCL.PageSelectRight',$true);$mcType=$asm.GetType('PCL.ModMinecraft',$true)
$instanceType=$asm.GetType('PCL.ModMinecraft+McInstance',$true);$cardType=$asm.GetType('PCL.ModMinecraft+McInstanceCardType',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='clone-groups-fixture'
$token=[Threading.CancellationToken]::None
function Invoke-Static([type]$type,[string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try{return ,$type.GetMethod($name,$flags).Invoke($null,$values)}catch{throw ($name+': '+$_.Exception.ToString())}
}
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};if(-not $caught){throw $message}}
function Version([string]$name){$folder=Join-Path $versions $name;Put (Join-Path $folder ($name+'.json')) ('{"id":"'+$name+'","clientVersion":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}');Put (Join-Path $folder 'PCL/Setup.ini') 'VersionArgumentIndieV2:True';return $folder}
function Origin([string]$clone,[string]$source){$data=[Newtonsoft.Json.Linq.JObject]::new();$data['schema']=1;$data['sourceVersionFolder']=[Newtonsoft.Json.Linq.JValue]::new($source);$data['sourceGameRoot']=[Newtonsoft.Json.Linq.JValue]::new($source);$data['sourceName']=[Newtonsoft.Json.Linq.JValue]::new([IO.Path]::GetFileName($source));$data['cloneName']=[Newtonsoft.Json.Linq.JValue]::new([IO.Path]::GetFileName($clone));Put (Join-Path $clone 'PCL/CloneOrigin.json') $data.ToString()}
function Build([string[]]$folders){return (Invoke-Static $groups 'Build' @($folders,$token))}
function Assert-AllPresent($model,[int]$count){$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($rootNode in $model.Roots){foreach($node in (Invoke-Static $groups 'Flatten' @($rootNode))){if(-not $seen.Add($node.VersionFolder)){throw 'Grouped version appears more than once'}}};if($seen.Count -ne $count -or $model.ByPath.Count -ne $count){throw 'A visible version disappeared from grouping'}}
function Instance([string]$path){$value=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$path));$value.Version=[PCLCS.McVersion]::new('1.20.1',$true);$value.State=[Enum]::Parse($asm.GetType('PCL.ModMinecraft+McInstance+McInstanceState'),'Original');$value.IsLoaded=$true;$value.Info='Fixture instance';$value.Logo='pack://application:,,,/images/Blocks/Grass.png';return $value}
$listDefinition=[Collections.Generic.List[object]].GetGenericTypeDefinition();$dictionaryDefinition=[Collections.Generic.Dictionary[object,object]].GetGenericTypeDefinition()
$instanceListType=$listDefinition.MakeGenericType([type[]]@($instanceType));$instanceMapType=$dictionaryDefinition.MakeGenericType([type[]]@([string],$instanceType));$cardMapType=$dictionaryDefinition.MakeGenericType([type[]]@($cardType,$instanceListType))
function New-InstanceMap {return ,([Activator]::CreateInstance($instanceMapType))}
function New-CardMap {return ,([Activator]::CreateInstance($cardMapType))}
function Add-Card($map,[string]$kind,[object[]]$values){$list=[Activator]::CreateInstance($instanceListType);foreach($value in $values){$list.Add($value)};$map.Add([Enum]::Parse($cardType,$kind),$list)}
function Init-Row($element){return $asm.GetType('PCL.MyVirtualizingElement').GetMethod('TryInit',$flags).Invoke($null,@($element))}
function Render-Page($cards,[bool]$hidden){$mcType.GetField('McInstanceList',$flags).SetValue($null,$cards);$page=[Activator]::CreateInstance($pageType);$page.ShowHidden=$hidden;$pageType.GetMethod('McInstanceListUI',$flags).Invoke($page,[object[]](,$null))|Out-Null;return $page}
function Group-Cards($element){$result=@();if($element -is $asm.GetType('PCL.MyCard') -and $null -ne $element.Tag){$result+=,$element};if($element -is [Windows.Controls.Panel]){foreach($child in $element.Children){$result+=@(Group-Cards $child)}};return $result}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclCloneGroups-'+[Guid]::NewGuid().ToString('N'));$versions=Join-Path $fixture 'minecraft/versions'
$app=$null;$junction=$null;$worker=$null
$oldCards=$mcType.GetField('McInstanceList',$flags).GetValue($null);$selectionField=$mcType.GetField('_McInstanceSelected',$flags);$oldSelection=$selectionField.GetValue($null)
try{
 $source=Version 'source';$clone=Version 'clone';$sibling=Version 'sibling';$grandchild=Version 'grandchild';Origin $clone $source;Origin $sibling $source;Origin $grandchild $clone
 $model=Build @($grandchild,$clone,$sibling,$source,$source.ToUpperInvariant())
 Assert-AllPresent $model 4
 if($model.Roots.Count -ne 1 -or $model.Roots[0].VersionFolder -cne $source -or $model.ByPath[$clone].ParentFolder -cne $source -or $model.ByPath[$grandchild].ParentFolder -cne $clone){throw 'Clone-of-clone or cross-order grouping failed'}
 $visible=Build @($clone,$sibling,$grandchild);Assert-AllPresent $visible 3
 if($visible.ByPath[$clone].ParentFolder -or $visible.ByPath[$sibling].ParentFolder -or $visible.Roots.Count -ne 2){throw 'Missing/hidden/locked source was used as a group parent'}
 $orphan=Version 'orphan';Origin $orphan (Join-Path $versions 'missing')
 $orphanModel=Build @($orphan);if($orphanModel.Roots.Count -ne 1 -or -not $orphanModel.Roots[0].DeclaredSourceFolder){throw 'Orphan clone disappeared or lost minimal source information'}
 $bad=Version 'bad';Put (Join-Path $bad 'PCL/CloneOrigin.json') '{"schema":"not-an-integer","sourceVersionFolder":"C:\\outside"}'
 $badJson=Version 'bad-json';Put (Join-Path $badJson 'PCL/CloneOrigin.json') '[]'
 $large=Version 'large';Put (Join-Path $large 'PCL/CloneOrigin.json') ('x'*65537)
 $external=Version 'external';$outsideSource=Join-Path $fixture 'other-minecraft/versions/source';Put (Join-Path $outsideSource 'keep.txt') 'keep-outside';Origin $external $outsideSource
 $renamed=Version 'renamed';Origin $renamed $source;$data=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText((Join-Path $renamed 'PCL/CloneOrigin.json')));$data['cloneName']=[Newtonsoft.Json.Linq.JValue]::new('previous-name');Put (Join-Path $renamed 'PCL/CloneOrigin.json') $data.ToString()
 $invalid=Build @($bad,$badJson,$large,$external,$renamed,$source);Assert-AllPresent $invalid 6
 if($invalid.Roots.Count -ne 6){throw 'Invalid relation did not fall back to a standalone version'}
 $cycleA=Version 'cycle-a';$cycleB=Version 'cycle-b';$cycleChild=Version 'cycle-child';Origin $cycleA $cycleB;Origin $cycleB $cycleA;Origin $cycleChild $cycleA
 $cycle=Build @($cycleA,$cycleB,$cycleChild);Assert-AllPresent $cycle 3
 if($cycle.Roots.Count -ne 3){throw 'Cyclic source chain was grouped'}
 $chain=@($source);$previous=$source;foreach($i in 1..36){$next=Version ('depth-'+$i);Origin $next $previous;$chain+=,$next;$previous=$next};Assert-AllPresent (Build $chain) $chain.Count
 $linkedClone=Version 'linked-clone';$junction=Join-Path $versions 'linked-source';New-Item -ItemType Junction -Path $junction -Target $outsideSource|Out-Null;Origin $linkedClone $junction
 $linked=Build @($linkedClone,$junction);Assert-AllPresent $linked 2;if($linked.Roots.Count -ne 2){throw 'Reparse source was followed for grouping'}
 [IO.Directory]::Delete($junction);$junction=$null
 if([IO.File]::ReadAllText((Join-Path $outsideSource 'keep.txt')) -cne 'keep-outside'){throw 'Relation parser modified linked data'}
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel();Reject {Invoke-Static $groups 'Build' @([string[]]@($source,$clone),$cancel.Token)} 'Canceled relation scan completed'
 # Initialize native resources without Startup, a window, a directory scan or a real delete.
 $app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true);$app.InitializeComponent();[Windows.Application]::ResourceAssembly=$asm
 $app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_Startup',$flags)))
 $app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$flags)))
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh',$flags).Invoke($null,@(0))|Out-Null
 $sourceInstance=Instance $source;$cloneInstance=Instance $clone;$siblingInstance=Instance $sibling;$grandInstance=Instance $grandchild;$siblingInstance.IsStar=$true
 $map=New-InstanceMap;foreach($value in @($sourceInstance,$cloneInstance,$siblingInstance,$grandInstance)){$map.Add($value.PathVersion.TrimEnd('\'),$value)}
 $choice=Invoke-Static $pageType 'BuildCloneChoice' @($model.ByPath[$source],$map,$null)
 if($choice -isnot [Windows.Controls.StackPanel] -or $choice.Children.Count -ne 2){throw 'Source row is not separate from its collapsible clone group'}
 $sourceRow=Init-Row $choice.Children[0];$card=$choice.Children[1]
 if(-not [object]::ReferenceEquals($sourceRow.Tag,$sourceInstance) -or -not $card.IsSwapped -or $card.SwapControl.Children.Count -ne 0 -or -not $card.Title.Contains('source')){throw 'Source selection or default lazy collapse contract failed'}
 $pageType.GetMethod('McInstanceListContent',$flags).Invoke($null,@($sourceRow,[EventArgs]::Empty))|Out-Null
 if(@($sourceRow.Buttons).Count -ne 3){throw 'Source row lost native favorite/delete/settings controls'}
 $card.IsSwapped=$false
 if($card.SwapControl.Children.Count -ne 3){throw 'Expanded clone group omitted a child or native spacer'}
 $childPane=$card.SwapControl.Children[0]
 if($childPane -isnot [Windows.Controls.StackPanel]){throw 'Clone-of-clone source row was lost'}
 $cloneRow=Init-Row $childPane.Children[0]
 if(-not [object]::ReferenceEquals($cloneRow.Tag,$cloneInstance)){throw 'Nested row is bound to its source instead of its own clone'}
 $pageType.GetMethod('McInstanceListContent',$flags).Invoke($null,@($cloneRow,[EventArgs]::Empty))|Out-Null
 if(@($cloneRow.Buttons).Count -ne 3){throw 'Nested clone lost native favorite/delete/settings controls'}
 $selectedChoice=Invoke-Static $pageType 'BuildCloneChoice' @($model.ByPath[$source],$map,$grandchild)
 if($selectedChoice.Children[1].IsSwapped -or $selectedChoice.Children[1].SwapControl.Children[0].Children[1].IsSwapped){throw 'Selected descendant did not expand its ancestor groups'}
 $cards=New-CardMap;Add-Card $cards 'OriginalLike' @($sourceInstance);Add-Card $cards 'API' @($cloneInstance,$siblingInstance,$grandInstance);Add-Card $cards 'Star' @($siblingInstance)
 $selectionField.SetValue($null,$grandInstance)
 $page=Render-Page $cards $false;$main=$page.FindName('PanMain');$renderedGroups=@(Group-Cards $main)
 if($main.Children.Count -ne 2 -or @($renderedGroups|Where-Object {$_.Tag -eq $source}).Count -ne 1){throw 'Cross-category grouping or separate favorite entry failed'}
 $hiddenCards=New-CardMap;Add-Card $hiddenCards 'OriginalLike' @($sourceInstance);Add-Card $hiddenCards 'Hidden' @($cloneInstance)
 $hiddenPage=Render-Page $hiddenCards $true
 if($hiddenPage.FindName('PanMain').Children.Count -ne 1 -or @(Group-Cards $hiddenPage.FindName('PanMain')).Count -ne 0){throw 'Hidden clone vanished or grouped under a source outside the current view'}
 $worker=[MeloongCore.RedoableWorker]::new([Action]{})
 $locks=$asm.GetType('PCL.ModMinecraft+McInstanceLock')
 if(-not $locks.GetMethod('TryAcquire',$flags).Invoke($null,@($worker,$sourceInstance.PathVersion))){throw 'Could not acquire fixture-only source lock'}
 try{
  $lockedPage=Render-Page $cards $false
  if(@(Group-Cards $lockedPage.FindName('PanMain')|Where-Object {$_.Tag -eq $source}).Count -ne 0 -or $lockedPage.FindName('PanMain').Children.Count -ne 2){throw 'Locked source was still a group parent or its clones disappeared'}
 }finally{
  $worker.Start([Threading.CancellationToken]::None,$null)
  $worker.WaitIfRunning(10000,[Threading.CancellationToken]::None)|Out-Null
  $worker=$null
 }
 # Model-only deletion contract: no directory/file deletion, no selected-instance setter and no loader dispatch.
 $target=Invoke-Static $pageType 'ResolveDeleteTarget' @($cloneRow,$cloneInstance)
 if(-not [object]::ReferenceEquals($target,$cloneInstance)){throw 'Nested delete target did not come from its own row Tag'}
 Reject {Invoke-Static $pageType 'ResolveDeleteTarget' @($cloneRow,$sourceInstance)} 'Mismatched delete target was accepted'
 $pruned=Invoke-Static $pageType 'WithoutDeletedInstance' @($cards,$clone)
 $all=@($pruned.Values|ForEach-Object {$_}|ForEach-Object {$_})
 if(@($all|Where-Object {$_.PathVersion.TrimEnd('\') -eq $clone}).Count -or -not ($all|Where-Object {$_.PathVersion.TrimEnd('\') -eq $source}) -or -not ($all|Where-Object {$_.PathVersion.TrimEnd('\') -eq $sibling}) -or -not ($all|Where-Object {$_.PathVersion.TrimEnd('\') -eq $grandchild})){throw 'Deleting one clone pruned a source, sibling or descendant'}
 if($null -ne (Invoke-Static $pageType 'SelectionAfterDelete' @($cloneInstance,$clone))){throw 'Deleted selected clone was retained'}
 if(-not [object]::ReferenceEquals((Invoke-Static $pageType 'SelectionAfterDelete' @($sourceInstance,$clone)),$sourceInstance)){throw 'Deleting a clone changed an unrelated selected source'}
 $afterDelete=Build @($source,$sibling,$grandchild);Assert-AllPresent $afterDelete 3
 if($afterDelete.ByPath[$grandchild].ParentFolder){throw 'Descendant disappeared into a deleted parent group'}
 foreach($path in @($source,$clone,$sibling,$grandchild)){if(-not [IO.File]::Exists((Join-Path $path ([IO.Path]::GetFileName($path)+'.json')))){throw 'UI/deletion contract touched a version directory'}}
 Write-Output 'Clone groups passed: chain/order/case handling, visibility and orphan fallback, invalid/oversized/external/reparse records, cycle/depth bounds, cancellation, native lazy cards and rows, selected descendant expansion, cross-category and favorite/hidden behavior, nested delete routing, model-only pruning and selection cleanup. No real version was deleted.'
}finally{
 $mcType.GetField('McInstanceList',$flags).SetValue($null,$oldCards);$selectionField.SetValue($null,$oldSelection)
 if($junction -and [IO.Directory]::Exists($junction)){[IO.Directory]::Delete($junction)}
 if($app){$app.Shutdown()}
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('PclCloneGroups-')){if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}}
}
