#Requires -Version 5.1
<#
.SYNOPSIS
Populate an existing, disposable Microsoft deployment-kit AD domain for BHTest validation.
.DESCRIPTION
Plan/WhatIf is offline. Apply/Verify run inside the lab DC using Windows PowerShell 5.1.
No forest creation, VM provisioning, GPO/firewall changes, remote execution, or password exports.
#>
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param(
 [ValidateSet('Plan','Apply','Verify')][string]$Mode='Plan',
 [ValidatePattern('^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$')][string]$ExpectedDomain,
 [switch]$LabOnly,
 [ValidateRange(10,5000)][int]$UserCount=120,
 [Security.SecureString]$LabPassword,
 [Security.SecureString]$Tier0Password,
 [switch]$IncludeReplicationScenario,
 [switch]$IncludeTier0Account,
 [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,14}$')][string]$DelegationTargetComputer,
 [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$rootName='BHTestLab'
$schemaVersion=1
function Get-BHLabPlan {
 param([int]$Count)
 $users=New-Object 'Collections.Generic.List[object]'
 $special=@(
  @('bht.maya','Maya Chen - Service Desk','People'),@('bht.ethan','Ethan Cole - Application Engineering','People'),
  @('bht.priya','Priya Shah - Identity Operations','People'),@('bht.mila','Mila Bennett - Credential Provisioning','People'),
  @('bht.lucas','Lucas Gray - Server Provisioning','People'),@('bht.mail','Mail-only Group Control','People'),
  @('bht.denied','Explicit Deny Control','People'),@('bht.isolated','Disconnected Access Control','People'),
  @('bht.disabled','Disabled Leaver Control','People'),@('bht.svcdeploy','BH Deploy Service','Services'),
  @('bht.svcapp','BH Application Service','Services'),@('bht.svcbackup','BH Backup Service','Services'),
  @('bht.svcsync','BH Identity Sync Service','Services'),@('bht.svclegacy','BH Legacy Web Service','Services'))
 foreach($s in $special){$users.Add([pscustomobject]@{Sam=$s[0];Name=$s[1];Container=$s[2];Enabled=($s[0] -ne 'bht.disabled')})}
 if($IncludeTier0Account){$users.Add([pscustomobject]@{Sam='bht.tier0';Name='BH Tier 0 Lab Administrator';Container='People';Enabled=$true})}
 for($i=1;$i -le $Count;$i++){$users.Add([pscustomobject]@{Sam=('bht.u'+$i.ToString('D5'));Name=('BH Employee '+$i.ToString('D5'));Container='People';Enabled=($i%47 -ne 0)})}
 $groups=New-Object 'Collections.Generic.List[object]'
 foreach($name in @('BH-Lab-Staff','BH-Lab-Helpdesk','BH-Lab-AppAdmins','BH-Lab-PathTarget','BH-Lab-RemoteUsers','BH-Lab-MailOnly')){$groups.Add([pscustomobject]@{Name=$name;Category=$(if($name -eq 'BH-Lab-MailOnly'){'Distribution'}else{'Security'})})}
 foreach($dept in @('Finance','Engineering','Sales','Operations','HR','IT')){$groups.Add([pscustomobject]@{Name=('BH-Lab-'+$dept);Category='Security'})}
 [pscustomobject]@{Users=$users.ToArray();Groups=$groups.ToArray();Containers=@('People','Groups','Services')}
}
function Write-BHLabJson($value,[string]$file){$value|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $file -Encoding UTF8}
function Get-BHLabDescription([string]$id){'BHTestLab:v1;LabId='+$id+'; disposable validation fixture'}
function Test-BHLabOwned($object,[string]$id){
 if(-not $object.Description -or -not $object.Description.StartsWith((Get-BHLabDescription $id),[StringComparison]::Ordinal)){throw ('Refusing to adopt an unowned object: '+$object.DistinguishedName)}
}
function Get-BHLabObject([string]$dn){
 # Use a selective filter, not a full-domain scan per created object.
 $escaped=$dn.Replace('\','\5c').Replace('*','\2a').Replace('(','\28').Replace(')','\29').Replace([string][char]0,'\00')
 @(Get-ADObject -LDAPFilter ('(distinguishedName='+$escaped+')') -SearchBase $script:domainDn -SearchScope Subtree -Server $script:server -Properties description,sAMAccountName,objectSid) | Select-Object -First 1
}
function Open-BHLabEntry([string]$dn){
 $auth=[DirectoryServices.AuthenticationTypes]::Secure -bor [DirectoryServices.AuthenticationTypes]::Signing -bor [DirectoryServices.AuthenticationTypes]::Sealing
 $entry=New-Object DirectoryServices.DirectoryEntry(('LDAP://'+$script:server+'/'+$dn),$null,$null,$auth)
 $entry.Options.SecurityMasks=[DirectoryServices.SecurityMasks]::Dacl
 $entry.RefreshCache(@('nTSecurityDescriptor'));return $entry
}
function Get-BHLabSchemaGuid([string]$attribute){
 $items=@(Get-ADObject -LDAPFilter ('(lDAPDisplayName='+$attribute+')') -SearchBase $script:schemaDn -Server $script:server -Properties schemaIDGUID)
 if($items.Count -ne 1){throw "Schema attribute unavailable or ambiguous: $attribute"}
 New-Object Guid(,$items[0].schemaIDGUID)
}
function New-BHLabRule([Security.Principal.SecurityIdentifier]$Sid,[DirectoryServices.ActiveDirectoryRights]$Rights,[Guid]$ObjectType,[Security.AccessControl.AccessControlType]$Access='Allow'){
 New-Object DirectoryServices.ActiveDirectoryAccessRule($Sid,$Rights,$Access,$ObjectType)
}
function Test-BHLabRule($security,$rule){
 foreach($existing in $security.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){
  if($existing.IdentityReference.Value -eq $rule.IdentityReference.Value -and $existing.ActiveDirectoryRights -eq $rule.ActiveDirectoryRights -and $existing.AccessControlType -eq $rule.AccessControlType -and $existing.ObjectType -eq $rule.ObjectType -and $existing.InheritanceType -eq $rule.InheritanceType){return $true}
 };return $false
}
function Ensure-BHLabRule([string]$dn,$rule){
 $entry=Open-BHLabEntry $dn
 try{$security=$entry.ObjectSecurity;if(-not (Test-BHLabRule $security $rule)){
  $script:journal.Add([ordered]@{Action='AddAce';Target=$dn;Trustee=$rule.IdentityReference.Value;Rights=[string]$rule.ActiveDirectoryRights;ObjectType=[string]$rule.ObjectType;Access=[string]$rule.AccessControlType;Status='Attempting'})
  Save-BHLabState
  $security.AddAccessRule($rule);$entry.ObjectSecurity=$security;$entry.CommitChanges();$script:journal[$script:journal.Count-1].Status='Applied';Save-BHLabState
 }}finally{$entry.Dispose()}
}
function Save-BHLabState {
 $script:manifest.Journal=@($script:journal.ToArray());Write-BHLabJson $script:manifest (Join-Path $OutputDirectory 'lab-manifest.json')
}
function Ensure-BHLabMember([string]$group,[string]$member){
 if(-not $script:membershipCache.ContainsKey($group)){$set=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);foreach($existing in Get-ADGroupMember -Identity $group -Server $script:server){[void]$set.Add($existing.DistinguishedName)};$script:membershipCache[$group]=$set}
 if(-not $script:membershipCache[$group].Contains($member)){$script:journal.Add([ordered]@{Action='AddMember';Target=$group;Member=$member;Status='Attempting'});Save-BHLabState;Add-ADGroupMember -Identity $group -Members $member -Server $script:server;[void]$script:membershipCache[$group].Add($member);$script:journal[$script:journal.Count-1].Status='Applied';Save-BHLabState}
}
function Get-BHLabExpectedRules($principals,[Guid]$memberGuid,[Guid]$keyGuid,[Guid]$spnGuid){
 $result=New-Object 'Collections.Generic.List[object]'
 foreach($spec in @(
  @('BH-Lab-Helpdesk','bht.svcdeploy','ExtendedRight','00299570-246d-11d0-a768-00aa006e0529','Allow','ForceChangePassword'),
  @('bht.svcdeploy','bht.svcapp','GenericWrite',[Guid]::Empty,'Allow','GenericWrite'),
  @('bht.svcapp','BH-Lab-AppAdmins','WriteDacl',[Guid]::Empty,'Allow','WriteDacl'),
  @('BH-Lab-AppAdmins','BH-Lab-PathTarget','WriteProperty',$memberGuid,'Allow','AddMember'),
  @('bht.ethan','bht.svcbackup','GenericAll',[Guid]::Empty,'Allow','GenericAll'),
  @('bht.svcbackup','BH-Lab-PathTarget','WriteOwner',[Guid]::Empty,'Allow','WriteOwner'),
  @('bht.priya','bht.svcsync','GenericAll',[Guid]::Empty,'Allow','GenericAll'),
  @('bht.mila','bht.svclegacy','WriteProperty',$keyGuid,'Allow','AddKeyCredentialLink'),
  @('bht.ethan','bht.svclegacy','WriteProperty',$spnGuid,'Allow','WriteSPN'),
  @('BH-Lab-MailOnly','bht.svcapp','GenericAll',[Guid]::Empty,'Allow','GenericAll'),
  @('bht.denied','BH-Lab-PathTarget','WriteProperty',$memberGuid,'Allow','AddMember'),
  @('bht.denied','BH-Lab-PathTarget','WriteProperty',$memberGuid,'Deny','Explicit deny control')
 )){
  $rule=New-BHLabRule $principals[$spec[0]].SID $spec[2] ([Guid]$spec[3]) $spec[4]
  $result.Add([pscustomobject]@{Source=$spec[0];Target=$spec[1];TargetDn=$principals[$spec[1]].DistinguishedName;Rule=$rule;Relationship=$spec[5]})
 };$result.ToArray()
}
$plan=Get-BHLabPlan $UserCount
$preview=[ordered]@{Mode='Offline plan';ExpectedDomain=$ExpectedDomain;RootOU='OU=BHTestLab,<detected domain DN>';Users=$plan.Users.Count;Groups=$plan.Groups.Count;Containers=$plan.Containers;IncludeReplicationScenario=[bool]$IncludeReplicationScenario;IncludeTier0Account=[bool]$IncludeTier0Account;DelegationTargetComputer=$DelegationTargetComputer;Changes=@('Create only tagged lab OUs/accounts/groups','Add explicit fixture memberships and object ACLs','Preserve existing users, passwords, ACL entries, GPOs, firewall and kit services');OptionalOutsideOUChanges=@('Replication scenario: three rights for the owned sync account on the domain root','Tier 0 scenario: add the owned bht.tier0 user to Domain Admins','Delegation target: one owned-principal ACL on the selected member computer, plus delegation targets on an owned service account');LiveADValidated=$false}
if($Mode -eq 'Plan' -or $WhatIfPreference){
 if($OutputDirectory){$null=New-Item -ItemType Directory -Path $OutputDirectory -Force;Write-BHLabJson $preview (Join-Path $OutputDirectory 'lab-plan.json')}
 $preview|ConvertTo-Json -Depth 8;return
}
if(-not $ExpectedDomain){throw 'Apply/Verify requires -ExpectedDomain with the exact DNS name displayed by Get-ADDomain inside the lab DC.'}
if($Mode -eq 'Apply' -and -not $LabOnly){throw 'Apply requires -LabOnly. This script deliberately creates unsafe permissions only for a disposable isolated lab.'}
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Run Apply/Verify in Windows PowerShell 5.1 inside the lab domain controller.'}
$machine=Get-CimInstance Win32_ComputerSystem
if($machine.DomainRole -notin @(4,5)){throw 'Run inside the lab domain controller, not the host workstation or a client VM.'}
if($machine.Model -notmatch '(?i)virtual|vmware|kvm|hvm'){throw 'This script is limited to a disposable virtual domain controller.'}
if(-not [string]::Equals($machine.Domain,$ExpectedDomain,[StringComparison]::OrdinalIgnoreCase)){throw 'The local DC domain does not match -ExpectedDomain. No AD discovery or changes performed.'}
Import-Module ActiveDirectory -ErrorAction Stop
Add-Type -AssemblyName System.DirectoryServices
$initialServer=if($machine.DNSHostName.EndsWith('.'+$machine.Domain,[StringComparison]::OrdinalIgnoreCase)){$machine.DNSHostName}else{$machine.DNSHostName+'.'+$machine.Domain}
$dc=Get-ADDomainController -Identity $machine.Name -Server $initialServer
if(-not [string]::Equals($dc.HostName,$initialServer,[StringComparison]::OrdinalIgnoreCase)){throw 'DC discovery resolved a different host than the local VM.'}
$script:server=$dc.HostName
$domain=Get-ADDomain -Server $script:server
if(-not [string]::Equals($domain.DNSRoot,$ExpectedDomain,[StringComparison]::OrdinalIgnoreCase)){throw "Domain mismatch. Expected '$ExpectedDomain', found '$($domain.DNSRoot)'. No changes made."}
$script:domainDn=$domain.DistinguishedName
$rootDse=Get-ADRootDSE -Server $script:server;$script:schemaDn=$rootDse.SchemaNamingContext
$rootDn='OU='+$rootName+','+$script:domainDn
# Search just once, fail closed on unowned name collisions. Pin every AD command to this DC.
$existing=@(Get-ADObject -LDAPFilter '(|(objectClass=organizationalUnit)(sAMAccountName=bht.*)(sAMAccountName=BH-Lab-*))' -SearchBase $script:domainDn -Server $script:server -Properties description,sAMAccountName,objectSid)
$root=@($existing|Where-Object DistinguishedName -EQ $rootDn)
$labId=$null
if($root.Count){
 if($root.Count -ne 1 -or $root[0].Description -notmatch '^BHTestLab:v1;LabId=([0-9a-f-]{36}); disposable validation fixture$'){throw 'An unowned BHTestLab OU already exists. Choose a fresh kit snapshot.'}
 $labId=$Matches[1]
}else{if($Mode -eq 'Verify'){throw 'No owned BHTestLab OU exists yet.'};$labId=[Guid]::NewGuid().ToString()}
foreach($object in $existing){
 if($object.sAMAccountName -and ($object.sAMAccountName -in @($plan.Users.Sam)+@($plan.Groups.Name))){
  if(-not $object.DistinguishedName.EndsWith(','+$rootDn,[StringComparison]::OrdinalIgnoreCase)){throw ('Account/group name collides outside the lab OU: '+$object.sAMAccountName)}
  Test-BHLabOwned $object $labId
  $plannedUser=@($plan.Users|Where-Object Sam -EQ $object.sAMAccountName)
  $expectedDn=if($plannedUser.Count){'CN='+$plannedUser[0].Sam+',OU='+$plannedUser[0].Container+','+$rootDn}else{'CN='+$object.sAMAccountName+',OU=Groups,'+$rootDn}
  if(-not [string]::Equals($object.DistinguishedName,$expectedDn,[StringComparison]::OrdinalIgnoreCase)){throw ('Owned principal was moved from its expected container: '+$object.sAMAccountName)}
 }
}
$knownSams=@{};foreach($object in $existing){if($object.sAMAccountName){$knownSams[[string]$object.sAMAccountName]=$true}}
$memberGuid=Get-BHLabSchemaGuid 'member';$keyGuid=Get-BHLabSchemaGuid 'msDS-KeyCredentialLink';$spnGuid=Get-BHLabSchemaGuid 'servicePrincipalName'
$targetComputer=$null
if($DelegationTargetComputer){
 $targetComputer=Get-ADComputer -Identity $DelegationTargetComputer -Server $script:server -Properties dNSHostName,userAccountControl,primaryGroupID
 if(($targetComputer.userAccountControl -band 8192) -ne 0 -or $targetComputer.primaryGroupID -in @(516,521)){throw 'The delegation target must be a disposable member computer, never a domain controller.'}
 if(-not $targetComputer.DNSHostName){throw 'The selected member computer has no DNS host name.'}
}
if(-not $OutputDirectory){$OutputDirectory=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'BHTestLab'}
$null=New-Item -ItemType Directory -Path $OutputDirectory -Force
$script:journal=New-Object 'Collections.Generic.List[object]'
$script:membershipCache=@{}
$manifestPath=Join-Path $OutputDirectory 'lab-manifest.json'
if($root.Count -and -not (Test-Path -LiteralPath $manifestPath)){throw 'An owned lab already exists. Reuse its original manifest output directory; existing elevated scenarios are never silently adopted with a different profile.'}
if(Test-Path -LiteralPath $manifestPath){
 $prior=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 if($prior.LabId -ne $labId -or $prior.DomainSid -ne $domain.DomainSID.Value){throw 'Output directory contains a manifest for another lab. Use its original directory or a fresh directory.'}
 if($prior.UserCount -ne $UserCount -or $prior.IncludeReplicationScenario -ne [bool]$IncludeReplicationScenario -or $prior.IncludeTier0Account -ne [bool]$IncludeTier0Account -or [string]$prior.DelegationTargetComputer -ne [string]$DelegationTargetComputer){throw 'Reuse the original profile flags and UserCount with this manifest. Optional elevated scenarios are never silently removed or hidden.'}
 foreach($entry in $prior.Journal){$script:journal.Add($entry)}
}
$script:manifest=[ordered]@{SchemaVersion=$schemaVersion;LabId=$labId;Domain=$domain.DNSRoot;DomainSid=$domain.DomainSID.Value;NetbiosName=$domain.NetBIOSName;DomainController=$script:server;RootOU=$rootDn;CreatedOrCheckedUtc=[DateTime]::UtcNow.ToString('o');Status='Checking';UserCount=$UserCount;IncludeReplicationScenario=[bool]$IncludeReplicationScenario;IncludeTier0Account=[bool]$IncludeTier0Account;DelegationTargetComputer=$DelegationTargetComputer;Principals=@();ExpectedRelationships=@();Journal=@();Notes=@('No passwords or secrets are exported.','No sessions are fabricated. Endpoint access/session scenarios require real lab VM configuration and logons.','No existing kit objects are moved, deleted, renamed or password-reset.','No automatic rollback: restore the pre-run lab VM checkpoints to reset the fixture.')}
try{
 if($Mode -eq 'Apply'){
  if(-not $PSCmdlet.ShouldProcess($ExpectedDomain,('Populate '+$rootDn+' with '+$plan.Users.Count+' users and '+$plan.Groups.Count+' groups; Replication='+[bool]$IncludeReplicationScenario+'; Tier0='+[bool]$IncludeTier0Account+'; MemberDelegation='+$DelegationTargetComputer))){return}
  $newRegular=@($plan.Users|Where-Object {$_.Sam -ne 'bht.tier0' -and -not $knownSams.ContainsKey($_.Sam)})
  $newTier0=$IncludeTier0Account -and -not $knownSams.ContainsKey('bht.tier0')
  if($newRegular.Count -and -not $LabPassword){$LabPassword=Read-Host 'Password for NEW nonprivileged lab users/services (must satisfy domain policy)' -AsSecureString}
  if($newTier0 -and -not $Tier0Password){$Tier0Password=Read-Host 'Separate password for the NEW privileged lab account bht.tier0' -AsSecureString}
  if(($newRegular.Count -and $LabPassword.Length -eq 0) -or ($newTier0 -and $Tier0Password.Length -eq 0)){throw 'Passwords for new lab accounts must not be empty.'}
  $script:manifest.Status='Applying';Save-BHLabState
  if(-not $root.Count){New-ADOrganizationalUnit -Name $rootName -Path $script:domainDn -Description (Get-BHLabDescription $labId) -ProtectedFromAccidentalDeletion $true -Server $script:server}
  foreach($container in $plan.Containers){$dn='OU='+$container+','+$rootDn;$found=Get-BHLabObject $dn;if($found){Test-BHLabOwned $found $labId}else{New-ADOrganizationalUnit -Name $container -Path $rootDn -Description (Get-BHLabDescription $labId) -ProtectedFromAccidentalDeletion $true -Server $script:server}}
  foreach($group in $plan.Groups){$dn='CN='+$group.Name+',OU=Groups,'+$rootDn;$found=Get-BHLabObject $dn;if($found){Test-BHLabOwned $found $labId}else{New-ADGroup -Name $group.Name -SamAccountName $group.Name -GroupScope Global -GroupCategory $group.Category -Path ('OU=Groups,'+$rootDn) -Description (Get-BHLabDescription $labId) -Server $script:server}}
  foreach($user in $plan.Users){
   $dn='CN='+$user.Sam+',OU='+$user.Container+','+$rootDn;$found=Get-BHLabObject $dn
   if($found){Test-BHLabOwned $found $labId;continue}
   $password=if($user.Sam -eq 'bht.tier0'){$Tier0Password}else{$LabPassword}
   New-ADUser -Name $user.Sam -DisplayName $user.Name -SamAccountName $user.Sam -UserPrincipalName ($user.Sam+'@'+$domain.DNSRoot) -Path ('OU='+$user.Container+','+$rootDn) -AccountPassword $password -Enabled $user.Enabled -ChangePasswordAtLogon $false -Description (Get-BHLabDescription $labId) -Server $script:server
  }
 }
 $principals=@{}
 foreach($user in $plan.Users){$u=Get-ADUser -Identity $user.Sam -Server $script:server -Properties description,Enabled,ServicePrincipalNames,msDS-AllowedToDelegateTo;Test-BHLabOwned $u $labId;$principals[$user.Sam]=$u}
 foreach($group in $plan.Groups){$g=Get-ADGroup -Identity $group.Name -Server $script:server -Properties description,GroupCategory;Test-BHLabOwned $g $labId;if([string]$g.GroupCategory -ne $group.Category){throw ('Unexpected group category: '+$group.Name)};$principals[$group.Name]=$g}
 $members=New-Object 'Collections.Generic.List[object]'
 foreach($pair in @(@('bht.maya','BH-Lab-Helpdesk'),@('bht.disabled','BH-Lab-Helpdesk'),@('bht.mail','BH-Lab-MailOnly'),@('bht.lucas','BH-Lab-RemoteUsers'))){$members.Add([pscustomobject]@{Source=$pair[0];Target=$pair[1]})}
 $departments=@('Finance','Engineering','Sales','Operations','HR','IT')
 for($i=1;$i -le $UserCount;$i++){$sam='bht.u'+$i.ToString('D5');$members.Add([pscustomobject]@{Source=$sam;Target='BH-Lab-Staff'});$members.Add([pscustomobject]@{Source=$sam;Target=('BH-Lab-'+$departments[($i-1)%6])});if($i -le 12){$members.Add([pscustomobject]@{Source=$sam;Target='BH-Lab-Helpdesk'})}}
 if($Mode -eq 'Apply'){foreach($pair in $members){Ensure-BHLabMember $principals[$pair.Target].DistinguishedName $principals[$pair.Source].DistinguishedName}}
 $rules=@(Get-BHLabExpectedRules $principals $memberGuid $keyGuid $spnGuid)
 if($IncludeReplicationScenario){
  foreach($guid in @('1131f6aa-9c07-11d1-f79f-00c04fc2dcd2','1131f6ad-9c07-11d1-f79f-00c04fc2dcd2','89e95b76-444d-4c62-991a-0facbeda640c')){
   $rules+=[pscustomobject]@{Source='bht.svcsync';Target=$domain.DNSRoot;TargetDn=$script:domainDn;Rule=(New-BHLabRule $principals['bht.svcsync'].SID 'ExtendedRight' ([Guid]$guid));Relationship='DCSync prerequisite'}
  }
 }
 if($targetComputer){$rbcdGuid=Get-BHLabSchemaGuid 'msDS-AllowedToActOnBehalfOfOtherIdentity';$rules+=[pscustomobject]@{Source='bht.lucas';Target=$targetComputer.Name;TargetDn=$targetComputer.DistinguishedName;Rule=(New-BHLabRule $principals['bht.lucas'].SID 'WriteProperty' $rbcdGuid);Relationship='AddAllowedToAct'}}
 if($Mode -eq 'Apply'){foreach($r in $rules){Ensure-BHLabRule $r.TargetDn $r.Rule}}
 $domainAdmins=Get-ADGroup -Identity ($domain.DomainSID.Value+'-512') -Server $script:server
 if($IncludeTier0Account -and $Mode -eq 'Apply'){Ensure-BHLabMember $domainAdmins.DistinguishedName $principals['bht.tier0'].DistinguishedName}
 if($targetComputer -and $Mode -eq 'Apply'){
  $service=$principals['bht.svclegacy'];$spn='HTTP/bh-lab-legacy.'+$domain.DNSRoot;$delegation='HOST/'+$targetComputer.DNSHostName
  if(@($service.ServicePrincipalNames) -notcontains $spn){Set-ADUser -Identity $service.DistinguishedName -ServicePrincipalNames @{Add=$spn} -Server $script:server}
  if(@($service.'msDS-AllowedToDelegateTo') -notcontains $delegation){Set-ADUser -Identity $service.DistinguishedName -Add @{'msDS-AllowedToDelegateTo'=$delegation} -Server $script:server}
 }
 $checks=New-Object 'Collections.Generic.List[object]'
 $verifiedMembers=@{}
 foreach($pair in $members){if(-not $verifiedMembers.ContainsKey($pair.Target)){$set=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);foreach($m in Get-ADGroupMember -Identity $principals[$pair.Target].DistinguishedName -Server $script:server){[void]$set.Add($m.DistinguishedName)};$verifiedMembers[$pair.Target]=$set};$checks.Add([pscustomobject]@{Kind='MemberOf';Source=$pair.Source;Target=$pair.Target;Passed=$verifiedMembers[$pair.Target].Contains($principals[$pair.Source].DistinguishedName)})}
 foreach($r in $rules){$entry=Open-BHLabEntry $r.TargetDn;try{$checks.Add([pscustomobject]@{Kind=$r.Relationship;Source=$r.Source;Target=$r.Target;Passed=(Test-BHLabRule $entry.ObjectSecurity $r.Rule)})}finally{$entry.Dispose()}}
 foreach($u in $plan.Users){$actual=Get-ADUser -Identity $u.Sam -Server $script:server -Properties Enabled;$checks.Add([pscustomobject]@{Kind='EnabledState';Source=$u.Sam;Target=[string]$u.Enabled;Passed=($actual.Enabled -eq $u.Enabled)})}
 if($IncludeTier0Account){$found=@(Get-ADGroupMember -Identity $domainAdmins.DistinguishedName -Server $script:server | Where-Object DistinguishedName -EQ $principals['bht.tier0'].DistinguishedName);$checks.Add([pscustomobject]@{Kind='Tier0Membership';Source='bht.tier0';Target='Domain Admins';Passed=($found.Count -eq 1)})}
 if($targetComputer){$service=Get-ADUser -Identity 'bht.svclegacy' -Server $script:server -Properties ServicePrincipalNames,msDS-AllowedToDelegateTo;$checks.Add([pscustomobject]@{Kind='AllowedToDelegate';Source='bht.svclegacy';Target=$targetComputer.Name;Passed=(@($service.'msDS-AllowedToDelegateTo') -contains ('HOST/'+$targetComputer.DNSHostName))})}
 $script:manifest.Principals=@(foreach($key in @($principals.Keys|Sort-Object)){[ordered]@{Sam=$key;DN=$principals[$key].DistinguishedName;SID=$principals[$key].SID.Value}})
 $script:manifest.ExpectedRelationships=@(foreach($r in $rules){[ordered]@{Source=$r.Source;Target=$r.Target;Type=$r.Relationship}})
 $script:manifest['KnownCollectorGapChecks']=@('Attribute-specific AddKeyCredentialLink, WriteSPN and AddAllowedToAct ACEs are deliberately configured as coverage probes. The current native ACL parser does not derive these labels. Directory verification does not assert collector completeness.')
 $failed=@($checks|Where-Object {-not $_.Passed})
 Write-BHLabJson ([ordered]@{Domain=$domain.DNSRoot;LabId=$labId;Mode=$Mode;VerifiedUtc=[DateTime]::UtcNow.ToString('o');Passed=($failed.Count -eq 0);Checks=$checks.ToArray();NotVerified=@('Successful exploitation or effective access across complete tokens','Real endpoint group membership and interactive sessions','Collector output; run BHTest against the lab and compare with this manifest')}) (Join-Path $OutputDirectory 'verification.json')
 $script:manifest.Status=if($failed.Count){'VerificationFailed'}else{'Verified'};Save-BHLabState
 if($failed.Count){throw ($failed.Count.ToString()+' lab verification checks failed. Inspect verification.json; reruns never reset existing passwords or delete accounts.')}
 Write-Host ('Verified '+$checks.Count+' directory checks. Lab OU: '+$rootDn)
 Write-Host ('Full-domain scan base: '+$script:domainDn+' (needed for paths to domain/Domain Admins outside the fixture OU).')
 Write-Host ('Custom path: '+$principals['bht.maya'].SID.Value+' -> '+$principals['BH-Lab-PathTarget'].SID.Value)
 if($IncludeTier0Account){Write-Host 'bht.tier0 is a real Domain Admin in this disposable lab. Its protected-object ACL is not modified. Log it on interactively to the selected lab endpoint to observe a real session.'}
 if($IncludeReplicationScenario){Write-Host 'The lab sync account has real domain replication permissions. Keep this scenario confined to the isolated evaluation lab.'}
}catch{
 $failure=$_
 if($Mode -eq 'Apply'){try{$script:manifest.Status='PartialOrFailed';$script:manifest['Failure']=$failure.Exception.Message;Save-BHLabState}catch{Write-Warning 'Could not write failure state; preserve this error output and restore the lab checkpoints if necessary.'}}
 throw $failure
}
