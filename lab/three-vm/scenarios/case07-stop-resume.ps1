param(
    [Parameter(Mandatory=$true)][string]$OutDir,
    [Parameter(Mandatory=$true)][string]$TargetRoot,
    [double]$StopAfterSec = 3.0,
    [int]$ObserveSec = 6,
    [int]$TimeoutSec = 300,
    [switch]$NoResume
)
# Recovery Gate case 7 - Stop then Resume.
# Clicks Stop mid-transfer, records quiescence + UI/engine truth, then clicks Resume
# and waits for the run to settle. Prints the same facts the pause cases need.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$log = Join-Path $OutDir 'case07.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
function Write-Log { param([string]$m) $line = "$(Get-Date -Format 'HH:mm:ss.fff') $m"; Add-Content -LiteralPath $log -Value $line -Encoding UTF8; Write-Host $line }

function Get-JobsDir { return (Join-Path $env:ProgramData 'PCMig\Jobs') }
function Get-LatestJobDir {
    $d = Get-JobsDir
    $latest = Get-ChildItem -LiteralPath $d -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
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
    $p = Find-RgAid $w 'Shell.Transfer.Pause'
    $r = Find-RgAid $w 'Shell.Transfer.Resume'
    $t = Find-RgAid $w 'Shell.Transfer.Stop'
    $s = Find-RgAid $w 'Shell.Transfer.Start'
    return [pscustomobject]@{
        Percent = (Get-RgText $w 'FooterPercentText')
        Bytes   = (Get-RgText $w 'FooterBytesText')
        Start   = if ($s) { $s.Current.IsEnabled } else { $null }
        Pause   = if ($p) { $p.Current.IsEnabled } else { $null }
        Stop    = if ($t) { $t.Current.IsEnabled } else { $null }
        Resume  = if ($r) { $r.Current.IsEnabled } else { $null }
        Message = ((Get-RgText $w 'StateMessageText') -replace "`r?`n", ' / ')
        Hint    = ((Get-RgText $w 'HintOperationalText') -replace "`r?`n", ' / ')
    }
}

$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Log 'NO-WINDOW'; exit 1 }

$start = Find-RgAid $w 'Shell.Transfer.Start'
if (-not $start -or -not $start.Current.IsEnabled) { Write-Log 'START-NOT-ENABLED'; exit 2 }
Invoke-RgClick $start | Out-Null
Write-Log ("START-CLICKED utc=" + (Get-Date).ToUniversalTime().ToString('o'))
$t0 = Get-Date

while (((Get-Date) - $t0).TotalSeconds -lt $StopAfterSec) { Start-Sleep -Milliseconds 100 }
$jobDir = Get-LatestJobDir
if (-not $jobDir) { Write-Log 'NO-JOB-DIR'; exit 3 }
Write-Log ("JOB-DIR=" + $jobDir)

$before = Get-UiSnapshot $w
$st = Get-JobState $jobDir
$ts = Get-TargetStats $TargetRoot
$sbtn = Find-RgAid $w 'Shell.Transfer.Stop'
$sUtc = (Get-Date).ToUniversalTime().ToString('o')
Write-Log ("STOP-CLICKED utc=$sUtc stopEnabled=$(if ($sbtn) { $sbtn.Current.IsEnabled } else { $null }) uiPercent=$($before.Percent) jobPercent=$(if ($st) { $st.percent } else { '' }) currentObject=$(if ($st) { $st.currentObjectId } else { '' }) bytes=$($ts.Bytes) files=$($ts.Files)")
Invoke-RgClick $sbtn | Out-Null

$samples = @()
for ($k = 1; $k -le $ObserveSec; $k++) {
    Start-Sleep -Seconds 1
    $ui = Get-UiSnapshot $w
    $st = Get-JobState $jobDir
    $ts = Get-TargetStats $TargetRoot
    $ph = ''; $ps = ''
    if ($st) { $ph = $st.phase; if ($st.PSObject.Properties.Name -contains 'pauseState') { $ps = $st.pauseState } }
    $samples += [pscustomobject]@{ sec = $k; uiPercent = $ui.Percent; startEn = $ui.Start; pauseEn = $ui.Pause; stopEn = $ui.Stop; resumeEn = $ui.Resume; phase = $ph; bytes = $ts.Bytes; files = $ts.Files; msg = $ui.Message; hint = $ui.Hint }
    Write-Log ("STOP-SAMPLE+$k uiPercent=$($ui.Percent) startEn=$($ui.Start) pauseEn=$($ui.Pause) stopEn=$($ui.Stop) resumeEn=$($ui.Resume) phase=$ph bytes=$($ts.Bytes) files=$($ts.Files) msg=[$($ui.Message)]")
}
$delta = $samples[-1].bytes - $samples[0].bytes
Write-Log ("STOP-STABILITY deltaBytes=$delta over ${ObserveSec}s")

if (-not $NoResume) {
    $rbtn = Find-RgAid $w 'Shell.Transfer.Resume'
    $ren = if ($rbtn) { $rbtn.Current.IsEnabled } else { $null }
    Write-Log ("RESUME-CLICKED utc=" + (Get-Date).ToUniversalTime().ToString('o') + " resumeEnabled=$ren")
    Invoke-RgClick $rbtn | Out-Null
    $t1 = Get-Date
    $settled = $false
    while (((Get-Date) - $t1).TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Seconds 2
        $ui = Get-UiSnapshot $w
        $st = Get-JobState $jobDir
        $ph = if ($st) { $st.phase } else { '' }
        $ts = Get-TargetStats $TargetRoot
        Write-Log ("POST-RESUME+$([int]((Get-Date) - $t1).TotalSeconds)s uiPercent=$($ui.Percent) phase=$ph jobPercent=$(if ($st) { $st.percent } else { '' }) bytes=$($ts.Bytes) files=$($ts.Files) msg=[$($ui.Message)]")
        if ($ph -in @('completed', 'completedWithErrors', 'failed')) { $settled = $true; break }
    }
    Write-Log ("RUN-SETTLED settled=$settled")
}

$fin = Get-JobState $jobDir
$fts = Get-TargetStats $TargetRoot
$fui = Get-UiSnapshot $w
$recv = @(Get-ChildItem -LiteralPath (Join-Path $jobDir 'receipts') -Filter *.json -ErrorAction SilentlyContinue).Count
$summary = @()
$summary += 'CASE07-SUMMARY'
$summary += "JOB-DIR=$jobDir"
$summary += "STOP-CLICK uiPercent=$($before.Percent) jobPercent=$(if ($st) { $st.percent } else { '' })"
$summary += "STOP-STABILITY deltaBytes=$delta over ${ObserveSec}s"
$summary += "POST-STOP startEn=$($samples[0].startEn) pauseEn=$($samples[0].pauseEn) stopEn=$($samples[0].stopEn) resumeEn=$($samples[0].resumeEn) phase=$($samples[0].phase)"
$summary += "POST-STOP-HINT=$($samples[0].hint)"
$summary += "FINAL phase=$(if ($fin) { $fin.phase } else { '' }) percent=$(if ($fin) { $fin.percent } else { '' })"
$summary += "FINAL-TARGET bytes=$($fts.Bytes) files=$($fts.Files)"
$summary += "FINAL-UI percent=$($fui.Percent) bytes=$($fui.Bytes)"
$summary += "RECEIPTS=$recv"
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'case07-summary.txt') -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }