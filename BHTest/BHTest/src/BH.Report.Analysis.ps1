# Dot-sourced by BH.Report.psm1 so these helpers share its identity labels.
function Get-BHExposureTargets {
    param([BH.Native.GraphIndex] $Index)
    $ids = [Collections.Generic.List[string]]::new()
    foreach ($row in $Index.Groups()) {
        if ([BH.Native.DirectoryIdentity]::IsPrivilegedGroup($row[0])) { $ids.Add($row[0]) }
    }
    foreach ($row in $Index.DomainTargets()) { $ids.Add($row[0]) }
    return ,$ids.ToArray()
}
function Convert-BHExposureFinding {
    param($Finding, [BH.Native.GraphIndex] $Index, [string] $Observation = 'Current')
    $source = Get-BHGraphNodeDetails $Index $Finding.Source
    $target = Get-BHGraphNodeDetails $Index $Finding.Target
    $steps = @(foreach ($step in $Finding.Steps) {
        $from = Get-BHGraphNodeDetails $Index $step.Source
        $to = Get-BHGraphNodeDetails $Index $step.Target
        @{ s = $step.Source; t = $step.Target; ty = $step.EdgeType; sn = $from.n; tn = $to.n }
    })
    return @{
        Id = $Finding.Id; Source = $source; Target = $target; Steps = $steps
        Priority = $Finding.Priority; Score = $Finding.Score; Reasons = @($Finding.Reasons)
        DirectMembers = $Finding.DirectMembers; MissingObjects = $Finding.MissingObjects
        Observation = $Observation; Evidence = 'Recorded chain; effective access and collection coverage need validation.'
        HopCount = $(if ($Finding.HopCount) { $Finding.HopCount } else { $Finding.Steps.Count })
        PathLimited = $Finding.PathLimited; NextPathId = $Finding.NextPathId; Sequence = $Finding.Sequence
    }
}
function Get-BHCollectionWindows {
    param([string[]] $DatasetPaths)
    foreach ($path in $DatasetPaths) {
        $found = $false
        foreach ($name in 'scan-metadata.json', 'merge-metadata.json') {
            $file = Join-Path $path $name
            if (-not (Test-Path -LiteralPath $file)) { continue }
            $metadata = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
            $row = [ordered]@{ Dataset = $path; Kind = $(if ($name -eq 'merge-metadata.json') { 'Merge processing time' } else { 'Collection window' }) }
            foreach ($field in 'StartedUtc','FinishedUtc','CreatedUtc','ScopeDescription','BaseDn','Server','Source','Status') {
                if ($metadata.PSObject.Properties[$field]) { $row[$field] = $metadata.$field }
            }
            [pscustomobject]$row
            $found = $true
            break
        }
        if (-not $found) { [pscustomobject]@{ Dataset = $path; Status = 'Collection time not supplied' } }
    }
}
function Get-BHReportAnalysis {
    param([BH.Native.GraphIndex] $Index, [string] $BaselineIndexPath, [string[]] $DatasetPaths, [string[]] $AdditionalIds,
        [string] $AnalysisMode = 'Quick', [int] $AnalysisDepth = 8, [long] $AnalysisVisitLimit = 50000,
        [int] $AnalysisSeconds = 0, [string] $CancelFile, [string] $Fingerprint, [scriptblock] $ProgressCallback)
    $notify = [Action[BH.Native.DiskAnalysisProgress]]{
        param($state)
        if ($ProgressCallback) {
            & $ProgressCallback ("Analysis: {0:N0} identities, {1:N0} processed, {2:N0} candidates, depth {3}; {4}" -f $state.Visited, $state.Processed, $state.Findings, $state.Depth, $state.Status)
        }
    }.GetNewClosure()
    $state = $Index.AnalyzeDisk((Get-BHExposureTargets $Index), $Fingerprint, $AnalysisDepth, $AnalysisVisitLimit, $AnalysisSeconds, $CancelFile, $notify)
    $preview = @($Index.DiskFindingPage([int]::MaxValue, 0, 200))
    $findings = [Collections.Generic.List[object]]::new()
    $removed = [Collections.Generic.List[object]]::new()
    $comparison = $null
    if ($BaselineIndexPath) {
        $baseline = [BH.Native.GraphIndex]::new((Get-Item -LiteralPath $BaselineIndexPath -ErrorAction Stop).FullName, $false)
        try {
            $diff = $Index.CompareSnapshot($BaselineIndexPath)
            foreach ($finding in $preview) {
                $observation = if ($finding.PathLimited) { 'Comparison not checked (long path)' } elseif ($baseline.ContainsPath($finding.Steps)) { 'Still observed' } else { 'Newly observed path' }
                $findings.Add((Convert-BHExposureFinding $finding $Index $observation))
            }
            $previousLimited = $true
            if ($baseline.HasDiskAnalysis()) {
                $previousState = $baseline.DiskAnalysisState()
                $previousRows = @($baseline.DiskFindingPage([int]::MaxValue, 0, 200))
                $previousLimited = $previousState.Status -ne 'Completed' -or $previousState.Findings -gt 200
            }
            else {
                $previous = $baseline.AnalyzeExposures((Get-BHExposureTargets $baseline), 50000, 200, 8)
                $previousRows = @($previous.Findings)
                $previousLimited = $previous.Truncated
            }
            foreach ($finding in $previousRows) {
                if (-not $finding.PathLimited -and -not $Index.ContainsPath($finding.Steps)) { $removed.Add((Convert-BHExposureFinding $finding $baseline 'No longer observed')) }
            }
            $comparison = @{
                Baseline = [IO.Path]::GetFullPath($BaselineIndexPath)
                AddedObjects = $diff.AddedNodes; RemovedObjects = $diff.RemovedNodes
                AddedRelationships = $diff.AddedEdges; RemovedRelationships = $diff.RemovedEdges; UnchangedRelationships = $diff.UnchangedEdges
                AddedSample = @($diff.AddedSample); RemovedSample = @($diff.RemovedSample)
                PreviousAnalysisLimited = $previousLimited
                Note = 'Full-index counts compare recorded identities and relationships. Path lists are bounded. Scope, collection methods, permissions and timing may differ; a missing relationship does not prove remediation.'
            }
        }
        finally { $baseline.Dispose() }
    }
    else { foreach ($finding in $preview) { $findings.Add((Convert-BHExposureFinding $finding $Index)) } }

    # Prioritize finding endpoints and steps when choosing detailed embedded records.
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($finding in $preview) {
        foreach ($step in $finding.Steps) { [void]$ids.Add($step.Source); [void]$ids.Add($step.Target) }
    }
    foreach ($id in $AdditionalIds) { if ($id) { [void]$ids.Add($id) } }
    $properties = [Collections.Generic.List[object]]::new()
    foreach ($id in $ids) {
        if ($properties.Count -eq 500) { break }
        $record = $Index.NodeRecord($id)
        $node = Get-BHGraphNodeDetails $Index $id
        $limited = $record -and $record.Length -gt 16384
        $values = if ($record -and -not $limited) { $record | ConvertFrom-Json } else { $null }
        $properties.Add(@{ i = $id; n = $node.n; t = $node.t; Properties = $values; Limited = [bool]$limited
            DirectMemberCount = $(if ($node.t -eq 'groups') { $Index.DirectMemberCount($id) } else { $null }) })
    }
    return @{
        Findings = $findings.ToArray(); NoLongerObserved = $removed.ToArray(); Comparison = $comparison
        Objects = $properties.ToArray(); CollectionWindows = @(Get-BHCollectionWindows $DatasetPaths)
        Visited = $state.Visited; Limited = ($state.Status -ne 'Completed'); MaxVisited = $AnalysisVisitLimit; MaxFindings = 200; MaxDepth = $AnalysisDepth
        Mode = $AnalysisMode; Status = $state.Status; StopReason = $state.StopReason; TotalFindings = $state.Findings
        Processed = $state.Processed; Pending = $state.Pending; PreviewLimited = ($state.Findings -gt 200); ElapsedSeconds = $state.ElapsedSeconds
        ObjectsLimited = ($ids.Count -gt 500)
        Method = 'Disk-backed reverse traversal from recognized privileged groups and all collected domains. One shortest recorded chain per identity; alternative routes may exist. Pure membership chains and unknown relationship types are excluded. HTML embeds at most 200 candidates; discovery continues independently. Findings describe potential exposure, not confirmed exploitation.'
    }
}

function Get-BHAnalysisFingerprint {
    param([string[]] $DatasetPaths)
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $kinds = Get-BHDatasetKinds
    foreach ($kind in $kinds.Nodes) { foreach ($file in Get-BHDatasetFiles $DatasetPaths "nodes_$kind") { [void]$files.Add([IO.Path]::GetFullPath($file)) } }
    foreach ($kind in $kinds.Edges) { foreach ($file in Get-BHDatasetFiles $DatasetPaths "edges_$kind") { [void]$files.Add([IO.Path]::GetFullPath($file)) } }
    foreach ($file in Get-BHDatasetFiles $DatasetPaths 'principals_external') { [void]$files.Add([IO.Path]::GetFullPath($file)) }
    foreach ($path in $DatasetPaths) {
        foreach ($name in 'scan-metadata.json','merge-metadata.json') {
            $file = Join-Path $path $name
            if (Test-Path -LiteralPath $file) { [void]$files.Add([IO.Path]::GetFullPath($file)) }
        }
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($file in @($files | Sort-Object)) {
            $line = $file.ToUpperInvariant() + ':' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash + "`n"
            $bytes = [Text.Encoding]::UTF8.GetBytes($line)
            [void]$hash.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
        }
        [void]$hash.TransformFinalBlock([byte[]]@(), 0, 0)
        return [BitConverter]::ToString($hash.Hash).Replace('-', '').ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}
