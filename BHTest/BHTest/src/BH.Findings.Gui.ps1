function Show-BHFindingBrowser {
    param($Owner, [string] $IndexPath)
    $dialog=[System.Windows.Forms.Form]::new();$dialog.Text='All discovered exposure candidates'
    $dialog.Size=[Drawing.Size]::new(1040,680);$dialog.MinimumSize=[Drawing.Size]::new(720,440)
    $dialog.StartPosition='CenterParent';$dialog.ShowInTaskbar=$false
    $db=[BH.Native.GraphIndex]::new($IndexPath,$false)
    try {
        $layout=[System.Windows.Forms.TableLayoutPanel]::new();$layout.Dock='Fill';$layout.Padding=[System.Windows.Forms.Padding]::new(12)
        $layout.ColumnCount=1;$layout.RowCount=3
        [void]$layout.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Absolute',52))
        [void]$layout.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Percent',100))
        [void]$layout.RowStyles.Add([System.Windows.Forms.RowStyle]::new('Absolute',42))
        $dialog.Controls.Add($layout)
        $state=$db.DiskAnalysisState();$heading=[System.Windows.Forms.Label]::new();$heading.Dock='Fill'
        $heading.Text="Analysis: $($state.Status). $($state.StopReason)`r`n$($state.Visited.ToString('N0')) visited; $($state.Processed.ToString('N0')) processed; $($state.Pending.ToString('N0')) pending; $($state.Findings.ToString('N0')) candidates. Pages contain 100 rows; validate recorded paths."
        $layout.Controls.Add($heading,0,0)
        $grid=[System.Windows.Forms.DataGridView]::new();$grid.Dock='Fill';$grid.ReadOnly=$true;$grid.AllowUserToAddRows=$false
        $grid.AllowUserToDeleteRows=$false;$grid.RowHeadersVisible=$false;$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$false
        $grid.AutoSizeColumnsMode='Fill';$grid.AccessibleName='All discovered finding rows'
        foreach($name in 'Source','Target','Hops','Priority','Score'){[void]$grid.Columns.Add($name,$name);$grid.Columns[$name].SortMode='NotSortable'}
        $grid.Columns['Source'].FillWeight=32;$grid.Columns['Target'].FillWeight=32;$grid.Columns['Hops'].FillWeight=8;$grid.Columns['Priority'].FillWeight=22;$grid.Columns['Score'].FillWeight=8
        $layout.Controls.Add($grid,0,1)
        $actions=[System.Windows.Forms.FlowLayoutPanel]::new();$actions.Dock='Fill';$actions.WrapContents=$false
        $previous=[System.Windows.Forms.Button]::new();$previous.Text='Previous';$next=[System.Windows.Forms.Button]::new();$next.Text='Next'
        $inspect=[System.Windows.Forms.Button]::new();$inspect.Text='Inspect path';$inspect.Width=100
        $pageLabel=[System.Windows.Forms.Label]::new();$pageLabel.AutoSize=$true;$pageLabel.Margin=[System.Windows.Forms.Padding]::new(10,7,0,0)
        $actions.Controls.AddRange(@($previous,$next,$inspect,$pageLabel));$layout.Controls.Add($actions,0,2)
        $paging=@{Current=@{Score=[int]::MaxValue;Sequence=0L};Next=$null;History=[Collections.Generic.List[object]]::new()}
        $load={
            $grid.Rows.Clear()
            $rows=@($db.DiskFindingPage($paging.Current.Score,$paging.Current.Sequence,101))
            $visible=@($rows | Select-Object -First 100)
            foreach($row in $visible){
                $source=$db.Node($row.Source);$target=$db.Node($row.Target)
                $from=if($source -and $source[0]){$source[0]}else{$row.Source}
                $to=if($target -and $target[0]){$target[0]}else{$row.Target}
                $index=$grid.Rows.Add([object[]]@($from,$to,$row.HopCount,$row.Priority,$row.Score));$grid.Rows[$index].Tag=$row
            }
            $next.Enabled=$rows.Count -gt 100;$previous.Enabled=$paging.History.Count -gt 0;$inspect.Enabled=$visible.Count -gt 0
            $pageLabel.Text="Page $($paging.History.Count+1) · $($visible.Count) rows"
            if($visible.Count){$last=$visible[-1];$paging.Next=@{Score=$last.Score;Sequence=$last.Sequence}}
        }.GetNewClosure()
        $next.Add_Click({$paging.History.Add($paging.Current);$paging.Current=$paging.Next;& $load}.GetNewClosure())
        $previous.Add_Click({$paging.Current=$paging.History[$paging.History.Count-1];$paging.History.RemoveAt($paging.History.Count-1);& $load}.GetNewClosure())
        $showDetails = ${function:Show-BHGraphObjectDetails}
        $showPath={
            if(-not $grid.CurrentRow){return}
            $finding=$grid.CurrentRow.Tag
            $summary=[ordered]@{Source=$finding.Source;Target=$finding.Target;HopCount=$finding.HopCount;Priority=$finding.Priority;Score=$finding.Score;Reasons=@($finding.Reasons)
                PathPreviewLimited=$finding.PathLimited;NextPathId=$finding.NextPathId;Note='One recorded chain; validate effective access and collection coverage. Long paths can be continued with the TraceFinding CLI.'}
            $steps=@(foreach($edge in $finding.Steps){
                $from=$db.Node($edge.Source);$to=$db.Node($edge.Target)
                @{From=$(if($from){$from[0]}else{$edge.Source});Relationship=$edge.EdgeType;To=$(if($to){$to[0]}else{$edge.Target});SourceId=$edge.Source;TargetId=$edge.Target}
            })
            & $showDetails $dialog ([pscustomobject]@{Id='Recorded path';Record=($summary | ConvertTo-Json -Depth 5);Relationships=$steps;RelationshipNote='Recorded path steps, with at most 256 embedded at once. Long paths can be continued through the TraceFinding CLI.'}) -Title 'Exposure candidate details'
        }.GetNewClosure()
        $inspect.Add_Click($showPath);$grid.Add_CellDoubleClick({& $showPath}.GetNewClosure())
        & $load
        [void]$dialog.ShowDialog($Owner)
    }finally{$db.Dispose();$dialog.Dispose()}
}
