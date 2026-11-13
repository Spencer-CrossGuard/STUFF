#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputPath)

$ErrorActionPreference = 'Stop'
Add-Type -Path @((Join-Path $PSScriptRoot 'BH.Native.cs'), (Join-Path $PSScriptRoot 'BH.Endpoint.Native.cs'))
[Console]::Out.WriteLine('{"Ready":true}')
[Console]::Out.Flush()
while ($null -ne ($line = [Console]::ReadLine())) {
    try {
        $request = $line | ConvertFrom-Json
        $result = [BH.Native.EndpointNative]::Collect($request.Host, $request.Id, $OutputPath)
        $json = $result | ConvertTo-Json -Depth 4 -Compress
    }
    catch { $json = @{ Error = $_.Exception.Message } | ConvertTo-Json -Compress }
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}
