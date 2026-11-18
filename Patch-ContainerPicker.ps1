# Adds the top-level containers (CN=Users, CN=Computers, CN=Builtin, ...) to the scan target picker.
# Run from the folder that contains BH.Gui.ps1 and the src folder. Backups are written first.
$files = @{
    core = (Resolve-Path '.\src\BH.Core.psm1').Path
    gui  = (Resolve-Path '.\BH.Gui.ps1').Path
}
function Read-Text($path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    [pscustomobject]@{ Text = [IO.File]::ReadAllText($path); Bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) }
}
function Count-Of($haystack, $needle) { ($haystack.Split([string[]]@($needle), [StringSplitOptions]::None).Count) - 1 }
$core = Read-Text $files.core
$gui = Read-Text $files.gui
if ($core.Text.Contains('Top-level containers (CN=Users')) { 'Already patched. Nothing to do.'; return }
$plans = @(
    [pscustomobject]@{ Name = 'BH.Core.psm1'; Path = $files.core; File = $core; Edits = @() },
    [pscustomobject]@{ Name = 'BH.Gui.ps1'; Path = $files.gui; File = $gui; Edits = @() }
)
$plans[0].Edits += ,@(@'
foreach ($entry in (Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn $root
'@, @'
# Top-level containers (CN=Users, CN=Computers, CN=Builtin, ...) are not OUs, so the OU search below never returns them.
    # They hold the built-in privileged groups, so list the ones directly under the domain root as well.
    # Failing here must not break the OU list.
    try {
        foreach ($entry in (Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn $root `
            -Filter '(|(objectClass=container)(objectClass=builtinDomain))' -Attributes @('distinguishedName', 'name') `
            -Scope ([System.DirectoryServices.Protocols.SearchScope]::OneLevel) -PageSize 500 -MaxRetries 1)) {

            $dn = $entry.DistinguishedName
            $name = if ($entry.Attributes.Contains('name')) { $entry.Attributes['name'][0].ToString() } else { $dn }
            $list.Add([pscustomobject]@{ Dn = $dn; Name = "$name (container)"; ParentDn = $root })
        }
    }
    catch { }

    foreach ($entry in (Invoke-BHPagedSearch -ConnectionFactory $factory -BaseDn $root
'@)
$plans[0].Edits += ,@(@'
Lists the domain root plus every organizationalUnit under it (flat,
'@, @'
Lists the domain root, the top-level containers directly under it, plus every organizationalUnit under it (flat,
'@)
$plans[1].Edits += ,@(@'
$script:ouCachePath = Join-Path $DataRoot 'ou-cache.tsv'
'@, @'
$script:ouCachePath = Join-Path $DataRoot 'ou-cache-v2.tsv'
'@)
$plans[1].Edits += ,@(@'
Loaded {0} OU(s) from cache
'@, @'
Loaded {0} OU(s) and container(s) from cache
'@)
$plans[1].Edits += ,@(@'
OU(s)/domain root found
'@, @'
OU(s), top-level container(s) and the domain root found
'@)
# Check every edit in both files before changing anything.
foreach ($plan in $plans) {
    foreach ($e in $plan.Edits) {
        $n = Count-Of $plan.File.Text $e[0]
        if ($n -ne 1) { throw "Cannot patch $($plan.Name): expected exactly 1 match but found $n for: $($e[0].Substring(0, [Math]::Min(70, $e[0].Length)))... These files are not the version this patch was written for. Nothing was changed." }
    }
}
foreach ($plan in $plans) {
    Copy-Item $plan.Path "$($plan.Path).container.bak" -Force
    $text = $plan.File.Text
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    foreach ($e in $plan.Edits) { $text = $text.Replace($e[0], $e[1].Replace("`n", $nl)) }
    [IO.File]::WriteAllText($plan.Path, $text, (New-Object System.Text.UTF8Encoding($plan.File.Bom)))
    'Patched ' + $plan.Name + '  (backup: ' + $plan.Path + '.container.bak)'
}
'Open the tool in a NEW PowerShell window, then click "Connect and load OUs" once to refresh the list.'
