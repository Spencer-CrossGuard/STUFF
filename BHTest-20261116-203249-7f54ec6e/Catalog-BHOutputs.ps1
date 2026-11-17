#Requires -Version 5.1
[CmdletBinding()]
param([string]$Root=(Join-Path $PSScriptRoot 'BHData'))
$ErrorActionPreference='Stop'
$rootDirectory=[IO.Path]::GetFullPath($Root)
New-Item -ItemType Directory -Path $rootDirectory -Force|Out-Null
$base=[Uri]::new($rootDirectory.TrimEnd('\')+'\')
$latestLink='<p>No completed run report is available yet.</p>'
$completed=foreach($folder in Get-ChildItem (Join-Path $rootDirectory 'runs') -Directory -ErrorAction SilentlyContinue){
 try{
  $entry=Get-Content (Join-Path $folder.FullName 'run.json') -Raw -ErrorAction Stop|ConvertFrom-Json
  if($entry.Status -eq 'Complete' -and $entry.ReportPath -and (Test-Path -LiteralPath $entry.ReportPath) -and (Test-Path -LiteralPath (Join-Path $folder.FullName 'START-HERE.html'))){
   [pscustomobject]@{Folder=$folder.FullName;Label=$entry.Label;StartedUtc=$entry.StartedUtc}
  }
 }catch{}
}
$latest=$completed|Sort-Object StartedUtc -Descending|Select-Object -First 1
if($latest){
 $latestUrl=$base.MakeRelativeUri([Uri]::new((Join-Path $latest.Folder 'START-HERE.html'))).ToString()
 $latestLink='<h2><a href="'+[Net.WebUtility]::HtmlEncode($latestUrl)+'">Open latest completed run</a></h2><p>'+[Net.WebUtility]::HtmlEncode($latest.Label)+'<br>'+[Net.WebUtility]::HtmlEncode($latest.StartedUtc)+'</p>'
}
$rows=foreach($file in Get-ChildItem $rootDirectory -Filter report.html -Recurse -File|Sort-Object LastWriteTime -Descending){
 $report=$file.FullName
 if(-not (Test-Path -LiteralPath $report)){continue}
 $url=$base.MakeRelativeUri([Uri]::new($report)).ToString()
 '<li><a href="'+[Net.WebUtility]::HtmlEncode($url)+'">'+[Net.WebUtility]::HtmlEncode($file.LastWriteTime.ToString('yyyy-MM-dd HH:mm')+' - '+$base.MakeRelativeUri([Uri]::new($file.DirectoryName+'\')).ToString())+'</a></li>'
}
$html='<!doctype html><html><head><meta charset="utf-8"><title>BHTest outputs</title><style>body{font:17px system-ui;max-width:960px;margin:50px auto;padding:20px}li{margin:12px 0}</style></head><body><h1>BHTest outputs</h1>'+$latestLink+'<h2><a href="runs/INDEX.html">Open run history</a></h2><p>For new work, open a run and its START-HERE page. Each run keeps its report, graph, collected data and logs together.</p><details><summary>Earlier saved reports ('+@($rows).Count+')</summary><p>Historical and validation reports are preserved here. They are not automatically the latest successful run.</p><ul>'+($rows -join "`n")+'</ul></details><p>No existing data was moved or deleted. To refresh this catalog, run Catalog-BHOutputs.ps1.</p></body></html>'
[IO.File]::WriteAllText((Join-Path $rootDirectory 'START-HERE.html'),$html,[Text.UTF8Encoding]::new($false))
