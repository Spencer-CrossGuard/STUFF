# Developer build only. The generated BHTest.Viewer.ps1 is independently portable.
[CmdletBinding()]
param([string]$OutputPath=(Join-Path $PSScriptRoot 'BHTest.Viewer.ps1'))
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'src'
function Read-Source([string]$name) { [IO.File]::ReadAllText((Join-Path $source $name),[Text.Encoding]::UTF8) }
$names=@('BH.Native.cs','BH.Report.Native.cs','BH.Index.Native.cs','BH.Analysis.Native.cs','BH.Deep.Native.cs','BH.Viewer.Native.cs','BH.Desktop.Native.cs')
$usings=New-Object 'Collections.Generic.SortedSet[string]'
$bodies=foreach($name in $names) {
    $code=Read-Source $name
    foreach($match in [regex]::Matches($code,'(?m)^using [^\r\n]+;')) { [void]$usings.Add($match.Value) }
    [regex]::Replace($code,'(?m)^using [^\r\n]+;','')
}
$native=($usings -join "`r`n")+"`r`n"+($bodies -join "`r`n")
# Transfer the same authored explanations/fixes into native strings at build time.
# No JavaScript engine, JSON library, or browser is required by desktop mode.
function CSharp-String([string]$value) { '"'+$value.Replace('\','\\').Replace('"','\"').Replace("`r",'\r').Replace("`n",'\n')+'"' }
$help=Read-Source 'report-helper.js'
$fixes=Read-Source 'report-findings.js'
$actions=@{}
foreach($match in [regex]::Matches($fixes,"(?m)^\s+(\w+):'([^'\r\n]*)'")) { $actions[$match.Groups[1].Value]=$match.Groups[2].Value }
$guide=New-Object 'Collections.Generic.List[string]'
foreach($call in [regex]::Matches($help,"(?m)^defineHelp\((.*)\);")) {
    $parts=@([regex]::Matches($call.Groups[1].Value,"'([^'\r\n]*)'") | ForEach-Object { $_.Groups[1].Value })
    if($parts.Count -lt 7) { throw 'Unsupported explanation syntax; update the desktop guide build parser.' }
    foreach($kind in $parts[0].Split('|')) {
        $fix=if($actions.ContainsKey($kind)){$actions[$kind]}else{$parts[5]}
        $values=@($parts[1..6]) + @($fix)
        $guide.Add('guide.Add('+(CSharp-String $kind)+',new string[]{'+(($values | ForEach-Object {CSharp-String $_}) -join ',')+'});')
    }
}
if($guide.Count -lt 30) { throw 'Desktop explanations were not fully packaged.' }
$native=$native.Replace('/*__DESKTOP_GUIDE__*/',($guide -join "`r`n"))
$report=Read-Source 'report-template.html'
$styles=[regex]::Match($report,'(?s)<style>.*?</style>').Value
function Section([string]$id) { [regex]::Match($report,'(?s)<section id="'+$id+'".*?</section>').Value }
$aside=[regex]::Match($report,'(?s)<aside class="helper".*?</aside>').Value
$html=Read-Source 'viewer-template.html'
$data='{"GeneratedUtc":"","Ego":{"Nodes":[],"Edges":[]},"HighValueNodes":[],"CoverageNotes":["Original collection scope and endpoint coverage are not stored in this index. Consult the original HTML report and scan metadata.","Paths show recorded relationships; effective access requires validation."],"Analysis":{"Objects":[],"Findings":[],"CollectionWindows":[],"Method":"Read-only full-index viewer; paged queries and bounded path views."}}'
$html=$html.Replace('/*__STYLES__*/',$styles).Replace('/*__GRAPH_MARKUP__*/',(Section 'graph')).Replace('/*__PATH_MARKUP__*/',(Section 'pathReview')).Replace('/*__HELPER_MARKUP__*/',$aside)
$html=$html.Replace('/*__DATA__*/',$data).Replace('/*__HELPER__*/',(Read-Source 'report-helper.js')).Replace('/*__FINDINGS__*/',(Read-Source 'report-findings.js')).Replace('/*__GRAPH__*/',(Read-Source 'report-graph.js')).Replace('/*__VIEWER__*/',(Read-Source 'viewer.js'))
$memory=New-Object IO.MemoryStream
$gzip=New-Object IO.Compression.GZipStream($memory,[IO.Compression.CompressionLevel]::Optimal,$true)
$bytes=[Text.Encoding]::UTF8.GetBytes($html); $gzip.Write($bytes,0,$bytes.Length); $gzip.Dispose()
$encoded=[Convert]::ToBase64String($memory.ToArray()); $memory.Dispose()
$launcher=(Read-Source 'viewer-launcher.ps1').Replace('/*__NATIVE__*/',$native).Replace('/*__HTML__*/',$encoded)
[IO.File]::WriteAllText($OutputPath,$launcher,(New-Object Text.UTF8Encoding($true)))
Write-Host "Built standalone viewer: $OutputPath"
