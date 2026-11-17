Set-StrictMode -Version Latest
$script:WorkerPath = Join-Path $PSScriptRoot 'BH.EndpointWorker.ps1'

function Start-BHEndpointCollection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ScanDir,
        [ValidateRange(1, 32)][int] $MaxWorkers = 4,
        [ValidateRange(2, 300)][int] $TimeoutSeconds = 20,
        [bool] $CompressOutput = $true,
        [scriptblock] $ProgressCallback
    )

    Initialize-BHNative
    $files = [string[]]@(Get-BHDatasetFiles @($ScanDir) 'nodes_computers')
    $targets = [BH.Native.EndpointTargetCursor]::new($files)
    $suffix = if ($CompressOutput) { '.gz' } else { '' }
    $edges = [BH.Native.NdjsonWriter]::new((Join-Path $ScanDir "edges_access.endpoints.ndjson$suffix"), $false)
    $statuses = [BH.Native.NdjsonWriter]::new((Join-Path $ScanDir "endpoint-status.ndjson$suffix"), $false)
    $totals = [ordered]@{ Attempted = 0L; Complete = 0L; Partial = 0L; Failed = 0L; TimedOut = 0L; Skipped = 0L; Edges = 0L }
    $slots = [System.Collections.Generic.List[object]]::new()
    $inputDone = $false
    $startupFailures = 0
    $sequence = 0
    $lastProgress = 0
    $phaseError = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $executable = Join-Path $PSHOME $(if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' })
    try {
        while (-not $inputDone -or $slots.Count -gt 0) {
            while (-not $inputDone -and $slots.Count -lt $MaxWorkers) {
                $target = $null
                while ($targets.MoveNext()) {
                    $candidate = $targets.Current
                    if ($candidate.Id -and [BH.Native.EndpointNative]::ValidHost($candidate.Host)) { $target = $candidate; break }
                    $totals.Skipped++
                    $statuses.WriteLine((@{ Id = $candidate.Id; Host = $candidate.Host; Status = 'Skipped'; Reason = 'Missing or invalid DNS hostname' } | ConvertTo-Json -Compress))
                }
                if (-not $target) { $inputDone = $true; break }
                $temporary = Join-Path $ScanDir "endpoint-worker-$([guid]::NewGuid().ToString('N')).tmp"
                $info = [Diagnostics.ProcessStartInfo]::new()
                $info.FileName = $executable
                $info.Arguments = '-NoProfile -NonInteractive -File "' + $script:WorkerPath + '" -OutputPath "' + $temporary + '"'
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.WindowStyle = 'Hidden'
                $info.RedirectStandardInput = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($info)
                $totals.Attempted++
                $slots.Add([pscustomobject]@{
                    Process = $process; Pending = $process.StandardOutput.ReadLineAsync()
                    ErrorOutput = $process.StandardError.ReadToEndAsync()
                    Target = $target; State = 'Starting'; Since = $clock.Elapsed.TotalSeconds; Temporary = $temporary
                })
            }

            foreach ($slot in $slots.ToArray()) {
                $elapsed = $clock.Elapsed.TotalSeconds - $slot.Since
                $limit = if ($slot.State -eq 'Starting') { 30 } else { $TimeoutSeconds }
                $remove = $false
                if ($slot.Pending.IsCompleted) {
                    $line = $slot.Pending.GetAwaiter().GetResult()
                    if ($null -eq $line) {
                        if ($slot.State -eq 'Starting') {
                            $startupFailures++
                            if ($startupFailures -ge 3) { throw 'Endpoint workers repeatedly failed to start. Check the installed native source files and PowerShell runtime.' }
                        }
                        $totals.Failed++
                        $reason = if ($slot.ErrorOutput.IsCompleted) { [string]$slot.ErrorOutput.GetAwaiter().GetResult() } else { 'Worker process exited' }
                        if ($reason.Length -gt 4000) { $reason = $reason.Substring(0, 4000) }
                        $statuses.WriteLine((@{ Id = $slot.Target.Id; Host = $slot.Target.Host; Status = 'Failed'; Reason = $reason } | ConvertTo-Json -Compress))
                        $remove = $true
                    }
                    else {
                        if ($line.Length -gt 65536) { throw 'An endpoint worker returned an oversized status message.' }
                        $reply = $line | ConvertFrom-Json
                        if ($slot.State -eq 'Starting') {
                            if (-not $reply.PSObject.Properties['Ready'] -or -not $reply.Ready) { throw 'Invalid endpoint worker startup response.' }
                            $request = @{ Host = $slot.Target.Host; Id = $slot.Target.Id } | ConvertTo-Json -Compress
                            $slot.Process.StandardInput.WriteLine($request)
                            $slot.Process.StandardInput.Flush()
                            $slot.State = 'Host'
                            $slot.Since = $clock.Elapsed.TotalSeconds
                            $slot.Pending = $slot.Process.StandardOutput.ReadLineAsync()
                        }
                        else {
                            if ($reply.PSObject.Properties['Error']) {
                                $totals.Failed++
                                $status = @{ Id = $slot.Target.Id; Host = $slot.Target.Host; Status = 'Failed'; Reason = $reply.Error }
                            }
                            else {
                                if (Test-Path -LiteralPath $slot.Temporary) { [BH.Native.EndpointNative]::Append($slot.Temporary, $edges) }
                                $totals.Edges += [long]$reply.Edges
                                $outcome = if ($reply.FailedOperations -eq 0 -and $reply.UnresolvedSessions -eq 0) { 'Complete' }
                                    elseif ($reply.SuccessfulOperations -gt 0) { 'Partial' } else { 'Failed' }
                                $totals[$outcome]++
                                $status = @{ Id = $slot.Target.Id; Host = $slot.Target.Host; Status = $outcome
                                    Edges = $reply.Edges; Errors = $reply.Errors; UnresolvedSessions = $reply.UnresolvedSessions }
                            }
                            $statuses.WriteLine(($status | ConvertTo-Json -Depth 4 -Compress))
                            if (Test-Path -LiteralPath $slot.Temporary) { [IO.File]::Delete($slot.Temporary) }
                            # Reuse this warm worker for the next valid target.
                            $next = $null
                            while ($targets.MoveNext()) {
                                $candidate = $targets.Current
                                if ($candidate.Id -and [BH.Native.EndpointNative]::ValidHost($candidate.Host)) { $next = $candidate; break }
                                $totals.Skipped++
                                $statuses.WriteLine((@{ Id = $candidate.Id; Host = $candidate.Host; Status = 'Skipped'; Reason = 'Missing or invalid DNS hostname' } | ConvertTo-Json -Compress))
                            }
                            if ($next) {
                                $slot.Target = $next
                                $slot.Process.StandardInput.WriteLine((@{ Host = $next.Host; Id = $next.Id } | ConvertTo-Json -Compress))
                                $slot.Process.StandardInput.Flush()
                                $slot.Pending = $slot.Process.StandardOutput.ReadLineAsync()
                                $slot.Since = $clock.Elapsed.TotalSeconds
                                $totals.Attempted++
                            }
                            else { $inputDone = $true; $remove = $true }
                        }
                    }
                }
                elseif ($elapsed -ge $limit) {
                    if ($slot.State -eq 'Starting') {
                        $startupFailures++
                        if ($startupFailures -ge 3) { throw 'Endpoint worker initialization repeatedly timed out.' }
                    }
                    $totals.TimedOut++
                    $statuses.WriteLine((@{ Id = $slot.Target.Id; Host = $slot.Target.Host; Status = 'TimedOut'; LimitSeconds = $limit } | ConvertTo-Json -Compress))
                    $remove = $true
                }
                if ($remove) {
                    if (-not $slot.Process.HasExited) { $slot.Process.Kill(); [void]$slot.Process.WaitForExit(3000) }
                    $slot.Process.Dispose()
                    if (Test-Path -LiteralPath $slot.Temporary) { [IO.File]::Delete($slot.Temporary) }
                    [void]$slots.Remove($slot)
                }
            }
            if ($ProgressCallback -and $clock.Elapsed.TotalSeconds - $lastProgress -ge 5) {
                $lastProgress = $clock.Elapsed.TotalSeconds
                & $ProgressCallback $totals
            }
            $sequence++
            if ($sequence % 50 -eq 0) { $edges.Flush(); $statuses.Flush() }
            if ($slots.Count) { Start-Sleep -Milliseconds 100 }
        }
    }
    catch { $phaseError = $_ }
    finally {
        foreach ($slot in $slots) {
            if (-not $slot.Process.HasExited) { $slot.Process.Kill(); [void]$slot.Process.WaitForExit(3000) }
            $slot.Process.Dispose()
            if (Test-Path -LiteralPath $slot.Temporary) { [IO.File]::Delete($slot.Temporary) }
        }
        $targets.Dispose()
        $edges.Dispose()
        $statuses.Dispose()
    }
    if ($phaseError) { $totals['Error'] = $phaseError.Exception.Message }
    $totals | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ScanDir 'endpoint-metadata.json') -Encoding UTF8
    if ($phaseError) { throw $phaseError }
    return $totals
}

Export-ModuleMember -Function Start-BHEndpointCollection
