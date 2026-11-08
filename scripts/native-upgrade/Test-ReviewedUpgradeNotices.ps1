#requires -Version 5.1
param([string]$UpgradeScriptPath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-NativeUpdateBridge.ps1'))
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Run in actual Windows PowerShell 5.1.'}
$policy=Join-Path $PSScriptRoot 'ReviewedUpgradeNotices.ps1'
foreach($path in @($policy,$UpgradeScriptPath)){
 $raw=[IO.File]::ReadAllBytes($path)
 if($raw[0] -ne 239 -or $raw[1] -ne 187 -or $raw[2] -ne 191){throw 'Unicode PS5 startup policy must retain its UTF-8 BOM.'}
}
. $policy
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($UpgradeScriptPath),[ref]$tokens,[ref]$errors)
if($errors.Count -ne 0){throw 'Actual upgrade script did not parse in PS5.'}
$evidence=@{Result='pending'}
foreach($name in @('Pending','Check-Log','Assert-LfNormalClose','Test-ClosedLogGeneration')){
 $functions=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
 if($functions.Count -ne 1){throw ('Expected one actual function: '+$name)}
 . ([scriptblock]::Create($functions[0].Extent.Text))
}
$assignments=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$ready'},$true))
if($assignments.Count -ne 1){throw 'Expected one actual Main readiness expression.'}
$readyExpression=[scriptblock]::Create('param($text,$window,$notice,$nativeProof,$newNotice) '+$assignments[0].Right.Extent.Text)
$noticeAssignments=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$newNotice'},$true))
if($noticeAssignments.Count -ne 1){throw 'Expected one actual metadata-bound native notice assignment.'}
$titleExpression=[scriptblock]::Create('param($nativeProof) '+$noticeAssignments[0].Right.Extent.Text)
$nl=[string][char]13+[char]10
$prefix='00:00:00.001 I [MessageDialogController] '
$script:target='D:\owned-ci\Install with spaces\LivingPCL Actual Target.exe'
$stages=@('第一阶段加载用时：','第二阶段加载用时：','第三阶段加载用时：','PCL 位置：',$script:target)
$stageLog=($stages -join $nl)+$nl
$count=0
function Assert-Value([bool]$Expected,[scriptblock]$Action,[string]$Label){
 $value=& $Action
 if($value -isnot [bool] -or $value -ne $Expected){throw ('Unexpected notice/readiness result: '+$Label)}
 $script:count++
}
function Assert-Refused([scriptblock]$Action,[string]$Label){
 $refused=$false
 try{& $Action | Out-Null}catch{$refused=$true}
 if(-not $refused){throw ('Unsafe notice accepted: '+$Label)}
 $script:count++
}
function Message-Log([string]$Title,[string]$Body,[string]$Kind='普通'){
 $rows=@($Kind+'弹窗：'+$Title)+@($Body -split '\r?\n'|Where-Object {$_.Length -gt 0})
 return (($rows | ForEach-Object {$prefix+$_}) -join $nl)+$nl
}
$legacyHighlights='- 主产品迁移至 C#/.NET 10，精简版需 .NET 10 Desktop Runtime'+$nl+'- 更新页新增版本搜索、当前版本标记与 Markdown 说明'+$nl+'- 修复图片、中文搜索、资源详情异常及删除版本卡顿'
$previousHighlights='- 汇总四个预发布版本，包含依赖、主题、登录隔离与本次实机修复'+$nl+'- C#/.NET 10 与 Windows x64；运行前需 .NET 10 Desktop Runtime'+$nl+'- 完善依赖索引、内嵌前置识别与补全、深浅色主题和面板不透明度'+$nl+'- 原生更新页支持版本搜索、通道选择与 Markdown 更新说明'+$nl+'- 备份、克隆、恢复显示实际进度；存档与资源支持正倒序'+$nl+'- 微软登录缓存使用 _LF 隔离；修复图标、中文搜索与删除卡顿'+$nl+'- 整包更新保护个人修改和世界数据，修复配置误关联与冲突预览'
$currentHighlights='- 全局项目收藏：下载、版本资源管理与收藏页同步，跨文件和版本去重'+$nl+'- 整合包更新独立侧栏，预览中处理缺失前置与不兼容模组'+$nl+'- 更新任务分项显示原生进度，增加文件读取和写入速度'+$nl+'- 顶栏快捷切换深浅色，圆角侧栏、渐变光圈与夜间星光'+$nl+'- 更新公告可选择下次更新前不再弹出，暂缓更新后可从顶栏重开'+$nl+'- 改善部分 Windows 10 启动兼容性，更新 README 与项目日历'+$nl+'- 精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime'
$newHighlights='- PCLLF 原创独立代码采用 MPL-2.0，关于页可离线查看许可与来源'+$nl+'- 个人基础设置同步独立侧栏，全局和版本设置均支持编辑后保存'+$nl+'- 修复跨平台前置误报和模组更新漏报，保留本地依赖版本范围检查'+$nl+'- 首页更多设置可统一收起面板，展开沿用 PCL 回弹动画'+$nl+'- 修复深色主题提示框，完善原生开关、键位和拖动条对照'+$nl+'- 预发布精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime'
foreach($version in @('dev_edition','1.4.0-pre-4','1.4.0','1.4.1','1.4.2-pre-1')){
 $highlights=if($version -cin @('dev_edition','1.4.2-pre-1')){$newHighlights}elseif($version -ceq '1.4.0-pre-4'){$legacyHighlights}elseif($version -ceq '1.4.0'){$previousHighlights}else{$currentHighlights}
 $script:nativeProof=[pscustomobject]@{versionBaseName=$version;displayName='PCL Living Fork';buildType=50;startupNoticeStyle='plain';startupNoticeSource='local';startupNoticeTitle=('PCL Living Fork '+$version+' 版本说明')}
 $script:newNotice='PCL Living Fork '+$version+' 版本说明'
 if((& $titleExpression $nativeProof) -cne $newNotice){throw 'Actual native title assignment ignored plain metadata.'};$count++
 $caption='PCL Living Fork '+$version+$nl+$nl+$highlights
 if((Get-ReviewedUpgradeCaption $version) -cne $caption){throw 'Reviewed caption differs from independent complete expected body.'}
 $count++
 $regular=Message-Log $newNotice ('PCL Living Fork '+$version+' 是基于 PCL 官方开源版的第三方版本。'+$nl+$nl+$highlights)
 $upgrade=Message-Log 'PCL Living Fork 更新说明' $caption
 foreach($log in @($regular,$upgrade,($regular+$upgrade),($upgrade+$regular))){
  Assert-Value $true {Test-ReviewedStartupNotice $log $newNotice $version -NativePhase} 'complete native notice'
  Check-Log $log $newNotice
  Assert-Value $true {& $readyExpression ($stageLog+$log) ([pscustomobject]@{Owned=$true}) $newNotice $nativeProof $newNotice} 'actual readiness with full known notice'
 }
 Assert-Value $false {Test-ReviewedStartupNotice (Message-Log 'PCL Living Fork 更新说明' '') $newNotice $version -NativePhase} 'title-only remains pending'
 $partial=Message-Log 'PCL Living Fork 更新说明' ('PCL Living Fork '+$version)
 Assert-Value $false {Test-ReviewedStartupNotice $partial $newNotice $version -NativePhase} 'caption prefix remains pending'
 Assert-Refused {Check-Log $partial $newNotice -RequireComplete} 'partial caption cannot be final close'
 Assert-Refused {Check-Log (Message-Log 'PCL Living Fork 更新说明' '') $newNotice -RequireComplete} 'title only cannot be final close'
 $closeLog='00:00:01.001 I [Owned] 收到关闭指令'+$nl+'00:00:01.002 I [Owned] 程序已退出，返回值：Success'+$nl
 Assert-LfNormalClose -1 ($upgrade+$closeLog) $newNotice
 $count++
 Assert-Refused {Assert-LfNormalClose -1 ($partial+$closeLog) $newNotice} 'actual close refuses incomplete caption'
 Assert-Refused {Assert-LfNormalClose 0 ($upgrade+$closeLog) $newNotice} 'actual close preserves exact self-Kill exit'
 Assert-Value $false {Test-ReviewedStartupNotice ($upgrade.TrimEnd([char]13,[char]10)) $newNotice $version -NativePhase} 'partial final line remains pending'
 Assert-Refused {Check-Log ($partial+$regular) $newNotice} 'missing caption before next dialog'
 Assert-Refused {Check-Log (Message-Log 'PCL Living Fork 更新说明' ($caption+' changed')) $newNotice} 'changed full body'
 Assert-Refused {Check-Log (Message-Log 'PCL Living Fork 更新说明' ($caption+$nl+'extra')) $newNotice} 'extra caption line'
 Assert-Refused {Check-Log (Message-Log 'PCL Living Fork 更新说明' $caption.Replace($version,'1.4.0-pre-5')) $newNotice} 'wrong caption version'
 foreach($kind in @('选择','输入')){
  Assert-Refused {Check-Log (Message-Log 'PCL Living Fork 更新说明' $caption $kind) $newNotice} 'same title with unsafe dialog kind'
 }
 Assert-Refused {Check-Log ($upgrade+(Message-Log 'unexpected destructive question' 'unknown')) $newNotice} 'unknown dialog after allowed upgrade'
 Assert-Refused {Check-Log ($regular+$upgrade+'00:00:00.002 E [Actual] failure'+$nl) $newNotice} 'Error after allowed notices'
 Assert-Refused {Check-Log $upgrade 'PCL Living Fork 1.3.4 版本说明'} 'native upgrade forbidden in old phase'
 for($missing=0;$missing -lt $stages.Count;$missing++){
  $incomplete=@();for($i=0;$i -lt $stages.Count;$i++){if($i -ne $missing){$incomplete+=$stages[$i]}}
  $bad=($incomplete -join $nl)+$nl+$upgrade
  Assert-Value $false {& $readyExpression $bad ([pscustomobject]@{Owned=$true}) $newNotice $nativeProof $newNotice} 'missing original stage/path'
 }
 Assert-Value $false {& $readyExpression ($stageLog+$upgrade) $null $newNotice $nativeProof $newNotice} 'missing actual window'
}
foreach($route in @(
 @{Version='dev_edition';Style='markdown-suppress';Source='local';Local='以下为当前版本的本地简述。'},
 @{Version='1.4.2-pre-1';Style='markdown-suppress';Source='local';Local='以下为当前版本的本地简述。'},
 @{Version='1.4.0';Style='markdown-suppress';Source='published-or-local';Local='以下为当前版本的本地简述。'},
 @{Version='1.4.0';Style='markdown';Source='published-or-local';Local='暂未读取到当前版本的完整发布说明，以下为本地简述。'},
 @{Version='1.4.1';Style='markdown-suppress';Source='published-or-local';Local='以下为当前版本的本地简述。'},
 @{Version='1.4.1';Style='markdown';Source='published-or-local';Local='暂未读取到当前版本的完整发布说明，以下为本地简述。'}
)){
 $script:nativeProof=[pscustomobject]@{versionBaseName=$route.Version;displayName='PCL Living Fork';buildType=50;startupNoticeStyle=$route.Style;startupNoticeSource=$route.Source;startupNoticeTitle=('PCL Living Fork '+$route.Version+' 更新说明')}
 $script:newNotice=& $titleExpression $nativeProof
 if($newNotice -cne $nativeProof.startupNoticeTitle){throw 'Actual bridge title assignment ignored rich metadata.'};$count++
 $caption='基于 PCL 官方开源版的第三方版本。'+$nl+$route.Local
 $rich=Message-Log $newNotice $caption
 Assert-Value $true {& $readyExpression ($stageLog+$rich) ([pscustomobject]@{Owned=$true}) $newNotice $nativeProof $newNotice} 'actual rich readiness'
 Check-Log $rich $newNotice -RequireComplete
 Assert-LfNormalClose -1 ($rich+$closeLog) $newNotice;$count++
 $partial=Message-Log $newNotice '基于 PCL 官方开源版的第三方版本。'
 Assert-Value $false {& $readyExpression ($stageLog+$partial) ([pscustomobject]@{Owned=$true}) $newNotice $nativeProof $newNotice} 'rich caption prefix pending'
 Assert-Refused {Check-Log $partial $newNotice -RequireComplete} 'rich prefix cannot finalize'
 Assert-Refused {Assert-LfNormalClose -1 ($partial+$closeLog) $newNotice} 'rich prefix cannot close'
 Assert-Refused {Check-Log (Message-Log $newNotice ($caption+$nl+'extra')) $newNotice} 'extra rich caption line'
 Assert-Refused {Check-Log (Message-Log $newNotice ($caption+' changed')) $newNotice} 'changed rich caption'
 Assert-Refused {Check-Log ($rich+$rich) $newNotice} 'duplicate rich record'
 Assert-Refused {Check-Log $rich.Replace('[MessageDialogController]','[Unknown]') $newNotice} 'rich caption logger ownership'
 Assert-Refused {Check-Log (Message-Log ('PCL Living Fork '+$route.Version+' 版本说明') 'old') $newNotice} 'old title cannot stand in for rich metadata'
 Assert-Refused {Check-Log (Message-Log 'PCL Living Fork 更新说明' 'generic') $newNotice} 'generic title cannot stand in for rich metadata'
 foreach($kind in @('选择','输入')){Assert-Refused {Check-Log (Message-Log $newNotice $caption $kind) $newNotice} 'unsafe rich same-title kind'}
 Assert-Refused {Check-Log ($rich+(Message-Log '删除所有版本？' 'unsafe')) $newNotice} 'unknown after rich'
 Assert-Refused {Check-Log ($rich+(Message-Log '' 'unsafe')) $newNotice} 'empty title after rich'
 Assert-Refused {Check-Log ($rich+'00:00:01.000 E [Actual] failure'+$nl) $newNotice} 'Error after rich'
 Assert-Value $false {& $readyExpression ($stageLog+$rich.TrimEnd([char]13,[char]10)) ([pscustomobject]@{Owned=$true}) $newNotice $nativeProof $newNotice} 'rich final-line partial'
 $published=Message-Log $newNotice ('基于 PCL 官方开源版的第三方版本。'+$nl+'以下为当前版本的完整发布说明。')
 if($route.Source -ceq 'published-or-local'){
  Assert-Value $true {& $readyExpression ($stageLog+$published) ([pscustomobject]@{Owned=$true}) $newNotice $nativeProof $newNotice} 'published caption is read-only admission, never click authorization'
 }else{Assert-Refused {Check-Log $published $newNotice} 'development metadata cannot claim online notes'}
}
Assert-Value $true {Test-ReviewedStartupNotice (Message-Log 'PCL Living Fork 1.3.4 版本说明' 'legacy fixture') 'PCL Living Fork 1.3.4 版本说明' ''} 'original old notice retained'
Assert-Refused {Test-ReviewedStartupNotice (Message-Log 'PCL Living Fork 更新说明' 'wrong') 'PCL Living Fork 1.4.0-pre-5 版本说明' '1.4.0-pre-5' -NativePhase} 'unreviewed future native version'
Assert-Refused {Test-ReviewedStartupNotice '' 'PCL Living Fork 1.4.0-pre-4 版本说明' 'dev_edition' -NativePhase} 'mismatched metadata binding'
$bytes=[Text.Encoding]::UTF8.GetBytes('normally closed owned generation')
$script:closedLogGeneration=[pscustomobject]@{ProcessId=70;Bytes=$bytes}
Assert-Value $true {Test-ClosedLogGeneration $bytes 71} 'prior exact closed log is still not current'
Assert-Value $false {Test-ClosedLogGeneration $bytes 70} 'same PID cannot bypass current checks'
Assert-Value $false {Test-ClosedLogGeneration ([Text.Encoding]::UTF8.GetBytes('changed log')) 71} 'changed log cannot bypass current checks'
Write-Output ('PASS '+$count+' actual PS5 native-upgrade notice/readiness regressions; metadata-bound plain/Markdown captions, old phase/unknown/kind/body/Error/partial records refused or pending. No Main/profile/process/window/input action.')

