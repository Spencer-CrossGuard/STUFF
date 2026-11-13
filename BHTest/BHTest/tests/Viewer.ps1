#Requires -Version 5.1
[CmdletBinding()]
param([string]$PowerShellPath=(Get-Process -Id $PID).Path,[string]$Artifacts)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('bh-viewer-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $scratch
function Assert($condition,$message){if(-not $condition){throw $message}}
function Rows($name,$values){$values | ForEach-Object {$_|ConvertTo-Json -Compress} | Set-Content -LiteralPath (Join-Path $dataset $name) -Encoding UTF8}
function ReadLog($path){$stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite);$reader=New-Object IO.StreamReader($stream);try{$reader.ReadToEnd()}finally{$reader.Dispose()}}
$process=$null;$base=$null;$headers=$null
try {
 $dataset=(New-Item -ItemType Directory -Path (Join-Path $scratch 'dataset')).FullName
 $domain='S-1-5-21-901-902-903';$admin=$domain+'-512';$support=$domain+'-1200'
 $groups=@(@{Id=$admin;Name='Domain Admins';SecurityEnabled=$true},@{Id=$support;Name='Support';SecurityEnabled=$true})
 for($i=0;$i -lt 270;$i++){$groups+=@{Id=($domain+'-'+(2000+$i));Name="Chain $i";SecurityEnabled=$true}}
 Rows 'nodes_groups.test.ndjson' $groups
 Rows 'nodes_users.test.ndjson' @(for($i=0;$i -lt 530;$i++){@{Id=($domain+'-'+(5000+$i));Name="User $i";Description=('Retained evidence '+('x'*1500))}})
 $edges=@(for($i=0;$i -lt 530;$i++){@{Source=($domain+'-'+(5000+$i));Target=$support;EdgeType='MemberOf'}})
 for($i=1;$i -lt 270;$i++){$edges+=@{Source=($domain+'-'+(2000+$i));Target=($domain+'-'+(1999+$i));EdgeType='MemberOf'}}
 Rows 'edges_memberof.test.ndjson' $edges
 Rows 'edges_acl.test.ndjson' @(@{Source=$support;Target=$admin;EdgeType='AddMember'},@{Source=($domain+'-2000');Target=$admin;EdgeType='GenericAll'})
 $report=Join-Path $scratch 'report'
 & (Join-Path $project 'BH.Gui.ps1') -Report -DatasetPaths $dataset -ReportOutputDir $report -AnalysisMode Deep -AnalysisSeconds 0
 $index=Join-Path $report 'graph.sqlite';$original=(Get-FileHash -LiteralPath $index).Hash
 # Launch a copy without src, modules, Node, or any companion code.
 $standalone=Join-Path $scratch 'Viewer.ps1';Copy-Item -LiteralPath (Join-Path $project 'BHTest.Viewer.ps1') -Destination $standalone
 $out=Join-Path $scratch 'stdout.txt';$err=Join-Path $scratch 'stderr.txt'
 $process=Start-Process -FilePath $PowerShellPath -ArgumentList @('-NoProfile','-File',('"'+$standalone+'"'),'-IndexPath',('"'+$index+'"'),'-Browser','-NoBrowser','-NoWindow') -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
 $url=$null
 for($wait=0;$wait -lt 200;$wait++){
  if(Test-Path -LiteralPath $out){$text=ReadLog $out;$match=[regex]::Match($text,'http://127\.0\.0\.1:\d+/#\S+');if($match.Success){$url=$match.Value;break}}
  if($process.HasExited){throw ('Viewer failed: '+[IO.File]::ReadAllText($err))};Start-Sleep -Milliseconds 100
 }
 Assert $url 'Viewer did not start within 20 seconds.'
 $base=$url.Split('#')[0].TrimEnd('/');$headers=@{Authorization='Bearer '+$url.Split('#')[1]}
 function Api($route){Invoke-RestMethod -Uri ($base+'/api/'+$route) -Headers $headers}
 function Rejected($route,$customHeaders,$code){$actual=0;try{Invoke-WebRequest -UseBasicParsing -Uri ($base+$route) -Headers $customHeaders | Out-Null}catch{$actual=[int]$_.Exception.Response.StatusCode};Assert ($actual -eq $code) "Expected $code for $route, got $actual"}
 $summary=Api 'summary';Assert ($summary.Analysis.Findings -eq 801) 'Incorrect saved finding count.'
 $seen=New-Object 'Collections.Generic.HashSet[string]';$score=[int]::MaxValue;$sequence=0L;$pages=0
 do{
  $page=Api "findings?score=$score&sequence=$sequence";$pages++
  foreach($finding in $page.Rows){Assert ($seen.Add($finding.Source.i)) 'Finding cursor repeated a source.'}
  $score=$page.AfterScore;$sequence=$page.AfterSequence
 }while($page.More)
 Assert ($seen.Count -eq 801 -and $pages -gt 4) 'Viewer failed to page beyond the HTML limit.'
 $filtered=Api 'findings?q=User%20529';Assert ($filtered.Rows.Count -eq 1 -and $filtered.Rows[0].Source.n -eq 'User 529') 'Global finding search missed a late candidate.'
 $all=New-Object 'Collections.Generic.HashSet[string]';$cursor=''
 do{$page=Api ('search?after='+[Uri]::EscapeDataString($cursor));foreach($node in $page.Rows){Assert ($all.Add($node.i)) 'Object cursor repeated an ID.'};$cursor=$page.After}while($page.More)
 Assert ($all.Count -eq 802) 'Object paging omitted data.'
 $node=Api ('object?id='+$domain+'-5529');$record=$node.Record|ConvertFrom-Json
 Assert ($record.Description.Length -gt 1500 -and -not $node.Limited) 'Compressed retained properties were not decoded.'
 $found=Api ('search?q=User%20529');Assert ($found.Rows.Count -eq 1) 'Full object search failed.'
 $found=Api ('search?q=User+529');Assert ($found.Rows.Count -eq 1) 'Form-style space encoding failed.'
 $in=Api ('relationships?id='+$support+'&direction=in');Assert ($in.Rows.Count -eq 50 -and $in.More) 'Incoming relationship paging failed.'
 $outgoing=Api ('relationships?id='+$support+'&direction=out');Assert ($outgoing.Rows.Count -eq 1 -and $outgoing.Rows[0].ty -eq 'AddMember') 'Outgoing relationships failed.'
 $long=Api ('recorded?id='+$domain+'-2269');Assert ($long.HopCount -eq 270 -and $long.Steps.Count -eq 128 -and $long.PathLimited) 'Long recorded path was silently truncated.'
 $count=$long.Steps.Count;while($long.PathLimited){$long=Api ('recorded?id='+$long.NextPathId);$count+=$long.Steps.Count};Assert ($count -eq 270) 'Saved path continuation lost steps.'
 $path=Api ('path?source='+$domain+'-2269&target='+$admin);Assert ($path.Status -eq 'Found' -and $path.HopCount -eq 270 -and $path.PathLimited) 'Full-index shortest path failed.'
 $limited=Api ('path?source='+$domain+'-5529&target='+$admin+'&limit=1');Assert ($limited.Status -eq 'VisitLimit') 'Visit limit was confused with no path.'
 $absent=Api ('path?source='+$admin+'&target='+$domain+'-5529');Assert ($absent.Status -eq 'NoPath') 'No-path result was incorrect.'
 Rejected '/api/summary' @{} 403
 Rejected '/api/summary' @{Authorization='Bearer wrong'} 403
 Rejected '/api/summary' @{Authorization=$headers.Authorization;Origin='https://example.invalid'} 403
 Rejected '/api/summary' @{Authorization=$headers.Authorization;Host='evil.invalid'} 403
 Rejected '/../../Windows/win.ini' $headers 404
 Rejected '/api/path?source=x&target=y&limit=-1' $headers 400
 $injection=Api 'search?q=%27%20OR%201%3D1%20--';Assert ($injection.Rows.Count -eq 0) 'Search interpreted SQL-like text.'
 $writeBlocked=$false;try{$write=[IO.File]::Open($index,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite);$write.Dispose()}catch{$writeBlocked=$true};Assert $writeBlocked 'Index was writable during a viewer session.'
 $null=Invoke-RestMethod -Uri ($base+'/api/stop') -Method Post -Headers $headers
 Assert ($process.WaitForExit(15000)) 'Viewer did not shut down.'
 Assert ($process.ExitCode -eq 0) ('Viewer shutdown failed: '+[IO.File]::ReadAllText($err))
 Assert ((Get-FileHash -LiteralPath $index).Hash -eq $original) 'Viewer changed the index.'
 Assert (-not (Test-Path ($index+'-wal'))) 'Viewer created a WAL sidecar.'
 if($Artifacts){$null=New-Item -ItemType Directory -Path $Artifacts -Force;Copy-Item -LiteralPath $index -Destination (Join-Path $Artifacts 'graph.sqlite')}
 Write-Host "PASS: standalone viewer; 801 findings / $pages pages; 802 objects; properties; relationships; 270-hop paths; budgets; session/origin/host protection; read-only hash; clean shutdown."
}finally{
 if($process -and -not $process.HasExited){try{if($base){$null=Invoke-RestMethod -Uri ($base+'/api/stop') -Method Post -Headers $headers}}catch{};if(-not $process.WaitForExit(15000)){$process.Kill()}}
 if($process){$process.Dispose()}
 if(Test-Path -LiteralPath $scratch){$resolved=[IO.Path]::GetFullPath($scratch);Assert ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) 'Unsafe scratch path';Remove-Item -LiteralPath $resolved -Recurse -Force}
}
