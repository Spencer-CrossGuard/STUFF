# Called only for the importer's private, unpublished staging dataset.
# The public workflow preserves original exports and existing collections.
function Get-BHGroupAttribute {
    param($Entry,[string]$Name)
    if($Entry.Attributes.Contains($Name) -and $Entry.Attributes[$Name].Count){return ,$Entry.Attributes[$Name][0]}
    return $null
}
function ConvertTo-BHGroupTypeObservation {
    param($Entry,[string]$ExpectedSid)
    $classes=@();if($Entry.Attributes.Contains('objectClass')){$classes=@($Entry.Attributes['objectClass'].GetValues([string]))}
    if('group' -notin $classes){throw 'NotGroup'}
    foreach($key in 'objectSid','objectGUID'){if(-not $Entry.Attributes.Contains($key) -or $Entry.Attributes[$key].Count -ne 1){throw 'MissingIdentity'}}
    if(-not $Entry.Attributes.Contains('groupType') -or $Entry.Attributes['groupType'].Count -ne 1){throw 'UnreadableGroupType'}
    $bytes=Get-BHGroupAttribute $Entry 'objectSid'
    if($bytes -isnot [byte[]]){throw 'MissingIdentity'}
    $sid=[Security.Principal.SecurityIdentifier]::new($bytes,0).Value
    if($sid -ne $ExpectedSid){throw 'IdentityMismatch'}
    $guidBytes=Get-BHGroupAttribute $Entry 'objectGUID'
    if($guidBytes -isnot [byte[]] -or $guidBytes.Length -ne 16){throw 'MissingIdentity'}
    $raw=Get-BHGroupAttribute $Entry 'groupType';$number=0L
    if($null -eq $raw -or -not [long]::TryParse([string]$raw,[ref]$number) -or $number -lt [int]::MinValue -or $number -gt [uint32]::MaxValue){throw 'UnreadableGroupType'}
    $bits=$number -band 0xffffffffL;$scope=$bits -band 0x0e
    if($scope -notin 2,4,8 -or ($bits -band 0x7ffffff0L) -ne 0 -or (($bits -band 1) -ne 0 -and $scope -ne 4)){throw 'UnsupportedGroupType'}
    [pscustomobject]@{SecurityEnabled=(($bits -band 0x80000000L) -ne 0);GroupType=[string]$raw;Sid=$sid;Guid=([guid]::new($guidBytes)).ToString('B').ToUpperInvariant();Dn=$Entry.DistinguishedName;Usn=[string](Get-BHGroupAttribute $Entry 'uSNChanged');WhenChanged=[string](Get-BHGroupAttribute $Entry 'whenChanged')}
}
function Update-BHImportedGroupTypes {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Server,
        [System.Management.Automation.PSCredential]$Credential,
        [ValidateRange(1,25000)][int]$MaxGroups=5000,[ValidateRange(1,600)][int]$TimeBudgetSeconds=60,
        [scriptblock]$ProgressCallback)
    $watch=[Diagnostics.Stopwatch]::StartNew();$selected=@{};$knownRows=0;$outsideRows=0;$preflightComplete=$true
    $files=@(Get-BHDatasetFiles @($Directory) 'nodes_groups')
    if(@($files|Where-Object {$_ -notlike '*.ndjson'}).Count){throw 'Group enrichment applies only to the new uncompressed import staging files.'}
    foreach($file in $files){foreach($line in [BH.Native.NdjsonReader]::Lines($file)){
        if($watch.Elapsed.TotalSeconds -ge $TimeBudgetSeconds){$preflightComplete=$false;break}
        $id=[BH.Native.JsonRecordReader]::ExtractField($line,'Id');$flag=[BH.Native.JsonRecordReader]::ExtractField($line,'SecurityEnabled')
        if($flag -in 'true','false'){$knownRows++;continue};if(-not $id -or $selected.ContainsKey($id)){continue}
        if($selected.Count -ge $MaxGroups){$outsideRows++;continue}
        $selected[$id]=[pscustomobject]@{Status='Pending';QueriedUtc=$null;ObservedUtc=$null;Observation=$null}
    };if(-not $preflightComplete){break}}
    # A later duplicate must not let a lookup replace a known imported value.
    if($preflightComplete -and $selected.Count){foreach($file in $files){foreach($line in [BH.Native.NdjsonReader]::Lines($file)){
        if($watch.Elapsed.TotalSeconds -ge $TimeBudgetSeconds){$preflightComplete=$false;break}
        $id=[BH.Native.JsonRecordReader]::ExtractField($line,'Id')
        if(($id -and $selected.ContainsKey($id)) -and [BH.Native.JsonRecordReader]::ExtractField($line,'SecurityEnabled') -in 'true','false'){$selected[$id].Status='KnownInExport'}
    };if(-not $preflightComplete){break}}}
    $connection=$null;$boundServer=$null;$domainSid=$null;$failure=$null;$limitHit=$false
    if($preflightComplete -and @($selected.Values|Where-Object Status -eq 'Pending').Count){
        try{
            if($ProgressCallback){& $ProgressCallback 'Reading missing group types using read-only LDAP searches'}
            $remaining=[Math]::Max(1,[int]($TimeBudgetSeconds-$watch.Elapsed.TotalSeconds))
            $connection=New-BHLdapConnectionBounded -Server $Server -Credential $Credential -ConnectTimeoutSeconds ([Math]::Min(10,$remaining)) -OperationTimeoutSeconds ([Math]::Min(5,$remaining))
            if($watch.Elapsed.TotalSeconds -ge $TimeBudgetSeconds){$limitHit=$true;throw 'WorkLimit'}
            $rootRequest=[DirectoryServices.Protocols.SearchRequest]::new('', '(objectClass=*)',[DirectoryServices.Protocols.SearchScope]::Base,[string[]]@('defaultNamingContext','dnsHostName'))
            $root=$connection.SendRequest($rootRequest)
            if($root.Entries.Count -ne 1){throw 'UnverifiedDomain'}
            $base=[string](Get-BHGroupAttribute $root.Entries[0] 'defaultNamingContext');$boundServer=[string](Get-BHGroupAttribute $root.Entries[0] 'dnsHostName')
            if(-not $base -or -not $boundServer){throw 'UnverifiedDomain'}
            if($watch.Elapsed.TotalSeconds -ge $TimeBudgetSeconds){$limitHit=$true;throw 'WorkLimit'}
            $domainRequest=[DirectoryServices.Protocols.SearchRequest]::new($base,'(objectClass=domainDNS)',[DirectoryServices.Protocols.SearchScope]::Base,[string[]]@('objectSid'))
            $domain=$connection.SendRequest($domainRequest)
            if($domain.Entries.Count -ne 1){throw 'UnverifiedDomain'}
            $domainBytes=Get-BHGroupAttribute $domain.Entries[0] 'objectSid'
            if($domainBytes -isnot [byte[]]){throw 'UnverifiedDomain'}
            $domainSid=[Security.Principal.SecurityIdentifier]::new($domainBytes,0).Value
            if($domainSid -notmatch '^S-1-5-21-\d+-\d+-\d+$'){throw 'UnverifiedDomain'}
            $done=0
            foreach($id in @($selected.Keys|Sort-Object)){
                $item=$selected[$id];if($item.Status -ne 'Pending'){continue}
                if($watch.Elapsed.TotalSeconds -ge $TimeBudgetSeconds){break}
                if($id -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$' -or $id.Substring(0,$id.LastIndexOf('-')) -ne $domainSid){$item.Status='OutsideSelectedDomain';continue}
                try{
                    $sid=[Security.Principal.SecurityIdentifier]::new($id);$binary=[byte[]]::new($sid.BinaryLength);$sid.GetBinaryForm($binary,0)
                    $escaped=($binary|ForEach-Object {'\'+$_.ToString('x2')}) -join ''
                    $request=[DirectoryServices.Protocols.SearchRequest]::new($base,('(&(objectClass=group)(objectSid='+$escaped+'))'),[DirectoryServices.Protocols.SearchScope]::Subtree,[string[]]@('objectClass','objectSid','objectGUID','groupType','uSNChanged','whenChanged','distinguishedName'))
                    $request.SizeLimit=2;$request.TimeLimit=[TimeSpan]::FromSeconds([Math]::Max(1,[Math]::Min(5,[int]($TimeBudgetSeconds-$watch.Elapsed.TotalSeconds))))
                    $connection.Timeout=$request.TimeLimit;$item.QueriedUtc=[DateTime]::UtcNow.ToString('o')
                    $response=$connection.SendRequest($request)
                    if($response.Entries.Count -eq 0){$item.Status='NotFound'}elseif($response.Entries.Count -ne 1){$item.Status='Ambiguous'}else{
                        $item.Observation=ConvertTo-BHGroupTypeObservation -Entry $response.Entries[0] -ExpectedSid $id
                        $item.ObservedUtc=[DateTime]::UtcNow.ToString('o');$item.Status='Resolved'
                    }
                }catch{
                    $code=[string]$_.Exception.Message
                    $item.Status=if($code -in 'NotGroup','MissingIdentity','IdentityMismatch','UnreadableGroupType','UnsupportedGroupType'){$code}else{'LookupFailed'}
                }
                $done++;if($ProgressCallback -and $done%100 -eq 0){& $ProgressCallback ("Read-only group lookups: $done / $($selected.Count)")}
            }
        }catch{if(-not $limitHit){$failure='LDAP connection or domain identity verification failed; original unknown classifications were retained.'}}
        finally{if($connection){$connection.Dispose()}}
    }
    foreach($item in $selected.Values){if($item.Status -eq 'Pending'){$item.Status=if(-not $preflightComplete -or -not $failure){'WorkLimit'}else{'Unavailable'}}}
    # No partial preflight can rule out a known duplicate, so apply nothing.
    if($preflightComplete -and $selected.Count){foreach($file in $files){
        $replacement=$file+'.group-types.tmp';$writer=[IO.StreamWriter]::new($replacement,$false,[Text.UTF8Encoding]::new($false))
        try{foreach($line in [BH.Native.NdjsonReader]::Lines($file)){
            $id=[BH.Native.JsonRecordReader]::ExtractField($line,'Id');$flag=[BH.Native.JsonRecordReader]::ExtractField($line,'SecurityEnabled')
            if(-not $selected.ContainsKey($id) -or $flag -in 'true','false'){$writer.WriteLine($line);continue}
            $item=$selected[$id];$record=$line|ConvertFrom-Json
            $record|Add-Member GroupTypeEnrichmentStatus $item.Status -Force
            if($item.QueriedUtc){$record|Add-Member GroupTypeQueriedUtc $item.QueriedUtc -Force}
            if($item.Status -eq 'Resolved'){
                $o=$item.Observation
                foreach($pair in @(@('SecurityEnabled',$o.SecurityEnabled),@('GroupType',$o.GroupType),@('GroupTypeSource','ReadOnlyLDAP'),@('GroupTypeServer',$boundServer),@('GroupTypeObservedUtc',$item.ObservedUtc),@('GroupTypeObservedSid',$o.Sid),@('GroupTypeObservedGuid',$o.Guid),@('GroupTypeObservedDn',$o.Dn),@('GroupTypeUsnChanged',$o.Usn),@('GroupTypeWhenChanged',$o.WhenChanged))){$record|Add-Member -NotePropertyName $pair[0] -NotePropertyValue $pair[1] -Force}
            }
            $writer.WriteLine(($record|ConvertTo-Json -Depth 30 -Compress))
        }}finally{$writer.Dispose()}
        Move-Item -LiteralPath $replacement -Destination $file -Force
    }}
    $counts=[ordered]@{};foreach($group in @($selected.Values|Group-Object Status)){$counts[$group.Name]=$group.Count}
    $resolved=@($selected.Values|Where-Object Status -eq 'Resolved').Count
    $unresolved=@($selected.Values|Where-Object {$_.Status -notin 'Resolved','KnownInExport'}).Count
    $summary=[pscustomobject]@{Requested=$true;Server=$Server;VerifiedServer=$boundServer;VerifiedDomainSid=$domainSid;SelectedGroups=$selected.Count;Resolved=$resolved;Unresolved=$unresolved;KnownRowsPreserved=$knownRows;RowsOutsideCandidateLimit=$outsideRows;Counts=$counts;Status=if($failure){'Unavailable'}elseif(-not $preflightComplete -or $unresolved -or $outsideRows){'Partial'}else{'Completed'};Failure=$failure;ElapsedSeconds=[Math]::Round($watch.Elapsed.TotalSeconds,2);PostExportEvidence=($resolved -gt 0)}
    if($ProgressCallback){& $ProgressCallback ("Group classification enrichment: $resolved resolved; $($summary.Status). Existing export evidence was not revalidated.")}
    return $summary
}
