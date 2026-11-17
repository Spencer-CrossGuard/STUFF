<#
.SYNOPSIS
Sets up the BHTest 2026-11-16 package (51 files) so you can paste each file's contents in, then checks the result.

.DESCRIPTION
Default: creates the folders and BLANK files under -Root. Existing files are left alone.
-Verify: compares every file under -Root with what it should contain and reports files that are
missing, still blank, truncated, saved in the wrong encoding, or different. Line-ending style
(CRLF or LF) and a leading byte-order mark are ignored for the comparison.

.EXAMPLE
.\Setup-BHTestFiles.ps1
.\Setup-BHTestFiles.ps1 -Verify
.\Setup-BHTestFiles.ps1 -Root 'C:\BHTest' -Verify
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'THE-OGCollection'),
    [switch] $Verify
)
$ErrorActionPreference = 'Stop'

# path | size in bytes | line count | SHA-256 of the text (UTF-8, LF line endings, no BOM, trailing newlines removed) | 1 if the original starts with a BOM
$manifest = @'
path|bytes|lines|sha256|bom
ADCS-SCOPE.md|8502|52|b4f3ad5e482fc98012a3e4c13bdeaa73f7ad60c92112e9885a7b4440dd0facc3|0
BH.Gui.ps1|104529|1760|7fe4b7e31db96dab1e69b00bfa4f27074607e01e0d18b6488bcd41abe1b68120|0
BHTest.Viewer.ps1|509867|4207|55a106dae573bdd7f117615c8e61cbc4c8b534cff6be766e7f04283e1cad5fa8|1
BUILD-INFO.json|7032|206|a99db2f7236fb27acc4f2d8f81733c4a49e18246b242bb80de29190b89fe7f91|0
Catalog-BHOutputs.ps1|2642|29|682f892b6621118778ac3370fa336466070ef821bbac300c0e94be6154ef515b|0
GPO-SCOPE.md|6501|41|8996de5d9f4e8071eb6e0311915d26b50179ca4c380c1fee50f1ac6c94da2df1|0
Open-Latest-Report.cmd|161|4|b5d0d9439a00eef397dd27dfd79821e21da9eb191cc734b90220bfb4adff16d1|0
OWNERSHIP.md|6102|43|d51e68fb4f95ecd82bc2aff1813b1860609bdab600c0095f21b77a9b60b2abae|0
PATH-PARITY.md|9260|51|0a944f749c2dd12721f4d2fe7612b5763f545643736df096e475e0e5e9bfdec1|0
README.md|56601|327|224376cdf993072f54f07514001dc86fad10df6d51e8aa723464e0f569eea328|0
RELEASE-NOTES.md|6071|31|f04433b032a6a99981b5ed01b2508a28e6cc1da133b1275abc6bd54814660b07|0
Start-BHTest.cmd|256|5|36699c39c33201c77f0aac647eb8efaf67ff40941ce8d7b1b84cc1bd92cc958e|0
Trace-ObjectAcl.ps1|10908|189|112207974be037860b6125c0e8eab2c9f9dcf13630fd0b2f488b37119589e9ae|0
src\BH.Adcs.Diagnostics.Native.cs|9454|104|96b0fa28cb3f2c0d3936e754f8e293678636b71b0ad74c656d64588eace1d535|0
src\BH.Adcs.Native.cs|20515|223|de866718cb9f5d74fdd493f415c2a28075d2a956ff548549598693166073e723|0
src\BH.Adcs.ps1|5252|54|d9369c59b942ab29009a7f95156fd68b024b10c2e9fa28c0e635aa5bcf8b94b7|0
src\BH.Analysis.Gui.ps1|2708|35|d10980b9b6acb925fd7c5bdbad214d2e34224d0de1826c2d6f4300da325d978f|0
src\BH.Analysis.Native.cs|9799|163|e1e64fd1b437c8c2cba947a879fc4c42d5a978bcfbb2fbf70bca88ea90fbe56f|0
src\BH.Collection.Native.cs|21346|322|be516f113dd25493b80b4354bced409bf4c398e8954ea80646d1db227afb7936|0
src\BH.Core.psm1|80463|1509|1fcb9b864098ab029729ad9454eb39589019b552aaa7ada44a7c1550bab7739d|0
src\BH.DcCache.ps1|6019|88|e156907ee21223eabc560a7d91fab92a76d779f979a53a106883a8687ebabe9e|0
src\BH.Deep.Native.cs|17004|222|60f8f32f6d537542bf87ee739531bca735ab575de8f5a5d4fd70e67fcd164e0b|0
src\BH.Desktop.Native.cs|85053|488|29ce4c238dbfee1f9e39ab7cf8055a0ffc54dc0781e2cd15fbf8a085ec279284|0
src\BH.Endpoint.Native.cs|9828|195|7746a21037d25668bbcdd9755cb5ef44472eb53552c421d8123ad5095ae597b3|0
src\BH.Endpoint.psm1|9975|166|6f9aba1db511a8ffa554c2e8ce58cfcb422e61aeea4b86658fd437fb5fbb804d|0
src\BH.EndpointWorker.ps1|715|18|0d033ad03b6e440df4d89f533835ec0fe958d8119f2d639624387f07ce4e0f8f|0
src\BH.Findings.Gui.ps1|5490|61|eb999429e55832d82ab4692cad4b7e626fd36d8aeb6599c55b0d1c8e38b3fa0f|0
src\BH.Gpo.Native.cs|20193|233|56af6dc48391a3a19107832c85e0865de478d1832d85c71706c3ed3a271b65a0|0
src\BH.GroupTypes.ps1|11052|117|f2923134a1876920717865f48e3a70728d2554fa89058ea21dd4133e2ce28d2e|0
src\BH.Gui.Layout.ps1|4782|118|f2f2cdcfee609620f2fcea6b1bd9b44fae3a279d729e06a24fdc3966bf9a38d1|0
src\BH.Help.Native.cs|6886|81|c848e3b3e7debb87f7bde41c80dc82b3ae8cb10101cd8b50612298a792adc197|0
src\BH.Index.Native.cs|52412|852|a7df05bd883aab0417cd3ce19dee503e5d2972c12b442a95d743d64af5a92970|0
src\BH.LatestReport.ps1|2056|27|ac90e372f9793c98fa9521c81a1912363bfee0cbe862a19ecbf0bf1e5d98d6b3|0
src\BH.Native.cs|43235|922|17cb3f3fbd1ce692e32c61af57db5df6d43a257a5c7302219c13a346cd7602e0|0
src\BH.Ownership.Native.cs|17650|191|e55b7d95c6f8dd17c182997d84b9780cc2167f1b2f3b33eaa107cd74128ca245|0
src\BH.PathExplorer.ps1|23610|499|ed0477bfd9d44d6b147d5ab247b10994e4570b8bf68691b46a2a6c2a40453d7a|0
src\BH.Report.Analysis.ps1|10393|160|84fe5c4df7f0ee66a1b6181625e8cb3e5b22a0db5fb10a9045627f6f868c03ad|0
src\BH.Report.Native.cs|1959|57|b02f7180313509047b1eb696e6bf7e16875b267d54d73cae3ce04a74c63f9d51|0
src\BH.Report.psm1|18971|304|9ba01ded95cb662f751979fede056a46ed81d55e4d9dc0838041ccf434711d3a|0
src\BH.Runs.psm1|4124|42|652686489c274358cd3e51b8c215153f623a872ac235fe1627cf688a92f72542|1
src\BH.SharpHound.Native.cs|21592|362|25e9e11f97e40b177a6c141272d64388d3e66684f23e34a2d29555e2698d3a77|0
src\BH.SharpHound.psm1|7328|129|175d63efe215d581532925a4a2deabedcb4f04cc76ecc78c00a278941103f113|0
src\BH.Viewer.Native.cs|30813|366|a07bd54a9519edb00bfcfa9ab60e45994bdd78d0ebeea43c4410c5abcf9f6e87|0
src\help-topics.json|60033|1392|a85c274a98518dc9617872f0c904acfee8af63ec2192e3de40df874c3c6e2fa0|0
src\report-findings.js|19459|165|9792cc91fb1676a8c13e678383a47b112926b225f7f83ee226821ae2b4aed59a|0
src\report-graph.js|17830|254|7fda4acb9a1ea0123ad17e29877fd4fa7d7c601350d55fc7706d81a4425444e2|0
src\report-helper.js|64720|175|d038fb923c4011c7e8994e176ffaba4b11ba9fc68c9bad0edc872088a21fb44d|0
src\report-template.html|17686|86|c4b460b7b1c692bc52a051961996c535dd41c837271c31932f835e62fdf0e8a5|0
src\viewer-launcher.ps1|5413|87|384bdd3e57cf8be5299f40af5e8dc8e64467b95c0839697674394d0a0557a5c5|0
src\viewer-template.html|5230|21|4909795924edb9d77f91e19f73f5ef6722febe23bdc7bfcef4d3a4e051b72d93|0
src\viewer.js|14081|111|e34f2f886251666f0578bbc7660bc376e1dc02a70eb5752bcfae9df1ee97af50|0
'@
$entries = @(($manifest.Trim() -split "\r?\n") | ConvertFrom-Csv -Delimiter '|')
$separator = [IO.Path]::DirectorySeparatorChar
function Get-EntryPath($Entry) { Join-Path $Root $Entry.path.Replace('\', $separator) }

if (-not $Verify) {
    $created = 0; $existing = 0
    foreach ($entry in $entries) {
        $path = Get-EntryPath $entry
        $folder = Split-Path $path -Parent
        if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
        if (Test-Path -LiteralPath $path) { $existing++; continue }
        New-Item -ItemType File -Path $path -Force | Out-Null
        $created++
    }
    Write-Host "Folder: $Root"
    Write-Host "Created $created blank file(s); $existing already existed and were not touched."
    Write-Host ''
    $entries | Select-Object @{ n = 'File'; e = { $_.path } }, @{ n = 'Size (KB)'; e = { [math]::Round([double]$_.bytes / 1KB, 1) } } | Format-Table -AutoSize
    Write-Host 'Next: paste each file''s contents into its blank file and save it as UTF-8.'
    Write-Host '  - Files that start with a byte-order mark (BHTest.Viewer.ps1, src\BH.Runs.psm1) should be saved as "UTF-8 with BOM".'
    Write-Host '  - Then run:  .\Setup-BHTestFiles.ps1 -Verify'
    return
}

function Get-FileText([byte[]] $Bytes) {
    $text = [Text.Encoding]::UTF8.GetString($Bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    return $text.Replace("`r`n", "`n").TrimEnd("`n")
}
function Get-Sha256Hex([string] $Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString('x2') }) }
    finally { $sha.Dispose() }
}

$problems = 0; $warnings = 0; $ok = 0
foreach ($entry in $entries) {
    $path = Get-EntryPath $entry
    $status = $null; $color = 'Green'
    if (-not (Test-Path -LiteralPath $path)) { $status = 'MISSING'; $color = 'Red' }
    else {
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -eq 0) { $status = 'BLANK (nothing pasted yet)'; $color = 'Red' }
        elseif (($bytes.Length -ge 2) -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) {
            $status = 'WRONG ENCODING (saved as UTF-16; save again as UTF-8)'; $color = 'Red'
        }
        else {
            $text = Get-FileText $bytes
            $lines = ($text.Length - $text.Replace("`n", '').Length) + 1
            $expectedLines = [int]$entry.lines
            if ((Get-Sha256Hex $text) -eq $entry.sha256) {
                $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
                if ($entry.bom -eq '1' -and -not $hasBom) { $status = 'OK, but save as "UTF-8 with BOM" (the original has one)'; $color = 'Yellow'; $warnings++ }
                else { $status = 'OK' }
            }
            elseif ($lines -lt $expectedLines) { $status = "TRUNCATED? has $lines lines, expected $expectedLines"; $color = 'Red' }
            elseif ($lines -eq $expectedLines) { $status = 'DIFFERENT (same line count: check the encoding, special characters or a typo)'; $color = 'Red' }
            else { $status = "DIFFERENT (has $lines lines, expected $expectedLines)"; $color = 'Red' }
        }
    }
    if ($color -eq 'Red') { $problems++ } elseif ($color -eq 'Green') { $ok++ }
    Write-Host ('{0,-34} {1}' -f $entry.path, $status) -ForegroundColor $color
}
Write-Host ''
Write-Host ("{0} of {1} files are exactly right; {2} problem(s); {3} warning(s)." -f ($ok + $warnings), $entries.Count, $problems, $warnings)
if ($problems) { Write-Host 'Fix the red files (paste them again), then re-run -Verify.' -ForegroundColor Red; exit 1 }
