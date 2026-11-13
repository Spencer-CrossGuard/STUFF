. (Join-Path $PSScriptRoot 'BH.Findings.Gui.ps1')

function Show-BHPathEdgeFilters {
    param($Owner, [string[]] $Types, [string[]] $Selected, [scriptblock] $MakeLayout, [scriptblock] $AddCell)

    $dialog = [System.Windows.Forms.Form]::new()
    $dialog.Text = 'Filter path relationships'
    $dialog.ClientSize = [System.Drawing.Size]::new(396, 470)
    $dialog.StartPosition = 'CenterParent'
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ShowInTaskbar = $false
    try {
        $layout = & $MakeLayout @('100%') @(32, '100%', 40) 12
        $dialog.Controls.Add($layout)
        $label = [System.Windows.Forms.Label]::new()
        $label.Text = 'Choose which relationships the path search can use.'
        & $AddCell $layout $label 0 0
        $choices = [System.Windows.Forms.CheckedListBox]::new()
        $choices.CheckOnClick = $true
        $choices.IntegralHeight = $false
        $choices.AccessibleName = 'Allowed relationships'
        foreach ($type in $Types) { [void]$choices.Items.Add($type, $null -eq $Selected -or $Selected -contains $type) }
        & $AddCell $layout $choices 0 1

        $actions = & $MakeLayout @(94, 94, '100%', 78, 78) @('100%')
        & $AddCell $layout $actions 0 2
        $all = [System.Windows.Forms.Button]::new()
        $all.Text = 'Select all'
        $all.Add_Click({
            for ($i = 0; $i -lt $choices.Items.Count; $i++) { $choices.SetItemChecked($i, $true) }
        }.GetNewClosure())
        & $AddCell $actions $all 0 0
        $none = [System.Windows.Forms.Button]::new()
        $none.Text = 'Clear all'
        $none.Add_Click({
            for ($i = 0; $i -lt $choices.Items.Count; $i++) { $choices.SetItemChecked($i, $false) }
        }.GetNewClosure())
        & $AddCell $actions $none 1 0
        $apply = [System.Windows.Forms.Button]::new()
        $apply.Text = 'Apply'
        $apply.DialogResult = 'OK'
        & $AddCell $actions $apply 3 0
        $cancel = [System.Windows.Forms.Button]::new()
        $cancel.Text = 'Cancel'
        $cancel.DialogResult = 'Cancel'
        & $AddCell $actions $cancel 4 0
        $dialog.AcceptButton = $apply
        $dialog.CancelButton = $cancel
        if ($dialog.ShowDialog($Owner) -eq 'OK') {
            return @{ Types = [string[]]@($choices.CheckedItems) }
        }
    }
    finally { $dialog.Dispose() }
}

function Show-BHGraphObjectDetails {
    param($Owner, $Details, [string] $Title = 'Collected object details')
    $dialog = [System.Windows.Forms.Form]::new()
    $dialog.Text = $Title
    $dialog.Size = [System.Drawing.Size]::new(860, 640)
    $dialog.MinimumSize = [System.Drawing.Size]::new(600, 400)
    $dialog.StartPosition = 'CenterParent'
    $dialog.ShowInTaskbar = $false
    try {
        $tabs = [System.Windows.Forms.TabControl]::new()
        $tabs.Dock = 'Fill'
        $dialog.Controls.Add($tabs)
        foreach ($name in 'Properties', 'Relationships') {
            $page = [System.Windows.Forms.TabPage]::new($name)
            $text = [System.Windows.Forms.TextBox]::new()
            $text.Multiline = $true; $text.ReadOnly = $true; $text.ScrollBars = 'Both'
            $text.Dock = 'Fill'; $text.WordWrap = $false
            $text.Font = [System.Drawing.Font]::new('Consolas', 10)
            $text.AccessibleName = "Collected object $($name.ToLowerInvariant())"
            if ($name -eq 'Properties') {
                $record = if ($Details.Record) { ($Details.Record | ConvertFrom-Json | ConvertTo-Json -Depth 20) } else { 'No retained object record in this index.' }
                $text.Text = "$($Details.Id)`r`n`r`n$record"
            }
            else {
                $relationshipNote = if ($Details.PSObject.Properties['RelationshipNote']) { $Details.RelationshipNote } else { 'Up to 100 recorded relationships; other relationships may exist. Collection timing is described in the report.' }
                $text.Text = "$relationshipNote`r`n`r`n" +
                    ($Details.Relationships | ConvertTo-Json -Depth 5)
            }
            $page.Controls.Add($text); [void]$tabs.TabPages.Add($page)
        }
        [void]$dialog.ShowDialog($Owner)
    }
    finally { $dialog.Dispose() }
}

function New-BHPathExplorer {
    param([string] $ReportsRoot, [scriptblock] $GetLastReport)

    $tab = [System.Windows.Forms.TabPage]::new('Path Explorer')
    $tab.BackColor = $clrBodyBg

    $layout = [System.Windows.Forms.TableLayoutPanel]::new()
    $layout.Dock = 'Fill'
    $layout.Padding = [System.Windows.Forms.Padding]::new(12)
    $layout.ColumnCount = 2
    $layout.RowCount = 4
    foreach ($width in 50, 50) {
        [void]$layout.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Percent', $width))
    }
    foreach ($height in 40, 235, 42) {
        [void]$layout.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Absolute', $height))
    }
    [void]$layout.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Percent', 100))
    $tab.Controls.Add($layout)

    $fileRow = [System.Windows.Forms.TableLayoutPanel]::new()
    $fileRow.Dock = 'Fill'
    $fileRow.ColumnCount = 5
    [void]$fileRow.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Absolute', 96))
    [void]$fileRow.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Percent', 100))
    [void]$fileRow.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Absolute', 86))
    [void]$fileRow.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Absolute', 116))
    [void]$fileRow.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Absolute', 96))
    $fileLabel = [System.Windows.Forms.Label]::new()
    $fileLabel.Text = 'Graph index'
    $fileLabel.TextAlign = 'MiddleLeft'
    $fileLabel.Dock = 'Fill'
    $indexPath = [System.Windows.Forms.TextBox]::new()
    $indexPath.AccessibleName = 'Graph index path'
    $indexPath.Anchor = 'Left,Right'
    $browse = [System.Windows.Forms.Button]::new()
    $browse.Text = 'Browse...'
    $browse.Dock = 'Fill'
    Set-BHButtonStyle $browse
    $latest = [System.Windows.Forms.Button]::new()
    $latest.Text = 'Use last report'
    $latest.Dock = 'Fill'
    Set-BHButtonStyle $latest
    $findingsButton = [System.Windows.Forms.Button]::new()
    $findingsButton.Text = 'Findings...'; $findingsButton.Dock = 'Fill'
    $findingsButton.AccessibleName = 'Browse all findings'
    Set-BHButtonStyle $findingsButton
    $showFindingBrowser = ${function:Show-BHFindingBrowser}
    $findingsButton.Add_Click({
        if (Test-Path -LiteralPath $indexPath.Text -PathType Leaf) { & $showFindingBrowser $tab.FindForm() $indexPath.Text }
        else { [System.Windows.Forms.MessageBox]::Show('Choose an existing graph.sqlite first.', 'BHTest') | Out-Null }
    }.GetNewClosure())
    $fileRow.Controls.Add($fileLabel, 0, 0)
    $fileRow.Controls.Add($indexPath, 1, 0)
    $fileRow.Controls.Add($browse, 2, 0)
    $fileRow.Controls.Add($latest, 3, 0)
    $fileRow.Controls.Add($findingsButton, 4, 0)
    $layout.Controls.Add($fileRow, 0, 0)
    $layout.SetColumnSpan($fileRow, 2)

    $pickers = foreach ($title in 'Source', 'Target') {
        $group = [System.Windows.Forms.GroupBox]::new()
        $group.Text = $title
        $group.Dock = 'Fill'
        $group.BackColor = $clrSurface
        $picker = [System.Windows.Forms.TableLayoutPanel]::new()
        $picker.Dock = 'Fill'
        $picker.Padding = [System.Windows.Forms.Padding]::new(6)
        $picker.ColumnCount = 2
        $picker.RowCount = 3
        [void]$picker.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Percent', 100))
        [void]$picker.ColumnStyles.Add([System.Windows.Forms.ColumnStyle]::new('Absolute', 76))
        [void]$picker.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Absolute', 23))
        [void]$picker.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Absolute', 32))
        [void]$picker.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Percent', 100))
        $label = [System.Windows.Forms.Label]::new()
        $label.Text = 'Object name or ID (double-click a result for details)'
        $label.Dock = 'Fill'
        $query = [System.Windows.Forms.TextBox]::new()
        $query.AccessibleName = "$title object name or ID"
        $query.Anchor = 'Left,Right'
        $search = [System.Windows.Forms.Button]::new()
        $search.Text = 'Search'
        $search.AccessibleName = "Search $($title.ToLowerInvariant())"
        $search.Dock = 'Fill'
        Set-BHButtonStyle $search
        $results = [System.Windows.Forms.ListBox]::new()
        $results.AccessibleName = "$title search results"
        $results.DisplayMember = 'Label'
        $results.HorizontalScrollbar = $true
        $results.IntegralHeight = $false
        $results.Dock = 'Fill'
        $picker.Controls.Add($label, 0, 0)
        $picker.Controls.Add($query, 0, 1)
        $picker.Controls.Add($search, 1, 1)
        $picker.Controls.Add($results, 0, 2)
        $picker.SetColumnSpan($results, 2)
        $group.Controls.Add($picker)
        [pscustomobject]@{ Title = $title; Group = $group; Query = $query; Search = $search; Results = $results }
    }
    $layout.Controls.Add($pickers[0].Group, 0, 1)
    $layout.Controls.Add($pickers[1].Group, 1, 1)

    $actions = [System.Windows.Forms.FlowLayoutPanel]::new()
    $actions.Dock = 'Fill'
    $actions.WrapContents = $false
    $find = [System.Windows.Forms.Button]::new()
    $find.Text = 'Find shortest path'
    $find.Size = [System.Drawing.Size]::new(144, 31)
    $find.Enabled = $false
    Set-BHButtonStyle $find -Primary
    $filterButton = [System.Windows.Forms.Button]::new()
    $filterButton.Text = 'Filter edges...'
    $filterButton.Size = [System.Drawing.Size]::new(108, 31)
    Set-BHButtonStyle $filterButton
    $filter = @{ Types = [BH.Native.GraphRelationships]::FilterTypes(); Selected = $null }
    $limitLabel = [System.Windows.Forms.Label]::new()
    $limitLabel.Text = 'Visit limit'
    $limitLabel.AutoSize = $true
    $limitLabel.Margin = [System.Windows.Forms.Padding]::new(12, 10, 4, 0)
    $limit = [System.Windows.Forms.NumericUpDown]::new()
    $limit.AccessibleName = 'Path search visit limit'
    $limit.Minimum = 2
    $limit.Maximum = 5000000
    $limit.Value = 250000
    $limit.ThousandsSeparator = $true
    $limit.Width = 94
    $limit.Margin = [System.Windows.Forms.Padding]::new(0, 7, 10, 0)
    $status = [System.Windows.Forms.Label]::new()
    $status.AutoSize = $true
    $status.Margin = [System.Windows.Forms.Padding]::new(5, 10, 0, 0)
    $status.Text = 'Select a graph index and search for both objects.'
    $actions.Controls.AddRange(@($find, $filterButton, $limitLabel, $limit, $status))
    $layout.Controls.Add($actions, 0, 2)
    $layout.SetColumnSpan($actions, 2)

    $hops = [System.Windows.Forms.DataGridView]::new()
    $hops.AccessibleName = 'Shortest path hops'
    $hops.Dock = 'Fill'
    $hops.ReadOnly = $true
    $hops.AllowUserToAddRows = $false
    $hops.AllowUserToDeleteRows = $false
    $hops.RowHeadersVisible = $false
    $hops.AutoSizeColumnsMode = 'Fill'
    $hops.SelectionMode = 'CellSelect'
    $hops.BackgroundColor = $clrSurface
    $hops.BorderStyle = 'FixedSingle'
    $hops.RowTemplate.Height = 29
    if (-not [System.Windows.Forms.SystemInformation]::HighContrast) {
        $hops.EnableHeadersVisualStyles = $false
        $hops.ColumnHeadersDefaultCellStyle.BackColor = $clrActiveBg
        $hops.ColumnHeadersDefaultCellStyle.ForeColor = $clrText
        $hops.ColumnHeadersDefaultCellStyle.Font = $fontBold
        $hops.GridColor = $clrBorder
        $hops.AlternatingRowsDefaultCellStyle.BackColor = $clrBodyBg
        $hops.DefaultCellStyle.ForeColor = $clrText
    }
    foreach ($column in @(@('From', 42), @('Relationship', 16), @('To', 42))) {
        $columnId = $hops.Columns.Add($column[0], $column[0])
        $hops.Columns[$columnId].FillWeight = $column[1]
        $hops.Columns[$columnId].SortMode = 'NotSortable'
    }
    $layout.Controls.Add($hops, 0, 3)
    $layout.SetColumnSpan($hops, 2)

    $worker = @{
        Shell = $null
        Async = $null
        Finished = $null
        Context = $null
        Timer = [System.Windows.Forms.Timer]::new()
    }
    $worker.Timer.Interval = 80
    $queryScript = {
        param($Path, $Mode, $First, $Second, $MaxVisited, $AllowedTypes)
        $ErrorActionPreference = 'Stop'
        $db = [BH.Native.GraphIndex]::new($Path, $false)
        try {
            if ($Mode -eq 'Inspect') {
                [pscustomobject]@{ Id = $First; Record = $db.NodeRecord($First)
                    Relationships = @(foreach ($row in $db.ObjectRelationships($First, 100)) {
                        @{ Source = $row[0]; Target = $row[1]; Type = $row[2]; Bucket = $row[3] }
                    }) }
                return
            }
            if ($Mode -eq 'Search') {
                $exact = $db.Node($First)
                if ($exact) {
                    [pscustomobject]@{ Id = $First; Name = $exact[0]; Kind = $exact[1] }
                }
                else {
                    foreach ($row in $db.Search($First)) {
                        [pscustomobject]@{ Id = $row[0]; Name = $row[1]; Kind = $row[2] }
                    }
                }
                return
            }

            if (-not $db.Node($First) -or -not $db.Node($Second)) {
                throw 'An endpoint is missing from this graph index. Search for both objects again.'
            }
            $result = $db.ShortestPath($First, $Second, $MaxVisited, $AllowedTypes)
            $rows = @(foreach ($edge in $result.Edges) {
                $from = $db.Node($edge.Source)
                $to = $db.Node($edge.Target)
                [pscustomobject]@{
                    From = if ($from -and $from[0]) { $from[0] } else { $edge.Source }
                    To = if ($to -and $to[0]) { $to[0] } else { $edge.Target }
                    FromId = $edge.Source
                    ToId = $edge.Target
                    Relationship = $edge.EdgeType
                }
            })
            [pscustomobject]@{
                Found = $result.NodeIds.Count -gt 0
                Truncated = $result.Truncated
                Hops = $rows
            }
        }
        finally { $db.Dispose() }
    }
    $updateButtons = {
        $idle = $null -eq $worker.Shell
        $fileRow.Enabled = $idle
        $filterButton.Enabled = $idle
        foreach ($picker in $pickers) { $picker.Group.Enabled = $idle }
        $find.Enabled = $idle -and $pickers[0].Results.SelectedIndex -ge 0 -and $pickers[1].Results.SelectedIndex -ge 0
    }.GetNewClosure()
    $runQuery = {
        param([string] $Mode, [string] $First, [string] $Second, [scriptblock] $Finished, $Context)
        if ($worker.Shell) { return }
        $path = $indexPath.Text.Trim()
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $status.Text = 'Choose an existing graph.sqlite file.'
            return
        }
        $shell = [PowerShell]::Create()
        try {
            $fullPath = (Get-Item -LiteralPath $path -ErrorAction Stop).FullName
            [void]$shell.AddScript($queryScript.ToString()).AddArgument($fullPath).AddArgument($Mode).
                AddArgument($First).AddArgument($Second).AddArgument([int]$limit.Value).AddArgument($filter.Selected)
            $worker.Async = $shell.BeginInvoke()
            $worker.Shell = $shell
            $worker.Finished = $Finished
            $worker.Context = $Context
            $status.Text = if ($Mode -eq 'Search') { 'Searching the index...' } elseif ($Mode -eq 'Inspect') { 'Reading object details...' } else { 'Finding the shortest path...' }
            & $updateButtons
            $worker.Timer.Start()
        }
        catch {
            $shell.Dispose()
            $status.Text = $_.Exception.Message
        }
    }.GetNewClosure()
    $worker.Timer.Add_Tick({
        if (-not $worker.Async.IsCompleted) { return }
        $worker.Timer.Stop()
        try {
            $output = @($worker.Shell.EndInvoke($worker.Async))
            & $worker.Finished $output $worker.Context
        }
        catch {
            $status.Text = $_.Exception.GetBaseException().Message
        }
        finally {
            $worker.Shell.Dispose()
            $worker.Shell = $null
            $worker.Async = $null
            $worker.Finished = $null
            $worker.Context = $null
            & $updateButtons
        }
    }.GetNewClosure())
    $tab.Add_Disposed({
        $worker.Timer.Stop()
        $worker.Timer.Dispose()
        if ($worker.Shell) { $worker.Shell.Dispose() }
    }.GetNewClosure())

    $useLatest = {
        $report = & $GetLastReport
        $candidate = if ($report) { Join-Path (Split-Path -Parent $report) 'graph.sqlite' }
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $candidate = Get-ChildItem -LiteralPath $ReportsRoot -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName 'graph.sqlite') -ErrorAction SilentlyContinue } |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if ($candidate) { $indexPath.Text = $candidate }
        else { $status.Text = 'No recent graph index found. Browse for an existing graph.sqlite file.' }
    }.GetNewClosure()
    $latest.Add_Click($useLatest)
    $browse.Add_Click({
        $dialog = [System.Windows.Forms.OpenFileDialog]::new()
        try {
            $dialog.Filter = 'Graph indexes (*.sqlite)|*.sqlite|All files (*.*)|*.*'
            $dialog.CheckFileExists = $true
            $dialog.InitialDirectory = $ReportsRoot
            if ($dialog.ShowDialog() -eq 'OK') { $indexPath.Text = $dialog.FileName }
        }
        finally { $dialog.Dispose() }
    }.GetNewClosure())
    $indexPath.Add_TextChanged({
        foreach ($picker in $pickers) { $picker.Results.Items.Clear() }
        $hops.Rows.Clear()
        $status.Text = 'Search for a source and a target.'
        & $updateButtons
    }.GetNewClosure())

    $showResults = {
        param($rows, $picker)
        foreach ($row in $rows) {
            $name = if ($row.Name) { $row.Name } else { $row.Id }
            [void]$picker.Results.Items.Add([pscustomobject]@{
                Id = $row.Id
                Label = "$name [$($row.Kind)]  $($row.Id)"
            })
        }
        if ($rows.Count -eq 1) { $picker.Results.SelectedIndex = 0 }
        $status.Text = switch ($rows.Count) {
            0 { "No $($picker.Title.ToLowerInvariant()) objects matched." }
            1 { "$($picker.Title) selected." }
            100 { 'Showing the first 100 matches. Refine your search for more.' }
            default { "$($rows.Count) matches. Select a $($picker.Title.ToLowerInvariant())." }
        }
    }.GetNewClosure()
    $showObjectDetails = ${function:Show-BHGraphObjectDetails}
    $showObject = {
        param($rows)
        if ($rows.Count) { & $showObjectDetails $tab.FindForm() $rows[0] }
        $status.Text = 'Object details loaded.'
    }.GetNewClosure()
    foreach ($picker in $pickers) {
        $picker.Results.Add_DoubleClick({
            if ($picker.Results.SelectedIndex -ge 0) { & $runQuery 'Inspect' $picker.Results.SelectedItem.Id '' $showObject }
        }.GetNewClosure())
        $picker.Results.Add_SelectedIndexChanged({
            $hops.Rows.Clear()
            & $updateButtons
            if ($find.Enabled) { $status.Text = 'Ready to find the shortest path.' }
        }.GetNewClosure())
        $picker.Query.Add_TextChanged({
            $picker.Results.Items.Clear()
            $hops.Rows.Clear()
            & $updateButtons
        }.GetNewClosure())
        $picker.Search.Add_Click({
            $term = $picker.Query.Text.Trim()
            if (-not $term) { $status.Text = "Enter a $($picker.Title.ToLowerInvariant()) name or ID."; return }
            $picker.Results.Items.Clear()
            $hops.Rows.Clear()
            & $updateButtons
            & $runQuery 'Search' $term '' $showResults $picker
        }.GetNewClosure())
        $picker.Query.Add_KeyDown({
            param($sender, $eventArgs)
            if ($eventArgs.KeyCode -eq 'Enter') {
                $eventArgs.SuppressKeyPress = $true
                $picker.Search.PerformClick()
            }
        }.GetNewClosure())
    }
    $showPath = {
        param($rows)
        $result = $rows[0]
        if ($result.Truncated) { $status.Text = 'Visit limit reached. Increase it and try again.'; return }
        if (-not $result.Found) { $status.Text = 'No path found in this dataset.'; return }
        if ($result.Hops.Count -eq 0) { $status.Text = 'Source and target are the same object.'; return }
        foreach ($hop in $result.Hops) {
            $row = $hops.Rows.Add([object[]]@($hop.From, $hop.Relationship, $hop.To))
            $hops.Rows[$row].Cells[0].ToolTipText = $hop.FromId
            $hops.Rows[$row].Cells[2].ToolTipText = $hop.ToId
        }
        $status.Text = "Shortest path found. $($result.Hops.Count) hop(s)."
    }.GetNewClosure()
    $find.Add_Click({
        $hops.Rows.Clear()
        & $runQuery 'Path' $pickers[0].Results.SelectedItem.Id $pickers[1].Results.SelectedItem.Id $showPath
    }.GetNewClosure())
    $showFilters = ${function:Show-BHPathEdgeFilters}
    $makeLayout = ${function:New-BHLayout}
    $addCell = ${function:Add-BHCell}
    $filterButton.Add_Click({
        $choice = & $showFilters -Owner $tab.FindForm() -Types $filter.Types -Selected $filter.Selected -MakeLayout $makeLayout -AddCell $addCell
        if ($choice) {
            if ($choice.Types.Count -eq $filter.Types.Length) { $filter.Selected = $null }
            else { $filter.Selected = [string[]]$choice.Types }
            $filterButton.Text = if ($null -eq $filter.Selected) { 'Filter edges...' } else { "Edges: $($choice.Types.Count) selected" }
            $hops.Rows.Clear()
            $status.Text = 'Edge filters updated.'
        }
    }.GetNewClosure())
    & $useLatest
    return $tab
}
