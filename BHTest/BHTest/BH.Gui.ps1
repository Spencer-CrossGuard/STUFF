#Requires -Version 5.1
<#
.SYNOPSIS
BH -- single entry point for the AD attack-path auditor: launches the
WinForms GUI by default, or runs one headless action (-Scan / -Merge /
-Report) for scripting/scheduling.

.EXAMPLE
.\BH.Gui.ps1
Launches the GUI (Scan / Merge / Report tabs).

.EXAMPLE
.\BH.Gui.ps1 -Scan -BaseDn 'OU=Site-A,DC=corp,DC=local' -Server 'dc01.corp.local' -OutputRoot 'C:\BH\scans'

.EXAMPLE
.\BH.Gui.ps1 -Merge -ScanPaths 'C:\BH\scans\scan-...-A','C:\BH\scans\scan-...-B' -MergeOutputRoot 'C:\BH\merged'

.EXAMPLE
.\BH.Gui.ps1 -Report -DatasetPaths 'C:\BH\merged\merged-...' -ReportOutputDir 'C:\BH\reports\latest'
#>
[CmdletBinding(DefaultParameterSetName = 'Gui')]
param(
    [Parameter(ParameterSetName = 'Gui')] [switch] $Gui,
    [string] $DataRoot,

    [Parameter(ParameterSetName = 'Scan', Mandatory)] [switch] $Scan,
    [Parameter(ParameterSetName = 'Scan', Mandatory)] [string] $BaseDn,
    [Parameter(ParameterSetName = 'Scan', Mandatory)] [string] $Server,
    [Parameter(ParameterSetName = 'Scan', Mandatory)] [string] $OutputRoot,
    [Parameter(ParameterSetName = 'Scan')] [System.Management.Automation.PSCredential] $Credential,
    [Parameter(ParameterSetName = 'Scan')] [int] $MaxThreads = 8,
    [Parameter(ParameterSetName = 'Scan')] [int] $PageSize = 1000,
    [Parameter(ParameterSetName = 'Scan')] [switch] $ResolveExternalNames,
    [Parameter(ParameterSetName = 'Scan')] [switch] $CollectEndpoints,
    [Parameter(ParameterSetName = 'Scan')] [ValidateRange(1, 32)] [int] $EndpointThreads = 4,
    [Parameter(ParameterSetName = 'Scan')] [ValidateRange(2, 300)] [int] $EndpointTimeoutSeconds = 20,
    [Parameter(ParameterSetName = 'Scan')]
    [Parameter(ParameterSetName = 'Merge')] [switch] $UncompressedOutput,
    [Parameter(ParameterSetName = 'Scan')] [switch] $SkipConfiguration,

    [Parameter(ParameterSetName = 'Merge', Mandatory)] [switch] $Merge,
    [Parameter(ParameterSetName = 'Merge', Mandatory)] [string[]] $ScanPaths,
    [Parameter(ParameterSetName = 'Merge', Mandatory)] [string] $MergeOutputRoot,

    [Parameter(ParameterSetName = 'Report', Mandatory)] [switch] $Report,
    [Parameter(ParameterSetName = 'Report', Mandatory)] [string[]] $DatasetPaths,
    [Parameter(ParameterSetName = 'Report', Mandatory)] [string] $ReportOutputDir,
    [Parameter(ParameterSetName = 'Report')] [switch] $ExportCsv,
    [Parameter(ParameterSetName = 'Report')] [string] $BaselineIndexPath,
    [Parameter(ParameterSetName = 'Report')] [ValidateSet('Quick','Deep')] [string] $AnalysisMode = 'Quick',
    [Parameter(ParameterSetName = 'Report')] [ValidateRange(0,1000000)] [int] $AnalysisDepth = 8,
    [Parameter(ParameterSetName = 'Report')] [ValidateRange(0,1000000000)] [long] $AnalysisVisitLimit = 50000,
    [Parameter(ParameterSetName = 'Report')] [ValidateRange(0,86400)] [int] $AnalysisSeconds = 0,
    [Parameter(ParameterSetName = 'Report')] [switch] $ResumeAnalysis,

    [Parameter(ParameterSetName = 'SharpHound', Mandatory)] [switch] $ImportSharpHound,
    [Parameter(ParameterSetName = 'SharpHound', Mandatory)] [string[]] $SharpHoundPaths,
    [Parameter(ParameterSetName = 'SharpHound')] [string] $ImportOutputRoot,

    [Parameter(ParameterSetName = 'Path', Mandatory)] [switch] $PathQuery,
    [Parameter(ParameterSetName = 'Path', Mandatory)]
    [Parameter(ParameterSetName = 'Search', Mandatory)]
    [Parameter(ParameterSetName = 'Inspect', Mandatory)]
    [Parameter(ParameterSetName = 'Findings', Mandatory)]
    [Parameter(ParameterSetName = 'Trace', Mandatory)] [string] $IndexPath,
    [Parameter(ParameterSetName = 'Inspect', Mandatory)] [string] $InspectObject,
    [Parameter(ParameterSetName = 'Findings', Mandatory)] [switch] $FindingPage,
    [Parameter(ParameterSetName = 'Findings')] [int] $AfterScore = [int]::MaxValue,
    [Parameter(ParameterSetName = 'Findings')] [long] $AfterSequence = 0,
    [Parameter(ParameterSetName = 'Findings')] [ValidateRange(1,200)] [int] $FindingPageSize = 100,
    [Parameter(ParameterSetName = 'Trace', Mandatory)] [string] $TraceFinding,
    [Parameter(ParameterSetName = 'Path', Mandatory)] [string] $SourceId,
    [Parameter(ParameterSetName = 'Path', Mandatory)] [string] $TargetId,
    [Parameter(ParameterSetName = 'Path')] [ValidateRange(2, 5000000)] [int] $MaxVisited = 250000,
    [Parameter(ParameterSetName = 'Path')] [string[]] $EdgeTypes,
    [Parameter(ParameterSetName = 'Search', Mandatory)] [string] $SearchName
)

$ErrorActionPreference = 'Stop'
if (-not $DataRoot) { $DataRoot = Join-Path $PSScriptRoot 'BHData' }
$srcDir = Join-Path $PSScriptRoot 'src'
Import-Module (Join-Path $srcDir 'BH.Core.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $srcDir 'BH.Report.psm1') -Force -DisableNameChecking
Initialize-BHNative

switch ($PSCmdlet.ParameterSetName) {

    'Scan' {
        Write-Host "Scanning $BaseDn via $Server (max $MaxThreads threads, page size $PageSize)..."
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $scanOptions = @{
            CollectEndpoints = $CollectEndpoints; EndpointThreads = $EndpointThreads; EndpointTimeoutSeconds = $EndpointTimeoutSeconds
            CompressOutput = (-not $UncompressedOutput); SkipConfiguration = $SkipConfiguration
        }
        $scanDir = Start-BHCollection -BaseDn $BaseDn -Server $Server -OutputRoot $OutputRoot `
            -Credential $Credential -MaxThreads $MaxThreads -PageSize $PageSize -ResolveExternalNames:$ResolveExternalNames @scanOptions `
            -ProgressCallback {
                param($objects, $edges, $errors)
                Write-Host ("`r  {0,12:N0} objects  {1,12:N0} edges  {2,6:N0} errors  ({3:N0}s)" -f $objects, $edges, $errors, $sw.Elapsed.TotalSeconds) -NoNewline
            }
        Write-Host ""
        Write-Host "Scan complete: $scanDir"
        Get-Content (Join-Path $scanDir 'scan-metadata.json') -Raw
    }

    'Merge' {
        Write-Host "Merging $($ScanPaths.Count) scan(s)..."
        $mergeDir = Start-BHMerge -ScanPaths $ScanPaths -OutputRoot $MergeOutputRoot -CompressOutput:(-not $UncompressedOutput) -ProgressCallback {
            param($phase, $label, $count)
            if ($phase -eq 'phase') { Write-Host "`n$label" }
            else { Write-Host "`r  $phase/$label : $count" -NoNewline }
        }
        Write-Host "`nMerge complete: $mergeDir"
        Get-Content (Join-Path $mergeDir 'merge-metadata.json') -Raw
    }

    'Report' {
        $analysisOptions = @{ AnalysisMode = $AnalysisMode; ResumeAnalysis = $ResumeAnalysis }
        foreach ($option in 'AnalysisDepth','AnalysisVisitLimit','AnalysisSeconds') {
            if ($PSBoundParameters.ContainsKey($option)) { $analysisOptions[$option] = $PSBoundParameters[$option] }
        }
        $reportPath = Start-BHReport -DatasetPaths $DatasetPaths -OutputDir $ReportOutputDir -ExportCsv:$ExportCsv -BaselineIndexPath $BaselineIndexPath @analysisOptions -ProgressCallback {
            param($label) Write-Host $label
        }
        Write-Host "Report written: $reportPath"
    }

    'SharpHound' {
        Import-Module (Join-Path $srcDir 'BH.SharpHound.psm1') -Force -DisableNameChecking
        if (-not $ImportOutputRoot) { $ImportOutputRoot = Join-Path $DataRoot 'scans' }
        $dataset = Start-BHSharpHoundImport -InputPaths $SharpHoundPaths -OutputRoot $ImportOutputRoot -ProgressCallback {
            param($label) Write-Host $label
        }
        Write-Host "Imported dataset: $dataset"
        Get-Content -LiteralPath (Join-Path $dataset 'scan-metadata.json') -Raw
    }

    'Path' {
        $index = [BH.Native.GraphIndex]::new((Get-Item -LiteralPath $IndexPath).FullName, $false)
        try {
            $result = $index.ShortestPath($SourceId, $TargetId, $MaxVisited, $EdgeTypes)
            [pscustomobject]@{
                Status = if ($result.Truncated) { 'VisitLimitReached' } elseif ($result.NodeIds.Count) { 'Found' } else { 'NoPathInDataset' }
                Edges  = $result.Edges.ToArray()
                Nodes  = $result.NodeIds.ToArray()
            } | ConvertTo-Json -Depth 4
        }
        finally { $index.Dispose() }
    }

    'Findings' {
        $index = [BH.Native.GraphIndex]::new((Get-Item -LiteralPath $IndexPath).FullName, $false)
        try {
            [pscustomobject]@{ Analysis = $index.DiskAnalysisState(); Findings = @($index.DiskFindingPage($AfterScore,$AfterSequence,$FindingPageSize))
                Note = 'Next page: use the last finding Score and Sequence as AfterScore and AfterSequence. Paths over 256 steps have NextPathId for continuation.' } | ConvertTo-Json -Depth 10
        }
        finally { $index.Dispose() }
    }
    'Trace' {
        $index = [BH.Native.GraphIndex]::new((Get-Item -LiteralPath $IndexPath).FullName, $false)
        try { ConvertTo-Json -InputObject @($index.DiskPathPage($TraceFinding,256)) -Depth 5 }
        finally { $index.Dispose() }
    }
    'Inspect' {
        $index = [BH.Native.GraphIndex]::new((Get-Item -LiteralPath $IndexPath).FullName, $false)
        try {
            $record = $index.NodeRecord($InspectObject)
            [pscustomobject]@{
                ObjectId = $InspectObject
                Properties = $(if ($record) { $record | ConvertFrom-Json } else { $null })
                Relationships = @(foreach ($row in $index.ObjectRelationships($InspectObject, 101) | Select-Object -First 100) {
                    @{ Source = $row[0]; Target = $row[1]; Type = $row[2]; Bucket = $row[3] }
                })
                RelationshipLimit = 100
                Note = 'Complete retained properties; relationships are a bounded sample of up to 100. Use path queries for other relationships.'
            } | ConvertTo-Json -Depth 12
        }
        finally { $index.Dispose() }
    }

    'Search' {
        $index = [BH.Native.GraphIndex]::new((Get-Item -LiteralPath $IndexPath).FullName, $false)
        try {
            foreach ($row in $index.Search($SearchName)) {
                [pscustomobject]@{ Id = $row[0]; Name = $row[1]; Type = $row[2] }
            }
        }
        finally { $index.Dispose() }
    }

    default {
        # ====================================================================
        # GUI -- WinForms, Scan / Merge / Report tabs. Each long-running
        # action (including connectivity checks and OU discovery) runs as a
        # background job (its own process) so the GUI thread never blocks;
        # a Forms timer polls the job's output stream and updates the UI on
        # the GUI thread, which is the safe pattern for WinForms + PowerShell
        # (driving controls directly from a background thread is not safe).
        #
        # Nothing here ever talks to LDAP directly on the GUI thread -- the
        # "looks frozen" symptom from v1 was a bad server/credential falling
        # straight into Start-BHCollection's long-scan retry ladder (several
        # minutes of silent backoff) before erroring. Test-BHLdapConnectivity
        # now fails fast (10s) and every background action reports live
        # status instead of leaving a static "Starting..." label on screen.
        # ====================================================================

        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        # Both of these can only be called once per process, before any
        # Win32 window exists. Running the GUI twice in the same console
        # session (same powershell.exe process) hits that on the second
        # run -- harmless to skip then, since the first run's setting is
        # still in effect -- so these are wrapped rather than left to throw.
        try { [System.Windows.Forms.Application]::EnableVisualStyles() } catch {}
        try { [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false) } catch {}

        $scansRoot = Join-Path $DataRoot 'scans'
        $mergedRoot = Join-Path $DataRoot 'merged'
        $reportsRoot = Join-Path $DataRoot 'reports'
        foreach ($d in @($DataRoot, $scansRoot, $mergedRoot, $reportsRoot)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }

        # ---- Palette / fonts -------------------------------------------------
        $clrHeaderBg   = [System.Drawing.Color]::FromArgb(23, 37, 56)
        $clrHeaderFg   = [System.Drawing.Color]::White
        $clrAccent     = [System.Drawing.Color]::FromArgb(38, 90, 160)
        $clrAccentHover = [System.Drawing.Color]::FromArgb(29, 72, 130)
        $clrOk         = [System.Drawing.Color]::FromArgb(24, 110, 75)
        $clrWarn       = [System.Drawing.Color]::FromArgb(134, 82, 15)
        $clrErr        = [System.Drawing.Color]::FromArgb(163, 42, 48)
        $clrBodyBg     = [System.Drawing.Color]::FromArgb(241, 245, 249)
        $clrSurface    = [System.Drawing.Color]::White
        $clrText       = [System.Drawing.Color]::FromArgb(32, 48, 68)
        $clrMuted      = [System.Drawing.Color]::FromArgb(76, 93, 116)
        $clrBorder     = [System.Drawing.Color]::FromArgb(181, 195, 212)
        $clrActiveBg   = [System.Drawing.Color]::FromArgb(224, 235, 249)
        $fontBase      = New-Object System.Drawing.Font('Segoe UI', 10)
        $fontBold      = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
        $fontHeader    = New-Object System.Drawing.Font('Segoe UI', 19, [System.Drawing.FontStyle]::Bold)
        $fontMono      = New-Object System.Drawing.Font('Consolas', 9)
        if ([System.Windows.Forms.SystemInformation]::HighContrast) {
            $clrHeaderBg = [System.Drawing.SystemColors]::Window
            $clrHeaderFg = [System.Drawing.SystemColors]::WindowText
            $clrBodyBg = [System.Drawing.SystemColors]::Control
            $clrSurface = [System.Drawing.SystemColors]::Window
            $clrText = [System.Drawing.SystemColors]::WindowText
            $clrMuted = $clrText
            $clrOk = $clrText; $clrWarn = $clrText; $clrErr = $clrText
        }
        . (Join-Path $srcDir 'BH.Gui.Layout.ps1')

        # Control factories share typography and leave positioning to the layout.
        function New-BHGroup([string] $Text) {
            $group = [System.Windows.Forms.GroupBox]::new()
            $group.Text = $Text
            $group.Font = $fontBold
            $group.BackColor = $clrSurface
            return $group
        }

        function New-BHLabel {
            param([string] $Text, [switch] $Bold)
            $label = [System.Windows.Forms.Label]::new()
            $label.Text = $Text
            $label.Font = if ($Bold) { $fontBold } else { $fontBase }
            return $label
        }

        function New-BHTextBox {
            $box = [System.Windows.Forms.TextBox]::new()
            $box.Font = $fontBase
            return $box
        }

        function New-BHButton {
            param([string] $Text, [switch] $Primary)
            $button = [System.Windows.Forms.Button]::new()
            $button.Text = $Text
            $button.Font = if ($Primary) { $fontBold } else { $fontBase }
            Set-BHButtonStyle $button -Primary:$Primary
            return $button
        }

        function New-BHProgressBar {
            return [System.Windows.Forms.ProgressBar]::new()
        }

        function Set-BHStatus($label, $text, $color) {
            $label.Text = $text
            $label.ForeColor = $color
        }

        # Background-job runner: polls every 400ms, surfaces each Receive-Job
        # item to -OnProgress, fires -OnTick every poll regardless (for live
        # elapsed-time display), and -OnFinished once the job ends. Returns
        # the job+timer so a Cancel button can Stop-Job and let the next
        # tick clean everything up through the normal OnFinished path.
        function New-BHJobRunner {
            param(
                [scriptblock] $ScriptBlock,
                [object[]] $ArgumentList,
                [scriptblock] $OnProgress,
                [scriptblock] $OnFinished,
                [scriptblock] $OnTick
            )

            # Run the caller's scriptblock inside a wrapper that merges every
            # stream (information/warning/error) into plain output with *>&1.
            # Otherwise Receive-Job does not hand Write-Host / Write-Information
            # records back as items, and the GUI never sees progress or log lines.
            $wrapper = { param($body, $argList) $inner = [scriptblock]::Create($body); & $inner @argList *>&1 }
            $job = Start-Job -ScriptBlock $wrapper -ArgumentList $ScriptBlock.ToString(), $ArgumentList
            $timer = New-Object System.Windows.Forms.Timer
            $timer.Interval = 400
            $timer.Add_Tick({
                if ($OnTick) { & $OnTick }
                $items = Receive-Job -Job $job -ErrorAction SilentlyContinue
                foreach ($item in $items) {
                    # Structured progress travels on the Information stream
                    # (so it can't pollute a function's return value inside
                    # the job); unwrap it back into the plain object here.
                    # (Job output arrives deserialized, so test for the
                    # MessageData property rather than the .NET type.)
                    if ($item -and $item.PSObject.Properties['MessageData'] -and
                        $item.MessageData -and $item.MessageData.PSObject.Properties['Kind']) {
                        $item = $item.MessageData
                    }
                    if ($OnProgress) { & $OnProgress $item }
                }
                if ($job.State -in @('Completed', 'Failed', 'Stopped')) {
                    $timer.Stop()
                    $timer.Dispose()
                    $jobErrors = $job.ChildJobs[0].Error
                    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
                    if ($OnFinished) { & $OnFinished $job.State $jobErrors }
                }
            }.GetNewClosure())
            $timer.Start()
            return [pscustomobject]@{ Job = $job; Timer = $timer }
        }

        function Stop-BHRunner($runner) {
            if ($runner -and $runner.Job -and $runner.Job.State -eq 'Running') {
                Stop-Job -Job $runner.Job -ErrorAction SilentlyContinue
            }
        }

        # ---- Form shell: header banner + tabs ---------------------------------
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'BHTest | Active Directory Attack-Path Auditor'
        $form.Size = New-Object System.Drawing.Size(1120, 850)
        $form.MinimumSize = New-Object System.Drawing.Size(880, 695)
        $form.StartPosition = 'CenterScreen'
        $form.Font = $fontBase
        $form.BackColor = $clrBodyBg
        $form.ForeColor = $clrText
        $form.AutoScaleMode = 'Dpi'

        $header = New-Object System.Windows.Forms.Panel
        $header.Dock = 'Top'
        $header.Height = 80
        $header.BackColor = $clrHeaderBg
        $lblTitle = New-Object System.Windows.Forms.Label
        $lblTitle.Text = 'BHTest'
        $lblTitle.Font = $fontHeader
        $lblTitle.ForeColor = $clrHeaderFg
        $lblTitle.AutoSize = $true
        $lblTitle.Location = New-Object System.Drawing.Point(19, 8)
        $lblSubtitle = New-Object System.Windows.Forms.Label
        $lblSubtitle.Text = 'Active Directory attack-path auditor'
        $lblSubtitle.Font = $fontBase
        $lblSubtitle.ForeColor = if ([System.Windows.Forms.SystemInformation]::HighContrast) { $clrHeaderFg } else { [System.Drawing.Color]::FromArgb(191, 208, 227) }
        $lblSubtitle.AutoSize = $true
        $lblSubtitle.Location = New-Object System.Drawing.Point(22, 48)
        $header.Controls.AddRange(@($lblTitle, $lblSubtitle))
        $form.Controls.Add($header)

        $tabs = New-Object System.Windows.Forms.TabControl
        $tabs.Dock = 'Fill'
        $tabs.Font = $fontBase
        $tabs.SizeMode = 'Fixed'
        $tabs.ItemSize = [System.Drawing.Size]::new(142, 38)
        if (-not [System.Windows.Forms.SystemInformation]::HighContrast) {
            $tabs.DrawMode = 'OwnerDrawFixed'
            $tabs.Add_DrawItem({
                param($sender, $item)
                $selected = $item.Index -eq $sender.SelectedIndex
                $background = if ($selected) { $clrActiveBg } else { $clrBodyBg }
                $foreground = if ($selected) { $clrAccentHover } else { $clrMuted }
                $brush = [System.Drawing.SolidBrush]::new($background)
                try {
                    $item.Graphics.FillRectangle($brush, $item.Bounds)
                    if ($selected) {
                        $brush.Color = $clrAccent
                        $item.Graphics.FillRectangle($brush, $item.Bounds.X, $item.Bounds.Bottom - 3, $item.Bounds.Width, 3)
                    }
                }
                finally { $brush.Dispose() }
                $flags = [System.Windows.Forms.TextFormatFlags]'HorizontalCenter,VerticalCenter,EndEllipsis'
                $font = if ($selected) { $fontBold } else { $fontBase }
                [System.Windows.Forms.TextRenderer]::DrawText($item.Graphics, $sender.TabPages[$item.Index].Text,
                    $font, $item.Bounds, $foreground, $flags)
                if ($sender.Focused -and $selected) { $item.DrawFocusRectangle() }
            })
        }
        $form.Controls.Add($tabs)

        # =======================================================================
        # Tab 1: Scan
        # =======================================================================
        $tabScan = New-Object System.Windows.Forms.TabPage 'Scan'
        $tabScan.BackColor = $clrBodyBg
        $tabScan.AutoScroll = $true
        $tabs.TabPages.Add($tabScan)

        # -- Connection group --
        $grpConn = New-BHGroup 'Connection'

        $lblServer = New-BHLabel 'Domain / DC'
        $txtServer = New-BHTextBox

        $lblUser = New-BHLabel 'Username'
        $txtUser = New-BHTextBox

        $lblPass = New-BHLabel 'Password:'
        $txtPass = New-BHTextBox
        $txtPass.UseSystemPasswordChar = $true

        $lblThreads = New-BHLabel 'Max threads:'
        $numThreads = New-Object System.Windows.Forms.NumericUpDown
        $numThreads.Font = $fontBase
        $numThreads.Minimum = 1; $numThreads.Maximum = 64; $numThreads.Value = 8

        $lblPageSize = New-BHLabel 'Page size:'
        $numPageSize = New-Object System.Windows.Forms.NumericUpDown
        $numPageSize.Font = $fontBase
        $numPageSize.Minimum = 1; $numPageSize.Maximum = 1000; $numPageSize.Value = 1000

        $btnTestConn = New-BHButton 'Connect && load OUs' -Primary
        $lblConnStatus = New-BHLabel 'Not connected.'
        $lblConnStatus.ForeColor = $clrMuted

        $connHint = New-BHLabel 'Leave the username blank to use your Windows account.'
        $connHint.ForeColor = $clrMuted

        $connectionLayout = New-BHLayout @(100, '50%', 100, '50%') @(34, 34, 37, 24) 8
        $grpConn.Controls.Add($connectionLayout)
        Add-BHCell $connectionLayout $lblServer 0 0
        Add-BHCell $connectionLayout $txtServer 1 0
        Add-BHCell $connectionLayout $lblUser 2 0
        Add-BHCell $connectionLayout $txtUser 3 0
        Add-BHCell $connectionLayout $lblPass 0 1
        Add-BHCell $connectionLayout $txtPass 1 1
        $limitsLayout = New-BHLayout @(100, 65, 78, 74, '100%') @('100%')
        Add-BHCell $connectionLayout $limitsLayout 2 1 2
        Add-BHCell $limitsLayout $lblThreads 0 0
        Add-BHCell $limitsLayout $numThreads 1 0
        Add-BHCell $limitsLayout $lblPageSize 2 0
        Add-BHCell $limitsLayout $numPageSize 3 0
        $connectLayout = New-BHLayout @(170, '100%') @('100%')
        Add-BHCell $connectionLayout $connectLayout 0 2 4
        Add-BHCell $connectLayout $btnTestConn 0 0
        Add-BHCell $connectLayout $lblConnStatus 1 0
        Add-BHCell $connectionLayout $connHint 0 3 4

        # -- Scan targets group (OU picker: tick one or more OUs) --
        $grpTarget = New-BHGroup 'Scan targets'

        $lblTargetHelp = New-BHLabel 'Directory tree' -Bold

        $treeOus = New-Object System.Windows.Forms.TreeView
        $treeOus.Font = $fontBase
        $treeOus.HideSelection = $false
        $treeOus.CheckBoxes = $true
        $treeOus.AccessibleName = 'Directory tree'
        $treeOus.BorderStyle = 'FixedSingle'

        $lblQueue = New-BHLabel 'Selected OUs' -Bold
        $lstQueue = New-Object System.Windows.Forms.ListBox
        $lstQueue.Font = $fontBase
        $lstQueue.HorizontalScrollbar = $true
        $lstQueue.IntegralHeight = $false
        $lstQueue.AccessibleName = 'Selected organizational units'
        $btnClearQueue = New-BHButton 'Clear selection'
        $lblQueueCount = New-BHLabel '0 selected'
        $lblBaseDn = New-BHLabel 'Base DN if no OUs are selected'
        $txtBaseDn = New-BHTextBox
        $txtBaseDn.AccessibleName = 'Base distinguished name'

        $lblOut = New-BHLabel 'Scans folder:'
        $txtOut = New-BHTextBox
        $txtOut.Text = $scansRoot
        $btnBrowseOut = New-BHButton 'Browse...'

        $chkAutoReport = New-Object System.Windows.Forms.CheckBox
        $chkAutoReport.Font = $fontBase
        $chkAutoReport.Text = 'Merge and build a report after all scans finish'
        $chkAutoReport.Checked = $true
        $chkCompress = [System.Windows.Forms.CheckBox]::new()
        $chkCompress.Text = 'Compress scan files'
        $chkCompress.Checked = $true
        $chkEndpoints = [System.Windows.Forms.CheckBox]::new()
        $chkEndpoints.Text = 'Collect endpoint groups and logons using my Windows account'

        $targetLayout = New-BHLayout @('62%', '38%') @(24, '100%', 33, 28, 28) 8
        $grpTarget.Controls.Add($targetLayout)
        Add-BHCell $targetLayout $lblTargetHelp 0 0
        Add-BHCell $targetLayout $lblQueue 1 0
        Add-BHCell $targetLayout $treeOus 0 1
        $queueLayout = New-BHLayout @('100%') @('100%', 34, 22, 28)
        Add-BHCell $targetLayout $queueLayout 1 1
        Add-BHCell $queueLayout $lstQueue 0 0
        $queueActions = New-BHLayout @(124, '100%') @('100%')
        Add-BHCell $queueLayout $queueActions 0 1
        Add-BHCell $queueActions $btnClearQueue 0 0
        Add-BHCell $queueActions $lblQueueCount 1 0
        Add-BHCell $queueLayout $lblBaseDn 0 2
        Add-BHCell $queueLayout $txtBaseDn 0 3
        $scanOutput = New-BHLayout @(100, '100%', 100) @('100%')
        Add-BHCell $targetLayout $scanOutput 0 2 2
        Add-BHCell $scanOutput $lblOut 0 0
        Add-BHCell $scanOutput $txtOut 1 0
        Add-BHCell $scanOutput $btnBrowseOut 2 0
        Add-BHCell $targetLayout $chkAutoReport 0 3
        Add-BHCell $targetLayout $chkCompress 1 3
        Add-BHCell $targetLayout $chkEndpoints 0 4 2

        # -- Run group --
        $btnScan = New-BHButton 'Start scan' -Primary
        $btnCancelScan = New-BHButton 'Cancel'
        $btnCancelScan.Enabled = $false
        $progScan = New-BHProgressBar
        $lblScanElapsed = New-BHLabel ''
        $lblScanProgress = New-BHLabel 'Connect to browse organizational units, or enter a base DN.'
        $lblScanProgress.ForeColor = $clrMuted

        $txtScanLog = New-Object System.Windows.Forms.TextBox
        $txtScanLog.Multiline = $true; $txtScanLog.ScrollBars = 'Vertical'; $txtScanLog.ReadOnly = $true
        $txtScanLog.Font = $fontMono
        $txtScanLog.BackColor = [System.Drawing.Color]::White

        $scanLayout = New-BHLayout @('100%') @(175, '68%', 43, 26, '32%') 18
        $tabScan.AutoScrollMinSize = [System.Drawing.Size]::new(0, 710)
        $scanLayout.MinimumSize = [System.Drawing.Size]::new(0, 710)
        $tabScan.Controls.Add($scanLayout)
        Add-BHCell $scanLayout $grpConn 0 0
        Add-BHCell $scanLayout $grpTarget 0 1
        $scanActions = New-BHLayout @(135, 88, '100%', 155) @('100%')
        Add-BHCell $scanLayout $scanActions 0 2
        Add-BHCell $scanActions $btnScan 0 0
        Add-BHCell $scanActions $btnCancelScan 1 0
        Add-BHCell $scanActions $progScan 2 0
        $progScan.Dock = 'None'
        $progScan.Anchor = 'Left,Right'
        $progScan.Height = 16
        Add-BHCell $scanActions $lblScanElapsed 3 0
        Add-BHCell $scanLayout $lblScanProgress 0 3
        Add-BHCell $scanLayout $txtScanLog 0 4
        Set-BHLogStyle $txtScanLog

        # ---- OU tree: lazy, backed by a compiled index -------------------------
        # A big domain can have tens of thousands of OUs. Parsing a JSON cache
        # of them and creating a TreeNode for every one up front is what made
        # startup slow. Instead the cache is a plain tab-separated file parsed
        # and indexed in C# (BH.Native.OuIndex), and tree nodes are only created
        # for a level when the user expands it.
        $script:ouIndex = $null
        $script:checkedDns = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $script:suppressCheck = $false

        function New-BHOuNode {
            param([string] $Name, [string] $Dn)
            $n = New-Object System.Windows.Forms.TreeNode($Name)
            $n.Tag = $Dn
            $script:suppressCheck = $true
            $n.Checked = $script:checkedDns.Contains($Dn)
            $script:suppressCheck = $false
            if ($script:ouIndex.HasChildren($Dn)) {
                $ph = New-Object System.Windows.Forms.TreeNode('...')
                $ph.Name = '__ph'
                [void]$n.Nodes.Add($ph)
            }
            return $n
        }

        function Show-BHOuTree {
            $treeOus.BeginUpdate()
            $treeOus.Nodes.Clear()
            if ($script:ouIndex -and $script:ouIndex.RootDn) {
                $rootNode = New-BHOuNode $script:ouIndex.RootName $script:ouIndex.RootDn
                [void]$treeOus.Nodes.Add($rootNode)
                $rootNode.Expand()
            }
            $treeOus.EndUpdate()
        }

        function Populate-BHOuTree {
            # Used after a live "Connect and load OUs". When -Server is given
            # the result is also written to the cache file for next launch.
            param($Items, [string] $Server)
            $rows = New-Object 'System.Collections.Generic.List[object]'
            foreach ($it in $Items) { $rows.Add($it) }
            $n = $rows.Count
            $dns = New-Object string[] $n
            $names = New-Object string[] $n
            $parents = New-Object string[] $n
            for ($i = 0; $i -lt $n; $i++) {
                $dns[$i] = [string]$rows[$i].Dn
                $names[$i] = [string]$rows[$i].Name
                $parents[$i] = [string]$rows[$i].ParentDn
            }
            $script:ouIndex = [BH.Native.OuIndex]::FromRows($dns, $names, $parents)
            if ($Server) {
                try { [BH.Native.OuIndex]::SaveTsv($script:ouCachePath, $Server, $dns, $names, $parents) } catch {}
            }
            Show-BHOuTree
        }

        $treeOus.Add_BeforeExpand({
            param($s, $e)
            $node = $e.Node
            if ($node.Nodes.Count -eq 1 -and $node.Nodes[0].Name -eq '__ph') {
                $treeOus.BeginUpdate()
                $node.Nodes.Clear()
                $made = New-Object 'System.Collections.Generic.List[System.Windows.Forms.TreeNode]'
                foreach ($k in $script:ouIndex.GetChildren([string]$node.Tag)) { $made.Add((New-BHOuNode $k[0] $k[1])) }
                $node.Nodes.AddRange($made.ToArray())
                $treeOus.EndUpdate()
            }
        })

        # ---- Multi-select: which OUs are ticked --------------------------------
        # The tree is built lazily, so a ticked OU may not have a TreeNode yet.
        # The set of ticked DNs is the source of truth; nodes read their tick
        # state from it when they are created.
        function Refresh-BHQueueList {
            $lstQueue.BeginUpdate()
            $lstQueue.Items.Clear()
            foreach ($d in ($script:checkedDns | Sort-Object)) { [void]$lstQueue.Items.Add($d) }
            $lstQueue.EndUpdate()
            $lblQueueCount.Text = "$($script:checkedDns.Count) selected"
        }

        function Uncheck-BHNodes {
            param($Nodes)
            foreach ($n in $Nodes) { $n.Checked = $false; Uncheck-BHNodes $n.Nodes }
        }

        $treeOus.Add_AfterCheck({
            param($s, $e)
            if ($script:suppressCheck) { return }
            $dn = [string]$e.Node.Tag
            if (-not $dn) { return }
            if ($e.Node.Checked) { [void]$script:checkedDns.Add($dn) } else { [void]$script:checkedDns.Remove($dn) }
            Refresh-BHQueueList
        })

        $btnClearQueue.Add_Click({
            $script:suppressCheck = $true
            Uncheck-BHNodes $treeOus.Nodes
            $script:suppressCheck = $false
            $script:checkedDns.Clear()
            Refresh-BHQueueList
        })

        # ---- Settings + OU cache persistence -----------------------------------
        # Saved to DataRoot, not the install folder, so it survives a
        # redeploy (re-copying the script files) and so each DataRoot gets
        # its own remembered config -- e.g. separate machines pointed at
        # separate scan output roots.
        $script:settingsPath = Join-Path $DataRoot 'gui-settings.json'
        $script:ouCachePath = Join-Path $DataRoot 'ou-cache.tsv'

        function Save-BHGuiSettings {
            # Best-effort only: a save failure here should never block
            # closing the GUI or finishing a scan. Password is deliberately
            # never written to disk. The OU list lives in its own file
            # (ou-cache.tsv), so closing the GUI no longer rewrites it.
            try {
                $settings = [ordered]@{
                    Server             = $txtServer.Text
                    UserName           = $txtUser.Text
                    MaxThreads         = [int]$numThreads.Value
                    PageSize           = [int]$numPageSize.Value
                    CompressOutput     = [bool]$chkCompress.Checked
                    CollectEndpoints   = [bool]$chkEndpoints.Checked
                    ScansFolder        = $txtOut.Text
                    MergeOutputRoot    = $txtMergeOut.Text
                    ReportOutputFolder = $txtReportOut.Text
                    LastBaseDn         = $txtBaseDn.Text
                    CheckedOus         = @($script:checkedDns)
                }
                $settings | ConvertTo-Json -Depth 3 | Set-Content -Path $script:settingsPath -Encoding UTF8
            }
            catch {}
        }

        function Load-BHOuCache {
            # Returns $true if a cache for the current server was loaded. It is
            # only reused for the server it was captured from -- a stale tree
            # from a different domain would be misleading.
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                $idx = [BH.Native.OuIndex]::LoadTsv($script:ouCachePath, $txtServer.Text)
                if (-not $idx) { return $false }
                $script:ouIndex = $idx
                Show-BHOuTree
                Set-BHStatus $lblConnStatus ("Loaded {0} OU(s) from cache in {1:N0} ms (saved {2}). Click 'Connect and load OUs' to refresh." -f $idx.Count, $sw.Elapsed.TotalMilliseconds, $idx.SavedUtc) $clrWarn
                return $true
            }
            catch { return $false }
        }

        function Load-BHGuiSettings {
            if (Test-Path $script:settingsPath) {
                $settings = $null
                try { $settings = Get-Content -Path $script:settingsPath -Raw | ConvertFrom-Json } catch {}
                if ($settings) {
                    if ($settings.Server) { $txtServer.Text = $settings.Server }
                    if ($settings.UserName) { $txtUser.Text = $settings.UserName }
                    if ($settings.MaxThreads) { $numThreads.Value = [Math]::Max($numThreads.Minimum, [Math]::Min($numThreads.Maximum, [int]$settings.MaxThreads)) }
                    if ($settings.PageSize) { $numPageSize.Value = [Math]::Max($numPageSize.Minimum, [Math]::Min($numPageSize.Maximum, [int]$settings.PageSize)) }
                    if ($settings.PSObject.Properties['CompressOutput']) { $chkCompress.Checked = [bool]$settings.CompressOutput }
                    if ($settings.PSObject.Properties['CollectEndpoints']) { $chkEndpoints.Checked = [bool]$settings.CollectEndpoints }
                    if ($settings.ScansFolder) { $txtOut.Text = $settings.ScansFolder }
                    if ($settings.MergeOutputRoot) { $txtMergeOut.Text = $settings.MergeOutputRoot }
                    if ($settings.ReportOutputFolder) { $txtReportOut.Text = $settings.ReportOutputFolder }
                    if ($settings.LastBaseDn) { $txtBaseDn.Text = $settings.LastBaseDn }
                    foreach ($d in @($settings.CheckedOus)) { if ($d) { [void]$script:checkedDns.Add([string]$d) } }
                }
            }
            [void](Load-BHOuCache)
            Refresh-BHQueueList
        }

        $treeOus.Add_AfterSelect({
            param($s, $e)
            if ($e.Node.Tag) { $txtBaseDn.Text = [string]$e.Node.Tag }
        })

        $btnTestConn.Add_Click({
            $btnTestConn.Enabled = $false
            $treeOus.Nodes.Clear()
            Set-BHStatus $lblConnStatus 'Connecting... (times out after 10s if unreachable)' $clrWarn

            $sb = {
                param($srcDir, $server, $userName, $password)
                Import-Module (Join-Path $srcDir 'BH.Core.psm1') -Force -DisableNameChecking
                Initialize-BHNative
                $cred = $null
                if ($userName) {
                    $sec = ConvertTo-SecureString -String $password -AsPlainText -Force
                    $cred = New-Object System.Management.Automation.PSCredential($userName, $sec)
                }
                $probe = Test-BHLdapConnectivity -Server $server -Credential $cred -TimeoutSeconds 10
                if (-not $probe.Ok) {
                    Write-Output ([pscustomobject]@{ Kind = 'error'; Message = $probe.Error })
                    return
                }
                Write-Output ([pscustomobject]@{ Kind = 'connected' })
                try {
                    $ous = Get-BHOuTree -Server $server -Credential $cred -TimeoutSeconds 20
                    Write-Output ([pscustomobject]@{ Kind = 'result'; Ous = $ous })
                }
                catch {
                    Write-Output ([pscustomobject]@{ Kind = 'error'; Message = "Connected, but listing OUs failed: $($_.Exception.Message)" })
                }
            }

            $args = @($srcDir, $txtServer.Text, $txtUser.Text, $txtPass.Text)
            $script:connRunner = New-BHJobRunner -ScriptBlock $sb -ArgumentList $args `
                -OnProgress {
                    param($item)
                    switch ($item.Kind) {
                        'connected' { Set-BHStatus $lblConnStatus 'Connected. Loading OUs...' $clrWarn }
                        'result' {
                            Populate-BHOuTree -Items $item.Ous -Server $txtServer.Text
                            Save-BHGuiSettings
                            Set-BHStatus $lblConnStatus "Connected -- $($item.Ous.Count) OU(s)/domain root found. Click one to set the scan target. (Cached for next time.)" $clrOk
                        }
                        'error' { Set-BHStatus $lblConnStatus "Failed: $($item.Message)" $clrErr }
                    }
                } `
                -OnFinished {
                    param($state, $jobErrors)
                    $btnTestConn.Enabled = $true
                    if ($jobErrors -and $jobErrors.Count -gt 0) {
                        Set-BHStatus $lblConnStatus "Failed: $($jobErrors[0])" $clrErr
                    }
                }
        })

        $btnBrowseOut.Add_Click({
            $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
            $dlg.Description = 'Choose a folder for scan output'
            if (Test-Path $txtOut.Text) { $dlg.SelectedPath = $txtOut.Text }
            if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $txtOut.Text = $dlg.SelectedPath }
        })

        # The multi-target scan, kept as its own scriptblock and run in a
        # background job: scans each target in turn, keeps going if one fails,
        # then optionally merges and builds the report. Everything it does is
        # also appended to a log file on disk, so the outcome survives a closed
        # window or a missed screen.
        $script:BatchScanJob = {
            param($srcDir, $targets, $server, $userName, $password, $outRoot, $maxThreads, $pageSize, $autoReport, $mergedRoot, $reportsRoot, $logPath, $compressOutput, $collectEndpoints)
            Import-Module (Join-Path $srcDir 'BH.Core.psm1') -Force -DisableNameChecking
            Import-Module (Join-Path $srcDir 'BH.Report.psm1') -Force -DisableNameChecking
            Initialize-BHNative

            function Log([string] $m) {
                $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $m
                try { Add-Content -Path $logPath -Value $line } catch {}
                Write-Output $line
            }

            $cred = $null
            if ($userName) {
                $sec = ConvertTo-SecureString -String $password -AsPlainText -Force
                $cred = New-Object System.Management.Automation.PSCredential($userName, $sec)
            }

            $scanDirs = New-Object 'System.Collections.Generic.List[string]'
            $failed = New-Object 'System.Collections.Generic.List[string]'
            $total = @($targets).Count
            Log "Batch started: $total target(s)."

            $i = 0
            foreach ($target in $targets) {
                $i++
                Log "=== Target $i of ${total}: $target ==="
                Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'target'; Index = $i; Total = $total; Dn = $target })
                $targetWatch = [System.Diagnostics.Stopwatch]::StartNew()
                try {
                    # Piped, not assigned: see the note in New-BHJobRunner about
                    # progress output being swallowed by an assignment.
                    Start-BHCollection -BaseDn $target -Server $server -OutputRoot $outRoot `
                        -Credential $cred -MaxThreads $maxThreads -PageSize $pageSize -CompressOutput:$compressOutput -CollectEndpoints:$collectEndpoints -ProgressCallback {
                            param($objects, $edges, $errors)
                            Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'progress'; Objects = $objects; Edges = $edges; Errors = $errors })
                        } | ForEach-Object {
                            if ($_ -is [string]) { $scanDirs.Add($_); [pscustomobject]@{ Kind = 'result'; ScanDir = $_ } } else { $_ }
                        }
                    Log ("Target {0} finished in {1:hh\:mm\:ss}." -f $i, $targetWatch.Elapsed)
                }
                catch {
                    $failed.Add($target)
                    Log "FAILED target ${i} ($target): $($_.Exception.Message)"
                }
            }

            Log "Scans complete: $($scanDirs.Count) succeeded, $($failed.Count) failed."
            foreach ($f in $failed) { Log "  failed: $f" }

            if ($autoReport -and $failed.Count -gt 0) {
                Log 'Automatic reporting skipped because at least one target was incomplete. Completed scans are available in the Report tab.'
            }
            elseif ($autoReport -and $scanDirs.Count -gt 0) {
                try {
                    $dataset = $null
                    if ($scanDirs.Count -ge 2) {
                        Log "Merging $($scanDirs.Count) scans ..."
                        $dataset = Start-BHMerge -ScanPaths $scanDirs.ToArray() -OutputRoot $mergedRoot -ProgressCallback {
                            param($phase, $label, $count)
                            if ($phase -eq 'phase') { Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'stage'; Label = $label }) }
                        }
                        $dataset = @($dataset)[-1]
                        Log "Merged dataset: $dataset"
                    }
                    else {
                        $dataset = $scanDirs[0]
                    }
                    Log 'Building report ...'
                    $reportDir = Join-Path $reportsRoot ('batch-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
                    $reportPath = Start-BHReport -DatasetPaths @($dataset) -OutputDir $reportDir -ProgressCallback {
                        param($label)
                        Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'stage'; Label = $label })
                    }
                    $reportPath = @($reportPath)[-1]
                    Log "Report: $reportPath"
                    Write-Output ([pscustomobject]@{ Kind = 'report'; ReportPath = $reportPath })
                }
                catch {
                    Log "Merge/report FAILED: $($_.Exception.Message)"
                    throw
                }
            }
            if ($failed.Count -gt 0) { throw "$($failed.Count) scan target(s) failed. See the batch log." }
            Log 'Batch finished.'
        }

        $btnScan.Add_Click({
            if ([string]::IsNullOrWhiteSpace($txtServer.Text)) {
                Set-BHStatus $lblScanProgress 'Enter a domain or domain controller first.' $clrWarn
                $txtServer.Focus() | Out-Null
                return
            }
            # Targets: the ticked OUs, or the single DN box if nothing is ticked.
            $picked = @($script:checkedDns | Sort-Object)
            if ($picked.Count -eq 0) { $picked = @($txtBaseDn.Text.Trim()) }
            $picked = @($picked | Where-Object { $_ })
            if ($picked.Count -eq 0) {
                [System.Windows.Forms.MessageBox]::Show('Tick at least one OU (or type a DN) first.', 'BH', 'OK', 'Warning') | Out-Null
                return
            }

            # An OU inside another ticked OU is already covered by its parent's
            # subtree scan; scanning it again would just do the same work twice.
            $targets = New-Object 'System.Collections.Generic.List[string]'
            $nested = New-Object 'System.Collections.Generic.List[string]'
            foreach ($d in $picked) {
                $inside = $false
                foreach ($other in $picked) {
                    if (-not [string]::Equals($other, $d, [System.StringComparison]::OrdinalIgnoreCase) -and
                        $d.EndsWith(',' + $other, [System.StringComparison]::OrdinalIgnoreCase)) { $inside = $true; break }
                }
                if ($inside) { $nested.Add($d) } else { $targets.Add($d) }
            }

            $btnScan.Enabled = $false
            $btnCancelScan.Enabled = $true
            $txtScanLog.Clear()
            $progScan.Style = 'Marquee'
            $progScan.MarqueeAnimationSpeed = 30
            Set-BHStatus $lblScanProgress 'Starting...' $clrWarn
            $script:scanStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $script:targetText = ''

            if ($nested.Count -gt 0) {
                $txtScanLog.AppendText("Skipping $($nested.Count) OU(s) already covered by a ticked parent OU:`r`n")
                foreach ($n in $nested) { $txtScanLog.AppendText("  $n`r`n") }
            }
            $logPath = Join-Path $DataRoot ('batch-log-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date))
            $txtScanLog.AppendText("Plan: $($targets.Count) target(s), scanned one after another. Log file: $logPath`r`n")

            $jobArgs = @($srcDir, [string[]]$targets.ToArray(), $txtServer.Text, $txtUser.Text, $txtPass.Text, $txtOut.Text,
                         [int]$numThreads.Value, [int]$numPageSize.Value, [bool]$chkAutoReport.Checked, $txtMergeOut.Text, $reportsRoot, $logPath,
                         [bool]$chkCompress.Checked, [bool]$chkEndpoints.Checked)

            $script:scanRunner = New-BHJobRunner -ScriptBlock $script:BatchScanJob -ArgumentList $jobArgs `
                -OnTick {
                    $lblScanElapsed.Text = "Elapsed: {0:hh\:mm\:ss}" -f $script:scanStopwatch.Elapsed
                } `
                -OnProgress {
                    param($item)
                    switch ($item.Kind) {
                        'target' {
                            $script:targetText = "Target $($item.Index) of $($item.Total)"
                            Set-BHStatus $lblScanProgress "$($script:targetText): $($item.Dn)" $clrWarn
                        }
                        'progress' {
                            if ($progScan.Style -ne 'Blocks') { $progScan.Style = 'Blocks'; $progScan.Value = 100 }
                            Set-BHStatus $lblScanProgress "$($script:targetText) | Objects: $($item.Objects)   Edges: $($item.Edges)   Errors: $($item.Errors)" ([System.Drawing.Color]::Black)
                        }
                        'stage' {
                            Set-BHStatus $lblScanProgress $item.Label $clrWarn
                        }
                        'result' {
                            $txtScanLog.AppendText("Scan folder: $($item.ScanDir)`r`n")
                        }
                        'report' {
                            $script:lastReportPath = $item.ReportPath
                            $btnOpenReport.Enabled = $true
                        }
                        default {
                            $txtScanLog.AppendText("$item`r`n")
                        }
                    }
                } `
                -OnFinished {
                    param($state, $jobErrors)
                    $btnScan.Enabled = $true
                    $btnCancelScan.Enabled = $false
                    $progScan.Style = 'Blocks'; $progScan.Value = if ($state -eq 'Completed') { 100 } else { 0 }
                    $finalColor = switch ($state) { 'Completed' { $clrOk }; 'Stopped' { $clrWarn }; default { $clrErr } }
                    Set-BHStatus $lblScanProgress "Finished: $state" $finalColor
                    if ($jobErrors -and $jobErrors.Count -gt 0) {
                        foreach ($e in $jobErrors) { $txtScanLog.AppendText("ERROR: $e`r`n") }
                    }
                    Refresh-BHDatasetLists
                }
        })

        $btnCancelScan.Add_Click({
            Set-BHStatus $lblScanProgress 'Cancelling...' $clrWarn
            Stop-BHRunner $script:scanRunner
        })

        # =======================================================================
        # Tab 2: Merge
        # =======================================================================
        $tabMerge = New-Object System.Windows.Forms.TabPage 'Merge scans'
        $tabMerge.BackColor = $clrBodyBg
        $tabMerge.AutoScroll = $true
        $tabs.TabPages.Add($tabMerge)

        $lblMergeHelp = New-BHLabel 'Select two or more scans/merged datasets to combine into one deduplicated dataset:'
        $lstScans = New-Object System.Windows.Forms.CheckedListBox
        $lstScans.Font = $fontBase
        $lstScans.CheckOnClick = $true

        $btnRefreshScans = New-BHButton 'Refresh list'

        $lblMergeOut = New-BHLabel 'Merged output root:'
        $txtMergeOut = New-BHTextBox
        $txtMergeOut.Text = $mergedRoot
        $btnBrowseMergeOut = New-BHButton 'Browse...'

        $btnMerge = New-BHButton 'Merge selected' -Primary
        $btnCancelMerge = New-BHButton 'Cancel'
        $btnCancelMerge.Enabled = $false
        $progMerge = New-BHProgressBar
        $lblMergeProgress = New-BHLabel ''
        $txtMergeLog = New-Object System.Windows.Forms.TextBox
        $txtMergeLog.Multiline = $true; $txtMergeLog.ScrollBars = 'Vertical'; $txtMergeLog.ReadOnly = $true
        $txtMergeLog.Font = $fontMono
        $txtMergeLog.BackColor = [System.Drawing.Color]::White

        $lblMergeHelp.Font = $fontBold
        $lstScans.IntegralHeight = $false
        $lstScans.AccessibleName = 'Scans and merged datasets'
        Set-BHDatasetLayout -Page $tabMerge -Help $lblMergeHelp -Datasets $lstScans `
            -OutputLabel $lblMergeOut -OutputPath $txtMergeOut -Browse $btnBrowseMergeOut `
            -Run $btnMerge -Cancel $btnCancelMerge -Progress $progMerge -Status $lblMergeProgress `
            -Log $txtMergeLog -Extra $btnRefreshScans

        function Refresh-BHDatasetLists {
            $allDirs = @()
            foreach ($root in @($scansRoot, $mergedRoot)) {
                if (Test-Path $root) {
                    $allDirs += Get-ChildItem -Path $root -Directory | Sort-Object Name -Descending | ForEach-Object { $_.FullName }
                }
            }
            $lstScans.Items.Clear()
            $lstDatasets.Items.Clear()
            foreach ($dir in $allDirs) {
                if ([IO.Path]::GetFileName($dir).StartsWith('.')) { continue }
                try { Assert-BHDatasetsReady -DatasetPaths @($dir) } catch { continue }
                [void]$lstScans.Items.Add($dir)
                [void]$lstDatasets.Items.Add($dir)
            }
        }

        $btnRefreshScans.Add_Click({ Refresh-BHDatasetLists })
        $btnBrowseMergeOut.Add_Click({
            $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
            $dlg.Description = 'Choose a folder for the merged dataset'
            if (Test-Path $txtMergeOut.Text) { $dlg.SelectedPath = $txtMergeOut.Text }
            if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $txtMergeOut.Text = $dlg.SelectedPath }
        })

        $btnMerge.Add_Click({
            $selected = @($lstScans.CheckedItems)
            if ($selected.Count -lt 2) {
                [System.Windows.Forms.MessageBox]::Show('Pick at least two scans to merge.', 'BH', 'OK', 'Warning') | Out-Null
                return
            }
            $btnMerge.Enabled = $false
            $btnCancelMerge.Enabled = $true
            $txtMergeLog.Clear()
            $progMerge.Style = 'Marquee'; $progMerge.MarqueeAnimationSpeed = 30
            Set-BHStatus $lblMergeProgress 'Merging...' $clrWarn

            $sb = {
                param($srcDir, $scanPaths, $outRoot)
                $ErrorActionPreference = 'Stop'
                Import-Module (Join-Path $srcDir 'BH.Core.psm1') -Force -DisableNameChecking
                Initialize-BHNative
                try {
                    Start-BHMerge -ScanPaths $scanPaths -OutputRoot $outRoot -ProgressCallback {
                        param($phase, $label, $count)
                        Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'progress'; Phase = $phase; Label = $label; Count = $count })
                    } | ForEach-Object {
                        if ($_ -is [string]) { [pscustomobject]@{ Kind = 'result'; MergeDir = $_ } } else { $_ }
                    }
                }
                catch {
                    Write-Output ([pscustomobject]@{ Kind = 'error'; Message = $_.Exception.Message })
                    throw
                }
            }

            $script:mergeRunner = New-BHJobRunner -ScriptBlock $sb -ArgumentList @($srcDir, [string[]]$selected, $txtMergeOut.Text) `
                -OnProgress {
                    param($item)
                    switch ($item.Kind) {
                        'progress' { Set-BHStatus $lblMergeProgress "$($item.Phase): $($item.Label) ($($item.Count))" ([System.Drawing.Color]::Black) }
                        'result' { $txtMergeLog.AppendText("Merged dataset: $($item.MergeDir)`r`n") }
                        'error' { Set-BHStatus $lblMergeProgress "Failed: $($item.Message)" $clrErr; $txtMergeLog.AppendText("ERROR: $($item.Message)`r`n") }
                    }
                } `
                -OnFinished {
                    param($state, $jobErrors)
                    $btnMerge.Enabled = $true
                    $btnCancelMerge.Enabled = $false
                    $progMerge.Style = 'Blocks'; $progMerge.Value = if ($state -eq 'Completed') { 100 } else { 0 }
                    $finalColor = switch ($state) { 'Completed' { $clrOk }; 'Stopped' { $clrWarn }; default { $clrErr } }
                    Set-BHStatus $lblMergeProgress "Finished: $state" $finalColor
                    if ($jobErrors -and $jobErrors.Count -gt 0) { foreach ($e in $jobErrors) { $txtMergeLog.AppendText("ERROR: $e`r`n") } }
                    Refresh-BHDatasetLists
                }
        })

        $btnCancelMerge.Add_Click({
            Set-BHStatus $lblMergeProgress 'Cancelling...' $clrWarn
            Stop-BHRunner $script:mergeRunner
        })

        # =======================================================================
        # Tab 3: Report
        # =======================================================================
        $tabReport = New-Object System.Windows.Forms.TabPage 'Report'
        $tabReport.BackColor = $clrBodyBg
        $tabReport.AutoScroll = $true
        $tabs.TabPages.Add($tabReport)

        $lblReportHelp = New-BHLabel 'Select one or more scans/merged datasets to report on:'
        $chkReportCsv = New-Object System.Windows.Forms.CheckBox
        $chkReportCsv.Text = 'Export CSV'
        $chkReportCsv.AutoSize = $true
        $chkCompareReport = New-Object System.Windows.Forms.CheckBox
        $chkCompareReport.Text = 'Compare previous index'
        $chkCompareReport.AutoSize = $true
        $reportOptions = New-Object System.Windows.Forms.FlowLayoutPanel
        $reportOptions.WrapContents = $false
        $reportOptions.Dock = 'Fill'
        $reportOptions.Controls.AddRange(@($chkReportCsv, $chkCompareReport))
        . (Join-Path $srcDir 'BH.Analysis.Gui.ps1')
        $script:reportAnalysis = @{ Mode = 'Quick'; Depth = 8; Visits = 50000L; Seconds = 0 }
        $analysisBar = [System.Windows.Forms.FlowLayoutPanel]::new()
        $analysisBar.Dock = 'Fill'; $analysisBar.WrapContents = $false
        $btnAnalysisSettings = New-BHButton 'Analysis settings...'
        $btnAnalysisSettings.Size = [Drawing.Size]::new(146,30)
        $chkResumeAnalysis = [System.Windows.Forms.CheckBox]::new()
        $chkResumeAnalysis.Text = 'Resume saved analysis'; $chkResumeAnalysis.AutoSize = $true; $chkResumeAnalysis.Enabled = $false
        $lblAnalysisSummary = New-BHLabel 'Quick: 8 hops, 50,000 identities; 200-row HTML preview'
        $lblAnalysisSummary.AutoSize = $true; $lblAnalysisSummary.Margin = [System.Windows.Forms.Padding]::new(8,7,0,0)
        $analysisBar.Controls.AddRange(@($btnAnalysisSettings, $chkResumeAnalysis, $lblAnalysisSummary))
        $btnAnalysisSettings.Add_Click({
            $choice = Show-BHAnalysisSettings $form $script:reportAnalysis
            if ($choice) {
                $script:reportAnalysis = $choice
                $chkResumeAnalysis.Enabled = $choice.Mode -eq 'Deep'
                if (-not $chkResumeAnalysis.Enabled) { $chkResumeAnalysis.Checked = $false }
                $lblAnalysisSummary.Text = "$($choice.Mode): hops $($choice.Depth), identities $($choice.Visits), seconds $($choice.Seconds) (0 = no cap)"
            }
        })
        $lstDatasets = New-Object System.Windows.Forms.CheckedListBox
        $lstDatasets.Font = $fontBase
        $lstDatasets.CheckOnClick = $true

        $lblReportOut = New-BHLabel 'Report output folder:'
        $txtReportOut = New-BHTextBox
        $txtReportOut.Text = (Join-Path $reportsRoot 'latest')
        $btnBrowseReportOut = New-BHButton 'Browse...'

        $btnReport = New-BHButton 'Generate report' -Primary
        $btnImportSharpHound = New-BHButton 'Import SharpHound...'
        $btnCancelReport = New-BHButton 'Cancel'
        $btnCancelReport.Enabled = $false
        $btnOpenReport = New-BHButton 'Open report.html'
        $btnOpenReport.Enabled = $false
        $progReport = New-BHProgressBar
        $lblReportProgress = New-BHLabel ''
        $txtReportLog = New-Object System.Windows.Forms.TextBox
        $txtReportLog.Multiline = $true; $txtReportLog.ScrollBars = 'Vertical'; $txtReportLog.ReadOnly = $true
        $txtReportLog.Font = $fontMono
        $txtReportLog.BackColor = [System.Drawing.Color]::White

        $lblReportHelp.Font = $fontBold
        $lstDatasets.IntegralHeight = $false
        $lstDatasets.AccessibleName = 'Report datasets'
        Set-BHDatasetLayout -Page $tabReport -Help $lblReportHelp -Datasets $lstDatasets `
            -OutputLabel $lblReportOut -OutputPath $txtReportOut -Browse $btnBrowseReportOut `
            -Run $btnReport -Cancel $btnCancelReport -Progress $progReport -Status $lblReportProgress `
            -Log $txtReportLog -Extra $reportOptions -ExtraWidth 290 -Open $btnOpenReport -Import $btnImportSharpHound -AnalysisOptions $analysisBar

        $script:lastReportPath = $null

        $btnBrowseReportOut.Add_Click({
            $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
            $dlg.Description = 'Choose a folder for the report output'
            if (Test-Path $txtReportOut.Text) { $dlg.SelectedPath = $txtReportOut.Text }
            if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $txtReportOut.Text = $dlg.SelectedPath }
        })

        $btnImportSharpHound.Add_Click({
            $script:activeReportAnalysis = $null
            $dialog = [System.Windows.Forms.OpenFileDialog]::new()
            try {
                $dialog.Title = 'Select SharpHound ZIP or JSON exports'
                $dialog.Filter = 'SharpHound exports (*.zip;*.json)|*.zip;*.json'
                $dialog.Multiselect = $true
                if ($dialog.ShowDialog($form) -ne 'OK') { return }
                $inputs = $dialog.FileNames
            }
            finally { $dialog.Dispose() }

            $btnImportSharpHound.Enabled = $false
            $btnReport.Enabled = $false
            $btnCancelReport.Enabled = $true
            $txtReportLog.Clear()
            $progReport.Style = 'Marquee'
            $progReport.MarqueeAnimationSpeed = 30
            Set-BHStatus $lblReportProgress 'Reading SharpHound exports...' $clrWarn
            $importJob = {
                param($srcDir, $inputs, $root)
                $ErrorActionPreference = 'Stop'
                Import-Module (Join-Path $srcDir 'BH.Core.psm1') -DisableNameChecking
                Import-Module (Join-Path $srcDir 'BH.SharpHound.psm1') -DisableNameChecking
                try {
                    $dataset = Start-BHSharpHoundImport -InputPaths $inputs -OutputRoot $root -ProgressCallback {
                        param($label)
                        Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'progress'; Label = $label })
                    }
                    [pscustomobject]@{ Kind = 'imported'; Dataset = $dataset }
                }
                catch {
                    [pscustomobject]@{ Kind = 'error'; Message = $_.Exception.Message }
                    throw
                }
            }
            $script:importedDataset = $null
            $script:reportRunner = New-BHJobRunner -ScriptBlock $importJob -ArgumentList @($srcDir, [string[]]$inputs, $scansRoot) `
                -OnProgress {
                    param($item)
                    if ($item.Kind -eq 'progress') {
                        Set-BHStatus $lblReportProgress $item.Label $clrWarn
                        $txtReportLog.AppendText("$($item.Label)`r`n")
                    }
                    elseif ($item.Kind -eq 'imported') { $script:importedDataset = $item.Dataset }
                    elseif ($item.Kind -eq 'error') { $txtReportLog.AppendText("$($item.Message)`r`n") }
                } `
                -OnFinished {
                    param($state, $jobErrors)
                    $btnImportSharpHound.Enabled = $true
                    $btnReport.Enabled = $true
                    $btnCancelReport.Enabled = $false
                    $progReport.Style = 'Blocks'
                    $progReport.Value = if ($state -eq 'Completed') { 100 } else { 0 }
                    if ($script:importedDataset) {
                        Refresh-BHDatasetLists
                        $position = $lstDatasets.Items.IndexOf($script:importedDataset)
                        if ($position -ge 0) { $lstDatasets.SetItemChecked($position, $true) }
                        Set-BHStatus $lblReportProgress 'SharpHound import complete. Generate the report to explore it.' $clrOk
                    }
                    else {
                        Set-BHStatus $lblReportProgress "SharpHound import: $state" $clrWarn
                        foreach ($errorRecord in $jobErrors) { $txtReportLog.AppendText("$errorRecord`r`n") }
                    }
                }
        })

        $btnReport.Add_Click({
            $selected = @($lstDatasets.CheckedItems)
            if ($selected.Count -lt 1) {
                [System.Windows.Forms.MessageBox]::Show('Pick at least one scan or merged dataset.', 'BH', 'OK', 'Warning') | Out-Null
                return
            }
            $baselinePath = $null
            if ($chkCompareReport.Checked) {
                $dialog = [System.Windows.Forms.OpenFileDialog]::new()
                try {
                    $dialog.Title = 'Select graph.sqlite from the previous report'
                    $dialog.Filter = 'BHTest graph index (*.sqlite)|*.sqlite'
                    if ($dialog.ShowDialog($form) -ne 'OK') { return }
                    $baselinePath = $dialog.FileName
                }
                finally { $dialog.Dispose() }
            }
            $btnReport.Enabled = $false
            $btnAnalysisSettings.Enabled = $false; $chkResumeAnalysis.Enabled = $false
            $script:lastAnalysisResult = $null
            $script:activeReportAnalysis = @{ Mode = $script:reportAnalysis.Mode; CancelFile = (Join-Path ([IO.Path]::GetFullPath($txtReportOut.Text)) 'analysis.cancel') }
            $btnImportSharpHound.Enabled = $false
            $btnCancelReport.Enabled = $true
            $btnOpenReport.Enabled = $false
            $script:lastReportPath = $null
            $txtReportLog.Clear()
            $progReport.Style = 'Marquee'; $progReport.MarqueeAnimationSpeed = 30
            Set-BHStatus $lblReportProgress 'Starting...' $clrWarn

            $sb = {
                param($srcDir, $datasetPaths, $outDir, $exportCsv, $baselinePath, $settings, $resume)
                $ErrorActionPreference = 'Stop'
                Import-Module (Join-Path $srcDir 'BH.Core.psm1') -Force -DisableNameChecking
                Import-Module (Join-Path $srcDir 'BH.Report.psm1') -Force -DisableNameChecking
                Initialize-BHNative
                try {
                    Start-BHReport -DatasetPaths $datasetPaths -OutputDir $outDir -ExportCsv:$exportCsv -BaselineIndexPath $baselinePath `
                        -AnalysisMode $settings.Mode -AnalysisDepth $settings.Depth -AnalysisVisitLimit $settings.Visits -AnalysisSeconds $settings.Seconds -ResumeAnalysis:$resume -ProgressCallback {
                        param($label) Write-Information -InformationAction Continue -MessageData ([pscustomobject]@{ Kind = 'progress'; Label = $label })
                    } | ForEach-Object {
                        if ($_ -is [string]) {
                            $db = [BH.Native.GraphIndex]::new((Join-Path (Split-Path $_ -Parent) 'graph.sqlite'), $false)
                            try { $analysisState = $db.DiskAnalysisState() } finally { $db.Dispose() }
                            [pscustomobject]@{ Kind = 'result'; ReportPath = $_; AnalysisStatus = $analysisState.Status; StopReason = $analysisState.StopReason }
                        } else { $_ }
                    }
                }
                catch {
                    Write-Output ([pscustomobject]@{ Kind = 'error'; Message = $_.Exception.Message })
                    throw
                }
            }

            $script:reportRunner = New-BHJobRunner -ScriptBlock $sb -ArgumentList @($srcDir, [string[]]$selected, $txtReportOut.Text, $chkReportCsv.Checked, $baselinePath, $script:reportAnalysis, $chkResumeAnalysis.Checked) `
                -OnProgress {
                    param($item)
                    switch ($item.Kind) {
                        'progress' { Set-BHStatus $lblReportProgress $item.Label ([System.Drawing.Color]::Black); $txtReportLog.AppendText("$($item.Label)`r`n") }
                        'result' { $script:lastReportPath = $item.ReportPath; $script:lastAnalysisResult = $item; $txtReportLog.AppendText("Report: $($item.ReportPath)`r`nAnalysis: $($item.AnalysisStatus) - $($item.StopReason)`r`n") }
                        'error' { Set-BHStatus $lblReportProgress "Failed: $($item.Message)" $clrErr; $txtReportLog.AppendText("ERROR: $($item.Message)`r`n") }
                    }
                } `
                -OnFinished {
                    param($state, $jobErrors)
                    $btnReport.Enabled = $true
                    $btnAnalysisSettings.Enabled = $true; $chkResumeAnalysis.Enabled = $script:reportAnalysis.Mode -eq 'Deep'
                    $script:activeReportAnalysis = $null
                    $btnImportSharpHound.Enabled = $true
                    $btnCancelReport.Enabled = $false
                    $progReport.Style = 'Blocks'; $progReport.Value = if ($state -eq 'Completed') { 100 } else { 0 }
                    $finalColor = switch ($state) { 'Completed' { $clrOk }; 'Stopped' { $clrWarn }; default { $clrErr } }
                    Set-BHStatus $lblReportProgress "Finished: $state" $finalColor
                    if ($script:lastAnalysisResult) {
                        $color = if ($script:lastAnalysisResult.AnalysisStatus -eq 'Completed') { $clrOk } else { $clrWarn }
                        Set-BHStatus $lblReportProgress "Report ready. Analysis: $($script:lastAnalysisResult.AnalysisStatus)" $color
                    }
                    if ($jobErrors -and $jobErrors.Count -gt 0) { foreach ($e in $jobErrors) { $txtReportLog.AppendText("ERROR: $e`r`n") } }
                    if ($script:lastReportPath) { $btnOpenReport.Enabled = $true }
                }
        })

        $btnCancelReport.Add_Click({
            if ($script:activeReportAnalysis -and $script:activeReportAnalysis.Mode -eq 'Deep') {
                [void][IO.Directory]::CreateDirectory((Split-Path $script:activeReportAnalysis.CancelFile -Parent))
                [IO.File]::WriteAllText($script:activeReportAnalysis.CancelFile, 'cancel')
                Set-BHStatus $lblReportProgress 'Deep cancellation requested. Preparation/comparison may finish; traversal saves a checkpoint.' $clrWarn
                return
            }
            Set-BHStatus $lblReportProgress 'Cancelling...' $clrWarn
            Stop-BHRunner $script:reportRunner
        })

        $btnOpenReport.Add_Click({
            if ($script:lastReportPath -and (Test-Path $script:lastReportPath)) {
                Start-Process $script:lastReportPath
            }
        })

        . (Join-Path $srcDir 'BH.PathExplorer.ps1')
        $pathExplorer = New-BHPathExplorer -ReportsRoot $reportsRoot -GetLastReport { $script:lastReportPath }
        $tabs.TabPages.Add($pathExplorer)

        # ---- Status strip ------------------------------------------------------
        $statusStrip = New-Object System.Windows.Forms.StatusStrip
        $statusStrip.BackColor = $clrSurface
        $statusStrip.ForeColor = $clrMuted
        $statusStrip.SizingGrip = $false
        $statusLabel = New-Object System.Windows.Forms.ToolStripStatusLabel
        $statusLabel.Text = "Data folder: $DataRoot"
        $statusLabel.Spring = $true
        $statusLabel.TextAlign = 'MiddleLeft'
        [void]$statusStrip.Items.Add($statusLabel)
        $form.Controls.Add($statusStrip)

        # WinForms docks the LAST-added control first, so a Fill control added
        # before the banner/status strip ends up laid out underneath them: the
        # tab strip was hidden behind the banner.
        # Bringing the tabs to the front makes them dock last, in the space
        # that is actually left.
        $tabs.BringToFront()

        Refresh-BHDatasetLists
        if ($lstScans.Items.Count -eq 0) {
            $lblMergeProgress.Text = 'Completed scans appear here after collection.'
            $lblReportProgress.Text = 'Run a scan to create your first report dataset.'
        }
        Load-BHGuiSettings
        $form.Add_FormClosing({
            param($s, $e)
            if ($script:scanRunner -and $script:scanRunner.Job -and $script:scanRunner.Job.State -eq 'Running') {
                $answer = [System.Windows.Forms.MessageBox]::Show(
                    'A scan is still running. Closing this window stops it (the log file keeps what finished so far). Close anyway?',
                    'BH', 'YesNo', 'Warning')
                if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { $e.Cancel = $true; return }
            }
            Save-BHGuiSettings
        })
        $form.Add_Shown({ $form.Activate() })
        [void]$form.ShowDialog()
    }
}
