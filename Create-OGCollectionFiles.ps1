# Creates blank placeholder files for the collector project inside
# THE-OGCollection on the current user's Desktop.
# Usage: powershell -ExecutionPolicy Bypass -File .\Create-OGCollectionFiles.ps1

$desktop = [Environment]::GetFolderPath('Desktop')
$root = Join-Path $desktop 'THE-OGCollection'

$files = @(
    'BH.Gui.ps1'
    'BHTest.Viewer.ps1'
    'Build-Viewer.ps1'
    'README.md'
    'src/BH.Analysis.Gui.ps1'
    'src/BH.Analysis.Native.cs'
    'src/BH.Collection.Native.cs'
    'src/BH.Core.psm1'
    'src/BH.Deep.Native.cs'
    'src/BH.Desktop.Native.cs'
    'src/BH.Endpoint.Native.cs'
    'src/BH.Endpoint.psm1'
    'src/BH.EndpointWorker.ps1'
    'src/BH.Findings.Gui.ps1'
    'src/BH.Gui.Layout.ps1'
    'src/BH.Index.Native.cs'
    'src/BH.Native.cs'
    'src/BH.PathExplorer.ps1'
    'src/BH.Report.Analysis.ps1'
    'src/BH.Report.Native.cs'
    'src/BH.Report.psm1'
    'src/BH.SharpHound.Native.cs'
    'src/BH.SharpHound.psm1'
    'src/BH.Viewer.Native.cs'
    'src/report-findings.js'
    'src/report-graph.js'
    'src/report-helper.js'
    'src/report-template.html'
    'src/viewer-launcher.ps1'
    'src/viewer-template.html'
    'src/viewer.js'
    'tests/Analysis.ps1'
    'tests/Deep.ps1'
    'tests/Desktop.ps1'
    'tests/Endpoints.ps1'
    'tests/Graph.cjs'
    'tests/LdapRequests.ps1'
    'tests/ReportHelper.cjs'
    'tests/Scheduler.ps1'
    'tests/Smoke.ps1'
    'tests/Viewer.ps1'
)

foreach ($f in $files) {
    $path = Join-Path $root $f
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    if (Test-Path $path) {
        Write-Host "Exists, skipped: $f"
    } else {
        New-Item -ItemType File -Path $path -Force | Out-Null
        Write-Host "Created: $f"
    }
}

Write-Host "`nDone. Files are in $root"
