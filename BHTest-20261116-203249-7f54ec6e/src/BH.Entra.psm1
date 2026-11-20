# BH.Entra.psm1 - Entra ID (Azure AD) collection entry point. Uses the native collector in BH.Entra.Native.cs,
# compiled together with the rest of the toolkit by Initialize-BHNative (BH.Core.psm1).

$script:EntraRoleFile = Join-Path $PSScriptRoot 'entra-roles.json'

function ConvertTo-BHPlainText([securestring]$Secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Get-BHEntraCertificate {
    param([string]$Thumbprint, [string]$PfxPath, [securestring]$PfxPassword)
    if ($PfxPath) {
        $password = if ($PfxPassword) { ConvertTo-BHPlainText $PfxPassword } else { $null }
        return New-Object Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $password)
    }
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
        $found = Get-ChildItem -LiteralPath $store -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(' ', '').ToUpperInvariant() } | Select-Object -First 1
        if ($found) { return $found }
    }
    throw "No certificate with thumbprint $Thumbprint was found in CurrentUser\My or LocalMachine\My."
}

function Format-BHDuration([double]$Seconds) {
    if ($Seconds -lt 0 -or [double]::IsInfinity($Seconds) -or [double]::IsNaN($Seconds)) { return 'unknown' }
    $span = [TimeSpan]::FromSeconds([Math]::Round($Seconds))
    if ($span.TotalHours -ge 1) { return '{0}h {1:00}m' -f [int]$span.TotalHours, $span.Minutes }
    return '{0}m {1:00}s' -f [int]$span.TotalMinutes, $span.Seconds
}

function Start-BHEntraCollection {
<#
.SYNOPSIS
Collects an Entra ID tenant into the same shard format as the AD collector.
.DESCRIPTION
Sign-in choices (pick one):
  -CertificateThumbprint, or -PfxPath [-PfxPassword]   app registration with a certificate (recommended)
  -ClientSecret                                         app registration with a secret
  -DeviceCode                                           a person signs in (for example a Global Reader)
  -AccessToken                                          a token you already have
Cloud defaults to USGovDoD (https://dod-graph.microsoft.us). Use -Cloud USGov for GCC High, Global for commercial.
App registration needs these Microsoft Graph APPLICATION permissions (read only) with admin consent:
  Directory.Read.All, RoleManagement.Read.Directory, Application.Read.All
  Optional: RoleEligibilitySchedule.Read.Directory (eligible role assignments, needs Entra ID P2).
A Global Reader signing in with -DeviceCode needs no app permissions set up by you.
Resume: pass -ResumeDirectory with the folder of an interrupted run.
#>
    [CmdletBinding()]
    param(
        [ValidateSet('USGovDoD', 'USGov', 'Global', 'China')][string]$Cloud = 'USGovDoD',
        [Parameter(Mandatory)][string]$TenantId,
        [string]$ClientId,
        [string]$CertificateThumbprint,
        [string]$PfxPath,
        [securestring]$PfxPassword,
        [securestring]$ClientSecret,
        [switch]$DeviceCode,
        [securestring]$AccessToken,
        [string]$OutputRoot = (Join-Path (Get-Location) 'entra-scans'),
        [string]$ResumeDirectory,
        [ValidateRange(1, 64)][int]$Threads = 16,
        [ValidateSet('All', 'RoleAssignable', 'None')][string]$Members = 'All',
        [switch]$SkipOwners,
        [switch]$SkipEligibleRoles,
        [switch]$SkipAppRoleAssignments,
        [string[]]$Sections,
        [switch]$NoCompress,
        [string]$GraphUrl,
        [string]$LoginUrl
    )

    Import-Module (Join-Path $PSScriptRoot 'BH.Core.psm1') -DisableNameChecking
    Initialize-BHNative

    $methods = @($CertificateThumbprint, $PfxPath, $ClientSecret, $AccessToken) | Where-Object { $_ }
    if (($methods.Count + [int][bool]$DeviceCode) -ne 1) { throw 'Choose exactly one sign-in method: -CertificateThumbprint, -PfxPath, -ClientSecret, -DeviceCode or -AccessToken.' }
    if (-not $DeviceCode -and -not $AccessToken -and -not $ClientId) { throw '-ClientId (the app registration id) is required with a certificate or secret.' }

    $cloudProfile = if ($GraphUrl) { [BH.Entra.CloudProfile]::Custom($GraphUrl, $(if ($LoginUrl) { $LoginUrl } else { $GraphUrl })) } else { [BH.Entra.CloudProfile]::Get($Cloud) }
    if ($AccessToken) { $tokens = New-Object BH.Entra.StaticTokenSource((ConvertTo-BHPlainText $AccessToken)) }
    elseif ($ClientSecret) { $tokens = New-Object BH.Entra.SecretTokenSource($cloudProfile, $TenantId, $ClientId, (ConvertTo-BHPlainText $ClientSecret)) }
    elseif ($DeviceCode) {
        $notify = [Action[string]] { param($m) Write-Host ''; Write-Host $m -ForegroundColor Yellow; Write-Host '' }
        $tokens = New-Object BH.Entra.DeviceCodeTokenSource($cloudProfile, $TenantId, $ClientId, $notify)
    }
    else { $tokens = New-Object BH.Entra.CertificateTokenSource($cloudProfile, $TenantId, $ClientId, (Get-BHEntraCertificate -Thumbprint $CertificateThumbprint -PfxPath $PfxPath -PfxPassword $PfxPassword)) }

    $options = New-Object BH.Entra.EntraOptions
    $options.OutputDirectory = if ($ResumeDirectory) { $ResumeDirectory } else { Join-Path $OutputRoot ('entra-{0}-{1}' -f $TenantId.Substring(0, [Math]::Min(8, $TenantId.Length)), (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    $options.TenantId = $TenantId
    $options.Threads = $Threads
    $options.Members = $Members
    $options.IncludeOwners = -not $SkipOwners
    $options.IncludeEligibleRoles = -not $SkipEligibleRoles
    $options.IncludeAppRoleAssignments = -not $SkipAppRoleAssignments
    $options.Compress = -not $NoCompress
    if ($Sections) { $options.Sections = New-Object 'System.Collections.Generic.HashSet[string]' (, [string[]]@($Sections | ForEach-Object { $_.ToLowerInvariant() })) }
    if (Test-Path -LiteralPath $script:EntraRoleFile) {
        foreach ($role in (Get-Content -LiteralPath $script:EntraRoleFile -Raw | ConvertFrom-Json)) { if ($role.privileged) { [void]$options.PrivilegedRoleTemplates.Add([string]$role.templateId) } }
    }

    $graph = New-Object BH.Entra.GraphClient($cloudProfile, $tokens, ($Threads * 2))
    $collector = New-Object BH.Entra.EntraCollector($graph, $options)
    Write-Host ('Entra collection: cloud {0}, tenant {1}' -f $cloudProfile.Name, $TenantId)
    Write-Host ('Output: {0}' -f $options.OutputDirectory)
    if ($ResumeDirectory) { Write-Host 'Resuming: finished sections are skipped, interrupted ones continue from their last saved page.' }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $task = $collector.Start()
    $lastLine = [DateTime]::MinValue; $lastRequests = 0L; $lastTime = 0.0
    try {
        while (-not $task.Wait(500)) {
            $p = $collector.Progress
            if (([DateTime]::UtcNow - $lastLine).TotalSeconds -lt 5) { continue }
            $lastLine = [DateTime]::UtcNow
            $now = $watch.Elapsed.TotalSeconds
            $rate = ($graph.Requests - $lastRequests) / [Math]::Max(0.1, $now - $lastTime); $lastRequests = $graph.Requests; $lastTime = $now
            if ($p.SectionTotal -gt 0) {
                $pct = [Math]::Min(100, 100.0 * $p.SectionDone / $p.SectionTotal)
                $detail = '{0:N0}/{1:N0} ({2:N0}%)' -f $p.SectionDone, $p.SectionTotal, $pct
            } else { $detail = '{0:N0} read so far' -f $p.SectionDone }
            Write-Host ('[{0}] {1}: {2} | objects {3:N0}, edges {4:N0} | {5:N1} req/s, throttled {6}, workers {7}' -f (Format-BHDuration $now), $p.Section, $detail, $p.Objects, $p.Edges, $rate, $graph.Throttles, $graph.Gate.Limit)
        }
    }
    finally {
        if (-not $task.IsCompleted) {
            Write-Host 'Stopping... finishing the current shard so the run can be resumed.' -ForegroundColor Yellow
            $collector.Cancel()
            try { [void]$task.Wait(60000) } catch { }
        }
    }
    if ($task.IsFaulted) {
        $failure = $task.Exception.GetBaseException()
        if ($failure -is [OperationCanceledException]) { Write-Host ('Cancelled. Resume with -ResumeDirectory "{0}"' -f $options.OutputDirectory); return }
        Write-Host ('Stopped: {0}' -f $failure.Message) -ForegroundColor Red
        Write-Host ('Run again with -ResumeDirectory "{0}" to continue.' -f $options.OutputDirectory)
        throw $failure
    }
    $p = $collector.Progress
    Write-Host ('Done in {0}: {1:N0} objects, {2:N0} edges, {3:N0} requests, {4} throttled.' -f (Format-BHDuration $watch.Elapsed.TotalSeconds), $p.Objects, $p.Edges, $graph.Requests, $graph.Throttles) -ForegroundColor Green
    foreach ($problem in $p.Problems) { Write-Host ('  note: {0}' -f $problem) -ForegroundColor DarkYellow }
    [pscustomobject]@{ Directory = $options.OutputDirectory; Objects = $p.Objects; Edges = $p.Edges; Requests = $graph.Requests; Throttled = $graph.Throttles; Problems = @($p.Problems) }
}

Export-ModuleMember -Function Start-BHEntraCollection
