#Requires -Version 5.1
[CmdletBinding()]
param([switch] $KeepArtifacts)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$entry=Join-Path $project 'BH.Gui.ps1'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('bh-analysis-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $scratch
function Assert($ok,$message){if(-not $ok){throw $message}}
function WriteRows($dir,$file,$rows){$rows | ForEach-Object {$_ | ConvertTo-Json -Compress} | Set-Content -LiteralPath (Join-Path $dir $file) -Encoding utf8}
function ReadReport($path){$html=Get-Content -LiteralPath $path -Raw;return ([regex]::Match($html,'(?m)^const DATA = (.*);$').Groups[1].Value | ConvertFrom-Json)}
try {
 $old=(New-Item -ItemType Directory -Path (Join-Path $scratch 'old')).FullName
 $now=(New-Item -ItemType Directory -Path (Join-Path $scratch 'now')).FullName
 $domain='S-1-5-21-1-2-3';$admin=$domain+'-512';$help=$domain+'-1200';$alice=$domain+'-1100';$bob=$domain+'-1101';$carol=$domain+'-1102'
 foreach($dir in @($old,$now)) {
  WriteRows $dir 'nodes_domains.test.ndjson' @(@{Id=$domain;Name='corp.test';Dn='DC=corp,DC=test'})
  WriteRows $dir 'nodes_groups.test.ndjson' @(@{Id=$admin;Name='Domain Admins';SecurityEnabled=$true},@{Id=$help;Name='Help Desk';SecurityEnabled=$true})
  WriteRows $dir 'nodes_users.test.ndjson' @(@{Id=$alice;Name='Alice';Disabled=$false},@{Id=$bob;Name='Bob'},@{Id=$carol;Name='Carol'})
  WriteRows $dir 'edges_memberof.test.ndjson' @(@{Source=$alice;Target=$help;EdgeType='MemberOf'},@{Source=$bob;Target=$admin;EdgeType='MemberOf'})
  WriteRows $dir 'edges_acl.test.ndjson' @(@{Source=$carol;Target=$domain;EdgeType='DCSync'},@{Source=$bob;Target='CN=Missing';EdgeType='WriteDacl';TargetIsDn=$true})
  @{Status='Complete';StartedUtc='2026-11-10T10:00:00Z';FinishedUtc='2026-11-10T10:05:00Z';BaseDn='DC=corp,DC=test';CoverageNotes=@('Synthetic scope; no remote collection.')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'scan-metadata.json') -Encoding utf8
 }
 WriteRows $old 'edges_acl.grant.ndjson' @(@{Source=$help;Target=$admin;EdgeType='AddMember'})
 WriteRows $now 'edges_acl.grant.ndjson' @(@{Source=$alice;Target=$admin;EdgeType='GenericAll'})
 $oldReport=Join-Path $scratch 'baseline';$newReport=Join-Path $scratch 'current'
 & $entry -Report -DatasetPaths $old -ReportOutputDir $oldReport
 $baseline=Join-Path $oldReport 'graph.sqlite'
 $baselineHash=(Get-FileHash -LiteralPath $baseline).Hash
 & $entry -Report -DatasetPaths $now -ReportOutputDir $newReport -BaselineIndexPath $baseline
 $oldData=ReadReport (Join-Path $oldReport 'report.html');$data=ReadReport (Join-Path $newReport 'report.html')
 Assert ($oldData.Analysis.Findings.Count -eq 3) 'Expected direct grant, inherited group path, and domain replication finding.'
 Assert (@($oldData.Analysis.Findings | Where-Object {$_.Source.i -eq $alice -and $_.Steps.Count -eq 2}).Count -eq 1) 'Group membership was not included in the complete chain.'
 Assert (@($data.Analysis.Findings | Where-Object {$_.Source.i -eq $bob}).Count -eq 0) 'Pure privileged membership or unresolved target became a finding.'
 Assert (@($data.Analysis.Findings | Where-Object Observation -eq 'Newly observed path').Count -eq 1) 'New path classification failed.'
 Assert (@($data.Analysis.Findings | Where-Object Observation -eq 'Still observed').Count -eq 1) 'Persisting domain capability was lost.'
 Assert ($data.Analysis.NoLongerObserved.Count -eq 2) 'Missing direct grant and dependent group chain were not compared.'
 Assert ($data.Analysis.Comparison.AddedRelationships -eq 1 -and $data.Analysis.Comparison.RemovedRelationships -eq 1 -and $data.Analysis.Comparison.UnchangedRelationships -eq 4) 'Full-index edge comparison counts are incorrect.'
 Assert ((Get-FileHash -LiteralPath $baseline).Hash -eq $baselineHash) 'Comparison changed the baseline.'
 Assert (@($data.Analysis.Objects | Where-Object {$_.i -eq $alice -and $_.Properties.Name -eq 'Alice'}).Count -eq 1) 'Retained object details were not embedded.'
 Assert ([DateTimeOffset]$data.Analysis.CollectionWindows[0].FinishedUtc -eq [DateTimeOffset]'2026-11-10T10:05:00Z') 'Collection time evidence was lost.'
 $db=[BH.Native.GraphIndex]::new($baseline,$false)
 try {
  $bounded=$db.AnalyzeExposures([string[]]@($admin,$domain),100,1,8)
  Assert ($bounded.Truncated -and $bounded.Findings.Count -eq 1) 'Analysis cap did not stop discovery.'
  $again=$db.AnalyzeExposures([string[]]@($admin,$domain),100,200,8)
  $sameFinding = @($again.Findings | Where-Object Source -eq $oldData.Analysis.Findings[0].Source.i)[0]
  Assert ($sameFinding.Id -eq $oldData.Analysis.Findings[0].Id) 'Finding identifiers are not stable.'
  Assert ($again.Findings[0].Reasons.Count -gt 1) 'Priority has no visible reasons.'
 } finally {$db.Dispose()}
 $blocked=$false
 try {& $entry -Report -DatasetPaths $now -ReportOutputDir $oldReport -BaselineIndexPath $baseline} catch {$blocked=$_.Exception.Message -like '*baseline graph index*'}
 Assert $blocked 'Baseline overwrite was not prevented.'
 $inspection=& $entry -InspectObject $alice -IndexPath (Join-Path $newReport 'graph.sqlite') | ConvertFrom-Json
 Assert ($inspection.Properties.Name -eq 'Alice' -and $inspection.Relationships.Count -ge 1) 'Full-index object inspection failed.'
 'Exposure analysis, path identity, priority reasons, snapshot changes, baseline protection, properties, timestamps, unresolved aliases, and bounds passed.'
 if($KeepArtifacts){"Artifacts: $scratch"}
} finally {
 if(-not $KeepArtifacts){$resolved=(Resolve-Path -LiteralPath $scratch).ProviderPath;if([string]::Equals($resolved,$scratch,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $resolved -Recurse -Force}}
}
