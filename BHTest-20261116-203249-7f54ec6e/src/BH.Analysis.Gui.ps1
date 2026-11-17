function Show-BHAnalysisSettings {
    param($Owner, [hashtable] $Settings)
    $dialog=[System.Windows.Forms.Form]::new()
    $dialog.Text='Analysis settings';$dialog.ClientSize=[Drawing.Size]::new(580,330)
    $dialog.StartPosition='CenterParent';$dialog.FormBorderStyle='FixedDialog'
    $dialog.MaximizeBox=$false;$dialog.MinimizeBox=$false;$dialog.ShowInTaskbar=$false
    try {
        $layout=New-BHLayout @(230,'100%') @(65,38,38,38,38,42) 14
        $dialog.Controls.Add($layout)
        $note=New-BHLabel 'Deep analysis saves search state on disk and can resume. Zero removes a particular limit. The HTML preview remains limited independently of discovery.'
        Add-BHCell $layout $note 0 0 2
        $mode=[System.Windows.Forms.ComboBox]::new();$mode.DropDownStyle='DropDownList';$mode.AccessibleName='Analysis mode'
        [void]$mode.Items.Add('Quick');[void]$mode.Items.Add('Deep');$mode.SelectedItem=$Settings.Mode
        $depth=[System.Windows.Forms.NumericUpDown]::new();$depth.Maximum=1000000;$depth.Value=$Settings.Depth;$depth.AccessibleName='Analysis maximum hops'
        $visits=[System.Windows.Forms.NumericUpDown]::new();$visits.Maximum=1000000000;$visits.ThousandsSeparator=$true;$visits.Value=$Settings.Visits;$visits.AccessibleName='Analysis identity budget'
        $seconds=[System.Windows.Forms.NumericUpDown]::new();$seconds.Maximum=86400;$seconds.Value=$Settings.Seconds;$seconds.AccessibleName='Analysis time budget seconds'
        Add-BHCell $layout (New-BHLabel 'Mode') 0 1;Add-BHCell $layout $mode 1 1
        Add-BHCell $layout (New-BHLabel 'Maximum hops (0 = no cap)') 0 2;Add-BHCell $layout $depth 1 2
        Add-BHCell $layout (New-BHLabel 'Identity budget (0 = no cap)') 0 3;Add-BHCell $layout $visits 1 3
        Add-BHCell $layout (New-BHLabel 'Time budget in seconds (0 = no cap)') 0 4;Add-BHCell $layout $seconds 1 4
        $mode.Add_SelectedIndexChanged({
            if($mode.SelectedItem -eq 'Deep'){$depth.Value=0;$visits.Value=0;$seconds.Value=600}
            else{$depth.Value=8;$visits.Value=50000;$seconds.Value=0}
        }.GetNewClosure())
        $buttons=New-BHLayout @('100%',90,90) @('100%')
        Add-BHCell $layout $buttons 0 5 2
        $apply=New-BHButton 'Apply' -Primary;$apply.DialogResult='OK'
        $cancel=New-BHButton 'Cancel';$cancel.DialogResult='Cancel'
        Add-BHCell $buttons $apply 1 0;Add-BHCell $buttons $cancel 2 0
        $dialog.AcceptButton=$apply;$dialog.CancelButton=$cancel
        if($dialog.ShowDialog($Owner) -eq 'OK') {
            return @{Mode=[string]$mode.SelectedItem;Depth=[int]$depth.Value;Visits=[long]$visits.Value;Seconds=[int]$seconds.Value}
        }
    } finally {$dialog.Dispose()}
}
