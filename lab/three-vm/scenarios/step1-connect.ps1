param(
    [string]$Host_ = 'localhost',
    [switch]$NoLaunch
)
# Step1: connect to the share host and dump what the share list looks like.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'

$exe = 'E:\Project\deepseek work\PCMig\src\PCMig.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe'
if (-not $NoLaunch) {
    Get-Process PCMig.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
    $pi = [System.Diagnostics.ProcessStartInfo]::new(); $pi.FileName = $exe; $pi.UseShellExecute = $true; $pi.WorkingDirectory = (Split-Path $exe -Parent)
    [System.Diagnostics.Process]::Start($pi) | Out-Null
    Start-Sleep 9
}

$w = Get-RgWindow -TimeoutSec 30
if (-not $w) { Write-Host 'NO-WINDOW'; exit 1 }
Write-Host ("WINDOW pid-ok rect=" + $w.Current.BoundingRectangle)

$hostInput = Find-RgAid $w 'Step1.HostInput' 10
if (-not $hostInput) { Write-Host 'NO-HOST-INPUT'; exit 1 }
$mode = Set-RgText $hostInput $Host_
Write-Host ("SET-HOST=" + $Host_ + " via " + $mode)

Start-Sleep -Milliseconds 500
$connect = Find-RgAid $w 'Step1.Connect'
Write-Host ("CONNECT-ENABLED=" + $connect.Current.IsEnabled)
Invoke-RgClick $connect | Out-Null

# wait for the share list to fill
$deadline = (Get-Date).AddSeconds(45)
$listCount = 0
do {
    Start-Sleep -Seconds 1
    $items = @()
    foreach ($e in (Get-RgAll $w)) { if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $items += $e } }
    $listCount = $items.Count
} while ($listCount -eq 0 -and (Get-Date) -lt $deadline)

Write-Host ("SHARE-ITEMS=" + $listCount)
Write-Host ("STEP1-STATUS=[" + (Get-RgText $w 'Step1.StatusText') + "]")
foreach ($e in $items) {
    $r = $e.Current.BoundingRectangle
    Write-Host ("  ITEM name=[" + $e.Current.Name + "] x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height)")
}
Save-RgState $w 'E:\PCMigLab\Staging\recovery-gate\state-step1-connected.txt'