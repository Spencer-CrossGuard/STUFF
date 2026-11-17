function Get-BHLatestReport {
    param([Parameter(Mandatory)][string]$DataRoot)
    $candidates=[Collections.Generic.List[object]]::new()
    try{
        $saved=Get-Content (Join-Path $DataRoot 'latest-report.json') -Raw -ErrorAction Stop|ConvertFrom-Json
        $index=Join-Path (Split-Path $saved.ReportPath -Parent) 'graph.sqlite'
        if((Test-Path -LiteralPath $saved.ReportPath -PathType Leaf) -and (Test-Path -LiteralPath $index -PathType Leaf) -and -not (Test-Path -LiteralPath (Join-Path (Split-Path $index -Parent) 'report.lock'))){$candidates.Add((Get-Item -LiteralPath $index))}
    }catch{}
    $runs=Join-Path $DataRoot 'runs'
    foreach($folder in @(Get-ChildItem -LiteralPath $runs -Directory -ErrorAction SilentlyContinue)){
        try{
            $record=Get-Content (Join-Path $folder.FullName 'run.json') -Raw -ErrorAction Stop|ConvertFrom-Json
            if($record.Status -ne 'Complete' -or -not $record.ReportPath){continue}
            $report=[IO.Path]::GetFullPath($record.ReportPath)
            $index=Join-Path (Split-Path $report -Parent) 'graph.sqlite'
            if((Test-Path -LiteralPath $report -PathType Leaf) -and (Test-Path -LiteralPath $index -PathType Leaf) -and -not (Test-Path -LiteralPath (Join-Path (Split-Path $index -Parent) 'report.lock'))){$candidates.Add((Get-Item -LiteralPath $index))}
        }catch{}
    }
    $reports=Join-Path $DataRoot 'reports'
    $folders=@($reports)+@(Get-ChildItem -LiteralPath $reports -Directory -ErrorAction SilentlyContinue|ForEach-Object FullName)
    foreach($folder in $folders){
        $index=Join-Path $folder 'graph.sqlite'
        if((Test-Path -LiteralPath (Join-Path $folder 'report.html') -PathType Leaf) -and (Test-Path -LiteralPath $index -PathType Leaf) -and -not (Test-Path -LiteralPath (Join-Path $folder 'report.lock'))){$candidates.Add((Get-Item -LiteralPath $index))}
    }
    $latest=$candidates|Sort-Object LastWriteTimeUtc,FullName -Descending|Select-Object -First 1
    if($latest){return Join-Path $latest.DirectoryName 'report.html'}
}
