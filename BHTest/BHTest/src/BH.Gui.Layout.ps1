function New-BHLayout {
    param([string[]] $Columns, [string[]] $Rows, [int] $Padding = 0)

    $layout = [System.Windows.Forms.TableLayoutPanel]::new()
    $layout.Dock = 'Fill'
    $layout.Margin = [System.Windows.Forms.Padding]::new(0)
    $layout.Padding = [System.Windows.Forms.Padding]::new($Padding)
    $layout.ColumnCount = $Columns.Count
    $layout.RowCount = $Rows.Count
    foreach ($size in $Columns) {
        $kind = if ($size.EndsWith('%')) { 'Percent' } else { 'Absolute' }
        [void]$layout.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new($kind, [int]$size.TrimEnd('%')))
    }
    foreach ($size in $Rows) {
        $kind = if ($size.EndsWith('%')) { 'Percent' } else { 'Absolute' }
        [void]$layout.RowStyles.Add([System.Windows.Forms.RowStyle]::new($kind, [int]$size.TrimEnd('%')))
    }
    return $layout
}

function Add-BHCell {
    param($Layout, $Control, [int] $Column, [int] $Row, [int] $Span = 1)

    $Layout.Controls.Add($Control, $Column, $Row)
    $Layout.SetColumnSpan($Control, $Span)
    if ($Control -is [System.Windows.Forms.Label]) {
        $Control.TextAlign = 'MiddleLeft'
        $Control.AutoEllipsis = $true
    }
    if (($Control -is [System.Windows.Forms.TextBox] -and -not $Control.Multiline) -or
        $Control -is [System.Windows.Forms.NumericUpDown]) {
        $Control.Anchor = 'Left,Right'
    }
    else {
        $Control.Dock = 'Fill'
    }
}

function Set-BHButtonStyle {
    param([System.Windows.Forms.Button] $Button, [switch] $Primary)

    if ([System.Windows.Forms.SystemInformation]::HighContrast) { return }
    $Button.FlatStyle = 'Flat'
    $Button.UseVisualStyleBackColor = $false
    $Button.FlatAppearance.BorderSize = 1
    if ($Primary) {
        $Button.ForeColor = [System.Drawing.Color]::White
        $Button.FlatAppearance.MouseOverBackColor = $clrAccentHover
        $Button.FlatAppearance.MouseDownBackColor = $clrHeaderBg
        $activeColor = $clrAccent
        $disabledBackground = $clrBodyBg
        $disabledBorder = $clrBorder
        $update = {
            param($sender, $eventArgs)
            $sender.BackColor = if ($sender.Enabled) { $activeColor } else { $disabledBackground }
            $sender.FlatAppearance.BorderColor = if ($sender.Enabled) { $activeColor } else { $disabledBorder }
        }.GetNewClosure()
        $Button.Add_EnabledChanged($update)
        & $update $Button $null
    }
    else {
        $Button.BackColor = $clrSurface
        $Button.ForeColor = $clrText
        $Button.FlatAppearance.BorderColor = $clrBorder
        $Button.FlatAppearance.MouseOverBackColor = $clrActiveBg
        $Button.FlatAppearance.MouseDownBackColor = $clrBorder
    }
}

function Set-BHLogStyle {
    param([System.Windows.Forms.TextBox] $Log)
    $Log.Font = $fontMono
    $Log.BackColor = $clrSurface
    $Log.ForeColor = $clrText
    $Log.BorderStyle = 'FixedSingle'
    $Log.AccessibleName = 'Activity log'
}

function Set-BHDatasetLayout {
    param($Page, $Help, $Datasets, $OutputLabel, $OutputPath, $Browse, $Run, $Cancel,
        $Progress, $Status, $Log, $Extra, $Open, $Import, $ExtraWidth = 126, $AnalysisOptions)

    $offset = if ($AnalysisOptions) { 1 } else { 0 }
    $rows = if ($AnalysisOptions) { @(36,40,'57%',40,44,28,'43%') } else { @(36,'57%',40,44,28,'43%') }
    $root = New-BHLayout @('100%') $rows 18
    $Page.Controls.Add($root)
    $headingColumns = if ($Import) { @('100%', 155, $ExtraWidth) } else { @('100%', $ExtraWidth) }
    $heading = New-BHLayout $headingColumns @('100%')
    Add-BHCell $root $heading 0 0
    Add-BHCell $heading $Help 0 0
    if ($Import) { Add-BHCell $heading $Import 1 0 }
    Add-BHCell $heading $Extra ($headingColumns.Count - 1) 0
    if ($AnalysisOptions) { Add-BHCell $root $AnalysisOptions 0 1 }
    Add-BHCell $root $Datasets 0 (1 + $offset)

    $output = New-BHLayout @(160, '100%', 100) @('100%')
    Add-BHCell $root $output 0 (2 + $offset)
    Add-BHCell $output $OutputLabel 0 0
    Add-BHCell $output $OutputPath 1 0
    Add-BHCell $output $Browse 2 0

    $actions = New-BHLayout @(150, 90, 160, '100%') @('100%')
    Add-BHCell $root $actions 0 (3 + $offset)
    Add-BHCell $actions $Run 0 0
    Add-BHCell $actions $Cancel 1 0
    $progressColumn = 2
    if ($Open) {
        Add-BHCell $actions $Open 2 0
        $progressColumn = 3
    }
    Add-BHCell $actions $Progress $progressColumn 0 (4 - $progressColumn)
    $Progress.Dock = 'None'
    $Progress.Anchor = 'Left,Right'
    $Progress.Height = 16
    Add-BHCell $root $Status 0 (4 + $offset)
    Add-BHCell $root $Log 0 (5 + $offset)
    Set-BHLogStyle $Log
}
