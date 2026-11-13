# Builds a portable HTML preview and a local indexed graph from scan datasets.
Set-StrictMode -Version Latest

$script:BHSearchIndexCap = 25000
$script:BHEgoGraphDepth = 4
$script:BHEgoGraphNodeCap = 4000
$script:BHEgoGraphEdgeCap = 12000
. (Join-Path $PSScriptRoot 'BH.Report.Analysis.ps1')

# Principals that are not objects you can find by searching a domain, or whose
# SID is the same in every domain, so they can be named without any lookup.
$script:BHWellKnownSids = @{
    'S-1-1-0' = 'Everyone'; 'S-1-3-0' = 'Creator Owner'; 'S-1-3-1' = 'Creator Group'
    'S-1-5-4' = 'Interactive'; 'S-1-5-6' = 'Service'; 'S-1-5-7' = 'Anonymous Logon'
    'S-1-5-9' = 'Enterprise Domain Controllers'; 'S-1-5-10' = 'Self'; 'S-1-5-11' = 'Authenticated Users'
    'S-1-5-18' = 'SYSTEM'; 'S-1-5-19' = 'Local Service'; 'S-1-5-20' = 'Network Service'
}
$script:BHBuiltinGroups = @{
    544 = 'Administrators (builtin)'; 545 = 'Users (builtin)'; 546 = 'Guests (builtin)'; 547 = 'Power Users (builtin)'
    548 = 'Account Operators (builtin)'; 549 = 'Server Operators (builtin)'; 550 = 'Print Operators (builtin)'
    551 = 'Backup Operators (builtin)'; 552 = 'Replicators (builtin)'; 554 = 'Pre-Windows 2000 Compatible Access (builtin)'
    555 = 'Remote Desktop Users (builtin)'; 556 = 'Network Configuration Operators (builtin)'
    557 = 'Incoming Forest Trust Builders (builtin)'; 558 = 'Performance Monitor Users (builtin)'
    559 = 'Performance Log Users (builtin)'; 560 = 'Windows Authorization Access Group (builtin)'
    561 = 'Terminal Server License Servers (builtin)'; 562 = 'Distributed COM Users (builtin)'
    568 = 'IIS_IUSRS (builtin)'; 569 = 'Cryptographic Operators (builtin)'; 573 = 'Event Log Readers (builtin)'
    574 = 'Certificate Service DCOM Access (builtin)'; 575 = 'RDS Remote Access Servers (builtin)'
    576 = 'RDS Endpoint Servers (builtin)'; 577 = 'RDS Management Servers (builtin)'
    578 = 'Hyper-V Administrators (builtin)'; 579 = 'Access Control Assistance Operators (builtin)'
    580 = 'Remote Management Users (builtin)'
}
$script:BHDomainRids = @{
    498 = 'Enterprise Read-only Domain Controllers'; 500 = 'Administrator'; 501 = 'Guest'; 502 = 'krbtgt'
    512 = 'Domain Admins'; 513 = 'Domain Users'; 514 = 'Domain Guests'; 515 = 'Domain Computers'
    516 = 'Domain Controllers'; 517 = 'Cert Publishers'; 518 = 'Schema Admins'; 519 = 'Enterprise Admins'
    520 = 'Group Policy Creator Owners'; 521 = 'Read-only Domain Controllers'; 522 = 'Cloneable Domain Controllers'
    525 = 'Protected Users'; 526 = 'Key Admins'; 527 = 'Enterprise Key Admins'; 553 = 'RAS and IAS Servers'
    571 = 'Allowed RODC Password Replication Group'; 572 = 'Denied RODC Password Replication Group'
}

function Get-BHWellKnownSid {
    <# Name + report type for a SID whose meaning is fixed by Windows; $null for anything else. #>
    param([string] $Sid)
    if (-not $Sid) { return $null }
    if ($script:BHWellKnownSids.ContainsKey($Sid)) { return @{ Name = $script:BHWellKnownSids[$Sid]; Type = 'special' } }
    if ($Sid -match '^S-1-5-32-(\d+)$') {
        $rid = [int]$Matches[1]
        if ($script:BHBuiltinGroups.ContainsKey($rid)) { return @{ Name = $script:BHBuiltinGroups[$rid]; Type = 'groups' } }
    }
    if ($Sid -match '^S-1-5-21-\d+-\d+-\d+-(\d+)$') {
        $rid = [int]$Matches[1]
        if ($script:BHDomainRids.ContainsKey($rid)) {
            $type = if ($rid -in 500, 501, 502) { 'users' } else { 'groups' }
            return @{ Name = $script:BHDomainRids[$rid]; Type = $type }
        }
    }
    return $null
}

function Export-BHCsvAssets {
    <#
        .SYNOPSIS
        Writes assets/*.csv -- the escape valve for anything past the
        interactive caps, so by definition it touches the entire dataset.
        That makes it the highest-volume single operation in the report
        phase; the actual write loop runs in C# (BH.Report.Native.cs,
        CsvExporter) rather than a per-line PowerShell loop + StreamWriter.
    #>
    [CmdletBinding()]
    param([string[]] $DatasetPaths, [string] $AssetsDir)

    New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null
    $kinds = Get-BHDatasetKinds
    $allPrefixes = @(foreach ($kind in $kinds.Nodes) { "nodes_$kind" }) +
        @(foreach ($kind in $kinds.Edges) { "edges_$kind" })

    foreach ($prefix in $allPrefixes) {
        $files = @(Get-BHDatasetFiles -DatasetPaths $DatasetPaths -Prefix $prefix)
        if ($files.Count -eq 0) { continue }
        $outPath = Join-Path $AssetsDir "$prefix.csv"

        # Field set per prefix mirrors what the collector actually writes; a
        # fixed header keeps the CSV well-formed even though NDJSON fields
        # are written in a stable but not schema-enforced order.
        $fields = if ($prefix -like 'nodes_*') {
            @('Id', 'Sid', 'Guid', 'Dn', 'Name', 'SamAccountName', 'Disabled', 'Dns', 'Os')
        } else {
            @('Source', 'Target', 'EdgeType')
        }

        [BH.Native.CsvExporter]::Export($files, $fields, $outPath)
    }
}

function Get-BHGraphNodeDetails {
    param([BH.Native.GraphIndex] $Index, [string] $Id)

    $row = $Index.Node($Id)
    if ($row) {
        return @{ i = $Id; n = $(if ($row[0]) { $row[0] } else { $Id }); t = $row[1] }
    }
    $known = Get-BHWellKnownSid $Id
    if ($known) { return @{ i = $Id; n = $known.Name; t = $known.Type } }
    return @{ i = $Id; n = $Id; t = '?' }
}

function Start-BHReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]] $DatasetPaths,
        [Parameter(Mandatory)][string] $OutputDir,
        [switch] $ExportCsv,
        [string] $BaselineIndexPath,
        [ValidateSet('Quick','Deep')][string] $AnalysisMode = 'Quick',
        [ValidateRange(0,1000000)][int] $AnalysisDepth = 8,
        [ValidateRange(0,1000000000)][long] $AnalysisVisitLimit = 50000,
        [ValidateRange(0,86400)][int] $AnalysisSeconds = 0,
        [switch] $ResumeAnalysis,
        [scriptblock] $ProgressCallback
    )

    Initialize-BHNative
    if ($AnalysisMode -eq 'Deep') {
        if (-not $PSBoundParameters.ContainsKey('AnalysisDepth')) { $AnalysisDepth = 0 }
        if (-not $PSBoundParameters.ContainsKey('AnalysisVisitLimit')) { $AnalysisVisitLimit = 0 }
        if (-not $PSBoundParameters.ContainsKey('AnalysisSeconds')) { $AnalysisSeconds = 600 }
    }
    if ($ResumeAnalysis -and $AnalysisMode -ne 'Deep') { throw 'Resume requires Deep analysis mode.' }
    Assert-BHDatasetsReady -DatasetPaths $DatasetPaths
    $nodeCounts = [ordered]@{ domains = 0L; ous = 0L; users = 0L; groups = 0L; computers = 0L; gpos = 0L }
    $edgeCounts = [ordered]@{ memberof = 0L; contains = 0L; gplink = 0L; acl = 0L; access = 0L; observed = 0L }
    $output = New-Item -ItemType Directory -Path $OutputDir -Force
    $indexPath = Join-Path $output.FullName 'graph.sqlite'
    if ($BaselineIndexPath -and [string]::Equals([IO.Path]::GetFullPath($BaselineIndexPath), $indexPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose a different report output folder so the selected baseline graph index is preserved.'
    }
    $reportPath = Join-Path $output.FullName 'report.html'
    $checkpoint = Join-Path $output.FullName 'analysis-checkpoint.sqlite'
    $cancelFile = Join-Path $output.FullName 'analysis.cancel'
    if ($AnalysisMode -eq 'Deep') {
        if ($ResumeAnalysis -and -not (Test-Path -LiteralPath $checkpoint)) { throw 'No analysis checkpoint exists in this output folder.' }
        if (-not $ResumeAnalysis -and (Test-Path -LiteralPath $checkpoint)) { throw 'A checkpoint already exists. Select Resume analysis or choose a new output folder.' }
        if ($BaselineIndexPath -and [string]::Equals([IO.Path]::GetFullPath($BaselineIndexPath), $checkpoint, [StringComparison]::OrdinalIgnoreCase)) { throw 'The baseline cannot be the current analysis checkpoint.' }
    }
    $suffix = [guid]::NewGuid().ToString('N')
    $workingIndex = "$indexPath.$suffix.tmp"
    $workingReport = "$reportPath.$suffix.tmp"
    try {
        $reportLock = [IO.FileStream]::new((Join-Path $output.FullName 'report.lock'), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
    }
    catch { throw 'The report output folder is already in use or cannot be locked. Wait for its running report or choose a different folder.' }

    try {
        if (Test-Path -LiteralPath $cancelFile) { [IO.File]::Delete($cancelFile) }
        if ($ProgressCallback) { & $ProgressCallback 'Verifying dataset fingerprint' }
        $fingerprint = Get-BHAnalysisFingerprint $DatasetPaths
        if ($ProgressCallback) { & $ProgressCallback $(if ($ResumeAnalysis) { 'Opening saved analysis snapshot' } else { 'Indexing nodes and edges' }) }
        $index = if ($ResumeAnalysis) { [BH.Native.GraphIndex]::new($checkpoint, $false, $true) } else { [BH.Native.GraphIndex]::new($workingIndex, $true) }
        try {
            if ($ResumeAnalysis) {
                if ($index.DiskSourceFingerprint() -ne $fingerprint) { throw 'Dataset fingerprint changed. Resume was rejected; use the original inputs or a new output folder.' }
            }
            else {
                Import-BHGraphDataset -Index $index -DatasetPaths $DatasetPaths -RetainNodes
                if ($AnalysisMode -eq 'Deep') {
                    $index.PrepareDiskAnalysis((Get-BHExposureTargets $index), $fingerprint)
                    $index.Dispose()
                    Move-Item -LiteralPath $workingIndex -Destination $checkpoint
                    $index = [BH.Native.GraphIndex]::new($checkpoint, $false, $true)
                }
            }
            foreach ($row in $index.NodeCounts()) { $nodeCounts[$row[0]] = [long]$row[1] }
            foreach ($row in $index.EdgeCounts()) { $edgeCounts[$row[0]] = [long]$row[1] }
            $reviewBuckets = [string[]]@('acl', 'access', 'observed' | Where-Object { $edgeCounts[$_] -gt 0 })
            $reviewRelationships = @(foreach ($row in $index.ReviewRelationships($reviewBuckets)) {
                $source = Get-BHGraphNodeDetails $index $row[0]
                $target = Get-BHGraphNodeDetails $index $row[1]
                @{ s = $row[0]; t = $row[1]; ty = $row[2]; sn = $source.n; tn = $target.n; tk = $target.t }
            })
            $unresolvedMembership = 0L
            $unresolvedAccess = 0L
            foreach ($row in $index.UnresolvedCounts()) {
                if ($row[0] -eq 'memberof') { $unresolvedMembership = [long]$row[1] }
                if ($row[0] -eq 'access') { $unresolvedAccess = [long]$row[1] }
            }
            $disabledUsers = 0L
            foreach ($row in $index.UserCounts()) {
                if ($row[0] -eq 'true') { $disabledUsers = [long]$row[1] }
            }
            $highValue = @(foreach ($row in $index.Groups()) {
                if ([BH.Native.DirectoryIdentity]::IsPrivilegedGroup($row[0])) {
                    @{ Id = $row[0]; Name = $(if ($row[1]) { $row[1] } else { $row[0] }); Type = 'groups' }
                }
            })
            $aclHolders = @(foreach ($row in $index.TopAclHolders()) {
                $name = if ($row[1]) { $row[1] } else { (Get-BHGraphNodeDetails $index $row[0]).n }
                @{ Id = $row[0]; Name = $name; Type = $(if ($row[2]) { $row[2] } else { '?' }); OutDegree = [long]$row[3] }
            })
            $searchIndex = @(foreach ($row in $index.SearchSample($script:BHSearchIndexCap + 1)) {
                @{ i = $row[0]; n = $row[1]; t = $row[2] }
            })
            $searchTruncated = $searchIndex.Count -gt $script:BHSearchIndexCap
            if ($searchTruncated) { $searchIndex = @($searchIndex[0..($script:BHSearchIndexCap - 1)]) }

            if ($ProgressCallback) { & $ProgressCallback 'Computing privileged-group neighborhood' }
            $seeds = @(foreach ($group in $highValue) { $group.Id })
            $graph = $index.Neighborhood($seeds, $script:BHEgoGraphDepth, $script:BHEgoGraphNodeCap, $script:BHEgoGraphEdgeCap)
            $details = @(foreach ($id in $graph.NodeIds) { Get-BHGraphNodeDetails $index $id })
            $edges = @(foreach ($edge in $graph.Edges) { @{ s = $edge.Source; t = $edge.Target; ty = $edge.EdgeType } })
            if ($ProgressCallback) { & $ProgressCallback 'Analyzing exposure paths and snapshot changes' }
            $evidenceIds = [string[]]@(@($details | ForEach-Object { $_.i }) + @($searchIndex | Select-Object -First 100 | ForEach-Object { $_.i }))
            $analysis = Get-BHReportAnalysis -Index $index -BaselineIndexPath $BaselineIndexPath -DatasetPaths $DatasetPaths -AdditionalIds $evidenceIds `
                -AnalysisMode $AnalysisMode -AnalysisDepth $AnalysisDepth -AnalysisVisitLimit $AnalysisVisitLimit -AnalysisSeconds $AnalysisSeconds `
                -CancelFile $cancelFile -Fingerprint $fingerprint -ProgressCallback $ProgressCallback
        }
        finally { $index.Dispose() }
        if ($AnalysisMode -eq 'Deep') { Copy-Item -LiteralPath $checkpoint -Destination $workingIndex }

        if ($ExportCsv) {
            if ($ProgressCallback) { & $ProgressCallback 'Exporting source rows to CSV' }
            Export-BHCsvAssets -DatasetPaths $DatasetPaths -AssetsDir (Join-Path $output.FullName 'assets')
        }

        $data = [ordered]@{
            GeneratedUtc = (Get-Date).ToUniversalTime().ToString('o')
            Sources = $DatasetPaths
            NodeCounts = $nodeCounts
            EdgeCounts = $edgeCounts
            ReviewRelationships = $reviewRelationships
            Analysis = $analysis
            EnabledUsers = $nodeCounts.users - $disabledUsers
            DisabledUsers = $disabledUsers
            HighValueNodes = $highValue
            TopAclHolders = $aclHolders
            SearchIndex = $searchIndex
            SearchIndexTruncated = $searchTruncated
            CsvExported = [bool]$ExportCsv
            UnresolvedMembership = $unresolvedMembership
            UnresolvedAccess = $unresolvedAccess
            CoverageNotes = @(@(Get-BHDatasetCoverageNotes $DatasetPaths) + @(
                if ($unresolvedAccess -gt 0) { "$unresolvedAccess access relationships have unresolved targets or sources, including SPNs outside the supplied data. They cannot be traversed until resolved." }
            ) | Sort-Object -Unique)
            Ego = @{
                Skipped = ($highValue.Count -eq 0)
                Reason = 'No recognized privileged group SIDs were found in the supplied data.'
                Nodes = $details
                Edges = $edges
                Truncated = $graph.Truncated
            }
        }

        $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'report-template.html') -Raw -Encoding UTF8
        $helper = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'report-helper.js') -Raw -Encoding UTF8
        $template = $template.Replace('/*__BH_HELPER__*/', $helper)
        $findingsScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'report-findings.js') -Raw -Encoding UTF8
        $template = $template.Replace('/*__BH_FINDINGS__*/', $findingsScript)
        $graphScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'report-graph.js') -Raw -Encoding UTF8
        $template = $template.Replace('/*__BH_GRAPH__*/', $graphScript)
        $json = $data | ConvertTo-Json -Depth 12 -Compress
        $json = $json.Replace('&', '\u0026').Replace('<', '\u003c').Replace('>', '\u003e').
            Replace([string][char]0x2028, '\u2028').Replace([string][char]0x2029, '\u2029')
        [System.IO.File]::WriteAllText($workingReport, $template.Replace('/*__BH_DATA__*/', $json))
        Move-Item -LiteralPath $workingIndex -Destination $indexPath -Force
        Move-Item -LiteralPath $workingReport -Destination $reportPath -Force
        if ($ProgressCallback) { & $ProgressCallback 'Done' }
        return $reportPath
    }
    finally {
        try {
            foreach ($temporary in $workingIndex, $workingReport) {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            }
        } finally { $reportLock.Dispose() }
    }
}

Export-ModuleMember -Function Start-BHReport, Export-BHCsvAssets
