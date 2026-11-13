#Requires -Version 5.1
[CmdletBinding()]
param([string] $ScratchRoot = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $project 'src/BH.Core.psm1') -DisableNameChecking
Initialize-BHNative
Import-Module (Join-Path $project 'src/BH.Endpoint.psm1') -DisableNameChecking
$module = Get-Module BH.Endpoint
$scratch = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) "bh-endpoints-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
$originalWorker = & $module { $script:WorkerPath }
try {
    $nodeFile = Join-Path $scratch 'nodes_computers.test.ndjson.gz'
    $writer = [BH.Native.NdjsonWriter]::new($nodeFile, $false)
    try {
        $index = 0
        foreach ($hostName in 'fast.test','slow.test','bad.test','') {
            $writer.WriteLine((@{ Id = "S-1-5-21-10-20-30-$((++$index) + 2000)"; Dns = $hostName; Name = $hostName } | ConvertTo-Json -Compress))
        }
    }
    finally { $writer.Dispose() }
    $fake = Join-Path $scratch 'FakeWorker.ps1'
    @'
param([string] $OutputPath)
$ErrorActionPreference = 'Stop'
[Console]::Out.WriteLine('{"Ready":true}')
[Console]::Out.Flush()
while ($null -ne ($line = [Console]::ReadLine())) {
    $request = $line | ConvertFrom-Json
    if ($request.Host -eq 'slow.test') { Start-Sleep -Seconds 15 }
    if ($request.Host -eq 'bad.test') {
        $reply = @{ Id = $request.Id; Host = $request.Host; Edges = 0; SuccessfulOperations = 0
            FailedOperations = 5; UnresolvedSessions = 0; Errors = @('Synthetic access denied') }
    }
    else {
        $edge = @{ Source = 'S-1-5-21-10-20-30-1100'; Target = $request.Id; EdgeType = 'AdminTo' } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($OutputPath, $edge + [Environment]::NewLine)
        $reply = @{ Id = $request.Id; Host = $request.Host; Edges = 1; SuccessfulOperations = 5
            FailedOperations = 0; UnresolvedSessions = 0; Errors = @() }
    }
    [Console]::Out.WriteLine(($reply | ConvertTo-Json -Compress))
    [Console]::Out.Flush()
}
'@ | Set-Content -LiteralPath $fake -Encoding UTF8
    & $module { param($Path) $script:WorkerPath = $Path } $fake
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $result = Start-BHEndpointCollection -ScanDir $scratch -MaxWorkers 2 -TimeoutSeconds 2
    if ($result.Attempted -ne 3 -or $result.Complete -ne 1 -or $result.Failed -ne 1 -or
        $result.TimedOut -ne 1 -or $result.Skipped -ne 1 -or $result.Edges -ne 1) {
        throw "Endpoint coverage totals are wrong: $($result | ConvertTo-Json -Compress)"
    }
    if ($clock.Elapsed.TotalSeconds -gt 12) { throw 'The stalled endpoint bypassed the parent deadline.' }
    $rows = @([BH.Native.NdjsonReader]::Lines((Join-Path $scratch 'edges_access.endpoints.ndjson.gz')))
    if ($rows.Count -ne 1) { throw 'Failed or timed-out endpoints contaminated the merged edges.' }
    $statusRows = @([BH.Native.NdjsonReader]::Lines((Join-Path $scratch 'endpoint-status.ndjson.gz')))
    if ($statusRows.Count -ne 4 -or @(Get-ChildItem -LiteralPath $scratch -Filter 'endpoint-worker-*.tmp').Count -ne 0) {
        throw 'Per-host status or temporary-file cleanup was incomplete.'
    }
    'Endpoint process reuse, timeout, failure coverage, gzip output and streaming input checks passed.'
}
finally {
    & $module { param($Path) $script:WorkerPath = $Path } $originalWorker
    $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ([string]::Equals($resolved, $scratch, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
