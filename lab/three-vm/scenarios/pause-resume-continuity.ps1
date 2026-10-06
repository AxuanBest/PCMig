param(
    [Parameter(Mandatory=$true)][string]$OutDir,
    [Parameter(Mandatory=$true)][string]$TargetRoot,
    [double]$PauseAtPercent = -1,
    [double]$PauseAfterSec = 2.0,
    [int]$PauseObserveSec = 8,
    [int]$ResumeSampleCount = 30,
    [int]$ResumeSampleMs = 200,
    [int]$RunningSampleMs = 250,
    [int]$TimeoutSec = 1800
)
# ══════════════════════════════════════════════════════════════════════════════
#  UI Closure 2026-10-05 · PHASE B-6 (user tech plan section 2 / section 9 evidence 2)
#  Real-UI driver: Start -> (running samples) -> Pause -> (observe) -> Resume
#  -> high-frequency resume samples -> settle.
#
#  Three independent truths per sample:
#    · UI truth     : FooterPercentText / FooterBytesText / FooterSpeedText (what the user sees)
#    · Engine truth : job-state.json (phase/percent/completedBytes/currentObjectId/pauseState)
#    · Target truth : real bytes on the target root (only sampled while paused / at the end,
#                     because a recursive walk is expensive during the run)
#  Verdicts:
#    · UI-DROP-DETECTED : displayed percent falls after Resume (the reported 42.9 -> 24.8 bug)
#    · RAW-BELOW-DISPLAY: engine raw percent is below the displayed percent (floor is holding)
#  Also extracts Serilog structured lines (ProgressTruthTransition / UnexpectedProgressRegression)
#  from %ProgramData%\PCMig\Logs\app-*.jsonl into progress-transitions.jsonl.
# ══════════════════════════════════════════════════════════════════════════════
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$log = Join-Path $OutDir 'pause-resume.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
$csv = Join-Path $OutDir 'resume-samples.csv'
if (Test-Path -LiteralPath $csv) { Remove-Item -LiteralPath $csv -Force }
Set-Content -LiteralPath $csv -Value 'utc,elapsedMs,phaseOfRun,uiPercent,uiBytes,uiSpeed,stateMsg,hint,jobPhase,jobPercent,jobCompletedBytes,currentObject,pauseState,targetBytes,targetFiles' -Encoding UTF8
function Write-Log { param([string]$m) $line = "$(Get-Date -Format 'HH:mm:ss.fff') $m"; Add-Content -LiteralPath $log -Value $line -Encoding UTF8; Write-Host $line }

function Get-JobsDir { return (Join-Path $env:ProgramData 'PCMig\Jobs') }
function Get-JobState {
    param([string]$Id)
    $d = Get-JobsDir
    if (-not $Id) {
        $latest = Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $latest) { return $null }
        $Id = $latest.Name
    }
    $p = Join-Path (Join-Path $d $Id) 'job-state.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    for ($i = 0; $i -lt 4; $i++) {
        try { $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; return [pscustomobject]@{ Id = $Id; Path = $p; Json = $j } }
        catch { Start-Sleep -Milliseconds 100 }
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
function Get-Ui {
    param($w)
    $pct = ''; $bytes = ''; $speed = ''; $msg = ''; $hint = ''
    try { $pct = Get-RgText $w 'FooterPercentText' } catch { }
    try { $bytes = Get-RgText $w 'FooterBytesText' } catch { }
    try { $speed = Get-RgText $w 'FooterSpeedText' } catch { }
    try { $msg = ((Get-RgText $w 'StateMessageText') -replace "`r?`n", ' / ') } catch { }
    try { $hint = ((Get-RgText $w 'HintOperationalText') -replace "`r?`n", ' / ') } catch { }
    return [pscustomobject]@{ Percent = $pct; Bytes = $bytes; Speed = $speed; Msg = $msg; Hint = $hint }
}
function Get-PctNumber {
    param([string]$Text)
    if (-not $Text) { return $null }
    $m = [regex]::Match($Text, '[-+]?\d+(\.\d+)?')
    if (-not $m.Success) { return $null }
    return [double]$m.Value
}
function Get-JsonValue {
    param($Json, [string]$Name)
    if (-not $Json) { return '' }
    if ($Json.PSObject.Properties.Name -contains $Name) { return $Json.$Name }
    return ''
}
function Write-Sample {
    param($w, [string]$JobId, [string]$PhaseOfRun, $Mark, [string]$Event, [switch]$WithTarget)
    $ui = Get-Ui $w
    $st = Get-JobState $JobId
    $j = if ($st) { $st.Json } else { $null }
    $tb = ''; $tf = ''
    if ($WithTarget) { $ts = Get-TargetStats $TargetRoot; $tb = $ts.Bytes; $tf = $ts.Files }
    $elapsed = ''
    if ($Mark -is [datetime]) { $elapsed = [int]((Get-Date) - $Mark).TotalMilliseconds }
    $row = @(
        (Get-Date).ToUniversalTime().ToString('o'), $elapsed, $PhaseOfRun,
        $ui.Percent, $ui.Bytes, $ui.Speed, $ui.Msg, $ui.Hint,
        (Get-JsonValue $j 'phase'), (Get-JsonValue $j 'percent'), (Get-JsonValue $j 'completedBytes'),
        (Get-JsonValue $j 'currentObjectId'), (Get-JsonValue $j 'pauseState'),
        $tb, $tf
    ) -join ','
    Add-Content -LiteralPath $csv -Value $row -Encoding UTF8
    if ($Event) {
        Write-Log ("SAMPLE [$Event] uiPercent=$($ui.Percent) uiBytes=$($ui.Bytes) jobPhase=$(Get-JsonValue $j 'phase') jobPercent=$(Get-JsonValue $j 'percent') jobBytes=$(Get-JsonValue $j 'completedBytes') obj=$(Get-JsonValue $j 'currentObjectId') pauseState=$(Get-JsonValue $j 'pauseState')" + $(if ($WithTarget) { " targetBytes=$tb" } else { '' }))
    }
    return [pscustomobject]@{
        Elapsed = $elapsed; UiPercent = (Get-PctNumber $ui.Percent); DisplayedPercent = $ui.Percent; UiBytes = $ui.Bytes
        JobPercent = (Get-JsonValue $j 'percent'); JobBytes = (Get-JsonValue $j 'completedBytes')
        JobPhase = (Get-JsonValue $j 'phase'); Msg = $ui.Msg; TargetBytes = $tb
    }
}

$w = Get-RgWindow -TimeoutSec 25
if (-not $w) { Write-Log 'NO-WINDOW'; exit 1 }

# ── user action 1: Start ───────────────────────────────────────────────────────
$start = Find-RgAid $w 'Shell.Transfer.Start'
if (-not $start -or -not $start.Current.IsEnabled) {
    Write-Log ('START-NOT-ENABLED present=' + ($start -ne $null) + ' enabled=' + $(if ($start) { $start.Current.IsEnabled } else { 'n/a' }))
    exit 2
}
$jobBefore = Get-JobState ''
$jobId = if ($jobBefore) { $jobBefore.Id } else { '' }
Write-Log ("JOB-BEFORE-START id=$jobId")
Invoke-RgClick $start | Out-Null
$t0 = Get-Date
Write-Log ("START-CLICKED utc=" + (Get-Date).ToUniversalTime().ToString('o'))

# ── running phase: high-frequency samples, pause when the trigger fires ────────
$paused = $false
$pausedUiPercent = $null
$runningSamples = @()
$nextCheck = (Get-Date)
while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds $RunningSampleMs
    $s = Write-Sample $w $jobId 'running' $t0 ''
    $runningSamples += $s
    if ((Get-Date) -lt $nextCheck) { continue }
    $nextCheck = (Get-Date).AddMilliseconds(600)
    $st = Get-JobState $jobId
    if (-not $st) { continue }
    $jobId = $st.Id
    $jp = Get-JsonValue $st.Json 'percent'
    $jph = Get-JsonValue $st.Json 'phase'
    $byPercent = ($PauseAtPercent -ge 0 -and $jp -ne '' -and [double]$jp -ge $PauseAtPercent)
    $bySecs = ($PauseAfterSec -ge 0 -and ((Get-Date) - $t0).TotalSeconds -ge $PauseAfterSec)
    if ($byPercent -or $bySecs) {
        $uiBefore = Get-Ui $w
        $pausedUiPercent = Get-PctNumber $uiBefore.Percent
        $pbtn = Find-RgAid $w 'Shell.Transfer.Pause'
        $pcUtc = (Get-Date).ToUniversalTime().ToString('o')
        $paused = $true
        Invoke-RgClick $pbtn | Out-Null
        Write-Log ("PAUSE-CLICKED utc=$pcUtc trigger=$(if ($byPercent) { 'percent' } else { 'seconds' }) jobPercent=$jp uiPercent=$($uiBefore.Percent) uiBytes=$($uiBefore.Bytes) obj=$(Get-JsonValue $st.Json 'currentObjectId') jobBytes=$(Get-JsonValue $st.Json 'completedBytes')")
        Set-Content -LiteralPath (Join-Path $OutDir 'pause-point.txt') -Value ("pauseClickUtc=" + $pcUtc + "`ntrigger=" + $(if ($byPercent) { 'percent' } else { 'seconds' }) + "`njobPercentAtClick=" + $jp + "`nuiPercentAtClick=" + $uiBefore.Percent + "`nuiBytesAtClick=" + $uiBefore.Bytes + "`ncurrentObjectAtClick=" + (Get-JsonValue $st.Json 'currentObjectId') + "`njobCompletedBytesAtClick=" + (Get-JsonValue $st.Json 'completedBytes')) -Encoding UTF8
        break
    }
    if ($jph -in @('Completed', 'CompletedWithErrors', 'Failed')) { Write-Log ("RUN-SETTLED-BEFORE-PAUSE phase=$jph"); break }
}
if (-not $paused) { Write-Log 'PAUSE-NEVER-TRIGGERED'; exit 3 }

# ── observe the paused state (target bytes must stop growing) ──────────────────
$pauseSamples = @()
for ($k = 1; $k -le $PauseObserveSec; $k++) {
    Start-Sleep -Seconds 1
    $s = Write-Sample $w $jobId 'paused' $null ("PAUSE+" + $k + "s") -WithTarget
    $pauseSamples += $s
}
$pauseDelta = 0
if ($pauseSamples.Count -ge 2) {
    $b0 = $pauseSamples[0].TargetBytes; $b1 = $pauseSamples[-1].TargetBytes
    if ($b0 -ne '' -and $b1 -ne '') { $pauseDelta = [long]$b1 - [long]$b0 }
}
$lastPaused = $pauseSamples[-1]
Write-Log ("PAUSE-SETTLED uiPercent=$($lastPaused.DisplayedPercent) jobPercent=$($lastPaused.JobPercent) jobBytes=$($lastPaused.JobBytes) targetBytes=$($lastPaused.TargetBytes) msg=[$($lastPaused.Msg)]")

# ── user action 2: Resume, then high-frequency sampling ────────────────────────
$rbtn = Find-RgAid $w 'Shell.Transfer.Resume'
$ren = if ($rbtn) { $rbtn.Current.IsEnabled } else { $null }
$uiAtResume = Get-Ui $w
$resumeUiPercent = Get-PctNumber $uiAtResume.Percent
$resumeUiBytes = $uiAtResume.Bytes
if (-not $ren) { Write-Log 'RESUME-NOT-ENABLED'; exit 4 }
$rUtc = (Get-Date).ToUniversalTime().ToString('o')
$t1 = Get-Date
Invoke-RgClick $rbtn | Out-Null
Write-Log ("RESUME-CLICKED utc=$rUtc uiPercentBeforeClick=$($uiAtResume.Percent) uiBytesBeforeClick=$resumeUiBytes")

$resumeSamples = @()
for ($i = 1; $i -le $ResumeSampleCount; $i++) {
    Start-Sleep -Milliseconds $ResumeSampleMs
    $s = Write-Sample $w $jobId 'resuming' $t1 ("RESUME+" + $i)
    $resumeSamples += $s
}

# ── verdicts ───────────────────────────────────────────────────────────────────
$minUi = $null
foreach ($s in $resumeSamples) {
    if ($s.UiPercent -eq $null) { continue }
    if ($minUi -eq $null -or $s.UiPercent -lt $minUi) { $minUi = $s.UiPercent }
}
$dropped = ($resumeUiPercent -ne $null -and $minUi -ne $null -and ($minUi -lt ($resumeUiPercent - 0.05)))
$rawBelowDisplay = $false
if ($resumeUiPercent -ne $null) {
    foreach ($s in $resumeSamples) {
        if ($s.JobPercent -ne '' -and [double]$s.JobPercent -lt ($resumeUiPercent - 0.05)) { $rawBelowDisplay = $true; break }
    }
}
Write-Log ("RESUME-VERDICT displayedPercentAtResume=$resumeUiPercent displayedMinAfterResume=$minUi UI-DROP-DETECTED=$dropped RAW-BELOW-DISPLAY=$rawBelowDisplay")

# ── follow through to settle ───────────────────────────────────────────────────
$settled = $false
while (((Get-Date) - $t1).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Seconds 2
    $s = Write-Sample $w $jobId 'after-resume' $null ''
    if ($s.JobPhase -in @('Completed', 'CompletedWithErrors', 'Failed')) { $settled = $true; break }
}
Write-Log ("RUN-SETTLED settled=$settled")

# ── extract structured transition log lines ────────────────────────────────────
$logDir = Join-Path $env:ProgramData 'PCMig\Logs'
$jsonl = Get-ChildItem -LiteralPath $logDir -Filter 'app-*.jsonl' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$hitCount = 0
if ($jsonl) {
    $hits = Select-String -LiteralPath $jsonl.FullName -Pattern 'ProgressTruthTransition|UnexpectedProgressRegression|floorBytes|reason=new-run' -ErrorAction SilentlyContinue | Select-Object -Last 800
    $hitCount = @($hits).Count
    $hits | ForEach-Object { $_.Line } | Set-Content -LiteralPath (Join-Path $OutDir 'progress-transitions.jsonl') -Encoding UTF8
}
Write-Log ("TRANSITION-LINES=$hitCount source=$(if ($jsonl) { $jsonl.Name } else { 'none' })")

$fin = Get-JobState $jobId
$fts = Get-TargetStats $TargetRoot
$summary = @()
$summary += 'PAUSE-RESUME-CONTINUITY-SUMMARY'
$summary += "JOB-ID=$jobId"
$summary += "TARGET-ROOT=$TargetRoot"
$summary += "PAUSE-POINT " + ((Get-Content -LiteralPath (Join-Path $OutDir 'pause-point.txt') -Raw) -replace "`r?`n", ' | ')
$summary += "RUNNING-SAMPLES=$($runningSamples.Count) (maxDisplayedPercentBeforePause=$(($runningSamples | Where-Object { $_.UiPercent -ne $null } | Measure-Object -Property UiPercent -Maximum).Maximum))"
$summary += "PAUSE-STABILITY deltaTargetBytes=$pauseDelta over ${PauseObserveSec}s"
$summary += "PAUSED displayedPercent=$($lastPaused.DisplayedPercent) jobPercent=$($lastPaused.JobPercent) jobBytes=$($lastPaused.JobBytes) targetBytes=$($lastPaused.TargetBytes)"
$summary += "RESUME-BEFORE displayedPercent=$resumeUiPercent displayedBytes=$resumeUiBytes"
$summary += "RESUME-AFTER-MIN displayedPercent=$minUi UI-DROP-DETECTED=$dropped RAW-BELOW-DISPLAY=$rawBelowDisplay"
$summary += 'RESUME-SEQUENCE (elapsedMs | displayedPercent | displayedBytes | rawPercent | rawBytes | rawPhase):'
foreach ($s in $resumeSamples) { $summary += ("  " + $s.Elapsed + " | " + $s.DisplayedPercent + " | " + $s.UiBytes + " | " + $s.JobPercent + " | " + $s.JobBytes + " | " + $s.JobPhase) }
$summary += "FINAL jobPhase=$(if ($fin) { Get-JsonValue $fin.Json 'phase' } else { '' }) jobPercent=$(if ($fin) { Get-JsonValue $fin.Json 'percent' } else { '' }) jobBytes=$(if ($fin) { Get-JsonValue $fin.Json 'completedBytes' } else { '' })"
$summary += "FINAL-TARGET bytes=$($fts.Bytes) files=$($fts.Files)"
$summary += "TRANSITION-LINES=$hitCount"
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'pause-resume-summary.txt') -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }
Write-Host ("PAUSE-RESUME-OUT=" + $OutDir)