#requires -Version 5.1
# Pure read-only log admission policy. This never touches a process/profile/UI.
function Get-ReviewedUpgradeCaption([string]$NativeVersion) {
 if($NativeVersion -cnotin @('dev_edition','1.4.0-pre-4','1.4.0','1.4.1','1.4.2-pre-1')){throw 'PENDING: Native upgrade notice version has not been reviewed.'}
 $nl=[string][char]13+[char]10
 $highlights=if($NativeVersion -cin @('dev_edition','1.4.2-pre-1')){'- PCLLF 原创独立代码采用 MPL-2.0，关于页可离线查看许可与来源'+$nl+'- 个人基础设置同步独立侧栏，全局和版本设置均支持编辑后保存'+$nl+'- 修复跨平台前置误报和模组更新漏报，保留本地依赖版本范围检查'+$nl+'- 首页更多设置可统一收起面板，展开沿用 PCL 回弹动画'+$nl+'- 修复深色主题提示框，完善原生开关、键位和拖动条对照'+$nl+'- 预发布精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime'}elseif($NativeVersion -ceq '1.4.0-pre-4'){'- 主产品迁移至 C#/.NET 10，精简版需 .NET 10 Desktop Runtime'+$nl+'- 更新页新增版本搜索、当前版本标记与 Markdown 说明'+$nl+'- 修复图片、中文搜索、资源详情异常及删除版本卡顿'}elseif($NativeVersion -ceq '1.4.0'){'- 汇总四个预发布版本，包含依赖、主题、登录隔离与本次实机修复'+$nl+'- C#/.NET 10 与 Windows x64；运行前需 .NET 10 Desktop Runtime'+$nl+'- 完善依赖索引、内嵌前置识别与补全、深浅色主题和面板不透明度'+$nl+'- 原生更新页支持版本搜索、通道选择与 Markdown 更新说明'+$nl+'- 备份、克隆、恢复显示实际进度；存档与资源支持正倒序'+$nl+'- 微软登录缓存使用 _LF 隔离；修复图标、中文搜索与删除卡顿'+$nl+'- 整包更新保护个人修改和世界数据，修复配置误关联与冲突预览'}else{'- 全局项目收藏：下载、版本资源管理与收藏页同步，跨文件和版本去重'+$nl+'- 整合包更新独立侧栏，预览中处理缺失前置与不兼容模组'+$nl+'- 更新任务分项显示原生进度，增加文件读取和写入速度'+$nl+'- 顶栏快捷切换深浅色，圆角侧栏、渐变光圈与夜间星光'+$nl+'- 更新公告可选择下次更新前不再弹出，暂缓更新后可从顶栏重开'+$nl+'- 改善部分 Windows 10 启动兼容性，更新 README 与项目日历'+$nl+'- 精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime'}
 return 'PCL Living Fork '+$NativeVersion+$nl+$nl+$highlights
}
function Get-ReviewedStartupNoticeTitle([string]$NativeVersion,[string]$NativeStyle='plain') {
 if($NativeVersion -cnotin @('dev_edition','1.3.5-pre-3','1.4.0-pre-4','1.4.0','1.4.1','1.4.2-pre-1')){throw 'PENDING: Native startup notice version has not been reviewed.'}
 if($NativeStyle -ceq 'plain'){return 'PCL Living Fork '+$NativeVersion+' 版本说明'}
 if($NativeStyle -cnotin @('markdown','markdown-suppress') -or $NativeVersion -cnotin @('dev_edition','1.4.0','1.4.1','1.4.2-pre-1') -or ($NativeStyle -ceq 'markdown' -and $NativeVersion -cnotin @('1.4.0','1.4.1'))){throw 'PENDING: Native startup notice style/version has not been reviewed.'}
 return 'PCL Living Fork '+$NativeVersion+' 更新说明'
}
function Get-ReviewedNativeStartupNotice([object]$NativeProof) {
 if($NativeProof.displayName -cne 'PCL Living Fork'){throw 'PENDING: Native startup notice product binding differs.'}
 $title=Get-ReviewedStartupNoticeTitle $NativeProof.versionBaseName $NativeProof.startupNoticeStyle
 if($NativeProof.startupNoticeTitle -cne $title){throw 'PENDING: Native startup notice metadata title differs.'}
 if($NativeProof.startupNoticeSource -cnotin @('local','published-or-local') -or ($NativeProof.startupNoticeStyle -ceq 'markdown' -and $NativeProof.startupNoticeSource -cne 'published-or-local') -or ($NativeProof.startupNoticeSource -ceq 'published-or-local' -and ($NativeProof.versionBaseName -cnotin @('1.4.0','1.4.1') -or $NativeProof.startupNoticeStyle -ceq 'plain' -or $NativeProof.buildType -ne 50))){throw 'PENDING: Native startup notice metadata source differs.'}
 return $title
}
function Test-ReviewedStartupNotice([string]$Text,[string]$Notice,[string]$NativeVersion,[switch]$NativePhase,[string]$NativeStyle='plain',[string]$NativeSource='local') {
 if($Text -match '(?m)^\d{2}:\d{2}:\d{2}\.\d{3} E '){throw 'Actual Main logged an Error.'}
 if($Text -match '主窗体加载完成前出现意料外的等待弹窗|处理等待中的弹窗失败|弹窗初始化失败|弹窗加载失败'){throw 'Actual initialization/dialog failed.'}
 if($NativePhase){
  if($Notice -cne (Get-ReviewedStartupNoticeTitle $NativeVersion $NativeStyle)){throw 'PENDING: Native notice phase/version/style binding differs.'}
  if($NativeSource -cnotin @('local','published-or-local') -or ($NativeStyle -ceq 'markdown' -and $NativeSource -cne 'published-or-local') -or ($NativeSource -ceq 'published-or-local' -and ($NativeVersion -cnotin @('1.4.0','1.4.1') -or $NativeStyle -ceq 'plain'))){throw 'PENDING: Native notice source binding differs.'}
 }
 $rich=$NativePhase -and $NativeStyle -cne 'plain'
 $ordinarySeen=$false;$upgradeRequested=$false;$reviewedCount=0
 foreach($dialog in [regex]::Matches($Text,'(?m)(?<Kind>普通|选择|输入)弹窗：(?<Title>[^\r\n]*)\r?\n')){
  $kind=$dialog.Groups['Kind'].Value;$title=$dialog.Groups['Title'].Value
  if($kind -ceq '普通' -and $title -ceq $Notice){if($rich){$reviewedCount++}else{$ordinarySeen=$true}}
  elseif($NativePhase -and -not $rich -and $kind -ceq '普通' -and $title -ceq 'PCL Living Fork 更新说明'){$upgradeRequested=$true;$reviewedCount++}
  else{throw ('PENDING: Unexpected actual dialog; never auto-clicked: '+$title)}
 }
 $upgradeVerified=$false;$upgradeIncomplete=$false
 if($upgradeRequested -or ($rich -and $reviewedCount -gt 0)){
  $reviewedTitle='PCL Living Fork 更新说明'
  $captions=@()
  if($rich){
   $reviewedTitle=$Notice
   $localLine=if($NativeStyle -ceq 'markdown'){'暂未读取到当前版本的完整发布说明，以下为本地简述。'}else{'以下为当前版本的本地简述。'}
   $captions+=('基于 PCL 官方开源版的第三方版本。'+[char]13+[char]10+$localLine)
   # Read-only admission of the exact published caption does not authorize UI
   # dismissal. The interaction observer separately requires the reviewed local body.
   if($NativeSource -ceq 'published-or-local'){$captions+=('基于 PCL 官方开源版的第三方版本。'+[char]13+[char]10+'以下为当前版本的完整发布说明。')}
  }else{$captions+=Get-ReviewedUpgradeCaption $NativeVersion}
  $records=[Collections.Generic.List[object]]::new();$current=$null
  foreach($line in [regex]::Matches($Text,'(?m)^\d{2}:\d{2}:\d{2}\.\d{3} I (?:<[^>\r\n]*> )?\[MessageDialogController\] (?<Content>[^\r\n]*)\r?\n')){
   $content=$line.Groups['Content'].Value.TrimEnd([char]13)
   $header=[regex]::Match($content,'^(?<Kind>普通|选择|输入)弹窗：(?<Title>.+)$')
   if($header.Success){
    if($null -ne $current){$current.Closed=$true}
    $current=[pscustomobject]@{Kind=$header.Groups['Kind'].Value;Title=$header.Groups['Title'].Value;Body=[Collections.Generic.List[string]]::new();Closed=$false}
    $records.Add($current)
   }elseif($null -ne $current -and $content.Length -gt 0){$current.Body.Add($content)}
  }
  $upgrades=@($records | Where-Object {$_.Kind -ceq '普通' -and $_.Title -ceq $reviewedTitle})
  if($upgrades.Count -ne 1 -or $reviewedCount -ne 1){throw 'PENDING: Startup notice has no unique owned MessageDialogController log record.'}
  $record=$upgrades[0]
  $prefixMatches=$false;$completeMatches=$false
  foreach($caption in $captions){
   $expected=@($caption -split '\r?\n' | Where-Object {$_.Length -gt 0})
   $matches=$record.Body.Count -le $expected.Count
   for($i=0;$matches -and $i -lt $record.Body.Count;$i++){if($record.Body[$i] -cne $expected[$i]){$matches=$false}}
   if($matches){$prefixMatches=$true;if($record.Body.Count -eq $expected.Count){$completeMatches=$true}}
  }
  if(-not $prefixMatches){throw 'PENDING: Reviewed startup notice has changed or extra caption lines.'}
  if(-not $completeMatches){
   if($record.Closed){throw 'PENDING: Reviewed upgrade notice ended before its full caption.'}
   $upgradeIncomplete=$true
  }else{$upgradeVerified=$true}
 }
 # A partial final write must be completed before it can be a stable checkpoint.
 return ($Text.EndsWith([string][char]10) -and -not $upgradeIncomplete -and ($ordinarySeen -or $upgradeVerified))
}

