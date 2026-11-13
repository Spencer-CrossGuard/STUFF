#Requires -Version 5.1
[CmdletBinding()]
param([string] $ScratchRoot = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $project 'src/BH.Core.psm1') -DisableNameChecking
Initialize-BHNative
$scratch = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) "bh-scheduler-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
Add-Type -TypeDefinition @'
using System.Threading;
public static class BHSchedulerProbe {
    public static int Active;
    public static int Peak;
    public static void Enter() {
        int active = Interlocked.Increment(ref Active);
        int peak;
        do { peak = Peak; if (peak >= active) break; } while (Interlocked.CompareExchange(ref Peak, active, peak) != peak);
    }
    public static void Exit() { Interlocked.Decrement(ref Active); }
}
'@
try {
    $source = [IO.File]::ReadAllText((Join-Path $project 'src/BH.Core.psm1'))
    $start = $source.IndexOf('function Invoke-BHWorker {')
    $end = $source.IndexOf('function Resolve-BHExternalPrincipals', $start)
    $fakeWorker = @'
function Invoke-BHWorker {
    param($ModuleRoot,$Server,$UserName,$Password,$BaseDn,$Scope,$OutDir,$ShardId,$PageSize,$ExcludeDns,
        $Counters,$CompressOutput,$DomainId,$Filter)
    [BHSchedulerProbe]::Enter()
    try {
        Start-Sleep -Milliseconds 10
        $Counters.AddObjects(1)
        $Counters.AddEdges(2)
        '{"MissingDescriptors":0}' | Set-Content -LiteralPath (Join-Path $OutDir "worker-$ShardId.json")
    }
    finally { [BHSchedulerProbe]::Exit() }
}

'@
    $source = $source.Remove($start,$end-$start).Insert($start,$fakeWorker)
    $export = $source.IndexOf('Export-ModuleMember -Function')
    $mocks = @'
function New-BHLdapConnectionBounded {
    $connection = [pscustomobject]@{}
    $connection | Add-Member -MemberType ScriptMethod -Name Dispose -Value {}
    $connection | Add-Member -MemberType ScriptMethod -Name SendRequest -Value {
        $sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-10-20-30')
        $bytes = [byte[]]::new($sid.BinaryLength)
        $sid.GetBinaryForm($bytes, 0)
        $attribute = [System.DirectoryServices.Protocols.DirectoryAttribute]::new()
        $attribute.Name = 'objectSid'
        [void]$attribute.Add($bytes)
        [pscustomobject]@{ Entries = @([pscustomobject]@{ Attributes = @{ objectSid = $attribute } }) }
    }
    return $connection
}
function Get-BHDefaultNamingContext {
    [pscustomobject]@{ DefaultNamingContext = 'DC=corp,DC=test'; ConfigurationNamingContext = $null }
}
function Invoke-BHPagedSearch {
    for ($i = 0; $i -lt 300; $i++) { [pscustomobject]@{ DistinguishedName = "OU=Child$i,DC=corp,DC=test" } }
}

'@
    $source = $source.Insert($export,$mocks)
    $modulePath = Join-Path $scratch 'BH.Core.psm1'
    [IO.File]::WriteAllText($modulePath,$source)
    $module = Import-Module $modulePath -PassThru -DisableNameChecking -Force
    $scan = & $module { param($Root) Start-BHCollection -BaseDn 'DC=corp,DC=test' -Server 'unused.test' -OutputRoot $Root -MaxThreads 4 -SkipConfiguration } (Join-Path $scratch 'scans')
    $metadata = Get-Content -LiteralPath (Join-Path $scan 'scan-metadata.json') -Raw | ConvertFrom-Json
    if ([BHSchedulerProbe]::Peak -gt 4 -or [BHSchedulerProbe]::Peak -lt 2 -or
        [BHSchedulerProbe]::Active -ne 0 -or $metadata.WorkItemCount -ne 302 -or
        $metadata.Counts.Objects -ne 302 -or $metadata.Counts.Edges -ne 604 -or $metadata.Status -ne 'Complete') {
        throw "Scheduler exceeded its bound or lost shards. Peak $([BHSchedulerProbe]::Peak). $($metadata | ConvertTo-Json -Depth 4 -Compress)"
    }
    'Bounded LDAP scheduling passed across 302 synthetic shards with a four-worker limit.'
}
finally {
    if ($module) { Remove-Module $module }
    $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ([string]::Equals($resolved, $scratch, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
