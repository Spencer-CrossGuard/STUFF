#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$IndexPath,[string]$ScreenshotDirectory,[string]$ViewerPath)
$ErrorActionPreference='Stop'
if(-not $ViewerPath){$ViewerPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'BHTest.Viewer.ps1'}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::SetUnhandledExceptionMode([Windows.Forms.UnhandledExceptionMode]::ThrowException)
[Windows.Forms.Control]::CheckForIllegalCrossThreadCalls=$true
Write-Host "Desktop QA process: $PID"
$content=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $ViewerPath).ProviderPath,[Text.Encoding]::UTF8)
$match=[regex]::Match($content,"(?s)\`$native=@'\r?\n(.*?)\r?\n'@")
if(-not $match.Success){throw 'Standalone embedded native source is missing.'}
if($PSVersionTable.PSEdition -eq 'Core'){
 $references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
 $references+=@([Windows.Forms.Form].Assembly.Location,[Drawing.Graphics].Assembly.Location)
 $references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*Windows*.dll'|Where-Object {$_.Name -match '^System\.(Windows\.Forms\.|Private\.Windows\.)'}|ForEach-Object FullName)
}else{$references=@('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll')}
Add-Type -TypeDefinition $match.Groups[1].Value -ReferencedAssemblies $references
Add-Type -TypeDefinition @'
using System;using System.Diagnostics;using System.Runtime.InteropServices;
public static class DesktopPortProbe {
 [DllImport("iphlpapi.dll",SetLastError=true)]static extern uint GetExtendedTcpTable(IntPtr table,ref int bytes,bool order,int family,int tableClass,uint reserved);
 public static int Listeners(){int count=0;foreach(int family in new int[]{2,23}){int bytes=0;uint code=GetExtendedTcpTable(IntPtr.Zero,ref bytes,false,family,5,0);if(code!=122&&code!=0)throw new Exception("TCP table unavailable: "+code);IntPtr table=Marshal.AllocHGlobal(bytes);try{code=GetExtendedTcpTable(table,ref bytes,false,family,5,0);if(code!=0)throw new Exception("TCP table unavailable: "+code);int rows=Marshal.ReadInt32(table),stride=family==2?24:56,state=family==2?0:48,pid=family==2?20:52;for(int i=0;i<rows;i++){IntPtr row=IntPtr.Add(table,4+i*stride);if(Marshal.ReadInt32(row,state)==2&&Marshal.ReadInt32(row,pid)==Process.GetCurrentProcess().Id)count++;}}finally{Marshal.FreeHGlobal(table);}}return count;}
}
'@
function Assert($condition,$message){if(-not $condition){throw $message}}
$sets=(Get-Command $ViewerPath).ParameterSets
Assert (@($sets|Where-Object {$_.Name -eq 'Desktop' -and $_.IsDefault}).Count -eq 1) 'Desktop mode is not the default launcher entry point.'
Assert (@($sets|Where-Object Name -eq 'Browser'|ForEach-Object Parameters|Where-Object {$_.Name -eq 'Browser' -and $_.IsMandatory}).Count -eq 1) 'Browser mode does not require explicit opt-in.'
$IndexPath=(Resolve-Path -LiteralPath $IndexPath).ProviderPath
$before=(Get-FileHash -LiteralPath $IndexPath).Hash
[Windows.Forms.Application]::EnableVisualStyles()
$window=New-Object BH.Native.DesktopWindow($IndexPath)
$window.Opacity=0
function Control($name){$found=$window.Controls.Find($name,$true);if($found.Count -ne 1){throw "Missing or duplicate control: $name"};$found[0]}
function Wait-Ready {
 $clock=[Diagnostics.Stopwatch]::StartNew();$stable=0
 do{[Windows.Forms.Application]::DoEvents();if(-not (Control 'CancelQuery').Enabled){$stable++}else{$stable=0};if($stable -ge 3){break};if($clock.Elapsed.TotalSeconds -gt 25){throw ('Desktop query timed out: '+(Control 'Status').Text)};Start-Sleep -Milliseconds 10}while($true)
 Assert ([DesktopPortProbe]::Listeners() -eq 0) 'Desktop mode opened a listening TCP port.'
 if((Control 'Status').ForeColor -eq [Drawing.Color]::FromArgb(255,135,150) -or (Control 'Status').Text -like 'Reading index*'){throw ('Desktop operation failed: '+(Control 'Status').Text)}
}
function Click($name){$c=Control $name;for($p=$c.Parent;$p;$p=$p.Parent){if($p -is [Windows.Forms.TabPage]){$p.Parent.SelectedTab=$p}};$c.PerformClick();Wait-Ready}
function Snapshot($control,$path){$bitmap=New-Object Drawing.Bitmap($control.Width,$control.Height);try{$control.DrawToBitmap($bitmap,(New-Object Drawing.Rectangle(0,0,$control.Width,$control.Height)));if($path){$bitmap.Save($path)};$memory=New-Object IO.MemoryStream;try{$bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png);$sha=[Security.Cryptography.SHA256]::Create();try{[Convert]::ToBase64String($sha.ComputeHash($memory.ToArray()))}finally{$sha.Dispose()}}finally{$memory.Dispose()}}finally{$bitmap.Dispose()}}
try{
 Assert ([DesktopPortProbe]::Listeners() -eq 0) 'Test process already has listening ports.'
 $window.Show();Wait-Ready
 Assert ((Control 'Summary').Text -like '*801 saved candidates*') 'Desktop summary omitted the full saved count.'
 Assert ((Control 'Findings').Rows.Count -eq 50) 'First finding page did not load.'
 $seen=New-Object 'Collections.Generic.HashSet[string]';$pages=0
 do{
  $pages++;foreach($row in (Control 'Findings').Rows){Assert ($seen.Add($row.Tag.Source.i)) 'Desktop finding paging repeated a source.'}
  if(-not (Control 'FindingNext').Enabled){break};Click 'FindingNext'
 }while($pages -lt 30)
 Assert ($seen.Count -eq 801 -and $pages -eq 17) 'Desktop paging omitted candidates beyond the HTML preview.'
 Click 'FindingPrevious';Assert ((Control 'Findings').Rows.Count -eq 50) 'Previous finding page failed.'
 (Control 'FindingSearch').Text='Chain 269';Click 'SearchFindings'
 Assert ((Control 'Findings').Rows.Count -eq 1) 'Global desktop finding filter failed.'
 Click 'OpenFinding';Assert ((Control 'PathSteps').Rows.Count -eq 128 -and (Control 'ContinuePath').Enabled) 'Long path segment was not explicit.'
 Assert ((Control 'PathNote').Text -like '*270 recorded hops*') 'Actual path length was omitted.'
 Click 'ContinuePath';Assert ((Control 'PathSteps').Rows.Count -eq 128) 'Second path segment failed.'
 Click 'ContinuePath';Assert ((Control 'PathSteps').Rows.Count -eq 14 -and -not (Control 'ContinuePath').Enabled) 'Final path segment failed.'
 (Control 'WorkspaceTabs').SelectedIndex=1;(Control 'ObjectSearch').Text='User 529';Click 'SearchObjects'
 Assert ((Control 'Objects').Rows.Count -eq 1) 'Global object search failed.'
 Click 'InspectObject';Assert ((Control 'Properties').Text -like '*Retained evidence*') 'Compressed object evidence was not decoded.'
 Assert ((Control 'Relationships').Rows.Count -eq 0) 'Incoming relationships for the source should be empty.'
 (Control 'Direction').SelectedIndex=1;Wait-Ready
 Assert ((Control 'Relationships').Rows.Count -eq 1 -and (Control 'Relationships').Rows[0].Tag.ty -eq 'MemberOf') 'Outgoing relationship search failed.'
 Click 'ExplainRelationship';Assert ((Control 'Helper').Text -like '*Recommended change to review*') 'Relationship explanation/fix is missing.'
 Click 'InspectOther';(Control 'Direction').SelectedIndex=0;Wait-Ready
 Assert ((Control 'Relationships').Rows.Count -eq 50 -and (Control 'RelationshipNext').Enabled) 'Incoming relationships did not page.'
 $firstRelationship=(Control 'Relationships').Rows[0].Tag.s;Click 'RelationshipNext'
 Assert ((Control 'Relationships').Rows.Count -eq 50 -and (Control 'Relationships').Rows[0].Tag.s -ne $firstRelationship) 'Next relationship page repeated data.'
 Click 'ObjectSource';(Control 'TargetId').Text='S-1-5-21-901-902-903-512';Click 'FindPath'
 Assert ((Control 'PathSteps').Rows.Count -eq 2) 'Desktop full-index shortest path failed.'
 Click 'ReadPath';$graph=Control 'PathGraph';$a=Snapshot $graph $null;Click 'ZoomIn';$b=Snapshot $graph $null;Assert ($a -ne $b) 'Graph zoom did not change its rendering.'
 Click 'NextNode';Assert ((Control 'Helper').Text -like 'Support*') 'Graph node navigation/helper failed.'
 Click 'ReadPath';Click 'InspectGraphNode';Assert ((Control 'ObjectIdentity').Text -like 'User 529*') 'Graph node inspection failed.'
 # Start and immediately cancel a native query; retain responsiveness and distinguish cancellation.
 (Control 'WorkspaceTabs').SelectedIndex=2;(Control 'FindPath').PerformClick();(Control 'CancelQuery').PerformClick()
 $clock=[Diagnostics.Stopwatch]::StartNew();while((Control 'CancelQuery').Enabled){[Windows.Forms.Application]::DoEvents();if($clock.Elapsed.TotalSeconds -gt 15){throw 'Cancel did not stop the query'};Start-Sleep -Milliseconds 10}
 Assert ((Control 'Status').Text -like '*cancelled*') 'Cancellation was presented as a completed search.'
 Click 'FindPath'
 (Control 'SourceId').Text='S-1-5-21-901-902-903-512';(Control 'TargetId').Text='S-1-5-21-901-902-903-5529';Click 'FindPath'
 Assert ((Control 'PathSteps').Rows.Count -eq 0 -and (Control 'PathNote').Text -like 'No path found*') 'No-path result left an unrelated old path displayed.'
 (Control 'SourceId').Text='S-1-5-21-901-902-903-5529';(Control 'TargetId').Text='S-1-5-21-901-902-903-512';Click 'FindPath'
 $writeBlocked=$false;try{$f=[IO.File]::Open($IndexPath,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite);$f.Dispose()}catch{$writeBlocked=$true};Assert $writeBlocked 'Open desktop session did not protect the index.'
 if($ScreenshotDirectory){$null=New-Item -ItemType Directory -Path $ScreenshotDirectory -Force;Click 'FitGraph';(Control 'PathSteps').CurrentCell=(Control 'PathSteps').Rows[1].Cells[0];[Windows.Forms.Application]::DoEvents();$null=Snapshot $window (Join-Path $ScreenshotDirectory 'desktop-viewer.png');$window.Size=New-Object Drawing.Size(1040,720);[Windows.Forms.Application]::DoEvents();Click 'FitGraph';$null=Snapshot $window (Join-Path $ScreenshotDirectory 'desktop-minimum.png')}
 # Closing during a query cancels it and releases the read-only snapshot safely.
 (Control 'FindPath').PerformClick();$window.Close();$clock=[Diagnostics.Stopwatch]::StartNew()
 while(-not $window.IsDisposed){[Windows.Forms.Application]::DoEvents();if($clock.Elapsed.TotalSeconds -gt 15){throw 'Close did not finish the active query'};Start-Sleep -Milliseconds 10}
 Assert ([DesktopPortProbe]::Listeners() -eq 0) 'Desktop shutdown left a listener.'
 Assert ((Get-FileHash -LiteralPath $IndexPath).Hash -eq $before) 'Desktop viewer changed the index.'
 $f=[IO.File]::Open($IndexPath,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite);$f.Dispose()
 Write-Host 'PASS: zero listening ports; native desktop UI; 801 candidates / 17 pages; global search; 270-hop continuation; properties; relationships; path query; graph rendering/zoom/navigation; helper/fixes; cancellation; safe close; unchanged index hash.'
}catch{Write-Host $_.Exception.ToString();throw}finally{if(-not $window.IsDisposed){$window.Close();$clock=[Diagnostics.Stopwatch]::StartNew();while(-not $window.IsDisposed -and $clock.Elapsed.TotalSeconds -lt 15){[Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 10};if(-not $window.IsDisposed){$window.Dispose()}}}
