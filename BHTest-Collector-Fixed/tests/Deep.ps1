#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$entry=Join-Path $project 'BH.Gui.ps1'
Import-Module (Join-Path $project 'src/BH.Core.psm1') -DisableNameChecking
Initialize-BHNative
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('bh-deep-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $scratch
function Assert($ok,$message){if(-not $ok){throw $message}}
function Rows($path,$values){$values | ForEach-Object {$_ | ConvertTo-Json -Compress} | Set-Content -LiteralPath $path -Encoding utf8}
function ReadReportData($path){$html=Get-Content -LiteralPath $path -Raw;return ([regex]::Match($html,'(?m)^const DATA = (.*);$').Groups[1].Value | ConvertFrom-Json)}
try {
 $domain='S-1-5-21-100-200-300';$admin=$domain+'-512';$support=$domain+'-1200';$alice=$domain+'-1100'
 $long=(New-Item -ItemType Directory -Path (Join-Path $scratch 'long')).FullName
 $groups=@(@{Id=$admin;Name='Domain Admins';SecurityEnabled=$true})
 for($i=0;$i -lt 12;$i++){$groups+=@{Id=($domain+'-'+(2000+$i));Name="Group $i";SecurityEnabled=$true}}
 Rows (Join-Path $long 'nodes_groups.test.ndjson') $groups
 Rows (Join-Path $long 'nodes_users.test.ndjson') @(@{Id=$alice;Name='Alice';Disabled=$false})
 $members=@(@{Source=$alice;Target=($domain+'-2011');EdgeType='MemberOf'})
 for($i=1;$i -lt 12;$i++){$members+=@{Source=($domain+'-'+(2000+$i));Target=($domain+'-'+(1999+$i));EdgeType='MemberOf'}}
 Rows (Join-Path $long 'edges_memberof.test.ndjson') $members
 Rows (Join-Path $long 'edges_acl.test.ndjson') @(@{Source=($domain+'-2000');Target=$admin;EdgeType='AddMember'})
 $quick=Join-Path $scratch 'quick';$deep=Join-Path $scratch 'deep'
 & $entry -Report -DatasetPaths $long -ReportOutputDir $quick
 & $entry -Report -DatasetPaths $long -ReportOutputDir $deep -AnalysisMode Deep -AnalysisSeconds 0
 $q=ReadReportData (Join-Path $quick 'report.html');$d=ReadReportData (Join-Path $deep 'report.html')
 Assert ($q.Analysis.Status -eq 'DepthLimit' -and @($q.Analysis.Findings | Where-Object {$_.Source.i -eq $alice}).Count -eq 0) 'Quick depth cap did not stop with an explicit reason.'
 $path=@($d.Analysis.Findings | Where-Object {$_.Source.i -eq $alice})[0]
 Assert ($d.Analysis.Status -eq 'Completed' -and $path.HopCount -eq 13 -and $path.Steps.Count -eq 13) 'Deep analysis missed the known thirteen-hop path.'
 $wide=(New-Item -ItemType Directory -Path (Join-Path $scratch 'wide')).FullName
 Rows (Join-Path $wide 'nodes_groups.test.ndjson') @(@{Id=$admin;Name='Domain Admins';SecurityEnabled=$true},@{Id=$support;Name='Support';SecurityEnabled=$true})
 Rows (Join-Path $wide 'nodes_users.test.ndjson') @(for($i=0;$i -lt 500;$i++){@{Id=($domain+'-'+(5000+$i));Name="User $i"}})
 Rows (Join-Path $wide 'edges_memberof.test.ndjson') @(for($i=0;$i -lt 500;$i++){@{Source=($domain+'-'+(5000+$i));Target=$support;EdgeType='MemberOf'}})
 Rows (Join-Path $wide 'edges_acl.test.ndjson') @(@{Source=$support;Target=$admin;EdgeType='AddMember'})
 $out=Join-Path $scratch 'wide-report'
 & $entry -Report -DatasetPaths $wide -ReportOutputDir $out -AnalysisMode Deep -AnalysisSeconds 0 -AnalysisVisitLimit 40
 $partial=ReadReportData (Join-Path $out 'report.html')
 Assert ($partial.Analysis.Status -eq 'VisitLimit' -and $partial.Analysis.Visited -eq 40) 'Visit budget did not pause inside a wide edge batch.'
 $checkpoint=Join-Path $out 'analysis-checkpoint.sqlite'
 $before=(Get-FileHash -LiteralPath $checkpoint).Hash
 $input=Join-Path $wide 'nodes_users.test.ndjson';$original=[IO.File]::ReadAllBytes($input)
 Add-Content -LiteralPath $input -Value ' '
 $rejected=$false
 try{& $entry -Report -DatasetPaths $wide -ReportOutputDir $out -AnalysisMode Deep -ResumeAnalysis -AnalysisSeconds 0}catch{$rejected=$_.Exception.Message -like '*fingerprint changed*'}
 Assert $rejected 'Resume accepted changed input.'
 Assert ((Get-FileHash -LiteralPath $checkpoint).Hash -eq $before) 'Rejected resume changed the checkpoint.'
 [IO.File]::WriteAllBytes($input,$original)
 & $entry -Report -DatasetPaths $wide -ReportOutputDir $out -AnalysisMode Deep -ResumeAnalysis -AnalysisSeconds 0
 $finished=ReadReportData (Join-Path $out 'report.html')
 Assert ($finished.Analysis.Status -eq 'Completed' -and $finished.Analysis.TotalFindings -eq 501 -and $finished.Analysis.Visited -eq 502) 'Resume skipped or duplicated identities after a partial batch.'
 Assert ($finished.Analysis.Findings.Count -eq 200 -and $finished.Analysis.PreviewLimited) 'Discovery was stopped by the HTML preview limit.'
 $db=[BH.Native.GraphIndex]::new((Join-Path $out 'graph.sqlite'),$false)
 try {
  $seen=[Collections.Generic.HashSet[string]]::new();$score=[int]::MaxValue;$sequence=0L
  do{$page=@($db.DiskFindingPage($score,$sequence,37));foreach($finding in $page){Assert ($seen.Add($finding.Source)) 'A cursor page repeated a source.'};if($page.Count){$score=$page[-1].Score;$sequence=$page[-1].Sequence}}while($page.Count -eq 37)
  Assert ($seen.Count -eq 501) 'Cursor pagination did not expose every discovered candidate.'
 }finally{$db.Dispose()}
 $held=[IO.FileStream]::new((Join-Path $out 'report.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try {
  $blocked=$false
  try{& $entry -Report -DatasetPaths $wide -ReportOutputDir $out -AnalysisMode Deep -ResumeAnalysis}catch{$blocked=$_.Exception.Message -like '*already in use*'}
  Assert $blocked 'Concurrent report generation was not rejected.'
 }finally{$held.Dispose();[IO.File]::Delete((Join-Path $out 'report.lock'))}
 # Cancellation and time limits are deterministic: a callback requests a stop after the first batch.
 foreach($mode in 'Cancel','Time'){
  $copy=Join-Path $scratch ($mode+'.sqlite');Copy-Item -LiteralPath (Join-Path $quick 'graph.sqlite') -Destination $copy
  $db=[BH.Native.GraphIndex]::new($copy,$false,$true)
  $cancel=Join-Path $scratch ($mode+'.cancel')
  try {
   $state=@{Once=$false;Cancel=$cancel;Mode=$mode}
   $callback=[Action[BH.Native.DiskAnalysisProgress]]{param($p)if(-not $state.Once){$state.Once=$true;if($state.Mode -eq 'Cancel'){[IO.File]::WriteAllText($state.Cancel,'cancel')}else{[Threading.Thread]::Sleep(1100)}}}.GetNewClosure()
   $fingerprint=$db.DiskSourceFingerprint()
   $stopped=$db.AnalyzeDisk([string[]]@($admin),$fingerprint,0,0,$(if($mode -eq 'Time'){1}else{0}),$cancel,$callback)
   Assert ($stopped.Status -eq $(if($mode -eq 'Time'){'TimeLimit'}else{'Cancelled'})) "$mode did not produce a specific stop reason."
  }finally{$db.Dispose()}
  if(Test-Path -LiteralPath $cancel){[IO.File]::Delete($cancel)}
  $db=[BH.Native.GraphIndex]::new($copy,$false,$true)
  try{$resumed=$db.AnalyzeDisk([string[]]@($admin),$db.DiskSourceFingerprint(),0,0,0,$cancel,$null);Assert ($resumed.Status -eq 'Completed') "$mode resume failed."}finally{$db.Dispose()}
 }
 $pageResult=& $entry -FindingPage -IndexPath (Join-Path $out 'graph.sqlite') -FindingPageSize 25 | ConvertFrom-Json
 Assert ($pageResult.Findings.Count -eq 25 -and $pageResult.Analysis.Findings -eq 501) 'Findings CLI did not page the full discovery index.'
 $trace=& $entry -TraceFinding $alice -IndexPath (Join-Path $deep 'graph.sqlite') | ConvertFrom-Json
 Assert ($trace.Count -eq 13) 'Trace CLI did not return the long recorded path.'
 # Long-path detail paging stays bounded without imposing a traversal-depth cap.
 $veryLong=(New-Item -ItemType Directory -Path (Join-Path $scratch 'very-long')).FullName
 $g=@(@{Id=$admin;Name='Domain Admins';SecurityEnabled=$true})
 for($i=0;$i -lt 270;$i++){$g+=@{Id=($domain+'-'+(20000+$i));Name="Nested $i";SecurityEnabled=$true}}
 Rows (Join-Path $veryLong 'nodes_groups.test.ndjson') $g
 Rows (Join-Path $veryLong 'edges_acl.test.ndjson') @(@{Source=($domain+'-20000');Target=$admin;EdgeType='AddMember'})
 Rows (Join-Path $veryLong 'edges_memberof.test.ndjson') @(for($i=1;$i -lt 270;$i++){@{Source=($domain+'-'+(20000+$i));Target=($domain+'-'+(19999+$i));EdgeType='MemberOf'}})
 $db=[BH.Native.GraphIndex]::new((Join-Path $scratch 'very-long.sqlite'),$true)
 try {
  $db.Import([string[]]@((Join-Path $veryLong 'nodes_groups.test.ndjson')),[string[]]@('groups'),
    [string[]]@((Join-Path $veryLong 'edges_acl.test.ndjson'),(Join-Path $veryLong 'edges_memberof.test.ndjson')),[string[]]@('acl','memberof'),[string[]]@(),$true)
  $state=$db.AnalyzeDisk([string[]]@($admin),'long-fixture',0,0,0,$null,$null)
  $first=@($db.DiskFindingPage([int]::MaxValue,0,200));$last=$first[-1]
  $second=@($db.DiskFindingPage($last.Score,$last.Sequence,200))
  $tail=@($second | Where-Object Source -eq ($domain+'-20269'))[0]
  Assert ($state.Status -eq 'Completed' -and $tail.HopCount -eq 270 -and $tail.PathLimited -and $tail.Steps.Count -eq 256) 'Traversal and path-preview limits were confused.'
  $continued=@($db.DiskPathPage($tail.NextPathId,256))
  Assert ($continued.Count -eq 14 -and $continued[-1].Target -eq $admin) 'Long-path continuation skipped or repeated a step.'
 }finally{$db.Dispose()}
 # Simulate process death during an uncommitted batch, then let SQLite recover it.
 $crash=Join-Path $scratch 'crash.sqlite';Copy-Item -LiteralPath (Join-Path $quick 'graph.sqlite') -Destination $crash
 $workerPath=Join-Path $scratch 'CrashWorker.ps1'
 $workerText=@"
param([string]`$Project,[string]`$Index)
`$ErrorActionPreference='Stop'
Import-Module (Join-Path `$Project 'src/BH.Core.psm1') -DisableNameChecking
Initialize-BHNative
`$db=[BH.Native.GraphIndex]::new(`$Index,`$false,`$true)
`$die=[Action[BH.Native.DiskAnalysisProgress]]{param(`$p)[Diagnostics.Process]::GetCurrentProcess().Kill()}
`$db.AnalyzeDisk([string[]]@('$admin'),`$db.DiskSourceFingerprint(),0,0,0,`$null,`$die)
"@
 $workerText | Set-Content -LiteralPath $workerPath -Encoding utf8
 $exe=Join-Path $PSHOME $(if($PSVersionTable.PSEdition -eq 'Desktop'){'powershell.exe'}else{'pwsh.exe'})
 $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=$exe
 $info.Arguments='-NoProfile -NonInteractive -File "'+$workerPath+'" -Project "'+$project+'" -Index "'+$crash+'"'
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $process=[Diagnostics.Process]::Start($info)
 try {
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Crash worker did not exit.'}
  Assert ($process.ExitCode -ne 0) 'Crash worker did not interrupt the analysis process.'
  Assert ([string]::IsNullOrWhiteSpace($stderr.GetAwaiter().GetResult())) 'Crash worker failed before entering native analysis.'
 }finally{$process.Dispose()}
 $db=[BH.Native.GraphIndex]::new($crash,$false,$true)
 try {
  Assert ($db.DiskAnalysisState().Visited -eq 9) 'Uncommitted discovery rows were not rolled back consistently.'
  $recovered=$db.AnalyzeDisk([string[]]@($admin),$db.DiskSourceFingerprint(),0,0,0,$null,$null)
  Assert ($recovered.Status -eq 'Completed' -and $recovered.Visited -eq 14) 'Interrupted-process checkpoint did not recover and finish.'
 }finally{$db.Dispose()}
 'Deep analysis passed: 13-hop paths, explicit bounds, wide-batch resume, changed-input rejection, 501 candidates beyond the preview cap, keyset pages, cancellation, time budgets and path tracing.'
}finally{$resolved=(Resolve-Path -LiteralPath $scratch).ProviderPath;if([string]::Equals($resolved,$scratch,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $resolved -Recurse -Force}}
