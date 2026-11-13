#Requires -Version 5.1
<# Local-only companion. Run inside a selected, domain-joined LAB MEMBER VM. #>
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param(
 [Parameter(Mandatory)][string]$ManifestPath,
 [ValidateSet('Plan','Apply','Verify')][string]$Mode='Plan',
 [string]$ExpectedDomain,[string]$ExpectedComputer,[switch]$LabOnly,
 [switch]$EnableAdminPath,[switch]$EnableRemoteMemberships,
 [string]$OutputDirectory
)
$ErrorActionPreference='Stop';Set-StrictMode -Version 2
$manifest=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
if($manifest.SchemaVersion -ne 1 -or $manifest.LabId -notmatch '^[0-9a-f-]{36}$' -or $manifest.Status -ne 'Verified'){throw 'Use a successfully verified lab manifest from Setup-BHTestADLab.ps1.'}
$assignments=New-Object 'Collections.Generic.List[object]'
if($EnableAdminPath){$assignments.Add([pscustomobject]@{LocalSid='S-1-5-32-544';DomainGroup='BH-Lab-AppAdmins';Relationship='AdminTo'})}
if($EnableRemoteMemberships){
 $assignments.Add([pscustomobject]@{LocalSid='S-1-5-32-555';DomainGroup='BH-Lab-RemoteUsers';Relationship='CanRDP'})
 $assignments.Add([pscustomobject]@{LocalSid='S-1-5-32-580';DomainGroup='BH-Lab-RemoteUsers';Relationship='CanPSRemote'})
}
if($Mode -eq 'Plan' -or $WhatIfPreference){[ordered]@{Mode='Offline plan';LabId=$manifest.LabId;Domain=$manifest.Domain;ExpectedComputer=$ExpectedComputer;Assignments=@($assignments.ToArray());ChangesFirewall=$false;EnablesRemoting=$false;CreatesSessions=$false}|ConvertTo-Json -Depth 6;return}
if(-not $ExpectedDomain -or -not $ExpectedComputer){throw 'Apply/Verify requires the exact -ExpectedDomain and -ExpectedComputer.'}
if($Mode -eq 'Apply' -and -not $LabOnly){throw 'Apply requires -LabOnly for this disposable VM.'}
if(-not $assignments.Count){throw 'Select -EnableAdminPath and/or -EnableRemoteMemberships.'}
if(-not [Environment]::Is64BitProcess){throw 'Run in 64-bit Windows PowerShell.'}
$machine=Get-CimInstance Win32_ComputerSystem
if($machine.DomainRole -in @(4,5)){throw 'Never run the endpoint helper on a domain controller.'}
if(-not $machine.PartOfDomain -or $machine.Model -notmatch '(?i)virtual|vmware|kvm|hvm'){throw 'Use a domain-joined disposable member VM.'}
if(-not [string]::Equals($env:COMPUTERNAME,$ExpectedComputer,[StringComparison]::OrdinalIgnoreCase)){throw 'Computer name mismatch.'}
if(-not [string]::Equals($machine.Domain,$ExpectedDomain,[StringComparison]::OrdinalIgnoreCase) -or -not [string]::Equals($manifest.Domain,$ExpectedDomain,[StringComparison]::OrdinalIgnoreCase)){throw 'Domain does not match the lab manifest and this VM.'}
if($manifest.DomainController -notmatch '\A[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z'){throw 'Invalid controller name in manifest.'}
if($Mode -eq 'Apply' -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Use an elevated PowerShell window inside the member VM.'}
Add-Type -AssemblyName System.DirectoryServices
function Open-LabDirectory([string]$dn){$auth=[DirectoryServices.AuthenticationTypes]::Secure -bor [DirectoryServices.AuthenticationTypes]::Signing -bor [DirectoryServices.AuthenticationTypes]::Sealing;New-Object DirectoryServices.DirectoryEntry(('LDAP://'+$manifest.DomainController+'/'+$dn),$null,$null,$auth)}
$root=Open-LabDirectory 'RootDSE'
try{$attributes=$root.get_Properties();$domainDn=[string]$attributes['defaultNamingContext'].Value}finally{$root.Dispose()}
if(-not [string]::Equals($manifest.RootOU,('OU=BHTestLab,'+$domainDn),[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest root OU does not match the actual directory.'}
$domainEntry=Open-LabDirectory $domainDn
try{$attributes=$domainEntry.get_Properties();$domainSid=New-Object Security.Principal.SecurityIdentifier($attributes['objectSid'].Value,0);if($domainSid.Value -ne $manifest.DomainSid){throw 'Domain SID mismatch.'}}finally{$domainEntry.Dispose()}
$owned=Open-LabDirectory $manifest.RootOU
$ownerTag='BHTestLab:v1;LabId='+$manifest.LabId+'; disposable validation fixture'
try{$attributes=$owned.get_Properties();if([string]$attributes['description'].Value -ne $ownerTag){throw 'Lab OU ownership marker does not match.'}}finally{$owned.Dispose()}
$resolved=New-Object 'Collections.Generic.List[object]'
foreach($a in $assignments){
 $principal=@($manifest.Principals|Where-Object Sam -EQ $a.DomainGroup)
 if($principal.Count -ne 1 -or -not $principal[0].DN.EndsWith(','+$manifest.RootOU,[StringComparison]::OrdinalIgnoreCase)){throw 'Selected lab group is not uniquely owned by this fixture.'}
 $entry=Open-LabDirectory $principal[0].DN
 try{$attributes=$entry.get_Properties();$sid=New-Object Security.Principal.SecurityIdentifier($attributes['objectSid'].Value,0);if($sid.Value -ne $principal[0].SID -or [string]$attributes['description'].Value -ne $ownerTag){throw 'Lab group identity/ownership mismatch.'}}finally{$entry.Dispose()}
 $local=Get-LocalGroup -SID $a.LocalSid
 $resolved.Add([pscustomobject]@{LocalGroup=$local.Name;DomainGroup=$a.DomainGroup;SID=$sid;Relationship=$a.Relationship})
}
if($Mode -eq 'Apply'){
 $changes=($resolved|ForEach-Object {$_.DomainGroup+' -> '+$_.LocalGroup}) -join '; '
 if(-not $PSCmdlet.ShouldProcess($ExpectedComputer,('Add local memberships: '+$changes))){return}
 foreach($a in $resolved){$present=@(Get-LocalGroupMember -Group $a.LocalGroup|Where-Object {$_.SID -and $_.SID.Value -eq $a.SID.Value});if(-not $present.Count){Add-LocalGroupMember -Group $a.LocalGroup -Member ($manifest.NetbiosName+'\'+$a.DomainGroup)}}
}
$checks=@(foreach($a in $resolved){$present=@(Get-LocalGroupMember -Group $a.LocalGroup|Where-Object {$_.SID -and $_.SID.Value -eq $a.SID.Value});[ordered]@{Computer=$ExpectedComputer;LocalGroup=$a.LocalGroup;DomainGroup=$a.DomainGroup;ExpectedRelationship=$a.Relationship;Passed=($present.Count -eq 1)}})
$result=[ordered]@{Mode=$Mode;LabId=$manifest.LabId;Domain=$ExpectedDomain;Computer=$ExpectedComputer;VerifiedUtc=[DateTime]::UtcNow.ToString('o');Passed=(@($checks|Where-Object {-not $_.Passed}).Count -eq 0);Checks=$checks;NotChanged=@('Firewall rules','Remote Desktop settings','WinRM services/configuration','AD computer location','Existing local group members');SessionInstructions='To test HasSession, sign in INTERACTIVELY inside this VM using a real lab user. For a Domain Admin path, use the separately created bht.tier0 account only in this isolated lab. Keep that lab session active while collecting. No logon is performed by this script.'}
if($OutputDirectory){$null=New-Item -ItemType Directory -Path $OutputDirectory -Force;$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputDirectory 'endpoint-verification.json') -Encoding UTF8}
$result|ConvertTo-Json -Depth 8
if(-not $result.Passed){throw 'One or more endpoint membership checks failed.'}
