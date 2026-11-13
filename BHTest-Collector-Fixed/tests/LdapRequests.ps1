#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $project 'src/BH.Core.psm1') -DisableNameChecking
Import-BHLdapAssemblies
$module=Get-Module BH.Core
function Assert($ok,$message){if(-not $ok){throw $message}}
function Entry([hashtable]$values){
 $type=[DirectoryServices.Protocols.SearchResultAttributeCollection];$attributes=[Activator]::CreateInstance($type,$true)
 $add=$type.GetMethods([Reflection.BindingFlags]'Instance,NonPublic')|Where-Object {$_.Name -eq 'Add' -and $_.GetParameters().Length -eq 2}|Select-Object -First 1
 foreach($key in $values.Keys){$a=[DirectoryServices.Protocols.DirectoryAttribute]::new();$a.Name=$key;foreach($value in $values[$key]){[void]$a.Add([string]$value)};[void]$add.Invoke($attributes,@($key,$a))}
 [pscustomobject]@{Attributes=$attributes;DistinguishedName='CN=Test,DC=corp,DC=contoso,DC=com'}
}
function MockRoot([hashtable]$available){
 $c=[pscustomobject]@{Available=$available;Requests=[Collections.Generic.List[object]]::new()}
 $c|Add-Member ScriptMethod SendRequest {
  param($request)
  $this.Requests.Add($request)
  $selected=@{};foreach($name in $request.Attributes){if($this.Available.ContainsKey($name)){$selected[$name]=$this.Available[$name]}}
  [pscustomobject]@{Entries=@(Entry $selected)}
 };$c
}
$domain='DC=corp,DC=contoso,DC=com';$configuration='CN=Configuration,'+$domain
$c=MockRoot @{defaultNamingContext=$domain;configurationNamingContext=$configuration}
$result=& $module {param($connection)Get-BHDefaultNamingContext $connection} $c
Assert ($result.DefaultNamingContext -eq $domain -and $result.ConfigurationNamingContext -eq $configuration) 'RootDSE lost the required or optional context.'
Assert (($c.Requests[0].Attributes -join '|') -eq 'defaultNamingContext|configurationNamingContext') 'RootDSE requested a joined/malformed attribute name.'
Assert ($c.Requests[0].DistinguishedName -eq '' -and $c.Requests[0].Scope -eq [DirectoryServices.Protocols.SearchScope]::Base) 'RootDSE search scope changed.'
$c=MockRoot @{defaultNamingContext=$domain};$result=& $module {param($connection)Get-BHDefaultNamingContext $connection} $c
Assert ($result.DefaultNamingContext -eq $domain -and $null -eq $result.ConfigurationNamingContext) 'Optional configuration context incorrectly became mandatory.'
$rejected=$false;try{$c=MockRoot @{configurationNamingContext=$configuration};& $module {param($connection)Get-BHDefaultNamingContext $connection} $c|Out-Null}catch{$rejected=$_.Exception.Message -like '*Returned attributes:*configurationNamingContext*'}
Assert $rejected 'Missing required context did not provide returned attribute names.'
$rejected=$false;try{$c=MockRoot @{defaultNamingContext=' '};& $module {param($connection)Get-BHDefaultNamingContext $connection} $c|Out-Null}catch{$rejected=$_.Exception.Message -like '*empty defaultNamingContext*'}
Assert $rejected 'Empty domain context was accepted.'
# Actual public paging path, with request inspection and a real paging control.
$ctor=[DirectoryServices.Protocols.PageResultResponseControl].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0]
$last=$ctor.Invoke(@(1,[byte[]]@(),$false,[byte[]]@()))
$c=[pscustomobject]@{Requests=[Collections.Generic.List[object]]::new();Disposed=$false;Response=[pscustomobject]@{Entries=@('one');Controls=@($last)}}
$c|Add-Member ScriptMethod SendRequest {param($request)$this.Requests.Add($request);$this.Response}
$c|Add-Member ScriptMethod Dispose {$this.Disposed=$true}
$attrs=[string[]]@('distinguishedName','name','objectClass','objectSid','memberOf','nTSecurityDescriptor')
$factory={$c}.GetNewClosure();$rows=@(Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn $domain -Attributes $attrs)
Assert ($rows.Count -eq 1 -and $c.Disposed) 'Paged search did not finish and dispose its connection.'
Assert (($c.Requests[0].Attributes -join '|') -eq ($attrs -join '|')) 'Collection request collapsed the attribute list.'
Assert (@($c.Requests[0].Controls|Where-Object {$_ -is [DirectoryServices.Protocols.SecurityDescriptorFlagControl]}).Count -eq 1) 'ACL collection control was lost.'
# Exercise each constructor, including standalone probe/warmup runspace expressions,
# without connecting to LDAP. Test values bind their real parameter positions.
$source=Join-Path $project 'src/BH.Core.psm1';$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
Assert (-not $errors) 'Core module contains parser errors.'
$constructors=@($ast.FindAll({param($a)$a -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $a.Static -and $a.Expression -is [Management.Automation.Language.TypeExpressionAst] -and $a.Expression.TypeName.FullName -eq 'System.DirectoryServices.Protocols.SearchRequest'},$true))
Assert ($constructors.Count -eq 7) 'Search request call sites changed; extend request coverage.'
$root=$BaseDn=$Dn=$domain;$Filter='(objectClass=*)';$Scope=[DirectoryServices.Protocols.SearchScope]::Subtree;$Attributes=$attrs;$wantedRange='memberOf;range=1500-*'
$naming=[pscustomobject]@{DefaultNamingContext=$domain};$parts=@('(objectSid=\01\02)')
foreach($constructor in $constructors){$request=Invoke-Expression $constructor.Extent.Text;Assert (-not @($request.Attributes|Where-Object {$_ -match '\s'}).Count) 'An attribute name contains joined whitespace.'}
Assert (-not ([IO.File]::ReadAllText($source) -match 'New-Object\s+System\.DirectoryServices\.Protocols\.SearchRequest')) 'Unsafe SearchRequest constructor syntax remains.'
Write-Host 'PASS: RootDSE contexts and errors; individual collection attributes; paging/ACL control; all seven search constructors including probe, warmup, ranged membership, domain SID and external lookup. No live LDAP connections.'
