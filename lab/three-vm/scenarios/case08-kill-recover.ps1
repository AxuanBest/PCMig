param(
    [Parameter(Mandatory=$true)][string]$OutDir,
    [Parameter(Mandatory=$true)][string]$TargetRoot,
    [double]$KillAfterSec = 3.0,
    [int]$OrphanObserveSec = 5,
    [int]$TimeoutSec = 600
)
# Recovery Gate case 8 - process kill then recovery (adopt the interrupted job and resume).
# Part A: start the transfer in the real app, then hard-kill PCMig.WinUI.exe.
#         Records target stats before/after and whether robocopy children survived.
# Part B: relaunch, reconnect, adopt the existing job, resume, wait for completion.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$log = Join-Path $OutDir 'case08.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
function Write-Log { param([string]$m) $line = "$(Get-Date -Format 'HH:mm:ss.fff') $m"; Add-Content -LiteralPath $log -Value $line -Encoding UTF8; Write-Host $line }

function Get-JobsDir { return (Join-Path $env:ProgramData 'PCMig\Jobs') }
function Get-LatestJobDir {
    $latest = Get-ChildItem -LiteralPath (Get-JobsDir) -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) { return $null }
    return $latest.FullName
}
function Get-JobState {
    param([string]$JobDir)
    $p = Join-Path $JobDir 'job-state.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    for ($i = 0; $i -lt 5; $i++) {
        try { return (Get-Content -LiteralPath $p -Raw | ConvertFrom-Json) } catch { Start-Sleep -Milliseconds 150 }
    }
    return $null
}
function Get-TargetStats {
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) { return @{ Bytes = 0; Files = 0 } }
    $f = Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue
    $sum = 0L; foreach ($x in $f) { $sum += $x.Length }
    return @{ Bytes = $sum; Files = $f.Count }
}
function Get-UiSnapshot {
    param($w)
    $s = Find-RgAid $w 'Shell.Transfer.Start'; $p = Find-RgAid $w 'Shell.Transfer.Pause'
    $t = Find-RgAid $w 'Shell.Transfer.Stop'; $r = Find-RgAid $w 'Shell.Transfer.Resume'
    return [pscustomobject]@{
        Percent = (Get-RgText $w 'FooterPercentText')
        Bytes   = (Get-RgText $w 'FooterBytesText')
        Start   = if ($s) { $s.Current.IsEnabled } else { $null }
        Pause   = if ($p) { $p.Current.IsEnabled } else { $null }
        Stop    = if ($t) { $t.Current.IsEnabled } else { $null }
        Resume  = if ($r) { $r.Current.IsEnabled } else { $null }
        Message = ((Get-RgText $w 'StateMessageText') -replace "`r?`n", ' / ')
    }
}

# ---------- Part A: start then kill ----------
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Log 'NO-WINDOW'; exit 1 }
$start = Find-RgAid $w 'Shell.Transfer.Start'
if (-not $start -or -not $start.Current.IsEnabled) { Write-Log 'START-NOT-ENABLED'; exit 2 }
Invoke-RgClick $start | Out-Null
Write-Log ("START-CLICKED utc=" + (Get-Date).ToUniversalTime().ToString('o'))
$t0 = Get-Date
while (((Get-Date) - $t0).TotalSeconds -lt $KillAfterSec) { Start-Sleep -Milliseconds 100 }

$jobDir = Get-LatestJobDir
$st = Get-JobState $jobDir
$ts = Get-TargetStats $TargetRoot
$ui = Get-UiSnapshot $w
$recv = @(Get-ChildItem -LiteralPath (Join-Path $jobDir 'receipts') -Filter *.json -ErrorAction SilentlyContinue).Count
$proc = Get-Process -Name 'PCMig.WinUI' -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
Write-Log ("BEFORE-KILL pid=$($proc.Id) jobDir=$jobDir jobPercent=$(if ($st) { $st.percent } else { '' }) currentObject=$(if ($st) { $st.currentObjectId } else { '' }) uiPercent=$($ui.Percent) receipts=$recv bytes=$($ts.Bytes) files=$($ts.Files)")

$rcBefore = @(Get-Process -Name 'Robocopy' -ErrorAction SilentlyContinue)
Write-Log ("ROBOCOPY-BEFORE-KILL count=$($rcBefore.Count) pids=[$(($rcBefore | ForEach-Object { $_.Id }) -join ',')]")

Stop-Process -Id $proc.Id -Force
$killUtc = (Get-Date).ToUniversalTime().ToString('o')
Write-Log ("KILLED pid=$($proc.Id) utc=$killUtc")

$orphanSamples = @()
for ($k = 1; $k -le $OrphanObserveSec; $k++) {
    Start-Sleep -Seconds 1
    $ts2 = Get-TargetStats $TargetRoot
    $rc = @(Get-Process -Name 'Robocopy' -ErrorAction SilentlyContinue)
    $orphanSamples += [pscustomobject]@{ sec = $k; bytes = $ts2.Bytes; files = $ts2.Files; robocopy = $rc.Count }
    Write-Log ("AFTER-KILL+$k bytes=$($ts2.Bytes) files=$($ts2.Files) robocopyCount=$($rc.Count) pids=[$(($rc | ForEach-Object { $_.Id }) -join ',')]")
}
$orphanDelta = $orphanSamples[-1].bytes - $orphanSamples[0].bytes
Write-Log ("ORPHAN-EFFECT deltaBytes=$orphanDelta over ${OrphanObserveSec}s robocopyAfter=$($orphanSamples[-1].robocopy)")

# stop any surviving robocopy children so the recovery run starts from a quiet filesystem
$rcNow = @(Get-Process -Name 'Robocopy' -ErrorAction SilentlyContinue)
if ($rcNow.Count -gt 0) {
    $rcNow | Stop-Process -Force
    Write-Log ("ORPHAN-KILLED count=$($rcNow.Count)")
    Start-Sleep -Seconds 2
    $ts3 = Get-TargetStats $TargetRoot
    Write-Log ("AFTER-ORPHAN-KILL bytes=$($ts3.Bytes) files=$($ts3.Files)")
}

# ---------- Part B: relaunch, reconnect, adopt, resume ----------
& 'E:\PCMigLab\Staging\recovery-gate\launch-app.ps1' | ForEach-Object { Write-Log $_ }
Start-Sleep -Seconds 5

$w2 = Get-RgWindow -TimeoutSec 30
if (-not $w2) { Write-Log 'NO-WINDOW-AFTER-RELAUNCH'; exit 4 }
Invoke-RgClick (Find-RgAid $w2 'Shell.Nav.Step1') | Out-Null
Start-Sleep -Seconds 2
Set-RgText (Find-RgAid $w2 'Step1.HostInput') 'localhost' | Out-Null
Start-Sleep -Milliseconds 400
Invoke-RgClick (Find-RgAid $w2 'Step1.Connect') | Out-Null
Start-Sleep -Seconds 6
Write-Log ("RECONNECT-STATUS=[" + (Get-RgText $w2 'Step1.StatusText') + "]")

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'E:\PCMigLab\Staging\tc-batch5\adopt-full.ps1' | ForEach-Object { Write-Log $_ }
Start-Sleep -Seconds 3

$w3 = Get-RgWindow -TimeoutSec 20
$before = Get-UiSnapshot $w3
Write-Log ("AFTER-ADOPT percent=$($before.Percent) bytes=$($before.Bytes) startEn=$($before.Start) pauseEn=$($before.Pause) stopEn=$($before.Stop) resumeEn=$($before.Resume) msg=[$($before.Message)]")

$rbtn = Find-RgAid $w3 'Shell.Transfer.Resume'
if ($rbtn -and $rbtn.Current.IsEnabled) {
    Invoke-RgClick $rbtn | Out-Null
    Write-Log ("RESUME-CLICKED utc=" + (Get-Date).ToUniversalTime().ToString('o'))
    $t1 = Get-Date
    $settled = $false
    while (((Get-Date) - $t1).TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Seconds 2
        $ui2 = Get-UiSnapshot $w3
        $st2 = Get-JobState $jobDir
        $ts4 = Get-TargetStats $TargetRoot
        $ph = if ($st2) { $st2.phase } else { '' }
        Write-Log ("POST-RESUME+$([int]((Get-Date) - $t1).TotalSeconds)s uiPercent=$($ui2.Percent) phase=$ph jobPercent=$(if ($st2) { $st2.percent } else { '' }) bytes=$($ts4.Bytes) files=$($ts4.Files) msg=[$($ui2.Message)]")
        if ($ph -in @('completed', 'completedWithErrors', 'failed')) { $settled = $true; break }
    }
    Write-Log ("RUN-SETTLED settled=$settled")
} else {
    Write-Log 'RESUME-NOT-ENABLED'
}

$fin = Get-JobState $jobDir
$fts = Get-TargetStats $TargetRoot
$fui = Get-UiSnapshot $w3
$recv2 = @(Get-ChildItem -LiteralPath (Join-Path $jobDir 'receipts') -Filter *.json -ErrorAction SilentlyContinue).Count
$summary = @()
$summary += 'CASE08-SUMMARY'
$summary += "JOB-DIR=$jobDir"
$summary += "KILL-AFTER-SEC=$KillAfterSec ORPHAN-DELTA-BYTES=$orphanDelta ORPHAN-ROBOCOPY-AFTER=$($orphanSamples[-1].robocopy)"
$summary += "FINAL phase=$(if ($fin) { $fin.phase } else { '' }) percent=$(if ($fin) { $fin.percent } else { '' })"
$summary += "FINAL-TARGET bytes=$($fts.Bytes) files=$($fts.Files)"
$summary += "FINAL-UI percent=$($fui.Percent) bytes=$($fui.Bytes)"
$summary += "RECEIPTS=$recv2"
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'case08-summary.txt') -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }