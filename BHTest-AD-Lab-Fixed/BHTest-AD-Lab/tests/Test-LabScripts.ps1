#Requires -Version 5.1
[CmdletBinding()]
param([string]$BHTestProject)
$ErrorActionPreference='Stop'
$package=Split-Path $PSScriptRoot -Parent
if(-not $BHTestProject){$BHTestProject=Join-Path (Split-Path $package -Parent) 'BHTest'}
$setup=Join-Path $package 'Setup-BHTestADLab.ps1';$endpoint=Join-Path $package 'Configure-BHTestLabEndpoint.ps1'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('bh-lab-scripts-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $scratch
function Assert($condition,$message){if(-not $condition){throw $message}}
function MustReject([scriptblock]$action,[string]$pattern){$rejected=$false;try{& $action|Out-Null}catch{$rejected=$_.Exception.Message -like $pattern};Assert $rejected ('Expected refusal: '+$pattern)}
try{
 foreach($file in @($setup,$endpoint)){
  $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
  Assert (-not $errors) ('Syntax error: '+$file)
  $commands=@($ast.FindAll({param($a)$a -is [Management.Automation.Language.CommandAst]},$true)|ForEach-Object GetCommandName)
  Assert (-not @($commands|Where-Object {$_ -match '^(Remove-AD|Move-AD|Install-ADDS|Set-ADAccountPassword|Set-ADDefaultDomainPasswordPolicy|Set-ExecutionPolicy|Disable-NetFirewall|Set-NetFirewall)'}).Count) 'Unexpected destructive/domain-policy command.'
 }
 $plan=& $setup -Mode Plan -ExpectedDomain lab.example.test -OutputDirectory $scratch|ConvertFrom-Json
 Assert ($plan.Users -eq 134 -and $plan.Groups -eq 12 -and -not $plan.LiveADValidated) 'Default offline plan is wrong.'
 $preview=& $setup -Mode Apply -ExpectedDomain lab.example.test -IncludeTier0Account -IncludeReplicationScenario -WhatIf|ConvertFrom-Json
 Assert ($preview.Users -eq 135 -and $preview.IncludeTier0Account -and $preview.IncludeReplicationScenario -and -not $preview.LiveADValidated) 'WhatIf did not stay offline.'
 MustReject {& $setup -Mode Apply -ExpectedDomain lab.example.test} '*requires -LabOnly*'
 MustReject {& $setup -Mode Verify} '*requires -ExpectedDomain*'
 $fake=[ordered]@{SchemaVersion=1;LabId=[Guid]::NewGuid().ToString();Status='Verified';Domain='lab.example.test';DomainSid='S-1-5-21-91-92-93';NetbiosName='LAB';DomainController='dc.lab.example.test';RootOU='OU=BHTestLab,DC=lab,DC=example,DC=test';Principals=@()}
 $manifest=Join-Path $scratch 'manifest.json';$fake|ConvertTo-Json|Set-Content -LiteralPath $manifest -Encoding UTF8
 $ep=& $endpoint -ManifestPath $manifest -Mode Plan -ExpectedComputer CLIENT1 -EnableAdminPath -EnableRemoteMemberships|ConvertFrom-Json
 Assert ($ep.Assignments.Count -eq 3 -and -not $ep.ChangesFirewall -and -not $ep.EnablesRemoting -and -not $ep.CreatesSessions) 'Endpoint preview implies changes outside its local-group scope.'
 MustReject {& $endpoint -ManifestPath $manifest -Mode Apply -ExpectedDomain lab.example.test -ExpectedComputer CLIENT1 -EnableAdminPath} '*requires -LabOnly*'
 MustReject {& $endpoint -ManifestPath $manifest -Mode Verify} '*requires the exact*'

 # Import only pure/helper function definitions; never execute the live setup body.
 Add-Type -AssemblyName System.DirectoryServices
 Assert ([DirectoryServices.DirectoryEntry].GetMethod('get_Options').ReturnType -eq [DirectoryServices.DirectoryEntryConfiguration]) 'CLR DirectoryEntry getter is unavailable.'
 Assert ([DirectoryServices.DirectoryEntryConfiguration].GetProperty('SecurityMasks').CanWrite) 'CLR SecurityMasks setter is unavailable.'
 $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($setup,[ref]$tokens,[ref]$errors)
 foreach($definition in $ast.FindAll({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){Invoke-Expression $definition.Extent.Text}
 $IncludeTier0Account=$false;$p=Get-BHLabPlan 120
 $names=@($p.Users.Sam)+@($p.Groups.Name)
 Assert (($names|Sort-Object -Unique).Count -eq $names.Count) 'Principal names collide.'
 Assert (-not @($names|Where-Object {$_.Length -gt 20}).Count) 'sAMAccountName exceeds 20 characters.'
 Assert (@($p.Users|Where-Object {-not $_.Enabled}).Count -ge 3) 'Disabled controls/background accounts missing.'
 $id=[Guid]::NewGuid().ToString();Test-BHLabOwned ([pscustomobject]@{Description=(Get-BHLabDescription $id);DistinguishedName='CN=owned'}) $id
 MustReject {Test-BHLabOwned ([pscustomobject]@{Description='kit object';DistinguishedName='CN=kit'}) $id} '*unowned object*'
 # Reproduce ADSI property shadowing without a live LDAP connection.
 $script:configuration=[pscustomobject]@{SecurityMasks=[DirectoryServices.SecurityMasks]::None}
 $script:shadowEntry=[pscustomobject]@{Options=@('LDAP attribute value');Refreshed=$false;Disposed=$false}
 $script:shadowEntry|Add-Member ScriptMethod get_Options {$script:configuration}
 $script:shadowEntry|Add-Member ScriptMethod RefreshCache {param($properties)$this.Refreshed=$true}
 $script:shadowEntry|Add-Member ScriptMethod Dispose {$this.Disposed=$true}
 function New-BHLabDirectoryEntry([string]$dn){$script:shadowEntry}
 $opened=Open-BHLabEntry 'CN=test'
 Assert ($script:configuration.SecurityMasks -eq [DirectoryServices.SecurityMasks]::Dacl -and $opened.Refreshed -and -not $opened.Disposed) 'ADSI shadowed Options broke the CLR configuration path.'

 $principals=@{};$i=1000
 foreach($name in $names){$principals[$name]=[pscustomobject]@{SID=(New-Object Security.Principal.SecurityIdentifier('S-1-5-21-91-92-93-'+$i));DistinguishedName=('CN='+$name+',OU=BHTestLab,DC=lab,DC=example,DC=test')};$i++}
 $member=[Guid]'bf9679c0-0de6-11d0-a285-00aa003049e2';$key=[Guid]'5b47d60f-6090-40b2-9f37-2a4de88f3063';$spn=[Guid]'f3a64788-5306-11d1-a9c5-0000f80367c1'
 $rules=@(Get-BHLabExpectedRules $principals $member $key $spn)
 Assert ($rules.Count -eq 12) 'Core fixture rule count changed unexpectedly.'
 Import-Module (Join-Path $BHTestProject 'src\BH.Core.psm1') -DisableNameChecking
 Initialize-BHNative
 $supported=@('ForceChangePassword','GenericWrite','WriteDacl','AddMember','GenericAll','WriteOwner')
 foreach($r in $rules){
  $security=New-Object DirectoryServices.ActiveDirectorySecurity;$security.AddAccessRule($r.Rule)
  Assert (Test-BHLabRule $security $r.Rule) ('Exact ACE read-back failed: '+$r.Relationship)
  $kind=if($r.Target -like 'BH-Lab-*'){'group'}else{'user'}
  $parsed=[BH.Native.SecurityDescriptor]::Parse($security.GetSecurityDescriptorBinaryForm(),$kind)
  if($r.Relationship -in $supported){Assert (@($parsed.Aces|Where-Object {$_.EdgeType -eq $r.Relationship -and $_.TrusteeSid -eq $r.Rule.IdentityReference.Value}).Count -eq 1) ('Collector cannot parse expected fixture relationship: '+$r.Relationship)}
  if($r.Relationship -in @('AddKeyCredentialLink','WriteSPN')){Assert (-not @($parsed.Aces|Where-Object EdgeType -EQ $r.Relationship).Count) 'Update the documented collector-gap expectations.'}
 }
 $denies=@($rules|Where-Object Source -EQ 'bht.denied');$sd=New-Object DirectoryServices.ActiveDirectorySecurity
 foreach($r in $denies){$sd.AddAccessRule($r.Rule)}
 $parsed=[BH.Native.SecurityDescriptor]::Parse($sd.GetSecurityDescriptorBinaryForm(),'group')
 Assert (-not @($parsed.Aces|Where-Object EdgeType -EQ AddMember).Count) 'Explicit deny control still emits AddMember.'
 $sd=New-Object DirectoryServices.ActiveDirectorySecurity
 foreach($guid in @('1131f6aa-9c07-11d1-f79f-00c04fc2dcd2','1131f6ad-9c07-11d1-f79f-00c04fc2dcd2','89e95b76-444d-4c62-991a-0facbeda640c')){$sd.AddAccessRule((New-BHLabRule $principals['bht.svcsync'].SID 'ExtendedRight' ([Guid]$guid)))}
 $parsed=[BH.Native.SecurityDescriptor]::Parse($sd.GetSecurityDescriptorBinaryForm(),'domain')
 Assert (@($parsed.Aces|Where-Object EdgeType -EQ DCSync).Count -eq 1) 'Replication ACE combination does not produce DCSync.'

 # In-memory directory adapter: same helper logic; no LDAP calls or AD writes.
 $script:journal=New-Object 'Collections.Generic.List[object]';$script:server='dc.lab.example.test'
 function Save-BHLabState {}
 $sd=New-Object DirectoryServices.ActiveDirectorySecurity
 $unrelated=New-BHLabRule (New-Object Security.Principal.SecurityIdentifier('S-1-5-21-91-92-93-9999')) 'GenericRead' ([Guid]::Empty)
 $sd.AddAccessRule($unrelated)
 $script:memoryEntry=[pscustomobject]@{ObjectSecurity=$sd;Commits=0}
 $script:memoryEntry|Add-Member ScriptMethod CommitChanges {$this.Commits++}
 $script:memoryEntry|Add-Member ScriptMethod Dispose {}
 $script:memoryEntry|Add-Member ScriptMethod get_ObjectSecurity {$this.ObjectSecurity}
 $script:memoryEntry|Add-Member ScriptMethod set_ObjectSecurity {param($security)$this.ObjectSecurity=$security}
 function Open-BHLabEntry([string]$dn){$script:memoryEntry}
 Ensure-BHLabRule 'CN=test' $rules[0].Rule;Ensure-BHLabRule 'CN=test' $rules[0].Rule
 Assert ($script:memoryEntry.Commits -eq 1 -and (Test-BHLabRule $script:memoryEntry.ObjectSecurity $unrelated)) 'ACL append/idempotence replaced unrelated rights or duplicated the write.'
 $script:membershipCache=@{};$script:queries=0;$script:adds=0
 $script:backing=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase);[void]$script:backing.Add('CN=KitUser')
 function Get-ADGroupMember($Identity,$Server){$script:queries++;foreach($dn in $script:backing){[pscustomobject]@{DistinguishedName=$dn}}}
 function Add-ADGroupMember($Identity,$Members,$Server){$script:adds++;[void]$script:backing.Add($Members)}
 Ensure-BHLabMember 'CN=group' 'CN=LabUser';Ensure-BHLabMember 'CN=group' 'CN=LabUser';Ensure-BHLabMember 'CN=group' 'CN=KitUser'
 Assert ($script:queries -eq 1 -and $script:adds -eq 1 -and $script:backing.Count -eq 2) 'Membership handling duplicates changes or replaces existing members.'
 $script:domainDn='DC=lab,DC=example,DC=test';$script:filterSeen=''
 function Get-ADObject($LDAPFilter,$SearchBase,$SearchScope,$Server,$Properties){$script:filterSeen=$LDAPFilter;Assert ($Server -eq 'dc.lab.example.test') 'Object lookup escaped the pinned DC.'}
 $null=Get-BHLabObject 'CN=Name (Test)*,OU=BHTestLab,DC=lab,DC=example,DC=test'
 Assert ($script:filterSeen -eq '(distinguishedName=CN=Name \28Test\29\2a,OU=BHTestLab,DC=lab,DC=example,DC=test)') 'DN lookup did not escape its LDAP filter correctly.'
 Write-Host 'PASS: script parsing; offline plans/WhatIf; refusal guards; unique account names; real .NET ACL serialization; six native relationship types; DCSync; deny control; known parser gaps; ACL/member preservation and idempotence. No live AD execution.'
}finally{
 $resolved=[IO.Path]::GetFullPath($scratch);if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
