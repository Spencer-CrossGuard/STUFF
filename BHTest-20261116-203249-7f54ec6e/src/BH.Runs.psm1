Set-StrictMode -Version Latest
function New-BHRun {
    param([Parameter(Mandatory)][string]$Root,[string]$Label='Directory scan',[string[]]$Targets=@())
    $id='run-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
    $directory=[IO.Path]::GetFullPath((Join-Path $Root $id))
    foreach($part in @('','data','logs')){New-Item -ItemType Directory -Path (Join-Path $directory $part) -Force|Out-Null}
    $record=[ordered]@{RunId=$id;Label=$Label;Targets=$Targets;Status='Running';StartedUtc=[DateTime]::UtcNow.ToString('o');UpdatedUtc=[DateTime]::UtcNow.ToString('o');ReportPath=$null;Error=$null}
    $record|ConvertTo-Json|Set-Content (Join-Path $directory 'run.json') -Encoding UTF8
    Update-BHRun -Directory $directory -Status Running
    [pscustomobject]@{Directory=$directory;RunId=$id;LogPath=(Join-Path $directory 'logs\collection.log')}
}
function Update-BHRun {
    param([Parameter(Mandatory)][string]$Directory,[ValidateSet('Running','Complete','Failed','Cancelled')][string]$Status,[string]$ReportPath,[string]$ErrorMessage)
    $path=Join-Path $Directory 'run.json'
    $record=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    $record.Status=$Status;$record.UpdatedUtc=[DateTime]::UtcNow.ToString('o')
    if($ReportPath){$record.ReportPath=[IO.Path]::GetFullPath($ReportPath)}
    if($ErrorMessage){$record.Error=$ErrorMessage}
    if($Status -eq 'Complete'){$record.Error=$null}
    $temporary=$path+'.tmp'
    $record|ConvertTo-Json|Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $path -Force
    $label=[Net.WebUtility]::HtmlEncode($record.Label)
    $errorText=[Net.WebUtility]::HtmlEncode([string]$record.Error)
    $reportLink='<p>No report is available yet. For a completed collection, select the run datasets in the Report tab.</p>'
    if($record.ReportPath -and (Test-Path -LiteralPath $record.ReportPath)){
        $base=[Uri]::new(([IO.Path]::GetFullPath($Directory).TrimEnd('\')+'\'))
        $relative=$base.MakeRelativeUri([Uri]::new($record.ReportPath)).ToString()
        $reportLink='<p><a href="'+[Net.WebUtility]::HtmlEncode($relative)+'">Open report</a></p>'
    }
    $html='<!doctype html><html><head><meta charset="utf-8"><title>Tessera run</title><style>body{font:17px system-ui;max-width:760px;margin:60px auto;padding:20px;background:#f5f7fa;color:#182336}a{color:#1655b0}li{margin:14px 0}</style></head><body><h1>'+ $label+'</h1><p>Status: <strong>'+$Status+'</strong></p><p>Run: '+$record.RunId+'</p>'+$reportLink+'<ul><li><a href="data/">Collected data</a></li><li><a href="logs/">Diagnostics and logs</a></li><li><a href="run.json">Run details</a></li></ul><p>'+ $errorText+'</p><p>Keep this whole run folder. The graph.sqlite beside the report opens in Tessera.Viewer.ps1. Running status can also mean an interrupted run; consult the log before using unfinished data.</p></body></html>'
    [IO.File]::WriteAllText((Join-Path $Directory 'START-HERE.html'),$html,[Text.UTF8Encoding]::new($false))
    @('Tessera '+$record.RunId,'Status: '+$Status,'Open START-HERE.html to review this run.','Report: '+$record.ReportPath,'Data: data','Log: logs\collection.log')|Set-Content (Join-Path $Directory 'START-HERE.txt') -Encoding UTF8
    $rows=foreach($folder in Get-ChildItem (Split-Path $Directory -Parent) -Directory|Sort-Object Name -Descending){
        $manifest=Join-Path $folder.FullName 'run.json'
        if(-not (Test-Path $manifest)){continue}
        try{$entry=Get-Content $manifest -Raw|ConvertFrom-Json}catch{continue}
        '<li><a href="'+[Uri]::EscapeDataString($folder.Name)+'/START-HERE.html">'+[Net.WebUtility]::HtmlEncode($entry.Label)+'</a> — '+[Net.WebUtility]::HtmlEncode($entry.Status)+' — '+[Net.WebUtility]::HtmlEncode($entry.StartedUtc)+'</li>'
    }
    [IO.File]::WriteAllText((Join-Path (Split-Path $Directory -Parent) 'INDEX.html'),('<!doctype html><meta charset="utf-8"><title>Tessera runs</title><h1>Tessera runs</h1><ul>'+($rows -join "`n")+'</ul>'),[Text.UTF8Encoding]::new($false))
}
Export-ModuleMember -Function New-BHRun,Update-BHRun
