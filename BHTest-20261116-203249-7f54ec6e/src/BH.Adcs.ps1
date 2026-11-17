# Template review only. This never creates graph edges or asserts CA effective access.
function Get-BHAdcsTemplateAssessment {
    param([Parameter(Mandatory)]$Record)
    $checks = [ordered]@{}
    function Field($name) { $p=$Record.PSObject.Properties[$name]; if($p){$p.Value} }
    function Number($name) { $v=Field $name; $n=0L; if($null -ne $v -and [long]::TryParse([string]$v,[ref]$n)){return $n}; return $null }
    function Boolean($name) { $v=Field $name; if($v -is [bool]){return $v}; return $null }
    $approval=Boolean 'AdcsRequiresManagerApproval'; $subject=Boolean 'AdcsEnrolleeSuppliesSubject'
    $auth=Boolean 'AdcsAuthenticationEnabled'
    $flags=Number 'EnrollmentFlags'; $nameFlags=Number 'CertificateNameFlags'
    $schema=Number 'SchemaVersion'; $signatures=Number 'RequiredSignatures'
    if($null -eq $approval -and $null -ne $flags){$approval=($flags -band 2) -ne 0}
    if($null -eq $subject -and $null -ne $nameFlags){$subject=($nameFlags -band 1) -ne 0}
    if($null -eq $auth){
        $eku=@(Field 'ExtendedKeyUsage'); $app=@(Field 'ApplicationPolicies')
        # Empty/absent LDAP lists may reflect unreadable attributes. Do not equate
        # that absence with an unrestricted certificate. Application-policy
        # interactions are deliberately unresolved in this first review pass.
        if($eku.Count -gt 0 -and $app.Count -eq 0){
            $authenticationOids=@('1.3.6.1.5.5.7.3.2','1.3.6.1.4.1.311.20.2.2','1.3.6.1.5.2.3.4','2.5.29.37.0')
            $auth=$false
            foreach($oid in $eku){if([string]$oid -notmatch '^\d+(\.\d+)+$'){$auth=$null;break};if($oid -in $authenticationOids){$auth=$true}}
        }
    }
    $checks.Subject=if($null -eq $subject){'Unknown: subject-name flag missing'}elseif($subject){'Pass: requester supplies subject'}else{'Fail: requester does not supply subject'}
    $checks.Approval=if($null -eq $approval){'Unknown: manager approval missing'}elseif($approval){'Fail: manager approval required'}else{'Pass: manager approval not required'}
    $checks.Authentication=if($null -eq $auth){'Unknown: authentication purpose or application-policy interaction'}elseif($auth){'Pass: authentication purpose recorded'}else{'Fail: authentication purpose not recorded in supplied EKUs'}
    $checks.Signatures=if($null -eq $schema -or $schema -lt 1 -or $schema -gt 4 -or $null -eq $signatures -or $signatures -lt 0){'Unknown: supported schema/signature metadata missing'}elseif($schema -gt 1 -and $signatures -gt 0){'Fail: authorized signatures required'}else{'Pass: no applicable signature requirement'}
    $values=@($checks.Values)
    $status=if(@($values|Where-Object {$_ -like 'Fail:*'}).Count){'Template conditions not matched'}elseif(@($values|Where-Object {$_ -like 'Unknown:*'}).Count){'Template metadata incomplete'}else{'Template conditions matched; domain path unverified'}
    [pscustomobject]@{Id=[string](Field 'Id');Name=[string](Field 'Name');Status=$status;Checks=($values -join '; ');NextStep='Verify publication, the same principal/token can enroll on both template and CA, CA chain and NTAuth trust in the target domain, and applicable host/DC authentication settings. Only supported CA evidence can produce ADCSESC1 candidates.'}
}

function Get-BHAdcsTemplateReview {
    param([string[]]$DatasetPaths,[int]$PreviewLimit=100)
    $records=@{}; $conflicts=@{}
    foreach($file in @(Get-BHDatasetFiles $DatasetPaths 'nodes_certtemplates')){
        foreach($line in [BH.Native.NdjsonReader]::Lines($file)){
            $record=$line|ConvertFrom-Json; $id=[string]$record.Id
            if(-not $id){continue}
            $assessment=Get-BHAdcsTemplateAssessment $record
            # Compare only prerequisites, not incidental names or property order.
            $signature=@('CertificateNameFlags','EnrollmentFlags','RequiredSignatures','SchemaVersion','ExtendedKeyUsage','ApplicationPolicies','AdcsRequiresManagerApproval','AdcsEnrolleeSuppliesSubject','AdcsAuthenticationEnabled')|ForEach-Object {
                $p=$record.PSObject.Properties[$_]; if($p){@($_,$p.Value)|ConvertTo-Json -Depth 6 -Compress}else{"$_ missing"}
            }
            $key=$signature -join '|'
            if($records.ContainsKey($id) -and $records[$id].Key -ne $key){$conflicts[$id]=$true}
            $records[$id]=@{Key=$key;Row=$assessment}
        }
    }
    $rows=@(foreach($id in $records.Keys){$row=$records[$id].Row;if($conflicts.ContainsKey($id)){$row.Status='Conflicting template snapshots';$row.Checks='Supplied scans disagree on ESC1 prerequisite metadata. Collect a consistent snapshot.'};$row})
    $rows=@($rows|Sort-Object Status,Name,Id)
    [pscustomobject]@{Total=$rows.Count;Matched=@($rows|Where-Object {$_.Status -eq 'Template conditions matched; domain path unverified'}).Count;Incomplete=@($rows|Where-Object {$_.Status -eq 'Template metadata incomplete'}).Count;Conflicting=$conflicts.Count;Truncated=($rows.Count -gt $PreviewLimit);Rows=@($rows|Select-Object -First $PreviewLimit);Note='Template configuration review only; not confirmed vulnerabilities or paths. Graph derivation separately evaluates imported direct-root CA prerequisites. LDAP-only, intermediate-chain and incomplete evidence remains outside that supported scope.'}
}
