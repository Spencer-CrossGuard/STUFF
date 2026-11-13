#Requires -Version 5.1
<#
.SYNOPSIS
Compares one object's live ACL with what the collector and report hold for it.

.DESCRIPTION
Reads the object's nTSecurityDescriptor from the domain controller (same
controls the collector uses), prints every raw ACE, prints the edges the
collector's parser derives from it, and, when -IndexPath is given, lists the
ACL edges stored in graph.sqlite for that object and flags any whose source
SID is NOT a trustee in the live DACL.

.EXAMPLE
.\Trace-ObjectAcl.ps1 -Server dc01.corp.local -Identity jsmith -IndexPath .\BHData\reports\latest\graph.sqlite

.EXAMPLE
.\Trace-ObjectAcl.ps1 -Identity 'S-1-5-21-1-2-3-1105' -DescriptorHex 0100048014...
Offline mode: decode a descriptor you already exported, no LDAP needed.
#>
[CmdletBinding(DefaultParameterSetName = 'Live')]
param(
    [Parameter(ParameterSetName = 'Live', Mandatory)] [string] $Server,
    [Parameter(Mandatory)] [string] $Identity,
    [Parameter(ParameterSetName = 'Live')] [System.Management.Automation.PSCredential] $Credential,
    [Parameter(ParameterSetName = 'Offline', Mandatory)] [string] $DescriptorHex,
    [string] $IndexPath,
    [ValidateSet('user', 'group', 'computers', 'ous', 'domain')] [string] $Kind = 'user'
)

$ErrorActionPreference = 'Stop'
if ($IndexPath) {
    # Check before any LDAP work. Resolve against PowerShell's current location: SQLite itself
    # resolves relative paths from the process working directory, which can differ.
    if (-not (Test-Path -LiteralPath $IndexPath -PathType Leaf)) {
        throw "Index file not found: '$IndexPath'. Pass the full path to the report's graph.sqlite, for example C:\path\to\BHData\reports\<report name>\graph.sqlite."
    }
    $IndexPath = (Resolve-Path -LiteralPath $IndexPath).ProviderPath
}
Import-Module (Join-Path $PSScriptRoot 'src/BH.Core.psm1') -Force -DisableNameChecking
Initialize-BHNative

$guidNames = @{
    '00299570-246d-11d0-a768-00aa006e0529' = 'User-Force-Change-Password'
    'bf9679c0-0de6-11d0-a285-00aa003049e2' = 'Member'
    '1131f6aa-9c07-11d1-f79f-00c04fc2dcd2' = 'DS-Replication-Get-Changes'
    '1131f6ad-9c07-11d1-f79f-00c04fc2dcd2' = 'DS-Replication-Get-Changes-All'
    '0e10c968-78fb-11d2-90d4-00c04f79dc55' = 'Enroll'
    'a05b8cc2-17bc-4802-a710-e7c15ab866a2' = 'AutoEnroll'
    '5b47d60f-6090-40b2-9f37-2a4de88f3063' = 'msDS-KeyCredentialLink'
    'f3a64788-5306-11d1-a9c5-0000f80367c1' = 'servicePrincipalName'
    'e362ed86-b728-0842-b27d-2dea7a9df218' = 'msDS-ManagedPassword'
    'f30e3bbe-9ff0-11d1-b603-0000f80367c1' = 'gPLink'
    'bf967aba-0de6-11d0-a285-00aa003049e2' = 'class: user'
    'bf967a86-0de6-11d0-a285-00aa003049e2' = 'class: computer'
    'bf967a9c-0de6-11d0-a285-00aa003049e2' = 'class: group'
}
$nameCache = @{}
function Get-AccountName([string] $Sid) {
    if (-not $nameCache.ContainsKey($Sid)) {
        $nameCache[$Sid] = try { ([Security.Principal.SecurityIdentifier]::new($Sid)).Translate([Security.Principal.NTAccount]).Value } catch { '' }
    }
    $nameCache[$Sid]
}
function Get-SidAt([byte[]] $Bytes, [int] $Offset) {
    $length = 8 + $Bytes[$Offset + 1] * 4
    [BH.Native.Sid]::ToSidStringSafe([byte[]]$Bytes[$Offset..($Offset + $length - 1)])
}
function Get-AceFlagText([int] $Flags) {
    $names = @()
    if ($Flags -band 0x01) { $names += 'ObjectInherit' }
    if ($Flags -band 0x02) { $names += 'ContainerInherit' }
    if ($Flags -band 0x04) { $names += 'NoPropagate' }
    if ($Flags -band 0x08) { $names += 'InheritOnly' }
    if ($Flags -band 0x10) { $names += 'Inherited' }
    $names -join '|'
}
function Get-GuidText($Guid) {
    if ($null -eq $Guid) { return '' }
    $key = $Guid.ToString()
    if ($guidNames.ContainsKey($key)) { "$($guidNames[$key]) ($key)" } else { $key }
}
function ConvertFrom-DescriptorAces([byte[]] $Sd) {
    $dacl = [BitConverter]::ToUInt32($Sd, 16)
    if ($dacl -eq 0) { return }
    $cursor = [int]$dacl
    $count = [BitConverter]::ToUInt16($Sd, $cursor + 4)
    $cursor += 8
    for ($i = 0; $i -lt $count; $i++) {
        $type = $Sd[$cursor]; $flags = $Sd[$cursor + 1]; $size = [BitConverter]::ToUInt16($Sd, $cursor + 2)
        $end = $cursor + $size
        $objectType = $null; $inheritedType = $null; $sidOffset = $null
        if ($type -eq 0 -or $type -eq 1) { $sidOffset = $cursor + 8 }
        elseif ($type -eq 5 -or $type -eq 6) {
            $objectFlags = [BitConverter]::ToUInt32($Sd, $cursor + 8)
            $p = $cursor + 12
            if ($objectFlags -band 1) { $objectType = [guid]::new([byte[]]$Sd[$p..($p + 15)]); $p += 16 }
            if ($objectFlags -band 2) { $inheritedType = [guid]::new([byte[]]$Sd[$p..($p + 15)]); $p += 16 }
            $sidOffset = $p
        }
        if ($null -ne $sidOffset) {
            $sid = Get-SidAt $Sd $sidOffset
            [pscustomobject]@{
                Index = $i; Kind = @{ 0 = 'Allow'; 1 = 'Deny'; 5 = 'AllowObject'; 6 = 'DenyObject' }[[int]$type]
                Flags = Get-AceFlagText $flags; Mask = ('0x{0:X8}' -f [BitConverter]::ToUInt32($Sd, $cursor + 4))
                ObjectType = Get-GuidText $objectType; InheritedObjectType = Get-GuidText $inheritedType
                Trustee = $sid; Name = (Get-AccountName $sid)
            }
        }
        else {
            [pscustomobject]@{ Index = $i; Kind = "Unsupported type $type"; Flags = ''; Mask = ''; ObjectType = ''; InheritedObjectType = ''; Trustee = ''; Name = '' }
        }
        $cursor = $end
    }
}

# --- obtain the descriptor -------------------------------------------------
$objectSid = $null
if ($PSCmdlet.ParameterSetName -eq 'Offline') {
    [byte[]]$descriptor = for ($i = 0; $i -lt $DescriptorHex.Length; $i += 2) { [Convert]::ToByte($DescriptorHex.Substring($i, 2), 16) }
    if ($Identity -match '^S-1-') { $objectSid = $Identity }
    $dn = $Identity
}
else {
    Import-BHLdapAssemblies
    $connection = New-BHLdapConnectionBounded -Server $Server -Credential $Credential
    try {
        $root = [System.DirectoryServices.Protocols.SearchRequest]::new('', '(objectClass=*)',
            [System.DirectoryServices.Protocols.SearchScope]::Base, [string[]]@('defaultNamingContext'))
        $base = $connection.SendRequest($root).Entries[0].Attributes['defaultNamingContext'][0]
        if ($Identity -match '^S-1-') {
            $sidBytes = (New-Object System.Security.Principal.SecurityIdentifier($Identity))
            $raw = New-Object byte[] $sidBytes.BinaryLength; $sidBytes.GetBinaryForm($raw, 0)
            $filter = '(objectSid=' + (-join ($raw | ForEach-Object { '\' + $_.ToString('x2') })) + ')'
            $scope = 'Subtree'
        }
        elseif ($Identity -match '^(CN|OU|DC)=') { $filter = '(objectClass=*)'; $base = $Identity; $scope = 'Base' }
        else { $filter = "(sAMAccountName=$($Identity -replace '([\\*()])', '\$1'))"; $scope = 'Subtree' }
        $request = [System.DirectoryServices.Protocols.SearchRequest]::new($base, $filter,
            [System.DirectoryServices.Protocols.SearchScope]$scope, [string[]]@('distinguishedName', 'objectSid', 'nTSecurityDescriptor'))
        [void]$request.Controls.Add([System.DirectoryServices.Protocols.SecurityDescriptorFlagControl]::new(
            [System.DirectoryServices.Protocols.SecurityMasks]::Owner -bor [System.DirectoryServices.Protocols.SecurityMasks]::Dacl))
        $response = $connection.SendRequest($request)
        if ($response.Entries.Count -ne 1) { throw "Expected exactly one object for '$Identity', found $($response.Entries.Count)." }
        $entry = $response.Entries[0]
        $dn = $entry.DistinguishedName
        $sidValues = $entry.Attributes['objectSid'].GetValues([byte[]])
        $objectSid = [BH.Native.Sid]::ToSidStringSafe([byte[]]$sidValues[0])
        $descriptor = [byte[]]($entry.Attributes['nTSecurityDescriptor'].GetValues([byte[]])[0])
    }
    finally { $connection.Dispose() }
}

Write-Host "Object: $dn"
if ($objectSid) { Write-Host "SID:    $objectSid" }
$ownerOffset = [BitConverter]::ToUInt32($descriptor, 4)
$owner = if ($ownerOffset -gt 0) { Get-SidAt $descriptor ([int]$ownerOffset) } else { $null }
Write-Host "Owner:  $owner $(if ($owner) { Get-AccountName $owner })"

$aces = @(ConvertFrom-DescriptorAces $descriptor)
Write-Host "`n=== Raw ACEs in the live DACL ($($aces.Count)) ==="
$aces | Format-Table Index, Kind, Trustee, Name, Mask, Flags, ObjectType, InheritedObjectType -AutoSize -Wrap | Out-String -Width 400 | Write-Host

$parsed = [BH.Native.SecurityDescriptor]::Parse($descriptor, $Kind)
Write-Host "=== Edges the collector derives from this descriptor ==="
$edgeRows = @($parsed.Aces | ForEach-Object { [pscustomobject]@{ Trustee = $_.TrusteeSid; Name = (Get-AccountName $_.TrusteeSid); Edge = $_.EdgeType; Inherited = $_.IsInherited } })
if ($parsed.OwnerSid) { $edgeRows += [pscustomobject]@{ Trustee = $parsed.OwnerSid; Name = (Get-AccountName $parsed.OwnerSid); Edge = 'Owns'; Inherited = $false } }
$edgeRows | Format-Table -AutoSize | Out-String -Width 300 | Write-Host

if ($IndexPath) {
    if (-not $objectSid) { throw 'The object SID is needed to query the index. Pass the SID as -Identity in offline mode.' }
    $liveTrustees = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($ace in $aces) { if ($ace.Trustee) { [void]$liveTrustees.Add($ace.Trustee) } }
    if ($owner) { [void]$liveTrustees.Add($owner) }
    $index = [BH.Native.GraphIndex]::new($IndexPath, $false)
    try {
        $stored = @($index.ObjectRelationships($objectSid, 1000) | Where-Object { $_[1] -eq $objectSid -and $_[3] -eq 'acl' })
    }
    finally { $index.Dispose() }
    Write-Host "=== ACL edges stored in the index with this object as target ($($stored.Count)) ==="
    $stored | ForEach-Object {
        [pscustomobject]@{ Source = $_[0]; Name = (Get-AccountName $_[0]); Edge = $_[2]; TrusteeInLiveDacl = $liveTrustees.Contains($_[0]) }
    } | Format-Table -AutoSize | Out-String -Width 300 | Write-Host
    $missing = @($stored | Where-Object { -not $liveTrustees.Contains($_[0]) })
    if ($missing.Count) {
        Write-Host "$($missing.Count) stored edge(s) name a trustee that is NOT in the live DACL. The index or report was built from different or older data than this domain controller currently returns." -ForegroundColor Yellow
    }
    else { Write-Host 'Every stored ACL edge for this object matches a trustee in the live DACL.' -ForegroundColor Green }
}
