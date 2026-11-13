#Requires -Version 5.1
[CmdletBinding()]
param([string] $ScratchRoot = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$entry = Join-Path $project 'BH.Gui.ps1'
$scratch = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) "bhtest-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch

function Assert-BHTest($condition, $message) {
    if (-not $condition) { throw $message }
}

function Write-BHFixture($directory, $file, $rows) {
    $rows | ForEach-Object { $_ | ConvertTo-Json -Compress } |
        Set-Content -LiteralPath (Join-Path $directory $file) -Encoding UTF8
}

function Get-BHSyntheticAcl([string] $Sddl, [string] $Kind) {
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
    $binary = [byte[]]::new($descriptor.BinaryLength)
    $descriptor.GetBinaryForm($binary, 0)
    return [BH.Native.SecurityDescriptor]::Parse($binary, $Kind)
}

function New-BHTestLdapEntry {
    param([string] $Dn, [hashtable] $Values)
    $collectionType = [System.DirectoryServices.Protocols.SearchResultAttributeCollection]
    $attributes = [Activator]::CreateInstance($collectionType, $true)
    $add = $collectionType.GetMethods([Reflection.BindingFlags]'Instance,NonPublic') |
        Where-Object { $_.Name -eq 'Add' -and $_.GetParameters().Length -eq 2 } | Select-Object -First 1
    foreach ($key in $Values.Keys) {
        $attribute = [System.DirectoryServices.Protocols.DirectoryAttribute]::new()
        $attribute.Name = $key
        $value = $Values[$key]
        if ($value -is [Security.Principal.SecurityIdentifier]) {
            $binary = [byte[]]::new($value.BinaryLength)
            $value.GetBinaryForm($binary, 0)
            [void]$attribute.Add($binary)
        }
        elseif ($value -is [byte[]]) { [void]$attribute.Add($value) }
        else { foreach ($item in $value) { [void]$attribute.Add([string]$item) } }
        [void]$add.Invoke($attributes, @($key, $attribute))
    }
    $constructor = [System.DirectoryServices.Protocols.SearchResultEntry].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic') |
        Where-Object { $_.GetParameters().Length -eq 2 } | Select-Object -First 1
    return $constructor.Invoke(@($Dn, $attributes))
}
function New-BHMockLdap([object[]] $Responses) {
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($response in $Responses) { $queue.Enqueue($response) }
    $ldap = [pscustomobject]@{
        Responses = $queue
        Cookies = [System.Collections.Generic.List[string]]::new()
        Disposed = $false
    }
    $ldap | Add-Member -MemberType ScriptMethod -Name SendRequest -Value {
        param($Request)
        $this.Cookies.Add([Convert]::ToBase64String($Request.Controls[0].Cookie))
        $response = $this.Responses.Dequeue()
        if ($response -is [Exception]) { throw $response }
        return $response
    }
    $ldap | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $this.Disposed = $true }
    return $ldap
}

try {
    $scan = (New-Item -ItemType Directory -Path (Join-Path $scratch 'scan')).FullName
    $later = (New-Item -ItemType Directory -Path (Join-Path $scratch 'later')).FullName
    $report = Join-Path $scratch 'report'
    $user = 'S-1-5-21-10-20-30-1100'
    $admin = 'S-1-5-21-10-20-30-512'
    $distribution = 'S-1-5-21-10-20-30-1200'
    $futureGroup = 'S-1-5-21-10-20-30-1300'
    $adminDn = 'CN=Privileged,DC=corp,DC=test'
    $futureDn = 'CN=Later,DC=corp,DC=test'
    $name = '</script><b>Test</b>'
    Write-BHFixture $scan 'nodes_users.a.ndjson' @(
        @{ Id = $user; Dn = 'CN=Test\, User,DC=corp,DC=test'; Name = $name; Disabled = $false }
    )
    Write-BHFixture $scan 'nodes_groups.a.ndjson' @(
        @{ Id = $admin; Dn = $adminDn; Name = 'Renamed admins'; SecurityEnabled = $true }
        @{ Id = $distribution; Dn = 'CN=Distribution,DC=corp,DC=test'; Name = 'Distribution'; SecurityEnabled = $false }
    )
    Write-BHFixture $scan 'edges_memberof.a.ndjson' @(
        @{ Source = $user; Target = $adminDn.ToLowerInvariant(); EdgeType = 'MemberOf'; TargetIsDn = $true }
        @{ Source = $user; Target = $futureDn; EdgeType = 'MemberOf'; TargetIsDn = $true }
        @{ Source = $user; Target = $distribution; EdgeType = 'MemberOf' }
    )
    Write-BHFixture $later 'nodes_groups.a.ndjson' @(
        @{ Id = $futureGroup; Dn = $futureDn; Name = 'Later'; SecurityEnabled = $true }
    )

    & $entry -Report -DatasetPaths @($scan, $scan) -ReportOutputDir $report -ExportCsv
    $graph = Join-Path $report 'graph.sqlite'
    $result = & $entry -PathQuery -IndexPath $graph -SourceId $user -TargetId $admin | ConvertFrom-Json
    Assert-BHTest ($result.Status -eq 'Found' -and $result.Edges.Count -eq 1) 'Raw DN membership path failed.'
    $result = & $entry -PathQuery -IndexPath $graph -SourceId $user -TargetId $distribution | ConvertFrom-Json
    Assert-BHTest ($result.Status -eq 'NoPathInDataset') 'A distribution group produced a privilege path.'
    $hit = & $entry -SearchName 'Test' -IndexPath $graph | Where-Object Id -eq $user
    Assert-BHTest ($hit.Name -eq $name) 'Index changed the directory display name.'

    $html = Get-Content -LiteralPath (Join-Path $report 'report.html') -Raw
    Assert-BHTest (-not $html.Contains($name)) 'Directory markup was embedded in the report.'
    $data = [regex]::Match($html, '(?m)^const DATA = (.*);$').Groups[1].Value | ConvertFrom-Json
    Assert-BHTest ($data.NodeCounts.users -eq 1 -and $data.EdgeCounts.memberof -eq 3) 'Duplicate input was not deduplicated.'
    Assert-BHTest ($data.HighValueNodes[0].Name -eq 'Renamed admins') 'Privileged group detection used the display name.'
    Assert-BHTest ($data.UnresolvedMembership -eq 1) 'Unresolved membership was not reported.'
    Assert-BHTest (Test-Path -LiteralPath (Join-Path $report 'assets/nodes_users.csv')) 'Requested CSV export is missing.'

    $mergeRoot = Join-Path $scratch 'merged'
    $merged = & $entry -Merge -ScanPaths @($scan, $scan) -MergeOutputRoot $mergeRoot | ConvertFrom-Json
    Assert-BHTest ($merged.EdgesUnresolved.memberof -eq 1) 'Merge discarded the unresolved edge.'
    & $entry -Report -DatasetPaths @((Join-Path $mergeRoot $merged.MergeId), $later) -ReportOutputDir $report
    $result = & $entry -PathQuery -IndexPath $graph -SourceId $user -TargetId $futureGroup | ConvertFrom-Json
    Assert-BHTest ($result.Status -eq 'Found') 'A later dataset did not resolve the retained DN.'

    '{"FinishedUtc":"2026-01-01T00:00:00Z","Status":"Incomplete","Errors":1}' |
        Set-Content -LiteralPath (Join-Path $scan 'scan-metadata.json')
    $rejected = $false
    try { & $entry -Report -DatasetPaths $scan -ReportOutputDir $report }
    catch { $rejected = $_.Exception.Message -like '*Dataset is incomplete*' }
    Assert-BHTest $rejected 'An incomplete scan was accepted for reporting.'

    Assert-BHTest ([BH.Native.DirectoryIdentity]::ParentDn('CN=Test\, User,DC=corp,DC=test') -eq 'DC=corp,DC=test') 'Escaped DN split failed.'
    Assert-BHTest ([BH.Native.DirectoryIdentity]::PrimaryGroupSid($user, '512') -eq $admin) 'Primary group SID failed.'
    $acl = Get-BHSyntheticAcl "O:SYD:(D;;WD;;;$user)(A;ID;WD;;;$user)(A;ID;WO;;;$admin)(A;IO;GA;;;$admin)" 'user'
    Assert-BHTest ($acl.Aces.Count -eq 1 -and $acl.Aces[0].EdgeType -eq 'WriteOwner' -and $acl.Aces[0].IsInherited) 'Deny, inheritance, or inherit-only handling failed.'
    $acl = Get-BHSyntheticAcl "D:(OA;;WP;bf9679c0-0de6-11d0-a285-00aa003049e2;;$user)" 'group'
    Assert-BHTest ($acl.Aces.Count -eq 1 -and $acl.Aces[0].EdgeType -eq 'AddMember') 'Object-specific AddMember right failed.'
    $acl = Get-BHSyntheticAcl "D:(OA;;CR;1131f6aa-9c07-11d1-f79f-00c04fc2dcd2;;$user)(OA;;CR;1131f6ad-9c07-11d1-f79f-00c04fc2dcd2;;$user)" 'domain'
    Assert-BHTest ($acl.Aces.Count -eq 1 -and $acl.Aces[0].EdgeType -eq 'DCSync') 'Paired DCSync rights failed.'

    Import-Module (Join-Path $project 'src/BH.Core.psm1') -DisableNameChecking
    Import-BHLdapAssemblies
    $ctor = [System.DirectoryServices.Protocols.PageResultResponseControl].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0]
    $more = $ctor.Invoke(@(2, [byte[]]@(42), $false, [byte[]]@()))
    $last = $ctor.Invoke(@(2, [byte[]]@(), $false, [byte[]]@()))
    $pageOne = [pscustomobject]@{ Entries = @('first'); Controls = @($more) }
    $pageTwo = [pscustomobject]@{ Entries = @('last'); Controls = @($last) }
    $mock = New-BHMockLdap @($pageOne, $pageTwo)
    $factory = { $mock }.GetNewClosure()
    $rows = @(Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn 'DC=corp,DC=test' -Attributes @('name') |
        ForEach-Object {
            if ($_ -eq 'first') { Assert-BHTest ($mock.Cookies.Count -eq 1) 'The first LDAP page was buffered.' }
            $_
        })
    Assert-BHTest (($rows -join ',') -eq 'first,last' -and $mock.Cookies[1] -eq 'Kg==' -and $mock.Disposed) 'Paging did not reuse the cookie on the same session.'

    $mock = New-BHMockLdap @($pageOne, [System.DirectoryServices.Protocols.LdapException]::new(81))
    $factory = { $mock }.GetNewClosure()
    $failed = $false
    try { Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn 'DC=corp,DC=test' -Attributes @('name') | Out-Null }
    catch {
        # ScriptMethod adds an extra PowerShell exception wrapper around the mock failure.
        for ($errorCause = $_.Exception; $errorCause; $errorCause = $errorCause.InnerException) {
            if ($errorCause -is [System.DirectoryServices.Protocols.LdapException]) { $failed = $true; break }
        }
    }
    Assert-BHTest ($failed -and $mock.Cookies.Count -eq 2 -and $mock.Disposed) 'A broken paging session did not fail the shard.'

    $sharp = (New-Item -ItemType Directory -Path (Join-Path $scratch 'sharp')).FullName
    $computer = 'S-1-5-21-10-20-30-2100'
    $sessionUser = 'S-1-5-21-10-20-30-1101'
    $sharpInputs = @{
        'users' = @(
            @{ ObjectIdentifier = $user; Properties = @{ name = 'Imported user'; enabled = $true } }
            @{ ObjectIdentifier = $sessionUser; Properties = @{ name = 'Privileged session'; enabled = $true } }
        )
        'groups' = @(
            @{ ObjectIdentifier = $admin; Properties = @{ name = 'Imported admins' }; Members = @(@{ ObjectIdentifier = $sessionUser }) }
        )
        'computers' = @(
            @{
                ObjectIdentifier = $computer
                Properties = @{ name = 'Imported workstation' }
                LocalGroups = @(@{ Collected = $true; ObjectIdentifier = "$computer-544"; Results = @(@{ ObjectIdentifier = $user }) })
                Sessions = @{ Collected = $true; Results = @(@{ ComputerSID = $computer; UserSID = $sessionUser }) }
                RegistrySessions = @{ Collected = $false; Results = @(@{ ComputerSID = $computer; UserSID = $admin }) }
            }
        )
    }
    foreach ($kind in $sharpInputs.Keys) {
        $envelope = [ordered]@{ data = $sharpInputs[$kind]; meta = @{ type = $kind; count = $sharpInputs[$kind].Count; version = 6 } }
        $envelope | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $sharp "$([guid]::NewGuid()).json") -Encoding UTF8
    }
    $zip = Join-Path $scratch 'sharp.zip'
    Compress-Archive -Path (Join-Path $sharp '*.json') -DestinationPath $zip
    $importRoot = Join-Path $scratch 'imports'
    $imported = & $entry -ImportSharpHound -SharpHoundPaths $zip -ImportOutputRoot $importRoot | ConvertFrom-Json
    Assert-BHTest ($imported.Counts.Objects -eq 4) 'SharpHound ZIP metadata or file routing was incorrect.'
    & $entry -Report -DatasetPaths (Join-Path $importRoot $imported.ScanId) -ReportOutputDir $report
    $result = & $entry -PathQuery -IndexPath $graph -SourceId $user -TargetId $admin | ConvertFrom-Json
    Assert-BHTest (($result.Edges.EdgeType -join ',') -eq 'AdminTo,HasSession,MemberOf') 'Imported local-admin and session chain was incorrect.'
    $result = & $entry -PathQuery -IndexPath $graph -SourceId $user -TargetId $admin -EdgeTypes @('AdminTo','MemberOf') | ConvertFrom-Json
    Assert-BHTest ($result.Status -eq 'NoPathInDataset') 'Edge filter did not exclude HasSession.'

    $invalid = Join-Path $scratch 'invalid.json'
    '{"data":[],"meta":{"type":"users","count":1,"version":6}}' | Set-Content -LiteralPath $invalid
    $rejected = $false
    try { & $entry -ImportSharpHound -SharpHoundPaths $invalid -ImportOutputRoot $importRoot }
    catch { $rejected = $_.Exception.Message -like '*count does not match*' }
    Assert-BHTest ($rejected -and @(Get-ChildItem -LiteralPath $importRoot -Directory -Force).Count -eq 1) 'A truncated SharpHound dataset was published.'

    $nativeFolder = (New-Item -ItemType Directory -Path (Join-Path $scratch 'native')).FullName
    $delegationDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new("D:(A;;CR;;;$user)(D;;CR;;;$admin)(A;;CR;;;$admin)")
    $delegationBytes = [byte[]]::new($delegationDescriptor.BinaryLength)
    $delegationDescriptor.GetBinaryForm($delegationBytes, 0)
    $trustees = [BH.Native.SecurityDescriptor]::AllowedToActPrincipals($delegationBytes)
    Assert-BHTest ($trustees.Length -eq 1 -and $trustees[0] -eq $user) 'Native delegation descriptor ignored a deny.'

    $attributes = @{}
    $values = @{
        objectClass = @('top', 'person', 'organizationalPerson', 'user', 'computer')
        objectSid = [Security.Principal.SecurityIdentifier]::new($computer)
        objectGUID = [guid]::NewGuid().ToByteArray()
        name = 'Native workstation'
        sAMAccountName = 'WORKSTATION$'
        dNSHostName = 'workstation.corp.test'
        primaryGroupID = '515'
        'msDS-AllowedToActOnBehalfOfOtherIdentity' = $delegationBytes
        sIDHistory = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-99-88-77-2100')
        servicePrincipalName = @(1..100 | ForEach-Object { "service$_/workstation.corp.test" })
        'msDS-AllowedToDelegateTo' = 'cifs/target.corp.test'
    }
    $nativeEntry = New-BHTestLdapEntry 'CN=Workstation,DC=corp,DC=test' $values
    $core = Get-Module BH.Core
    $originalSearch = & $core { ${function:Invoke-BHPagedSearch} }
    try {
        & $core {
            param($Entry)
            $script:FixtureEntry = $Entry
            function script:Invoke-BHPagedSearch { $script:FixtureEntry }
        } $nativeEntry
        $counters = [BH.Native.ProgressCounters]::new()
        Invoke-BHWorker -ModuleRoot (Join-Path $project 'src') -Server 'unused.test' -BaseDn 'DC=corp,DC=test' `
            -Scope Subtree -OutDir $nativeFolder -ShardId 'native' -PageSize 1000 -Counters $counters
        Assert-BHTest ($counters.Objects -eq 1 -and $counters.Errors -eq 0) 'Native worker failed to process the synthetic LDAP entry.'
    }
    finally { & $core { param($Original) Set-Item -Path Function:script:Invoke-BHPagedSearch -Value $Original } $originalSearch }
    $nativeEdges = @(Get-Content -LiteralPath (Join-Path $nativeFolder 'edges_access.native.ndjson') | ConvertFrom-Json)
    $rbcd = $nativeEdges | Where-Object EdgeType -eq 'AllowedToAct'
    $history = $nativeEdges | Where-Object EdgeType -eq 'HasSIDHistory'
    Assert-BHTest ($rbcd.Source -eq $user -and $rbcd.Target -eq $computer) 'Native AllowedToAct direction was incorrect.'
    Assert-BHTest ($history.Source -eq $computer -and $history.Target -eq 'S-1-5-21-99-88-77-2100') 'Native SID history collection failed.'

    $compressedFolder = (New-Item -ItemType Directory -Path (Join-Path $scratch 'compressed')).FullName
    $templateId = [guid]::NewGuid()
    $template = New-BHTestLdapEntry 'CN=NativeTemplate,CN=Templates,DC=corp,DC=test' @{
        objectClass = @('top','pKICertificateTemplate')
        objectGUID = $templateId.ToByteArray()
        name = 'NativeTemplate'
        displayName = 'Native certificate template'
        'msPKI-Certificate-Name-Flag' = '1'
        'msPKI-Enrollment-Flag' = '0'
        'msPKI-RA-Signature' = '0'
        pKIExtendedKeyUsage = @('1.3.6.1.5.5.7.3.2')
    }
    $caId = [guid]::NewGuid()
    $authority = New-BHTestLdapEntry 'CN=NativeCA,CN=Enrollment,DC=corp,DC=test' @{
        objectClass = @('top','pKIEnrollmentService')
        objectGUID = $caId.ToByteArray()
        name = 'NativeCA'
        certificateTemplates = @('NativeTemplate')
    }
    $targetComputer = 'S-1-5-21-10-20-30-2101'
    $delegationTarget = New-BHTestLdapEntry 'CN=Target,DC=corp,DC=test' @{
        objectClass = @('top', 'user', 'computer')
        objectSid = [Security.Principal.SecurityIdentifier]::new($targetComputer)
        name = 'Delegation target'
        servicePrincipalName = @('cifs/target.corp.test')
    }
    $trustId = [guid]::NewGuid()
    $trustedDomain = New-BHTestLdapEntry 'CN=remote.test,CN=System,DC=corp,DC=test' @{
        objectClass = @('top','trustedDomain')
        objectGUID = $trustId.ToByteArray()
        name = 'remote.test'
        securityIdentifier = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-40-50-60')
        trustDirection = '1'
        trustAttributes = '8'
        trustType = '2'
    }
    try {
        & $core {
            param($Rows)
            $script:FixtureRows = $Rows
            function script:Invoke-BHPagedSearch { $script:FixtureRows }
        } @($nativeEntry, $template, $authority, $delegationTarget, $trustedDomain)
        $compressedCounters = [BH.Native.ProgressCounters]::new()
        $nativeOptions = @{
            ModuleRoot = (Join-Path $project 'src'); Server = 'unused.test'; BaseDn = 'DC=corp,DC=test'
            Scope = 'Subtree'; OutDir = $compressedFolder; ShardId = 'compressed'; PageSize = 1000
            Counters = $compressedCounters; CompressOutput = $true
            DomainId = 'S-1-5-21-10-20-30'
        }
        Invoke-BHWorker @nativeOptions
        Assert-BHTest ($compressedCounters.Objects -eq 5 -and $compressedCounters.Errors -eq 0) 'Native compressed collection failed.'
    }
    finally { & $core { param($Original) Set-Item -Path Function:script:Invoke-BHPagedSearch -Value $Original } $originalSearch }
    Assert-BHTest (Test-Path -LiteralPath (Join-Path $compressedFolder 'nodes_certtemplates.compressed.ndjson.gz')) 'Native certificate template was not stored as compressed NDJSON.'
    & $entry -Report -DatasetPaths $compressedFolder -ReportOutputDir $report -ExportCsv
    $database = [BH.Native.GraphIndex]::new($graph, $false)
    try {
        $record = $database.NodeRecord($templateId.ToString('B').ToUpperInvariant()) | ConvertFrom-Json
        $review = @($database.ReviewRelationships())
        Assert-BHTest ($review.Count -le 120 -and @($review | Where-Object { $_[2] -eq 'PublishedTo' }).Count -eq 1) 'Report review evidence omitted inventory outside the privileged-group graph.'
        Assert-BHTest ($record.CertificateNameFlags -eq '1' -and $record.ExtendedKeyUsage[0] -eq '1.3.6.1.5.5.7.3.2') 'Certificate evidence was lost during indexing.'
        $computerEvidence = $database.NodeRecord($computer) | ConvertFrom-Json
        Assert-BHTest ($computerEvidence.Spns.Count -eq 100) 'Compressed object evidence did not round-trip.'
        $nativePath = $database.ShortestPath($computer, $targetComputer, 100)
        Assert-BHTest ($nativePath.Edges.Count -eq 1 -and $nativePath.Edges[0].EdgeType -eq 'AllowedToDelegate') 'Native constrained-delegation targets were not resolved.'
        $trustEvidence = $database.NodeRecord($trustId.ToString('B').ToUpperInvariant()) | ConvertFrom-Json
        Assert-BHTest ($trustEvidence.TrustDirection -eq '1' -and $trustEvidence.TargetDomainSid -eq 'S-1-5-21-40-50-60') 'Native trust properties were lost.'
    }
    finally { $database.Dispose() }
    $merged = & $entry -Merge -ScanPaths @($compressedFolder) -MergeOutputRoot $mergeRoot | ConvertFrom-Json
    Assert-BHTest ($merged.NodeCounts.certtemplates -eq 1 -and $merged.NodeCounts.trusts -eq 1 -and $merged.EdgeCounts.access -eq 3) 'Compressed native collection did not survive merge.'
    $published = [BH.Native.NdjsonReader]::Lines((Join-Path (Join-Path $mergeRoot $merged.MergeId) 'edges_observed.ndjson.gz')) | ConvertFrom-Json |
        Where-Object EdgeType -eq 'PublishedTo'
    Assert-BHTest ($published.Source -eq $templateId.ToString('B').ToUpperInvariant() -and -not $published.SourceIsDn) 'Published certificate template relationship was not resolved during merge.'
    $rangedFolder = (New-Item -ItemType Directory -Path (Join-Path $scratch 'ranged')).FullName
    $rangedEntry = New-BHTestLdapEntry 'CN=Ranged,DC=corp,DC=test' @{
        objectClass = @('top', 'user'); objectSid = [Security.Principal.SecurityIdentifier]::new($user)
        'memberOf;range=0-0' = @($adminDn)
    }
    $rangeReader = [Func[System.DirectoryServices.Protocols.SearchResultEntry,string,string[]]]{
        param($Entry, $Attribute)
        if ($Attribute -ne 'memberOf') { throw 'Unexpected ranged attribute.' }
        return [string[]]@('CN=Privileged,DC=corp,DC=test', 'CN=Later,DC=corp,DC=test')
    }
    $rangedWriter = [BH.Native.LdapEntryWriter]::new($rangedFolder, 'test', $true, 'S-1-5-21-10-20-30', $rangeReader)
    try { $rangedWriter.Write($rangedEntry) } finally { $rangedWriter.Dispose() }
    $rangedEdges = @([BH.Native.NdjsonReader]::Lines((Join-Path $rangedFolder 'edges_memberof.test.ndjson.gz')) | ConvertFrom-Json)
    Assert-BHTest ($rangedEdges.Count -eq 2 -and $rangedEdges[1].Target -eq 'CN=Later,DC=corp,DC=test') 'Native ranged attributes were truncated.'
    'BHTest smoke checks passed.'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ([string]::Equals($resolved, $scratch, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
