# GUI discovery is read-only and runs in a background job. Cache only DNS names.
function Get-BHDcAttribute {
    param($Entry,[string]$Name)
    if($Entry.Attributes.Contains($Name) -and $Entry.Attributes[$Name].Count){return [string]$Entry.Attributes[$Name].GetValues([string])[0]}
}
function ConvertTo-BHDcHost {
    param([string]$Name)
    $value=($Name -replace '^\\\\','').Trim().TrimEnd('.').ToLowerInvariant()
    if($value -match '^(?=.{1,253}$)[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$'){return $value}
}
function Test-BHSameDc {
    param([string]$First,[string]$Second)
    $a=ConvertTo-BHDcHost $First;$b=ConvertTo-BHDcHost $Second
    return $a -and $b -and ($a -eq $b -or (-not ($a.Contains('.') -and $b.Contains('.')) -and $a.Split('.')[0] -eq $b.Split('.')[0]))
}
function Read-BHDcCache {
    param([string]$Path,[string]$Domain)
    try{
        $data=Get-Content -LiteralPath $Path -Raw -ErrorAction Stop|ConvertFrom-Json
        if(-not $Domain -or $data.Version -ne 1 -or $data.Domain -ne $Domain){return}
        @($data.Hosts)|ForEach-Object {ConvertTo-BHDcHost $_}|Where-Object {$_ -and $_.EndsWith('.'+$Domain,[StringComparison]::OrdinalIgnoreCase)}|Sort-Object -Unique
    }catch{}
}
function Save-BHDcCache {
    param([string]$Path,[string]$Domain,[string[]]$Hosts)
    if(-not $Domain){return}
    $names=@($Hosts|ForEach-Object {ConvertTo-BHDcHost $_}|Where-Object {$_ -and $_.EndsWith('.'+$Domain,[StringComparison]::OrdinalIgnoreCase)}|Sort-Object -Unique)
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force|Out-Null
        [ordered]@{Version=1;Domain=$Domain;UpdatedUtc=[DateTime]::UtcNow.ToString('o');Hosts=$names}|ConvertTo-Json|Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $Path -Force
    }finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
}
function Get-BHDcInventory {
    param([string]$Server,[string]$Domain,[pscredential]$Credential)
    $connection=New-BHLdapConnectionBounded -Server $Server -Credential $Credential -ConnectTimeoutSeconds 5 -OperationTimeoutSeconds 5
    try{
        $request=[DirectoryServices.Protocols.SearchRequest]::new('', '(objectClass=*)',[DirectoryServices.Protocols.SearchScope]::Base,[string[]]@('defaultNamingContext','dnsHostName'))
        $response=$connection.SendRequest($request)
        if($response.Entries.Count -ne 1){throw 'No domain RootDSE returned.'}
        $base=Get-BHDcAttribute $response.Entries[0] 'defaultNamingContext'
        $hostName=ConvertTo-BHDcHost (Get-BHDcAttribute $response.Entries[0] 'dnsHostName')
        $actualDomain=(($base -split ','|Where-Object {$_ -like 'DC=*'}|ForEach-Object {$_.Substring(3)}) -join '.').ToLowerInvariant()
        if(-not $hostName -or -not $base -or $actualDomain -ne $Domain){throw 'The server does not identify as a DC in the current logon domain.'}
        $hosts=[Collections.Generic.List[string]]::new();$hosts.Add($hostName)
        $complete=$false;$errorText=$null
        try{
            $request=[DirectoryServices.Protocols.SearchRequest]::new($base,'(&(objectCategory=computer)(userAccountControl:1.2.840.113556.1.4.803:=8192)(dNSHostName=*))',[DirectoryServices.Protocols.SearchScope]::Subtree,[string[]]@('dNSHostName'))
            $page=[DirectoryServices.Protocols.PageResultRequestControl]::new(500);[void]$request.Controls.Add($page)
            do{
                $response=$connection.SendRequest($request)
                foreach($entry in $response.Entries){$name=ConvertTo-BHDcHost (Get-BHDcAttribute $entry 'dNSHostName');if($name -and $name.EndsWith('.'+$Domain,[StringComparison]::OrdinalIgnoreCase)){$hosts.Add($name)}}
                $cookie=@($response.Controls|Where-Object {$_ -is [DirectoryServices.Protocols.PageResultResponseControl]})
                $page.Cookie=if($cookie.Count){$cookie[0].Cookie}else{[byte[]]@()}
            }while($page.Cookie.Length)
            $complete=$true
        }catch{$errorText=$_.Exception.Message}
        [pscustomobject]@{Server=$hostName;Hosts=@($hosts|Sort-Object -Unique);Complete=$complete;Error=$errorText}
    }finally{$connection.Dispose()}
}
function Get-BHDcDiscovery {
    param([string]$LogonServer,[string]$Domain,[string[]]$CachedHosts=@(),[scriptblock]$Query)
    $primary=ConvertTo-BHDcHost $LogonServer
    $selected=$null;$complete=$false;$hosts=@($CachedHosts);$errorText=$null
    if(-not $Domain){return [pscustomobject]@{Selected=$null;Hosts=@();Complete=$false;Error='No Windows logon domain. Enter a DC manually.'}}
    if(-not $Query){$Query={param($server,$domain) Get-BHDcInventory -Server $server -Domain $domain}}
    $sources=@($primary)+@($CachedHosts|Where-Object {-not (Test-BHSameDc $_ $primary)}|Select-Object -First 2)
    foreach($server in @($sources|Where-Object {$_}|Select-Object -Unique)){
        try{
            $result=& $Query $server $Domain
            if(Test-BHSameDc $server $primary){$selected=$result.Server}
            $hosts=if($result.Complete){@($result.Hosts)}else{@($hosts)+@($result.Hosts)}
            $errorText=$result.Error;$complete=[bool]$result.Complete
            if($complete){break}
        }catch{$errorText=$_.Exception.Message}
    }
    $hosts=@($hosts|ForEach-Object {ConvertTo-BHDcHost $_}|Where-Object {$_ -and $_.EndsWith('.'+$Domain,[StringComparison]::OrdinalIgnoreCase) -and ($selected -or -not (Test-BHSameDc $_ $primary))}|Sort-Object -Unique)
    [pscustomobject]@{Selected=$selected;Hosts=$hosts;Complete=$complete;Error=$errorText}
}
function Set-BHDcSuggestions {
    param($TextBox,[string[]]$Hosts)
    $items=[Windows.Forms.AutoCompleteStringCollection]::new()
    foreach($name in @($Hosts|Sort-Object -Unique)){[void]$items.Add($name)}
    $TextBox.AutoCompleteMode=[Windows.Forms.AutoCompleteMode]::Suggest
    $TextBox.AutoCompleteSource=[Windows.Forms.AutoCompleteSource]::CustomSource
    $TextBox.AutoCompleteCustomSource=$items
}
