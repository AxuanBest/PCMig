param(
    [Parameter(Mandatory=$true)][string]$OutDir,
    [Parameter(Mandatory=$true)][string]$TargetRoot,
    [double]$FirstPauseAfterSec = 1.5,
    [int]$RepeatClicks = 3,
    [int]$ClickSpacingMs = 900,
    [int]$ObserveSec = 6,
    [int]$TimeoutSec = 300,
    [switch]$NoResume
)
# Recovery Gate case 5 — Repeated Pause clicks.
# Clicks Pause several times in rapid succession around the first click and records,
# per click, the UI truth + pause-request file state + target stats. Then measures
# quiescence and (unless -NoResume) resumes and waits for the run to settle.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$log = Join-Path $OutDir 'case05.log'
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
    $s = Find-RgAid $w 'Shell.Transfer.Start'
    return [pscustomobject]@{
        Percent = (Get-RgText $w 'FooterPercentText')
        Bytes   = (Get-RgText $w 'FooterBytesText')
        Speed   = (Get-RgText $w 'FooterSpeedText')
        Pause   = if ($p) { $p.Current.IsEnabled } else { $null }
        PauseText = if ($p) { $p.Current.Name } else { '' }
        Resume  = if ($r) { $r.Current.IsEnabled } else { $null }
        Start   = if ($s) { $s.Current.IsEnabled } else { $null }
        Message = ((Get-RgText $w 'StateMessageText') -replace "`r?`n", ' / ')
    }
}

$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Log 'NO-WINDOW'; exit 1 }

$start = Find-RgAid $w 'Shell.Transfer.Start'
if (-not $start -or -not $start.Current.IsEnabled) { Write-Log 'START-NOT-ENABLED'; exit 2 }
Invoke-RgClick $start | Out-Null
$sUtc = (Get-Date).ToUniversalTime().ToString('o')
Write-Log ("START-CLICKED utc=$sUtc")
$t0 = Get-Date

Write-Log ("WAIT-FIRST-PAUSE sec=$FirstPauseAfterSec")
while (((Get-Date) - $t0).TotalSeconds -lt $FirstPauseAfterSec) { Start-Sleep -Milliseconds 100 }

$jobDir = Get-LatestJobDir
if (-not $jobDir) { Write-Log 'NO-JOB-DIR'; exit 3 }
Write-Log ("JOB-DIR=" + $jobDir)

$clicks = @()
for ($i = 1; $i -le $RepeatClicks; $i++) {
    $btn = Find-RgAid $w 'Shell.Transfer.Pause'
    $before = Get-UiSnapshot $w
    $st = Get-JobState $jobDir
    $jco = if ($st) { $st.currentObjectId } else { '' }
    $jp = if ($st) { $st.percent } else { '' }
    $prPath = Join-Path $jobDir 'pause.request'
    $prBefore = if (Test-Path -LiteralPath $prPath) { (Get-Content -LiteralPath $prPath -Raw).Trim() } else { '<missing>' }
    $recvBefore = @(Get-ChildItem -LiteralPath (Join-Path $jobDir 'receipts') -Filter *.json -ErrorAction SilentlyContinue).Count
    $cUtc = (Get-Date).ToUniversalTime().ToString('o')
    $enabled = if ($btn) { $btn.Current.IsEnabled } else { $null }
    Invoke-RgClick $btn | Out-Null
    $clicks += [pscustomobject]@{ n = $i; utc = $cUtc; enabled = $enabled; pauseText = $before.PauseText; uiPercent = $before.Percent; jobPercent = $jp; currentObject = $jco; prBefore = $prBefore; receipts = $recvBefore }
    Write-Log ("PAUSE-CLICK#$i utc=$cUtc enabled=$enabled pauseText=[$($before.PauseText)] uiPercent=$($before.Percent) jobPercent=$jp currentObject=$jco receipts=$recvBefore prBefore=[$prBefore]")
    Start-Sleep -Milliseconds $ClickSpacingMs
}

# pause-request file state after the click burst
$prPath = Join-Path $jobDir 'pause.request'
$prAfter = if (Test-Path -LiteralPath $prPath) { (Get-Content -LiteralPath $prPath -Raw).Trim() } else { '<missing>' }
Write-Log ("PAUSE-REQUEST-FILE after-burst exists=" + (Test-Path -LiteralPath $prPath) + " content=[$prAfter]")

# quiescence sampling
$samples = @()
for ($k = 1; $k -le $ObserveSec; $k++) {
    Start-Sleep -Seconds 1
    $ui = Get-UiSnapshot $w
    $st = Get-JobState $jobDir
    $ts = Get-TargetStats $TargetRoot
    $ph2 = ''; $ps2 = ''
    if ($st) { $ph2 = $st.phase; if ($st.PSObject.Properties.Name -contains 'pauseState') { $ps2 = $st.pauseState } }
    $samples += [pscustomobject]@{ sec = $k; uiPercent = $ui.Percent; pauseEn = $ui.Pause; resumeEn = $ui.Resume; msg = $ui.Message; jobPhase = $ph2; pauseState = $ps2; bytes = $ts.Bytes; files = $ts.Files }
    Write-Log ("SAMPLE+$k uiPercent=$($ui.Percent) pauseEn=$($ui.Pause) resumeEn=$($ui.Resume) phase=$(if ($st) { $st.phase } else { '' }) bytes=$($ts.Bytes) files=$($ts.Files) msg=[$($ui.Message)]")
}
$delta = $samples[-1].bytes - $samples[0].bytes
Write-Log ("PAUSE-STABILITY deltaBytes=$delta over ${ObserveSec}s")

if (-not $NoResume) {
    $rbtn = Find-RgAid $w 'Shell.Transfer.Resume'
    $ren = if ($rbtn) { $rbtn.Current.IsEnabled } else { $null }
    $rUtc = (Get-Date).ToUniversalTime().ToString('o')
    Invoke-RgClick $rbtn | Out-Null
    Write-Log ("RESUME-CLICKED utc=$rUtc resumeEnabled=$ren")
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
$summary = @()
$summary += "CASE05-SUMMARY"
$summary += "JOB-DIR=$jobDir"
$summary += "CLICK-COUNT=$($clicks.Count)"
foreach ($c in $clicks) { $summary += ("CLICK#" + $c.n + " enabled=" + $c.enabled + " pauseText=[" + $c.pauseText + "] uiPercent=" + $c.uiPercent + " jobPercent=" + $c.jobPercent + " currentObject=" + $c.currentObject + " receipts=" + $c.receipts + " prBefore=[" + $c.prBefore + "]") }
$summary += "PAUSE-REQUEST-FILE-AFTER-BURST exists=$(Test-Path -LiteralPath $prPath) content=[$prAfter]"
$summary += "PAUSE-STABILITY deltaBytes=$delta over ${ObserveSec}s"
$summary += "FINAL phase=$(if ($fin) { $fin.phase } else { '' }) percent=$(if ($fin) { $fin.percent } else { '' })"
$summary += "FINAL-TARGET bytes=$($fts.Bytes) files=$($fts.Files)"
$summary += "FINAL-UI percent=$($fui.Percent) bytes=$($fui.Bytes)"
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'case05-summary.txt') -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }