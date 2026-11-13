# BH.Core.psm1 - LDAP collection, scan metadata, and dataset merge.
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Native type loader
# ---------------------------------------------------------------------------

function Initialize-BHNative {
    [CmdletBinding()]
    param()

    # Every native type the rest of the code relies on. Checked both before
    # and after compiling, so a missing one produces a plain-English cause
    # instead of "Unable to find type [BH.Native.OuIndex]" deep in the GUI.
    $requiredTypes = @(
        'BH.Native.Sid', 'BH.Native.SecurityDescriptor', 'BH.Native.JsonRecordWriter', 'BH.Native.NdjsonWriter',
        'BH.Native.JsonRecordReader', 'BH.Native.ProgressCounters', 'BH.Native.OuIndex',
        'BH.Native.CsvExporter', 'BH.Native.UnresolvedSids',
        'BH.Native.DirectoryIdentity', 'BH.Native.GraphIndex', 'BH.Native.GraphRelationships', 'BH.Native.SharpHoundImport',
        'BH.Native.LdapEntryWriter', 'BH.Native.NdjsonReader', 'BH.Native.EndpointNative', 'BH.Native.ExposureAnalysis', 'BH.Native.DiskAnalysisProgress'
    )
    $missing = @($requiredTypes | Where-Object { -not ([System.Management.Automation.PSTypeName]$_).Type })

    if ($missing.Count -eq 0) { return }

    if ($missing.Count -lt $requiredTypes.Count) {
        # Some BH.Native types are already loaded in this process but not all
        # of them: they came from an OLDER build of the .cs files. .NET cannot
        # replace a type once it is loaded, so the only fix is a fresh process.
        throw ("This PowerShell session already loaded an older build of the BH native code (missing: {0}). " +
               "Close this PowerShell window, open a new one, and run the script again. " +
               "If it still happens in a fresh window, src\BH.Native.cs / src\BH.Report.Native.cs were not updated to the latest version.") -f ($missing -join ', ')
    }

    # Compile together so the index and CSV writer can reuse NDJSON primitives.
    $csPaths = @(
        (Join-Path $PSScriptRoot 'BH.Native.cs'),
        (Join-Path $PSScriptRoot 'BH.Report.Native.cs')
        (Join-Path $PSScriptRoot 'BH.Index.Native.cs'),
        (Join-Path $PSScriptRoot 'BH.SharpHound.Native.cs'),
        (Join-Path $PSScriptRoot 'BH.Collection.Native.cs'),
        (Join-Path $PSScriptRoot 'BH.Endpoint.Native.cs'),
        (Join-Path $PSScriptRoot 'BH.Analysis.Native.cs'),
        (Join-Path $PSScriptRoot 'BH.Deep.Native.cs')
    )
    foreach ($p in $csPaths) {
        if (-not (Test-Path $p)) {
            throw "$(Split-Path $p -Leaf) not found next to BH.Core.psm1 (expected at $p)."
        }
    }

    Add-Type -AssemblyName System.DirectoryServices.Protocols
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        Add-Type -Path $csPaths -ReferencedAssemblies 'System.dll','System.Core.dll','System.Xml.dll','System.DirectoryServices.Protocols.dll' -ErrorAction Stop
    }
    else {
        $references = @([System.DirectoryServices.Protocols.LdapConnection].Assembly.Location) +
            @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
        Add-Type -Path $csPaths -ReferencedAssemblies $references -ErrorAction Stop
    }

    $stillMissing = @($requiredTypes | Where-Object { -not ([System.Management.Automation.PSTypeName]$_).Type })
    if ($stillMissing.Count -gt 0) {
        throw ("The native source files compiled but are missing: {0}. src\BH.Native.cs and src\BH.Report.Native.cs are not the latest versions -- re-copy both." -f ($stillMissing -join ', '))
    }
}

# ---------------------------------------------------------------------------
# Common helpers
# ---------------------------------------------------------------------------

function New-BHScanFolder {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RootPath,
        [Parameter(Mandatory)][string] $ScopeDescription
    )

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $name = "scan-$stamp-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $path = Join-Path $RootPath $name
    New-Item -ItemType Directory -Path $path -Force | Out-Null

    $meta = [ordered]@{
        ScanId      = $name
        Scope       = $ScopeDescription
        StartedUtc  = (Get-Date).ToUniversalTime().ToString('o')
        FinishedUtc = $null
        Status      = 'Running'
        Source      = 'BHTest LDAP'
        Counts      = [ordered]@{}
        Errors      = 0
    }
    $meta | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $path 'scan-metadata.json') -Encoding UTF8

    return $path
}

function Write-BHLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Message,
        [ValidateSet('Info', 'Warn', 'Error')][string] $Level = 'Info'
    )
    $line = "[{0:HH:mm:ss}] [{1}] {2}" -f (Get-Date), $Level, $Message
    switch ($Level) {
        'Warn'  { Write-Warning $line }
        'Error' { Write-Error $line -ErrorAction Continue }
        default { Write-Host $line }
    }
}

# ---------------------------------------------------------------------------
# Collector  -  LDAP-based, paged + streamed, parallelized across sub-OUs.
# ---------------------------------------------------------------------------

function Import-BHLdapAssemblies {
    [CmdletBinding()]
    param()
    Add-Type -AssemblyName System.DirectoryServices.Protocols -ErrorAction Stop
    Add-Type -AssemblyName System.DirectoryServices -ErrorAction Stop
}

function Invoke-BHWithRetry {
    <#
        .SYNOPSIS
        Runs $Action, retrying on transient LDAP errors (server busy,
        connection reset, timeout) with capped exponential backoff + jitter
        before giving up. A real DC can drop a connection mid-scan (reboot,
        LDAP thread-pool exhaustion)  -  without this, one blip kills an
        hours-long run instead of a few seconds' pause.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock] $Action,
        [int] $MaxRetries = 5,
        [string] $Description = 'LDAP operation'
    )
    $attempt = 0
    while ($true) {
        try {
            return & $Action
        }
        catch [System.DirectoryServices.Protocols.LdapException], [System.DirectoryServices.Protocols.DirectoryOperationException] {
            $attempt++
            if ($attempt -gt $MaxRetries) { throw }
            $delayMs = [int]([Math]::Min(30000, 500 * [Math]::Pow(2, $attempt)) + (Get-Random -Maximum 250))
            Write-BHLog -Level Warn -Message "$Description failed (attempt $attempt/$MaxRetries): $($_.Exception.Message). Retrying in ${delayMs}ms."
            Start-Sleep -Milliseconds $delayMs
        }
    }
}

function New-BHLdapConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Server,
        [System.Management.Automation.PSCredential] $Credential,
        [int] $PageSize = 1000,
        [int] $TimeoutSeconds = 120
    )

    $identifier = New-Object System.DirectoryServices.Protocols.LdapDirectoryIdentifier($Server, 389)
    $conn = New-Object System.DirectoryServices.Protocols.LdapConnection($identifier)
    $conn.SessionOptions.ProtocolVersion = 3
    $conn.SessionOptions.Signing = $true
    $conn.SessionOptions.Sealing = $true
    # Deliberately not chasing referrals: auto-following one would mean
    # silently talking to a DC we didn't name, in another domain/forest,
    # using these same credentials. Scan that subtree explicitly instead.
    $conn.SessionOptions.ReferralChasing = [System.DirectoryServices.Protocols.ReferralChasingOptions]::None
    $conn.Timeout = New-Object System.TimeSpan(0, 0, $TimeoutSeconds)

    if ($Credential) {
        $networkCred = New-Object System.Net.NetworkCredential(
            $Credential.UserName, $Credential.Password)
        $conn.Credential = $networkCred
        $conn.AuthType = [System.DirectoryServices.Protocols.AuthType]::Negotiate
    }
    else {
        $conn.AuthType = [System.DirectoryServices.Protocols.AuthType]::Negotiate
    }

    return $conn
}

function Get-BHDefaultNamingContext {
    <#
        .SYNOPSIS
        Reads the domain naming context and the optional configuration naming
        context used for certificate and site inventory. Missing required
        results produce a specific error before collection begins.
    #>
    param([Parameter(Mandatory)] $Connection)
    # New-Object flattens a params-string[] here into a single space-joined name.
    # Bind the typed constructor explicitly for RootDSE and all collection searches.
    $req = [System.DirectoryServices.Protocols.SearchRequest]::new(
        '', '(objectClass=*)', [System.DirectoryServices.Protocols.SearchScope]::Base, [string[]]@('defaultNamingContext', 'configurationNamingContext'))
    # MaxRetries 2, not the long-scan default of 5: both callers (OU
    # discovery, GPO-container lookup before a scan) are quick pre-flight
    # lookups against a server Test-BHLdapConnectivity already confirmed
    # is reachable moments earlier -- repeating the full 5-retry/30s-cap
    # ladder here just turns a real problem into several minutes of silent
    # retrying before it's reported.
    $resp = Invoke-BHWithRetry -Description 'Root DSE lookup' -MaxRetries 2 -Action { $Connection.SendRequest($req) }

    if (-not $resp -or -not $resp.Entries -or $resp.Entries.Count -eq 0) {
        throw 'Root DSE query returned no results. Is the server actually a domain controller?'
    }
    $entry = $resp.Entries[0]
    if (-not $entry.Attributes.Contains('defaultNamingContext') -or $entry.Attributes['defaultNamingContext'].Count -eq 0) {
        $returned = @($entry.Attributes.AttributeNames) -join ', '
        throw "Root DSE returned no defaultNamingContext value. Returned attributes: [$returned]. Verify the selected AD DS controller and the account's directory access."
    }
    $defaultContext = Get-BHAttrString $entry 'defaultNamingContext'
    if ([string]::IsNullOrWhiteSpace($defaultContext)) { throw 'Root DSE returned an empty defaultNamingContext value.' }

    [pscustomobject]@{
        DefaultNamingContext = $defaultContext
        ConfigurationNamingContext = if ($entry.Attributes.Contains('configurationNamingContext') -and $entry.Attributes['configurationNamingContext'].Count -gt 0) { Get-BHAttrString $entry 'configurationNamingContext' } else { $null }
    }
}

function Test-BHLdapConnectivity {
    <#
        .SYNOPSIS
        One quick bind + root DSE query -- "is this server/credential
        combination even reachable at all" -- with a wall-clock timeout
        that is actually enforced. Used to fail fast on a bad server name
        or bad credentials, instead of falling into the long-scan retry
        ladder (5 retries with backoff up to 30s each) and looking hung for
        minutes before finally erroring.

        LdapConnection.Timeout does NOT bound this by itself -- it only
        bounds how long SendRequest waits for a response once a connection
        already exists, not the initial TCP connect, which was confirmed
        to hang well past two minutes against an unreachable address even
        with a 2-minute Timeout configured. The attempt is run on its own
        Runspace via PowerShell.BeginInvoke() and the wait is bounded with
        AsyncWaitHandle.WaitOne(timeout): if it doesn't finish in time, this
        function simply stops waiting and reports a timeout rather than
        blocking the caller (the orphaned attempt is told to Stop() and is
        left to unwind on its own rather than risk blocking further on a
        stuck call).

        The long-scan path (Invoke-BHPagedSearch, Invoke-BHWithRetry) is
        still what handles a connection dying *mid*-scan -- this is only
        the initial "can we even talk to it" check, kept separate and fast
        on purpose.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Server,
        [System.Management.Automation.PSCredential] $Credential,
        [int] $TimeoutSeconds = 10
    )

    $userName = $null
    $password = $null
    if ($Credential) {
        $userName = $Credential.UserName
        $password = $Credential.GetNetworkCredential().Password
    }

    $probeScript = {
        param($server, $userName, $password, $timeoutSeconds)
        try {
            Add-Type -AssemblyName System.DirectoryServices.Protocols -ErrorAction Stop
            $identifier = New-Object System.DirectoryServices.Protocols.LdapDirectoryIdentifier($server, 389)
            $conn = New-Object System.DirectoryServices.Protocols.LdapConnection($identifier)
            $conn.SessionOptions.ProtocolVersion = 3
            $conn.SessionOptions.Signing = $true
            $conn.SessionOptions.Sealing = $true
            $conn.SessionOptions.ReferralChasing = [System.DirectoryServices.Protocols.ReferralChasingOptions]::None
            $conn.Timeout = New-Object System.TimeSpan(0, 0, $timeoutSeconds)
            if ($userName) {
                $conn.Credential = New-Object System.Net.NetworkCredential($userName, $password)
            }
            $conn.AuthType = [System.DirectoryServices.Protocols.AuthType]::Negotiate

            $req = [System.DirectoryServices.Protocols.SearchRequest]::new(
                '', '(objectClass=*)', [System.DirectoryServices.Protocols.SearchScope]::Base, [string[]]@('defaultNamingContext'))
            $null = $conn.SendRequest($req)
            try { $conn.Dispose() } catch {}
            return [pscustomobject]@{ Ok = $true; Error = $null }
        }
        catch {
            return [pscustomobject]@{ Ok = $false; Error = $_.Exception.Message }
        }
    }

    $ps = [System.Management.Automation.PowerShell]::Create()
    try {
        [void]$ps.AddScript($probeScript).AddArgument($Server).AddArgument($userName).AddArgument($password).AddArgument($TimeoutSeconds)

        $asyncResult = $ps.BeginInvoke()
        $completedInTime = $asyncResult.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds + 2))

        if (-not $completedInTime) {
            try { $ps.Stop() } catch {}
            return [pscustomobject]@{ Ok = $false; Error = "Connection attempt to '$Server' did not respond within ${TimeoutSeconds}s (the TCP connect itself appears to be hanging -- check the server name, DNS, or firewall)." }
        }

        $output = @($ps.EndInvoke($asyncResult))
        if ($ps.Streams.Error.Count -gt 0) {
            return [pscustomobject]@{ Ok = $false; Error = $ps.Streams.Error[0].ToString() }
        }
        if ($output.Count -gt 0 -and $output[0]) {
            return $output[0]
        }
        return [pscustomobject]@{ Ok = $false; Error = 'No result returned from the connectivity check.' }
    }
    finally {
        $ps.Dispose()
    }
}

function New-BHLdapConnectionBounded {
    <#
        .SYNOPSIS
        Like New-BHLdapConnection, but forces the INITIAL connect to happen
        within a bounded wall-clock wait (same BeginInvoke/WaitOne pattern
        as Test-BHLdapConnectivity) instead of relying on
        LdapConnection.Timeout alone, which was confirmed unreliable for
        bounding a fresh connect -- and confirmed NOT limited to clearly-
        unreachable servers: a worker's own brand-new connection to a
        server the main scan thread had just connected to successfully
        moments earlier was still observed hanging for minutes. Every
        connection the collector actually opens during a real scan goes
        through this now, not just the one-off reachability probe, since
        the hang can recur on any fresh connect, not only a bad one.

        Returns a live, already-connected LdapConnection (the warm-up
        request already ran), so the caller's subsequent requests reuse a
        connection that's already up rather than paying the connect cost
        again.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Server,
        [System.Management.Automation.PSCredential] $Credential,
        [int] $PageSize = 1000,
        [int] $ConnectTimeoutSeconds = 20,
        [int] $OperationTimeoutSeconds = 120,
        [int] $Port = 389
    )

    $userName = $null
    $password = $null
    if ($Credential) {
        $userName = $Credential.UserName
        $password = $Credential.GetNetworkCredential().Password
    }

    $warmupScript = {
        param($server, $userName, $password, $operationTimeoutSeconds, $port)
        Add-Type -AssemblyName System.DirectoryServices.Protocols -ErrorAction Stop
        $identifier = New-Object System.DirectoryServices.Protocols.LdapDirectoryIdentifier($server, $port)
        $conn = New-Object System.DirectoryServices.Protocols.LdapConnection($identifier)
        $conn.SessionOptions.ProtocolVersion = 3
        $conn.SessionOptions.Signing = $true
        $conn.SessionOptions.Sealing = $true
        $conn.SessionOptions.ReferralChasing = [System.DirectoryServices.Protocols.ReferralChasingOptions]::None
        $conn.Timeout = New-Object System.TimeSpan(0, 0, $operationTimeoutSeconds)
        if ($userName) {
            $conn.Credential = New-Object System.Net.NetworkCredential($userName, $password)
        }
        $conn.AuthType = [System.DirectoryServices.Protocols.AuthType]::Negotiate

        $req = [System.DirectoryServices.Protocols.SearchRequest]::new(
            '', '(objectClass=*)', [System.DirectoryServices.Protocols.SearchScope]::Base, [string[]]@('defaultNamingContext'))
        $null = $conn.SendRequest($req)
        return $conn
    }

    $ps = [System.Management.Automation.PowerShell]::Create()
    try {
        [void]$ps.AddScript($warmupScript).AddArgument($Server).AddArgument($userName).AddArgument($password).AddArgument($OperationTimeoutSeconds).AddArgument($Port)

        $asyncResult = $ps.BeginInvoke()
        $completedInTime = $asyncResult.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($ConnectTimeoutSeconds))

        if (-not $completedInTime) {
            try { $ps.Stop() } catch {}
            throw "Connecting to '$Server' did not complete within ${ConnectTimeoutSeconds}s (the TCP connect itself appears to be hanging)."
        }

        $output = @($ps.EndInvoke($asyncResult))
        if ($ps.Streams.Error.Count -gt 0) {
            throw $ps.Streams.Error[0].Exception
        }
        if ($output.Count -eq 0 -or -not $output[0]) {
            throw "Connecting to '$Server' did not return a usable connection."
        }
        return $output[0]
    }
    finally {
        # Deliberately not disposing $ps here: disposing a PowerShell
        # instance can dispose/invalidate objects it constructed in some
        # hosts, and the whole point is to hand the live LdapConnection
        # back to the caller still usable. It's a small, bounded leak of
        # the PowerShell wrapper object itself (not the connection), traded
        # for not risking the connection it successfully opened.
    }
}

function Get-BHOuTree {
    <#
        .SYNOPSIS
        Lists the domain root plus every organizationalUnit under it (flat,
        with ParentDn for the caller to rebuild the hierarchy -- the GUI
        uses this to build a TreeView so a scan target can be clicked
        instead of typed). Uses a short connection timeout and a small
        retry budget: this is a "let the user pick something" convenience,
        not the scan itself, so it should fail fast and clearly too.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Server,
        [System.Management.Automation.PSCredential] $Credential,
        [int] $TimeoutSeconds = 15
    )

    Import-BHLdapAssemblies
    $factory = { New-BHLdapConnectionBounded -Server $Server -Credential $Credential -ConnectTimeoutSeconds $TimeoutSeconds }.GetNewClosure()

    $probe = & $factory
    $root = $null
    try {
        $naming = Get-BHDefaultNamingContext -Connection $probe
        $root = $naming.DefaultNamingContext
    }
    finally { try { $probe.Dispose() } catch {} }

    $list = New-Object System.Collections.Generic.List[pscustomobject]
    $list.Add([pscustomobject]@{ Dn = $root; Name = $root; ParentDn = $null })

    foreach ($entry in (Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn $root `
        -Filter '(objectClass=organizationalUnit)' -Attributes @('distinguishedName', 'name') `
        -Scope ([System.DirectoryServices.Protocols.SearchScope]::Subtree) -PageSize 500 -MaxRetries 1)) {

        $dn = $entry.DistinguishedName
        $name = if ($entry.Attributes.Contains('name')) { $entry.Attributes['name'][0].ToString() } else { $dn }
        $parent = Get-BHParentDn -Dn $dn
        $list.Add([pscustomobject]@{ Dn = $dn; Name = $name; ParentDn = $parent })
    }

    return $list
}

# Attributes pulled for every principal-ish object. nTSecurityDescriptor
# requires the SecurityDescriptorFlagControl (OWNER=0x1 | DACL=0x4) to come
# back as raw bytes without needing SACL rights.
$script:BHObjectAttributes = @(
    'objectClass', 'objectSid', 'objectGUID', 'distinguishedName', 'name',
    'sAMAccountName', 'userAccountControl', 'memberOf', 'primaryGroupID', 'groupType',
    'gPLink', 'nTSecurityDescriptor', 'displayName', 'operatingSystem', 'dNSHostName',
    'msDS-AllowedToActOnBehalfOfOtherIdentity', 'sIDHistory', 'servicePrincipalName', 'msDS-AllowedToDelegateTo',
    'securityIdentifier', 'trustDirection', 'trustType', 'trustAttributes', 'flatName',
    'certificateTemplates', 'msPKI-Certificate-Name-Flag', 'msPKI-Enrollment-Flag',
    'msPKI-RA-Signature', 'pKIExtendedKeyUsage', 'msPKI-Template-Schema-Version'
)
$script:BHNodeFilter = '(|(objectClass=user)(objectClass=group)(objectClass=organizationalUnit)(objectClass=domainDNS)(objectClass=groupPolicyContainer)(objectClass=container)(objectClass=builtinDomain)(objectClass=trustedDomain))'

function Invoke-BHPagedSearch {
    <#
        .SYNOPSIS
        Streams SearchResultEntry objects via a paged LDAP search. Yields
        one page at a time to the pipeline  -  callers must process and
        discard each entry rather than collecting the whole stream, or the
        "streaming" property is lost at the PowerShell layer.

        Takes a $ConnectionFactory instead of a live connection: a scan
        against a large subtree can outlive the LDAP connection. Paging
        cookies belong to that connection. The caller must restart the
        shard from its first page if the connection is lost.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock] $ConnectionFactory,
        [Parameter(Mandatory)][string] $BaseDn,
        [string] $Filter = '(objectClass=*)',
        [string[]] $Attributes = $script:BHObjectAttributes,
        [int] $PageSize = 1000,
        [System.DirectoryServices.Protocols.SearchScope] $Scope = [System.DirectoryServices.Protocols.SearchScope]::Subtree,
        [int] $MaxRetries = 5
    )

    $connection = & $ConnectionFactory
    $pageRequestControl = New-Object System.DirectoryServices.Protocols.PageResultRequestControl($PageSize)
    $sdFlagControl = $null
    if ($Attributes -contains 'nTSecurityDescriptor') {
        $sdFlagControl = [System.DirectoryServices.Protocols.SecurityDescriptorFlagControl]::new(
            [System.DirectoryServices.Protocols.SecurityMasks]::Owner -bor [System.DirectoryServices.Protocols.SecurityMasks]::Dacl)
    }

    $lastCookie = $null

    try {
        do {
            $req = [System.DirectoryServices.Protocols.SearchRequest]::new($BaseDn, $Filter, $Scope, [string[]]$Attributes)
            [void]$req.Controls.Add($pageRequestControl)
            if ($sdFlagControl) { [void]$req.Controls.Add($sdFlagControl) }

            $attempt = 0
            $resp = $null
            while ($true) {
                try {
                    $resp = $connection.SendRequest($req)
                    break
                }
                catch [System.DirectoryServices.Protocols.DirectoryOperationException] {
                    if ($_.Exception.Response -and
                        $_.Exception.Response.ResultCode -eq [System.DirectoryServices.Protocols.ResultCode]::SizeLimitExceeded) {
                        throw
                    }
                    if (-not $_.Exception.Response -or $_.Exception.Response.ResultCode -ne [System.DirectoryServices.Protocols.ResultCode]::Busy) { throw }
                    $attempt++
                    if ($attempt -gt $MaxRetries) { throw }
                    $delayMs = [int]([Math]::Min(30000, 500 * [Math]::Pow(2, $attempt)) + (Get-Random -Maximum 250))
                    Write-BHLog -Level Warn -Message "LDAP server busy paging '$BaseDn' (attempt $attempt/$MaxRetries). Retrying in ${delayMs}ms."
                    Start-Sleep -Milliseconds $delayMs
                }
                catch [System.DirectoryServices.Protocols.LdapException] {
                    if ($pageRequestControl.Cookie.Length -gt 0 -or $_.Exception.ErrorCode -notin @(51, 52, 81, 85, 91)) { throw }
                    $attempt++
                    if ($attempt -gt $MaxRetries) { throw }
                    $delayMs = [int]([Math]::Min(30000, 500 * [Math]::Pow(2, $attempt)) + (Get-Random -Maximum 250))
                    Write-BHLog -Level Warn -Message "LDAP connection error paging '$BaseDn' (attempt $attempt/$MaxRetries): $($_.Exception.Message). Reconnecting in ${delayMs}ms."
                    Start-Sleep -Milliseconds $delayMs
                    try { $connection.Dispose() } catch {}
                    $connection = & $ConnectionFactory
                }
            }

            foreach ($entry in $resp.Entries) {
                Write-Output $entry
            }

            $pageResponse = $resp.Controls |
                Where-Object { $_ -is [System.DirectoryServices.Protocols.PageResultResponseControl] } |
                Select-Object -First 1

            if ($pageResponse -and $pageResponse.Cookie -and $pageResponse.Cookie.Length -gt 0) {
                # Guard against a server that keeps returning the same
                # cookie with no entries instead of ending the search  - 
                # seen on some buggy LDAP proxies  -  so a scan can't hang
                # forever on one subtree.
                if ($lastCookie -and $resp.Entries.Count -eq 0 -and
                    $lastCookie.Length -eq $pageResponse.Cookie.Length -and
                    -not (Compare-Object $lastCookie $pageResponse.Cookie -SyncWindow 0)) {
                    throw "LDAP paging on '$BaseDn' repeated an empty page."
                }
                $lastCookie = $pageResponse.Cookie
                $pageRequestControl.Cookie = $pageResponse.Cookie
            }
            elseif ($pageResponse) {
                break
            }
            else { throw "LDAP paging response control missing for '$BaseDn'." }
        } while ($true)
    }
    finally {
        try { $connection.Dispose() } catch {}
    }
}

function Get-BHAttrString {
    param($Entry, [string] $Name)
    if ($Entry.Attributes.Contains($Name)) {
        $val = $Entry.Attributes[$Name][0]
        if ($val -is [byte[]]) { return [System.Text.Encoding]::UTF8.GetString($val) }
        return [string]$val
    }
    return $null
}

function Get-BHAttrBytes {
    <#
        .SYNOPSIS
        The first value of a binary attribute (objectSid, objectGUID,
        nTSecurityDescriptor) as a real byte[], or $null if absent.

        Indexing the attribute ($entry.Attributes['objectSid'][0]) is not safe
        for this: the LDAP library returns a *string* instead of byte[] whenever
        the bytes happen to be valid UTF-8. That is guaranteed for builtin
        principals (S-1-5-32-544 and friends are all small byte values) and
        happens by chance for the occasional ordinary SID or GUID, and a cast of
        that string to byte[] throws. GetValues([byte[]]) always yields bytes.
    #>
    param($Entry, [string] $Name)
    if (-not $Entry.Attributes.Contains($Name)) { return $null }
    $values = $Entry.Attributes[$Name].GetValues([byte[]])
    if (-not $values -or $values.Length -eq 0) { return $null }
    return , ([byte[]]$values[0])
}

function Get-BHAttrStringArray {
    param($Entry, [string] $Name)
    if ($Entry.Attributes.Contains($Name)) {
        $out = New-Object System.Collections.Generic.List[string]
        foreach ($v in $Entry.Attributes[$Name].GetValues([string])) { $out.Add($v) }
        return $out
    }
    return (New-Object System.Collections.Generic.List[string])
}

function Get-BHAttrStringArrayRanged {
    <#
        .SYNOPSIS
        Like Get-BHAttrStringArray, but follows AD's range-retrieval
        convention (the server returning "memberOf;range=0-1499" instead
        of "memberOf" when a multi-valued attribute has more values than
        it will return in one response). Without this, an account nested
        in more groups than the server's per-response limit would silently
        lose MemberOf edges past the cutoff  -  plausible for service
        accounts or admins with broad nested-group membership.

        Falls back to a plain read when the attribute came back whole, so
        this is a transparent drop-in for the common case.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Dn,
        [Parameter(Mandatory)][scriptblock] $ConnectionFactory,
        [Parameter(Mandatory)][ref] $Connection
    )

    $out = New-Object System.Collections.Generic.List[string]

    if ($Entry.Attributes.Contains($Name)) {
        foreach ($v in $Entry.Attributes[$Name].GetValues([string])) { $out.Add($v) }
        return $out
    }

    $rangedKey = $null
    foreach ($key in $Entry.Attributes.AttributeNames) {
        if ($key -is [string] -and $key.StartsWith("$Name;range=", [System.StringComparison]::OrdinalIgnoreCase)) {
            $rangedKey = $key
            break
        }
    }
    if (-not $rangedKey) { return $out }

    foreach ($v in $Entry.Attributes[$rangedKey].GetValues([string])) { $out.Add($v) }
    if ($rangedKey.EndsWith('-*')) { return $out } # server already said "that's everything"

    # More values exist beyond this first chunk. Page through them with
    # follow-up base-scope searches against the object's own DN, each
    # asking for the next slice by range.
    while ($true) {
        $next = $out.Count
        $wantedRange = "$Name;range=$next-*"
        $req = [System.DirectoryServices.Protocols.SearchRequest]::new(
            $Dn, '(objectClass=*)', [System.DirectoryServices.Protocols.SearchScope]::Base, [string[]]@($wantedRange))

        $resp = Invoke-BHWithRetry -Description "Ranged attribute fetch ($Name) on $Dn" -Action {
            if (-not $Connection.Value) { $Connection.Value = & $ConnectionFactory }
            try {
                $Connection.Value.SendRequest($req)
            }
            catch [System.DirectoryServices.Protocols.LdapException] {
                try { $Connection.Value.Dispose() } catch {}
                $Connection.Value = $null
                throw
            }
        }

        if ($resp.Entries.Count -eq 0) { throw "No entry returned for $wantedRange on '$Dn'." }
        $entry2 = $resp.Entries[0]

        $foundKey = $null
        foreach ($key in $entry2.Attributes.AttributeNames) {
            if ($key -is [string] -and $key.StartsWith("$Name;range=", [System.StringComparison]::OrdinalIgnoreCase)) {
                $foundKey = $key
                break
            }
        }
        if (-not $foundKey -or -not $foundKey.StartsWith("$Name;range=$next-", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Missing or unexpected attribute range for $wantedRange on '$Dn'."
        }

        $chunk = $entry2.Attributes[$foundKey].GetValues([string])
        foreach ($v in $chunk) { $out.Add($v) }

        if ($foundKey.EndsWith('-*') -or $chunk.Count -eq 0) { break }
    }

    return $out
}

function Get-BHObjectClassKind {
    <# Maps an objectClass set to one of our node types; returns $null for anything we don't model yet. #>
    param([string[]] $Classes)
    if ($Classes -contains 'computer') { return 'computer' }
    if ($Classes -contains 'group') { return 'group' }
    if ($Classes -contains 'user') { return 'user' }       # covers inetOrgPerson subclass too
    if ($Classes -contains 'domainDNS') { return 'domain' }
    if ($Classes -contains 'groupPolicyContainer') { return 'gpo' }
    if ($Classes -contains 'organizationalUnit' -or $Classes -contains 'container' -or $Classes -contains 'builtinDomain') { return 'ou' }
    return $null
}

function Get-BHParentDn {
    param([string] $Dn)
    return [BH.Native.DirectoryIdentity]::ParentDn($Dn)
}

function ConvertFrom-BHGpLink {
    <#
        .SYNOPSIS
        Parses a gPLink attribute value like
        "[LDAP://cn={GUID},cn=policies,cn=system,DC=corp,DC=local;0][...]"
        into a list of GPO GUIDs (braces included, upper-cased, matching how
        groupPolicyContainer cn values are formatted).
    #>
    param([string] $GpLinkValue)
    $result = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrEmpty($GpLinkValue)) { return $result }
    $matches = [System.Text.RegularExpressions.Regex]::Matches(
        $GpLinkValue, '\[LDAP://cn=(\{[0-9A-Fa-f-]+\})[^;\]]*;(\d+)\]',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    foreach ($m in $matches) {
        if (([int]$m.Groups[2].Value -band 1) -eq 0) {
            $result.Add($m.Groups[1].Value.ToUpperInvariant())
        }
    }
    return $result
}

function Write-BHWorkerLog {
    <# Appends one timestamped line to worker-<shard>.log in the scan folder. Written straight to disk each call so a hung worker still leaves a trail showing the last stage it reached. #>
    param([string] $OutDir, [string] $ShardId, [string] $Message)
    try {
        $line = ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), $Message) + "`r`n"
        [System.IO.File]::AppendAllText((Join-Path $OutDir "worker-$ShardId.log"), $line)
    }
    catch {}
}

function Invoke-BHWorker {
    param(
        [string] $ModuleRoot,
        [string] $Server,
        [string] $UserName,
        [System.Security.SecureString] $Password,
        [string] $BaseDn,
        [System.DirectoryServices.Protocols.SearchScope] $Scope,
        [string] $OutDir,
        [string] $ShardId,
        [int] $PageSize,
        [string[]] $ExcludeDns,
        [BH.Native.ProgressCounters] $Counters,
        [bool] $CompressOutput = $false,
        [string] $DomainId,
        [string] $Filter = $script:BHNodeFilter
    )

    Initialize-BHNative
    Import-BHLdapAssemblies
    Write-BHWorkerLog $OutDir $ShardId "start: scope=$Scope baseDn='$BaseDn'"
    $credential = if ($UserName) { [pscredential]::new($UserName, $Password) } else { $null }
    $factory = { New-BHLdapConnectionBounded -Server $Server -Credential $credential }.GetNewClosure()
    $rangeConnection = $null
    $rangeReader = [Func[System.DirectoryServices.Protocols.SearchResultEntry,string,string[]]] {
        param($entry, $attribute)
        $options = @{ Entry = $entry; Name = $attribute; Dn = $entry.DistinguishedName; ConnectionFactory = $factory; Connection = ([ref]$rangeConnection) }
        [string[]]@(Get-BHAttrStringArrayRanged @options)
    }
    $excluded = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dn in $ExcludeDns) { [void]$excluded.Add($dn) }
    $writer = [BH.Native.LdapEntryWriter]::new($OutDir, $ShardId, $CompressOutput, $DomainId, $rangeReader)
    $reportedObjects = 0L
    $reportedEdges = 0L
    $logged = 0
    try {
        Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn $BaseDn -Scope $Scope -PageSize $PageSize -Filter $Filter |
            ForEach-Object {
                if ($excluded.Contains($_.DistinguishedName)) { return }
                $entry = $_
                try {
                    $writer.Write($entry)
                    if ($writer.Objects - $reportedObjects -ge 2000) {
                        $Counters.AddObjects($writer.Objects - $reportedObjects)
                        $Counters.AddEdges($writer.Edges - $reportedEdges)
                        $reportedObjects = $writer.Objects
                        $reportedEdges = $writer.Edges
                        $writer.Flush()
                    }
                }
                catch {
                    $Counters.AddError()
                    if ($logged -lt 10) {
                        Write-BHWorkerLog $OutDir $ShardId "entry error on '$($entry.DistinguishedName)': $($_.Exception.Message)"
                        $logged++
                    }
                }
            }
        $Counters.AddObjects($writer.Objects - $reportedObjects)
        $Counters.AddEdges($writer.Edges - $reportedEdges)
        Write-BHWorkerLog $OutDir $ShardId "done: $($writer.Objects) objects, $($writer.Edges) edges; $($writer.MissingDescriptors) unreadable security descriptors"
        @{
            Objects = $writer.Objects
            Edges = $writer.Edges
            MissingDescriptors = $writer.MissingDescriptors
        } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $OutDir "worker-$ShardId.json") -Encoding UTF8
    }
    catch {
        Write-BHWorkerLog $OutDir $ShardId "FAILED during search: $($_.Exception.Message)"
        throw
    }
    finally {
        $writer.Dispose()
        if ($rangeConnection) { $rangeConnection.Dispose() }
    }
}
function Get-BHDnDomain {
    <# "CN=Bob,OU=x,DC=corp,DC=example,DC=com" -> "corp.example.com" #>
    param([string] $Dn)
    $parts = foreach ($m in [regex]::Matches($Dn, '(?i)(?:^|,)DC=([^,]+)')) { $m.Groups[1].Value }
    (@($parts) -join '.').ToLowerInvariant()
}

function ConvertTo-BHSidFilter {
    <# Builds an LDAP "(|(objectSid=...)...)" filter from SID strings; $null if none parse. #>
    param([string[]] $Sids)
    $parts = New-Object 'System.Collections.Generic.List[string]'
    foreach ($text in $Sids) {
        try {
            $sid = New-Object System.Security.Principal.SecurityIdentifier($text)
            $bytes = New-Object byte[] $sid.BinaryLength
            $sid.GetBinaryForm($bytes, 0)
            $escaped = -join ($bytes | ForEach-Object { '\' + $_.ToString('x2') })
            $parts.Add("(objectSid=$escaped)")
        }
        catch {}
    }
    if ($parts.Count -eq 0) { return $null }
    return '(|' + ($parts -join '') + ')'
}

function Resolve-BHExternalPrincipals {
    <#
        .SYNOPSIS
        Looks up names for permission holders that live outside the scanned
        OUs. A scan of a few OUs records ACLs whose trustees (Domain Admins,
        builtin groups, other teams' groups) were never collected, so the
        report would otherwise show them as bare SIDs. This finds those SIDs,
        asks the domain for their name/type in batches, and writes the answers
        to principals_external.ext.ndjson in the scan folder. It is a name
        lookup only: these principals are not added as graph nodes, so node
        counts and paths are unchanged. Best effort -- the caller treats any
        failure as non-fatal.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ScanDir,
        [Parameter(Mandatory)][string] $Server,
        [System.Management.Automation.PSCredential] $Credential,
        [int] $MaxSids = 20000,
        [int] $MaxSeconds = 300
    )

    $nodeFiles = [string[]]@(Get-ChildItem -Path $ScanDir -Filter 'nodes_*.ndjson*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $aclFiles = [string[]]@(Get-ChildItem -Path $ScanDir -Filter 'edges_acl.*.ndjson*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    if ($aclFiles.Count -eq 0) { return 0 }

    $unknown = [BH.Native.UnresolvedSids]::Collect($nodeFiles, $aclFiles, $MaxSids)
    if ($unknown.Length -eq 0) { return 0 }
    if ($unknown.Length -gt $MaxSids) {
        Write-BHLog -Level Warn -Message "$($unknown.Length) outside permission holders found; looking up only the first $MaxSids."
        $unknown = $unknown[0..($MaxSids - 1)]
    }
    Write-BHLog -Message "Looking up names for $($unknown.Length) permission holder(s) outside the scanned OUs ..."

    $factory = { New-BHLdapConnectionBounded -Server $Server -Credential $Credential }.GetNewClosure()
    $conn = & $factory
    $root = (Get-BHDefaultNamingContext -Connection $conn).DefaultNamingContext

    $writer = [BH.Native.NdjsonWriter]::new((Join-Path $ScanDir 'principals_external.ext.ndjson'), $false)
    $resolvedSids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $state = @{ Resolved = 0 }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $plural = @{ group = 'groups'; user = 'users'; computer = 'computers' }

    # Writes one name record per matched entry. Entries found in the forest Global
    # Catalog get their domain appended (taken from the DN) so same-named principals
    # in different domains stay distinguishable.
    $emit = {
        param($Entries, [bool] $FromForest)
        foreach ($entry in $Entries) {
            [byte[]]$sidBytes = $null
            $sidBytes = Get-BHAttrBytes $entry 'objectSid'
            if (-not $sidBytes) { continue }
            $sidStr = [BH.Native.Sid]::ToSidStringSafe($sidBytes)
            $kind = Get-BHObjectClassKind -Classes (Get-BHAttrStringArray -Entry $entry -Name 'objectClass')
            if (-not $sidStr -or -not $kind -or -not $plural.ContainsKey($kind)) { continue }
            $label = Get-BHAttrString $entry 'name'
            if ($FromForest -and $label) {
                $domain = Get-BHDnDomain $entry.DistinguishedName
                if ($domain) { $label = "$label@$domain" }
            }
            $rec = (New-Object BH.Native.JsonRecordWriter).Begin().Prop('Id', $sidStr).PropS('Name', $label).Prop('Type', $plural[$kind]).Prop('Dn', $entry.DistinguishedName).End()
            $writer.WriteLine($rec)
            [void]$resolvedSids.Add($sidStr)
            $state.Resolved++
        }
    }

    # Looks SIDs up in batches of 40 over one connection holder, with a single
    # reconnect-and-retry per batch and a give-up after 3 consecutive failures.
    $runBatches = {
        param($Holder, $Factory, [string] $SearchBase, [string[]] $Sids, [bool] $FromForest)
        $failures = 0
        for ($i = 0; $i -lt $Sids.Length; $i += 40) {
            if ($watch.Elapsed.TotalSeconds -gt $MaxSeconds) {
                Write-BHLog -Level Warn -Message "Name lookup stopped after $MaxSeconds s; the remaining holders keep their SIDs in the report."
                return
            }
            $last = [Math]::Min($i + 40, $Sids.Length) - 1
            $filter = ConvertTo-BHSidFilter ([string[]]@($Sids[$i..$last]))
            if (-not $filter) { continue }
            $req = [System.DirectoryServices.Protocols.SearchRequest]::new(
                $SearchBase, $filter, [System.DirectoryServices.Protocols.SearchScope]::Subtree,
                [string[]]@('distinguishedName', 'name', 'objectClass', 'objectSid'))
            try {
                try { $resp = $Holder.Conn.SendRequest($req) }
                catch {
                    # one reconnect-and-retry, in case the connection went stale
                    try { $Holder.Conn.Dispose() } catch {}
                    $Holder.Conn = & $Factory
                    $resp = $Holder.Conn.SendRequest($req)
                }
                $failures = 0
            }
            catch {
                $failures++
                Write-BHLog -Level Warn -Message "Name lookup batch failed: $($_.Exception.Message)"
                if ($failures -ge 3) { Write-BHLog -Level Warn -Message 'Giving up on name lookup after 3 failed batches.'; return }
                continue
            }
            & $emit $resp.Entries $FromForest
        }
    }

    $holder = @{ Conn = $conn }
    $gcHolder = @{ Conn = $null }
    try {
        # Pass 1: the scanned domain.
        & $runBatches $holder $factory $root $unknown $false

        # Pass 2: anything still unnamed belongs to another domain. In a forest the
        # Global Catalog holds every domain's objects, so an empty search base on
        # port 3268 finds them. (The scanned domain's own directory cannot.)
        $pending = @($unknown | Where-Object { -not $resolvedSids.Contains($_) })
        if ($pending.Count -gt 0 -and $watch.Elapsed.TotalSeconds -le $MaxSeconds) {
            Write-BHLog -Message "$($pending.Count) holder(s) are not in the scanned domain; looking them up in the forest Global Catalog ..."
            $gcFactory = { New-BHLdapConnectionBounded -Server $Server -Credential $Credential -Port 3268 }.GetNewClosure()
            try { $gcHolder.Conn = & $gcFactory }
            catch { Write-BHLog -Level Warn -Message "The Global Catalog (port 3268) could not be reached, so holders in other domains keep their SIDs: $($_.Exception.Message)" }
            if ($gcHolder.Conn) { & $runBatches $gcHolder $gcFactory '' ([string[]]$pending) $true }
        }
    }
    finally {
        $writer.Dispose()
        foreach ($h in @($holder, $gcHolder)) { if ($h.Conn) { try { $h.Conn.Dispose() } catch {} } }
    }
    $resolved = $state.Resolved
    Write-BHLog -Message "Resolved names for $resolved of $($unknown.Length) outside permission holder(s)."
    return $resolved
}

function Start-BHCollection {
    <#
        .SYNOPSIS
        Runs a full scan against one base DN (a domain root or an OU
        subtree), parallelized across its immediate child containers, and
        writes a scan folder of NDJSON shards plus scan-metadata.json.

        .PARAMETER BaseDn
        Domain or OU distinguished name to scan, e.g.
        'OU=Workstations,DC=corp,DC=local' or 'DC=corp,DC=local'.

        .PARAMETER Server
        LDAP server (a DC hostname, or a domain name to let DNS/SRV pick one).

        .PARAMETER MaxThreads
        Upper bound on concurrent LDAP subtree workers. Keep this modest
        against a single DC (8-16)  -  the bottleneck is almost always the
        DC's own LDAP thread pool, not the client.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $BaseDn,
        [Parameter(Mandatory)][string] $Server,
        [Parameter(Mandatory)][string] $OutputRoot,
        [System.Management.Automation.PSCredential] $Credential,
        [ValidateRange(1, 128)][int] $MaxThreads = 8,
        [ValidateRange(1, 1000)][int] $PageSize = 1000,
        [switch] $ResolveExternalNames,
        [bool] $CompressOutput = $true,
        [switch] $SkipConfiguration,
        [switch] $CollectEndpoints,
        [ValidateRange(1, 32)][int] $EndpointThreads = 4,
        [ValidateRange(2, 300)][int] $EndpointTimeoutSeconds = 20,
        [scriptblock] $ProgressCallback
    )

    Initialize-BHNative
    Import-BHLdapAssemblies

    Write-BHLog -Message "Connecting to $Server ..."
    $moduleRoot = $PSScriptRoot
    $mainConnFactory = { New-BHLdapConnectionBounded -Server $Server -Credential $Credential -ConnectTimeoutSeconds 10 }.GetNewClosure()
    $mainConn = & $mainConnFactory
    try {
        $naming = Get-BHDefaultNamingContext -Connection $mainConn
        $domainRequest = [System.DirectoryServices.Protocols.SearchRequest]::new($naming.DefaultNamingContext,
            '(objectClass=domainDNS)', [System.DirectoryServices.Protocols.SearchScope]::Base, [string[]]@('objectSid'))
        $domainResponse = $mainConn.SendRequest($domainRequest)
        if ($domainResponse.Entries.Count -ne 1) { throw 'Could not read the domain SID.' }
        $domainId = [BH.Native.Sid]::ToSidStringSafe((Get-BHAttrBytes $domainResponse.Entries[0] 'objectSid'))
        if (-not $domainId) { throw 'The domain object did not contain a valid SID.' }
    }
    finally { $mainConn.Dispose() }

    Write-BHLog -Message "Discovering scan targets under '$BaseDn' ..."
    $children = @(Invoke-BHPagedSearch -ConnectionFactory $mainConnFactory -BaseDn $BaseDn -Scope OneLevel `
        -Filter '(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))' `
        -Attributes @('distinguishedName') -PageSize $PageSize -MaxRetries 2 |
        ForEach-Object { $_.DistinguishedName })
    Write-BHLog -Message "Found $($children.Count) child container(s) under '$BaseDn'."
    $scanDir = New-BHScanFolder -RootPath $OutputRoot -ScopeDescription "$Server/$BaseDn"

    $workItems = New-Object System.Collections.Generic.List[hashtable]
    for ($i = 0; $i -lt $children.Count; $i++) {
        $workItems.Add(@{ BaseDn = $children[$i]; Scope = [System.DirectoryServices.Protocols.SearchScope]::Subtree; ShardId = "c$i" })
    }
    # Base DN itself, one level only (its own attributes + any leaf objects
    # not inside one of the child containers already dispatched above).
    $workItems.Add(@{ BaseDn = $BaseDn; Scope = [System.DirectoryServices.Protocols.SearchScope]::Base; ShardId = 'base' })
    $workItems.Add(@{ BaseDn = $BaseDn; Scope = [System.DirectoryServices.Protocols.SearchScope]::OneLevel; ShardId = 'baseleaf'; ExcludeDns = $children })

    $gpoContainer = "CN=Policies,CN=System,$($naming.DefaultNamingContext)"
    if ($gpoContainer -ine $BaseDn -and -not $gpoContainer.EndsWith(",$BaseDn", [StringComparison]::OrdinalIgnoreCase)) {
        $workItems.Add(@{ BaseDn = $gpoContainer; Scope = [System.DirectoryServices.Protocols.SearchScope]::Subtree; ShardId = 'gpo' })
    }

    if (-not $SkipConfiguration -and $naming.ConfigurationNamingContext) {
        $workItems.Add(@{
            BaseDn = $naming.ConfigurationNamingContext
            Scope = [System.DirectoryServices.Protocols.SearchScope]::Subtree
            ShardId = 'configuration'
            Filter = '(|(objectClass=pKICertificateTemplate)(objectClass=pKIEnrollmentService)(objectClass=certificationAuthority)(objectClass=site))'
        })
    }
    if ($BaseDn -ine $naming.DefaultNamingContext -and
        -not ("CN=System,$($naming.DefaultNamingContext)").EndsWith(",$BaseDn", [StringComparison]::OrdinalIgnoreCase) -and
        $BaseDn -ine "CN=System,$($naming.DefaultNamingContext)") {
        $workItems.Add(@{
            BaseDn = "CN=System,$($naming.DefaultNamingContext)"
            Scope = [System.DirectoryServices.Protocols.SearchScope]::Subtree
            ShardId = 'trusts'
            Filter = '(objectClass=trustedDomain)'
        })
    }

    Write-BHLog -Message "Collecting $($workItems.Count) work items with at most $MaxThreads active workers ..."
    $userName = if ($Credential) { $Credential.UserName } else { $null }
    $password = if ($Credential) { $Credential.Password } else { $null }
    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(1, $MaxThreads)
    $pool.Open()
    $handles = [System.Collections.Generic.List[object]]::new()
    $counters = [BH.Native.ProgressCounters]::new()
    $workerErrors = [System.Collections.Generic.List[string]]::new()
    $workerFailureCount = 0
    $missingDescriptors = 0L
    $nextWorkItem = 0
    $doneShards = 0
    $workerDispatch = {
        param($Options, $UserName, $Password, $ModuleRoot)
        $ErrorActionPreference = 'Stop'
        Import-Module (Join-Path $ModuleRoot 'BH.Core.psm1') -DisableNameChecking
        $Options.UserName = $UserName
        $Options.Password = $Password
        for ($attempt = 0; ; $attempt++) {
            try { Invoke-BHWorker @Options; break }
            catch [System.DirectoryServices.Protocols.LdapException] {
                if ($attempt -ge 2 -or $_.Exception.ErrorCode -notin @(51, 52, 81, 85, 91)) { throw }
                $Options.Counters.Reset()
                Write-Warning "LDAP connection lost on shard '$($Options.ShardId)'. Restarting from page one."
                Start-Sleep -Milliseconds (1000 * ($attempt + 1))
            }
        }
    }
    $heartbeat = [Diagnostics.Stopwatch]::StartNew()
    $lastHeartbeat = 0
    try {
        while ($nextWorkItem -lt $workItems.Count -or $handles.Count -gt 0) {
            while ($nextWorkItem -lt $workItems.Count -and $handles.Count -lt $MaxThreads) {
                $item = $workItems[$nextWorkItem++]
                $shardCounters = [BH.Native.ProgressCounters]::new()
                $options = @{
                    ModuleRoot = $moduleRoot; Server = $Server; BaseDn = $item.BaseDn
                    Scope = $item.Scope; OutDir = $scanDir; ShardId = $item.ShardId
                    PageSize = $PageSize; ExcludeDns = $item['ExcludeDns']; Counters = $shardCounters
                    CompressOutput = $CompressOutput; DomainId = $domainId
                }
                if ($item['Filter']) { $options.Filter = $item.Filter }
                $shell = [PowerShell]::Create()
                $shell.RunspacePool = $pool
                [void]$shell.AddScript($workerDispatch).AddArgument($options).AddArgument($userName).
                    AddArgument($password).AddArgument($moduleRoot)
                $handles.Add([pscustomobject]@{
                    Shell = $shell; Async = $shell.BeginInvoke(); Counters = $shardCounters; ShardId = $item.ShardId
                })
            }
            foreach ($handle in $handles.ToArray()) {
                if (-not $handle.Async.IsCompleted) { continue }
                $failed = $false
                try { $null = $handle.Shell.EndInvoke($handle.Async) }
                catch {
                    $failed = $true
                    if ($workerErrors.Count -lt 20) { $workerErrors.Add("Shard '$($handle.ShardId)': $($_.Exception.Message)") }
                }
                if ($handle.Shell.Streams.Error.Count -gt 0) {
                    $failed = $true
                    foreach ($errorRecord in $handle.Shell.Streams.Error) {
                        if ($workerErrors.Count -lt 20) { $workerErrors.Add($errorRecord.ToString()) }
                    }
                }
                if ($failed) { $workerFailureCount++ }
                $counters.AddObjects($handle.Counters.Objects)
                $counters.AddEdges($handle.Counters.Edges)
                $counters.AddErrors($handle.Counters.Errors)
                $metricsPath = Join-Path $scanDir "worker-$($handle.ShardId).json"
                if (Test-Path -LiteralPath $metricsPath) {
                    $metrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
                    $missingDescriptors += [long]$metrics.MissingDescriptors
                }
                $handle.Shell.Dispose()
                [void]$handles.Remove($handle)
                $doneShards++
            }
            if ($ProgressCallback) {
                $active = [BH.Native.ProgressCounters]::Sum([BH.Native.ProgressCounters[]]@($handles | ForEach-Object { $_.Counters }))
                & $ProgressCallback ($counters.Objects + $active.Objects) ($counters.Edges + $active.Edges) ($counters.Errors + $active.Errors) $doneShards $workItems.Count 'Collecting directory objects'
            }
            if ($heartbeat.Elapsed.TotalSeconds - $lastHeartbeat -ge 10) {
                $lastHeartbeat = $heartbeat.Elapsed.TotalSeconds
                Write-BHLog -Message "$nextWorkItem of $($workItems.Count) shards started. $($handles.Count) active; $($counters.Objects) saved objects."
            }
            if ($handles.Count) { Start-Sleep -Milliseconds 100 }
        }
    }
    finally {
        foreach ($handle in $handles) {
            try { $handle.Shell.Stop() } finally { $handle.Shell.Dispose() }
        }
        $pool.Dispose()
    }
    foreach ($message in $workerErrors) { Write-BHLog -Level Error -Message $message }

    $endpointSummary = $null
    $endpointError = $null
    if ($CollectEndpoints -and $workerFailureCount -eq 0 -and $counters.Errors -eq 0) {
        Write-BHLog -Message 'Collecting endpoint local groups and logged-on users with the current Windows identity ...'
        if ($ProgressCallback) { & $ProgressCallback $counters.Objects $counters.Edges $counters.Errors $doneShards $workItems.Count 'Collecting endpoint groups and logons (per-host counts are in the log)' }
        try {
            Import-Module (Join-Path $moduleRoot 'BH.Endpoint.psm1') -DisableNameChecking
            $endpointOptions = @{
                ScanDir = $scanDir; MaxWorkers = $EndpointThreads; TimeoutSeconds = $EndpointTimeoutSeconds
                CompressOutput = $CompressOutput
                ProgressCallback = {
                    param($summary)
                    Write-BHLog -Message "Endpoints: $($summary.Attempted) started, $($summary.Complete) complete, $($summary.Partial) partial, $($summary.Failed) failed, $($summary.TimedOut) timed out."
                }
            }
            $endpointSummary = Start-BHEndpointCollection @endpointOptions
        }
        catch {
            $endpointError = $_.Exception.Message
            Write-BHLog -Level Warn -Message "Endpoint collection stopped: $endpointError"
            $endpointMetadata = Join-Path $scanDir 'endpoint-metadata.json'
            if (Test-Path -LiteralPath $endpointMetadata) { $endpointSummary = Get-Content -LiteralPath $endpointMetadata -Raw | ConvertFrom-Json }
        }
    }

    # Names for permission holders outside the scanned OUs (best effort: a
    # failure here is reported but never affects the scan data itself).
    $externalResolved = 0
    try {
        if ($ResolveExternalNames -and $workerFailureCount -eq 0 -and $counters.Errors -eq 0) {
            if ($ProgressCallback) { & $ProgressCallback $counters.Objects $counters.Edges $counters.Errors $doneShards $workItems.Count 'Looking up names for permission holders outside the scanned domain (up to 5 minutes)' }
            $externalResolved = @(Resolve-BHExternalPrincipals -ScanDir $scanDir -Server $Server -Credential $Credential)[-1]
        }
    }
    catch {
        Write-BHLog -Level Warn -Message "Name lookup for outside permission holders failed (scan data is unaffected): $($_.Exception.Message)"
    }

    # Pull the per-object error lines each worker logged (first 10 per
    # worker) into the scan output and metadata, so "906 errors" always
    # comes with the actual messages.
    $entryErrors = New-Object System.Collections.Generic.List[string]
    foreach ($wl in (Get-ChildItem -Path $scanDir -Filter 'worker-*.log' -File -ErrorAction SilentlyContinue)) {
        foreach ($line in (Get-Content -Path $wl.FullName -ErrorAction SilentlyContinue)) {
            if ($entryErrors.Count -lt 20 -and $line -match 'entry error|FAILED') { $entryErrors.Add("$($wl.BaseName): $line") }
        }
    }
    foreach ($line in ($entryErrors | Select-Object -First 15)) { Write-BHLog -Level Warn -Message $line }

    $meta = Get-Content (Join-Path $scanDir 'scan-metadata.json') -Raw | ConvertFrom-Json
    $meta.FinishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $meta.Counts = [ordered]@{ Objects = $counters.Objects; Edges = $counters.Edges }
    $meta.Errors = $counters.Errors
    $meta.Status = if ($workerFailureCount -gt 0 -or $counters.Errors -gt 0) { 'Incomplete' } else { 'Complete' }
    $meta | Add-Member -NotePropertyName WorkItemCount -NotePropertyValue $workItems.Count -Force
    $meta | Add-Member -NotePropertyName WorkerFailureCount -NotePropertyValue $workerFailureCount -Force
    $meta | Add-Member -NotePropertyName WorkerErrors -NotePropertyValue @($workerErrors | Select-Object -First 20) -Force
    $meta | Add-Member -NotePropertyName EntryErrorSamples -NotePropertyValue @($entryErrors | Select-Object -First 20) -Force
    $meta | Add-Member -NotePropertyName ExternalNamesResolved -NotePropertyValue $externalResolved -Force
    $notes = [System.Collections.Generic.List[string]]::new()
    if ($missingDescriptors) { $notes.Add("$missingDescriptors objects had no readable security descriptor. ACL coverage is incomplete.") }
    if ($SkipConfiguration -or -not $naming.ConfigurationNamingContext) { $notes.Add('Configuration-partition certificate and site collection was skipped.') }
    if (-not $CollectEndpoints) { $notes.Add('Endpoint local groups and logged-on users were not collected. Enable endpoint collection for those relationships.') }
    elseif ($endpointError) { $notes.Add("Endpoint collection failed: $endpointError") }
    elseif ($null -ne $endpointSummary) {
        $notes.Add("Endpoint collection: $($endpointSummary.Complete) complete, $($endpointSummary.Partial) partial, $($endpointSummary.Failed) failed, $($endpointSummary.TimedOut) timed out, $($endpointSummary.Skipped) skipped. See endpoint-status.ndjson files for per-host coverage.")
    }
    if ($null -ne $endpointSummary) {
        $meta | Add-Member -NotePropertyName EndpointCollection -NotePropertyValue $endpointSummary -Force
        $meta.Counts.Edges += $endpointSummary.Edges
    }
    $notes.Add('Certificate enrollment and trust relationships are inventory facts. Full AD CS and trust attack-path derivation requires further analysis.')
    $meta | Add-Member -NotePropertyName CoverageNotes -NotePropertyValue $notes.ToArray() -Force
    $meta | Add-Member -NotePropertyName MissingSecurityDescriptors -NotePropertyValue $missingDescriptors -Force
    $meta | Add-Member -NotePropertyName Compressed -NotePropertyValue $CompressOutput -Force
    $meta | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $scanDir 'scan-metadata.json') -Encoding UTF8

    if ($counters.Errors -gt 0 -and $counters.Objects -eq 0) {
        Write-BHLog -Level Warn -Message "Scan returned objects from the directory but failed to process $($counters.Errors) of them (0 saved). This is a processing error, not a permissions gap -- see the 'entry error' lines above or the worker-*.log files in the scan folder."
    }
    elseif ($counters.Objects -eq 0 -and $workerFailureCount -eq 0) {
        Write-BHLog -Level Warn -Message "Scan completed with 0 objects and no worker errors -- dispatched $($workItems.Count) work item(s) against '$BaseDn'. If you expected objects here, this points at a permissions/visibility gap (the bind account may not have rights to see this OU's contents) rather than a crash."
    }

    if ($meta.Status -eq 'Incomplete') {
        throw "Incomplete scan at '$scanDir'. See scan-metadata.json and the worker logs for details."
    }
    return $scanDir
}

# ---------------------------------------------------------------------------
# Merge and reporting share a disk-backed identity and edge index.
# ---------------------------------------------------------------------------

$script:BHNodeTypes = @('domains', 'ous', 'users', 'groups', 'computers', 'gpos', 'sites', 'siteservers',
    'sitesubnets', 'trusts', 'certtemplates', 'enterprisecas', 'rootcas', 'aiacas', 'ntauthstores', 'issuancepolicies')
$script:BHEdgeTypes = @('memberof', 'contains', 'gplink', 'acl', 'access', 'observed')

function Get-BHDatasetKinds {
    return @{ Nodes = $script:BHNodeTypes; Edges = $script:BHEdgeTypes }
}

function Get-BHDatasetCoverageNotes {
    param([string[]] $DatasetPaths)
    foreach ($path in $DatasetPaths) {
        foreach ($metadataName in 'scan-metadata.json', 'merge-metadata.json') {
            $file = Join-Path $path $metadataName
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            $metadata = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
            if ($metadata.PSObject.Properties['CoverageNotes']) { $metadata.CoverageNotes }
        }
    }
}

function Get-BHDatasetFiles {
    param([string[]] $DatasetPaths, [string] $Prefix)
    foreach ($dataset in $DatasetPaths) {
        foreach ($file in (Get-ChildItem -LiteralPath $dataset -File -Filter "$Prefix*.ndjson*" | Sort-Object Name)) {
            $stem = $file.Name -replace '\.ndjson(\.gz)?$', ''
            if ($file.Name -match '\.ndjson(\.gz)?$' -and ($stem -eq $Prefix -or $stem.StartsWith("$Prefix."))) { $file.FullName }
        }
    }
}

function Set-BHIndexProgress {
    <#
        .SYNOPSIS
        Routes the index's native progress hook to a scriptblock, at most once every
        2 seconds (a change of step always passes). The scriptblock receives
        (label, done, total, records). Pass no scriptblock to remove the hook.
    #>
    param([BH.Native.GraphIndex] $Index, [scriptblock] $OnProgress)
    if (-not $OnProgress) { $Index.Progress = $null; return }
    $state = @{ Last = -2000L; Label = ''; Clock = [System.Diagnostics.Stopwatch]::StartNew() }
    $Index.Progress = [Action[string, long, long, long]]{
        param($label, $done, $total, $records)
        $now = $state.Clock.ElapsedMilliseconds
        if ($label -ne $state.Label -or ($now - $state.Last) -ge 2000) {
            $state.Label = $label
            $state.Last = $now
            # A display problem must never abort the data load.
            try { & $OnProgress $label $done $total $records } catch {}
        }
    }.GetNewClosure()
}

function Import-BHGraphDataset {
    param(
        [BH.Native.GraphIndex] $Index,
        [string[]] $DatasetPaths,
        [switch] $RetainNodes,
        [scriptblock] $OnProgress
    )

    $nodePaths = [System.Collections.Generic.List[string]]::new()
    $kinds = [System.Collections.Generic.List[string]]::new()
    $edgePaths = [System.Collections.Generic.List[string]]::new()
    $buckets = [System.Collections.Generic.List[string]]::new()
    foreach ($kind in $script:BHNodeTypes) {
        foreach ($path in (Get-BHDatasetFiles $DatasetPaths "nodes_$kind")) {
            $nodePaths.Add($path)
            $kinds.Add($kind)
        }
    }
    foreach ($bucket in $script:BHEdgeTypes) {
        foreach ($path in (Get-BHDatasetFiles $DatasetPaths "edges_$bucket")) {
            $edgePaths.Add($path)
            $buckets.Add($bucket)
        }
    }
    if ($nodePaths.Count -eq 0) { throw 'No node datasets were found in the supplied folders.' }
    Set-BHIndexProgress -Index $Index -OnProgress $OnProgress
    try {
        $Index.Import($nodePaths.ToArray(), $kinds.ToArray(), $edgePaths.ToArray(), $buckets.ToArray(),
            @(Get-BHDatasetFiles $DatasetPaths 'principals_external'), [bool]$RetainNodes)
    }
    finally { Set-BHIndexProgress -Index $Index -OnProgress $null }
}

function Start-BHMerge {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]] $ScanPaths,
        [Parameter(Mandatory)][string] $OutputRoot,
        [bool] $CompressOutput = $true,
        [scriptblock] $ProgressCallback
    )

    Initialize-BHNative
    Assert-BHDatasetsReady -DatasetPaths $ScanPaths
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $mergeId = "merged-$stamp-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $root = New-Item -ItemType Directory -Path $OutputRoot -Force
    $mergeDir = Join-Path $root.FullName $mergeId
    $working = New-Item -ItemType Directory -Path (Join-Path $root.FullName ".$mergeId.tmp")
    $indexPath = Join-Path $working.FullName 'merge.sqlite'

    # Detail updates: (label, records, done, total). 'done' and 'total' are bytes of input
    # while loading and output steps while writing; the caller only needs their ratio.
    $mergeProgress = $ProgressCallback
    $detail = if ($mergeProgress) {
        { param($label, $done, $total, $records) & $mergeProgress 'detail' $label $records $done $total }.GetNewClosure()
    }

    try {
        if ($ProgressCallback) { & $ProgressCallback 'phase' 'Step 1 of 3: Loading into a working index' 0 }
        $index = [BH.Native.GraphIndex]::new($indexPath, $true)
        try {
            Import-BHGraphDataset -Index $index -DatasetPaths $ScanPaths -RetainNodes -OnProgress $detail
            if ($ProgressCallback) { & $ProgressCallback 'phase' 'Step 2 of 3: Counting (reads the whole working index)' 0 }
            $nodeCounts = [ordered]@{}
            $edgeCounts = [ordered]@{}
            $unresolved = [ordered]@{}
            foreach ($kind in $script:BHNodeTypes) { $nodeCounts[$kind] = 0L }
            foreach ($bucket in $script:BHEdgeTypes) { $edgeCounts[$bucket] = 0L; $unresolved[$bucket] = 0L }
            foreach ($row in $index.NodeCounts()) { $nodeCounts[$row[0]] = [long]$row[1] }
            foreach ($row in $index.EdgeCounts()) { $edgeCounts[$row[0]] = [long]$row[1] }
            foreach ($row in $index.UnresolvedCounts()) { $unresolved[$row[0]] = [long]$row[1] }

            if ($ProgressCallback) { & $ProgressCallback 'phase' 'Step 3 of 3: Writing merged files (one full pass per type, can be slow)' 0 }
            Set-BHIndexProgress -Index $index -OnProgress $detail
            $externalCount = $index.WriteMerged($working.FullName, $script:BHNodeTypes, $script:BHEdgeTypes, $CompressOutput)
            Set-BHIndexProgress -Index $index -OnProgress $null
        }
        finally { $index.Dispose() }
        [IO.File]::Delete($indexPath)
        $meta = [ordered]@{
            MergeId = $mergeId
            SourceScans = $ScanPaths
            FinishedUtc = (Get-Date).ToUniversalTime().ToString('o')
            Status = 'Complete'
            Compressed = $CompressOutput
            NodeCounts = $nodeCounts
            EdgeCounts = $edgeCounts
            EdgesUnresolved = $unresolved
            ExternalNames = $externalCount
            CoverageNotes = @(Get-BHDatasetCoverageNotes $ScanPaths | Sort-Object -Unique)
        }
        $meta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $working.FullName 'merge-metadata.json') -Encoding UTF8
        [IO.Directory]::Move($working.FullName, $mergeDir)
        return $mergeDir
    }
    finally {
        if ([IO.Directory]::Exists($working.FullName)) {
            foreach ($file in [IO.Directory]::EnumerateFiles($working.FullName)) { [IO.File]::Delete($file) }
            [IO.Directory]::Delete($working.FullName)
        }
    }
}

function Assert-BHDatasetsReady {
    param([string[]] $DatasetPaths)
    foreach ($path in $DatasetPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Dataset folder not found: $path" }
        foreach ($metadataName in 'scan-metadata.json', 'merge-metadata.json') {
            $metadataPath = Join-Path $path $metadataName
            if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { continue }
            $meta = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
            if (($meta.PSObject.Properties['Status'] -and $meta.Status -ne 'Complete') -or
                -not $meta.FinishedUtc -or
                ($meta.PSObject.Properties['WorkerFailureCount'] -and $meta.WorkerFailureCount -gt 0) -or
                ($meta.PSObject.Properties['Errors'] -and $meta.Errors -gt 0)) {
                throw "Dataset is incomplete: $path. See $metadataName and the worker logs."
            }
        }
    }
}

Export-ModuleMember -Function `
    Initialize-BHNative, New-BHScanFolder, Write-BHLog, `
    Start-BHCollection, Invoke-BHWorker, Import-BHLdapAssemblies, New-BHLdapConnection, New-BHLdapConnectionBounded, Invoke-BHPagedSearch, `
    Test-BHLdapConnectivity, Get-BHOuTree, Resolve-BHExternalPrincipals, `
    Start-BHMerge, Get-BHDatasetFiles, Get-BHDatasetKinds, Get-BHDatasetCoverageNotes, Import-BHGraphDataset, Assert-BHDatasetsReady
