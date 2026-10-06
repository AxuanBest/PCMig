# round3-scenario-G.ps1 - PHASE G scenario G (Holding / network stall) end-to-end evidence.
#
# Proves (execution book section 18 / section 31):
#   Progress Head = FACT
#                            (pixel: blue-fill rightmost column stays identical)
#   Push Band = ACTIVITY
#                            (pixel: luma INSIDE the fill capsule keeps changing
#                             across consecutive frames while the fill edge does not)
#   Forbidden: Head must never creep without evidence
#
# Stall method: freeze robocopy.exe workers via ntdll NtSuspendProcess while the
#   PCMig app keeps running. Target growth stops; app stays responsive.
#   Controllable analog of a network/SMB stall.
#
# ASCII only. Run via:
#   powershell -NoProfile -ExecutionPolicy Bypass -File round3-scenario-G.ps1
#
# Lesson: never name a parameter Pid - it is a read-only automatic variable.

param(
    [string]$OutDir       = 'E:\PCMigLab\Staging\phG-holding',
    [int]   $StallMs      = 3000,
    [int]   $StallCount   = 3,
    [double]$StallFromPct = 8,
    [int]   $SampleMs     = 250,
    [int]   $FrameMs      = 130,
    [int]   $TimeoutSec   = 240,
    [switch]$NoFrames
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
. 'E:\PCMigLab\Staging\recovery-gate\round3-proc-lib.ps1'

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$log = Join-Path $OutDir 'scenario-G.log'
Set-Content -LiteralPath $log -Value 'Round-3 PHASE G scenario G (Holding) - robocopy freeze method, pixel-level' -Encoding UTF8
$csv = Join-Path $OutDir 'scenario-G.csv'
Set-Content -LiteralPath $csv -Value 'utc,elapsedMs,phase,pct,bytes,stateline,engineBytes,enginePct,jobPhase,jobPercent,jobCommitted,jobFailed,stalled,workerCount,stallIndex,fillRightDip,fillLeftDip,hostW,bluePixels,maxLuma,meanLumaInFill,deltaFill,deltaTrack' -Encoding UTF8
function W { param([string]$m) $l = "$(Get-Date -Format 'HH:mm:ss.fff') $m"; Add-Content -LiteralPath $log -Value $l -Encoding UTF8; Write-Host $l }

function Get-EngineBytes {
    # The state line carries the ENGINE truth, independent of the animated Head:
    #   e.g. [moving 13.64 GB / 42 GB (32.7%), object 0/41]
    # This separates [truth frozen] from [display catching up].
    param([string]$Text)
    if (-not $Text) { return '' }
    $m = [regex]::Match($Text, '([0-9]+(?:\.[0-9]+)?)\s*(TB|GB|MB|KB|B)\s*/\s*([0-9]+(?:\.[0-9]+)?)\s*(TB|GB|MB|KB|B)')
    if (-not $m.Success) { return '' }
    $mult = @{ 'TB' = 1099511627776.0; 'GB' = 1073741824.0; 'MB' = 1048576.0; 'KB' = 1024.0; 'B' = 1.0 }
    $done = [double]$m.Groups[1].Value * $mult[$m.Groups[2].Value.ToUpper()]
    $pct = [regex]::Match($Text, '\(\([0-9]+(?:\.[0-9]+)?\)%\)')
    $p = if ($pct.Success) { $pct.Groups[1].Value } else { '' }
    return ("{0}|{1}" -f [long]$done, $p)
}

function Get-PctFrom { param([string]$Text) if (-not $Text) { return -1 }; $m = [regex]::Match($Text, '([0-9]+(?:\.[0-9]+)?)\s*%'); if ($m.Success) { return [double]$m.Groups[1].Value }; return -1 }

# ---- fast hero ROI probe: one locked bitmap pass, returns geometry + luma stats ----
function Get-Rg3HeroProbe {
    param($Win)
    $el = Find-RgAid $Win 'TotalImmersiveProgress'
    if (-not $el) { return $null }
    $r = $el.Current.BoundingRectangle
    if ([int]$r.Width -le 0 -or [int]$r.Height -le 0) { return $null }
    $pad = 8
    $rx = [Math]::Max(0, [int]$r.X - $pad); $ry = [Math]::Max(0, [int]$r.Y - $pad)
    $rw = [int]$r.Width + 2 * $pad; $rh = [int]$r.Height + 2 * $pad
    $bmp = New-Object System.Drawing.Bitmap($rw, $rh, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rx, $ry, 0, 0, (New-Object System.Drawing.Size($rw, $rh)))
    $g.Dispose()
    $rect = New-Object System.Drawing.Rectangle(0, 0, $rw, $rh)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $rh)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    $allRows = New-Object 'int[][]' $rh
    $lumaRow = New-Object int[] $rw
    $rowBuf = New-Object 'int[][]' $rh
    for ($j = 0; $j -lt $rh; $j++) {
        $rowBuf[$j] = New-Object int[] $rw
        $o0 = $j * $stride
        for ($i = 0; $i -lt $rw; $i++) {
            $o = $o0 + $i * 3
            $b = $bytes[$o]; $gg = $bytes[$o + 1]; $rr = $bytes[$o + 2]
            $l = $rr; if ($gg -gt $l) { $l = $gg }; if ($b -gt $l) { $l = $b }
            $rowBuf[$j][$i] = $l
        }
    }
    $rightMost = -1; $leftMost = -1; $blue = 0; $max = 0
    for ($i = 0; $i -lt $rw; $i++) {
        $colMax = 0; $colBlue = 0
        for ($j = 0; $j -lt $rh; $j++) {
            $l = $rowBuf[$j][$i]
            if ($l -gt $colMax) { $colMax = $l }
            $o = $j * $stride + $i * 3
            $b = $bytes[$o]; $rr = $bytes[$o + 2]
            if ($b -gt 150 -and ($b - $rr) -gt 50) { $colBlue++ }
        }
        $lumaRow[$i] = $colMax
        if ($colMax -gt $max) { $max = $colMax }
        if ($colBlue -gt 0) { $blue += $colBlue; if ($i -gt $rightMost) { $rightMost = $i }; if ($leftMost -lt 0) { $leftMost = $i } }
    }
    # mean luma strictly inside the capsule so Push Band motion shows up
    $meanIn = -1.0
    if ($rightMost -gt 30) {
        $s = [int]($leftMost + ($rightMost - $leftMost) * 0.30)
        $e = [int]($leftMost + ($rightMost - $leftMost) * 0.85)
        if ($e -le $s) { $e = $s + 1 }
        $sum = 0; $n = 0
        for ($i = $s; $i -lt $e -and $i -lt $rw; $i++) { $sum += $lumaRow[$i]; $n++ }
        if ($n -gt 0) { $meanIn = [Math]::Round($sum / $n, 2) }
    }
    return [pscustomobject]@{
        RectX = $rx; RectY = $ry; W = $rw; H = $rh; Pad = $pad
        HostW = [int]$r.Width; FillRightDip = $(if ($rightMost -ge 0) { [double]($rightMost - $pad) } else { -1 })
        FillLeftDip = $(if ($leftMost -ge 0) { [double]($leftMost - $pad) } else { -1 })
        BluePixels = $blue; MaxLuma = $max; MeanLumaInFill = $meanIn; LumaRow = $lumaRow; Rows = $rowBuf
    }
}

function Save-Rg3Hero {
    param($Probe, [string]$Tag)
    if (-not $Probe) { return }
    $bmp = New-Object System.Drawing.Bitmap($Probe.W, $Probe.H, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($Probe.RectX, $Probe.RectY, 0, 0, (New-Object System.Drawing.Size($Probe.W, $Probe.H)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir ("hero-$Tag.png")), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Get-Rg3FrameDelta {
    # Sum |luma difference| over a column band between two frames.
    # -BandKind: fill  = inside the fill capsule (isolates the Head)
    #            track = ahead of capsule on empty track (isolates the Push Band)
    param($A, $B, [string]$BandKind = 'fill', [int]$FromDip = -1, [int]$ToDip = -1)
    if (-not $A -or -not $B) { return -1 }
    if (-not $A.Rows -or -not $B.Rows) { return -1 }
    if ($A.Rows.Length -ne $B.Rows.Length) { return -1 }
    $pad = $A.Pad
    $colA = $FromDip; $colB = $ToDip
    if ($colA -lt 0 -or $colB -lt 0) {
        if ($BandKind -eq 'fill') {
            $colA = [Math]::Max(0, [int]($A.FillLeftDip + $pad) + 4)
            $colB = [Math]::Max($colA + 1, [int]($A.FillRightDip + $pad) - 4)
        } else {
            $colA = [Math]::Min($A.W - 1, [int]($A.FillRightDip + $pad) + 6)
            $colB = [Math]::Min($A.W - 1, $colA + 120)
        }
    }
    if ($colA -lt 0) { $colA = 0 }
    if ($colB -le $colA) { return -1 }
    $sum = 0L; $n = 0
    for ($i = $colA; $i -le $colB -and $i -lt $A.W; $i++) {
        for ($j = 0; $j -lt $A.Rows.Length; $j++) {
            $sum += [Math]::Abs($A.Rows[$j][$i] - $B.Rows[$j][$i]); $n++
        }
    }
    if ($n -eq 0) { return -1 }
    return $sum
}

function Add-Rg3Row {
    param([string]$Phase, [int]$ElapsedMs, [double]$Pct, [string]$Bytes, [string]$State, [bool]$Stalled,
          [int]$WorkerCount, [int]$StallIndex, $Probe, [long]$DeltaFill, [long]$DeltaTrack)
    $fr = ''; $fl = ''; $hw = ''; $bp = ''; $mx = ''; $mn = ''
    if ($Probe) { $fr = $Probe.FillRightDip; $fl = $Probe.FillLeftDip; $hw = $Probe.HostW; $bp = $Probe.BluePixels; $mx = $Probe.MaxLuma; $mn = $Probe.MeanLumaInFill }
    $eb = Get-EngineBytes $State
    $ebv = ''; $epv = ''
    if ($eb) { $parts = $eb -split '\|'; $ebv = $parts[0]; if ($parts.Count -gt 1) { $epv = $parts[1] } }
    $js = Get-Rg3JobState
    $jp = ''; $jpc = ''; $jc = ''; $jf = ''
    if ($js) { $jp = $js.Phase; $jpc = $js.Percent; $jc = $js.Committed; $jf = $js.Failed }
    Add-Content -LiteralPath $csv -Value (@(
        (Get-Date).ToUniversalTime().ToString('o'), $ElapsedMs, $Phase, $Pct, ($Bytes -replace ',', ''),
        ((($State -replace "`r?`n", ' / ')) -replace ',', ';'), $ebv, $epv, $jp, $jpc, $jc, $jf, $Stalled, $WorkerCount, $StallIndex,
        $fr, $fl, $hw, $bp, $mx, $mn, $DeltaFill, $DeltaTrack) -join ',') -Encoding UTF8
}

$app = Get-Process -Name 'PCMig.WinUI' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $app) { W 'NO-APP'; exit 1 }
$appPid = $app.Id
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { W 'NO-WINDOW'; exit 1 }
W ("APP pid=$appPid window=" + $w.Current.Name)

# ---- pre-start baseline shot ----
$base = Get-Rg3HeroProbe $w
if ($base) { Save-Rg3Hero $base 'idle'; W ("IDLE-PROBE hostW=" + $base.HostW + " maxLuma=" + $base.MaxLuma + " bluePixels=" + $base.BluePixels + " fillRightDip=" + $base.FillRightDip) }

$start = Find-RgAid $w 'Shell.Transfer.Start'
if (-not $start -or -not $start.Current.IsEnabled) { W 'START-NOT-ENABLED'; exit 2 }
Invoke-RgClick $start | Out-Null
$t0 = Get-Date
W 'START-CLICKED'

$stallIndex = 0; $stalledNow = $false; $samples = 0
$stallLog = @(); $holdingFrames = @()

while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds $SampleMs
    $elapsed = [int]((Get-Date) - $t0).TotalMilliseconds
    $workers = Get-Rg3WorkerPids -AppPid $appPid
    $pctTxt = Get-RgText $w 'TotalPercentText'
    $pct = Get-PctFrom $pctTxt
    $byteTxt = Get-RgText $w 'TotalBytesText'
    $state = Get-RgText $w 'StateLineText'

    if (-not $stalledNow -and $stallIndex -lt $StallCount -and $pct -ge $StallFromPct -and $workers.Count -gt 0) {
        # ---------- OPEN STALL ----------
        $pre = Get-Rg3HeroProbe $w
        Save-Rg3Hero $pre 'stall-pre'
        Add-Rg3Row 'pre-stall' $elapsed $pct $byteTxt $state $false $workers.Count $stallIndex $pre -1 -1
        $samples++
        $n = Suspend-Rg3Process -Id $workers
        $frozenPids = @()
        foreach ($wp in $workers) { $frozenPids += [int]$wp }
        $stalledNow = $true
        W ("STALL-OPEN index=$stallIndex suspended=$n/" + $workers.Count + " pids=" + ($workers -join ',') + " stallMs=$StallMs pct=$pct fillRightDip=" + $(if ($pre) { $pre.FillRightDip } else { 'n/a' }))
        $stallLog += [pscustomobject]@{ Index = $stallIndex; OpenPct = $pct; OpenMs = $elapsed; Pids = ($workers -join ','); SuspendReport = "$n/$($workers.Count)"; FillRightAtOpen = $(if ($pre) { $pre.FillRightDip } else { -1 }) }

        # ---------- DENSE FRAME SERIES INSIDE THE STALL ----------
        $stallDeadline = (Get-Date).AddMilliseconds($StallMs)
        $prevProbe = $pre
        $fpct0 = $pct; $fpctMin = $pct; $fpctMax = $pct
        $edgeMin = 99999.0; $edgeMax = -99999.0
        $dFillSum = 0L; $dTrackSum = 0L; $deltaCount = 0
        $engineMin = -1L; $engineMax = -1L
        $jcsMin = -1L; $jcsMax = -1L; $wcMax = 0
        $frameIdx = 0
        while ((Get-Date) -lt $stallDeadline) {
            $e2 = [int]((Get-Date) - $t0).TotalMilliseconds
            $p2 = Get-PctFrom (Get-RgText $w 'TotalPercentText')
            $b2 = Get-RgText $w 'TotalBytesText'
            $s2 = Get-RgText $w 'StateLineText'
            if ($p2 -ge 0) { if ($p2 -lt $fpctMin) { $fpctMin = $p2 }; if ($p2 -gt $fpctMax) { $fpctMax = $p2 } }
            $eb = Get-EngineBytes $s2
            if ($eb) {
                $ev = [long](($eb -split '\|')[0])
                if ($engineMin -lt 0 -or $ev -lt $engineMin) { $engineMin = $ev }
                if ($ev -gt $engineMax) { $engineMax = $ev }
            }
            $probe = $null; $dFill = -1; $dTrack = -1
            # Keep the stall airtight: the engine may kill the current worker and spawn a fresh
            # robocopy.exe. Any new worker must be frozen in the SAME frame, otherwise the engine
            # truth keeps inching forward and the window is no longer a true stall.
            $fresh = Get-Rg3WorkerPids -AppPid $appPid
            $newOnes = @($fresh | Where-Object { $workers -notcontains $_ })
            if ($newOnes.Count -gt 0) {
                $nf = Suspend-Rg3Process -Id $newOnes
                foreach ($np in $newOnes) { $frozenPids += [int]$np }
                $workers = $fresh
                W ("STALL-REFREEZE index=$stallIndex new=" + ($newOnes -join ',') + " frozen=$nf")
            }
            if (-not $NoFrames) {
                $probe = Get-Rg3HeroProbe $w
                $dFill = Get-Rg3FrameDelta $prevProbe $probe 'fill'
                $dTrack = Get-Rg3FrameDelta $prevProbe $probe 'track'
                if ($dFill -ge 0) { $dFillSum += $dFill }
                if ($dTrack -ge 0) { $dTrackSum += $dTrack }
                if ($dFill -ge 0 -or $dTrack -ge 0) { $deltaCount++ }
                if ($probe -and $probe.FillRightDip -ge 0) {
                    if ($probe.FillRightDip -lt $edgeMin) { $edgeMin = $probe.FillRightDip }
                    if ($probe.FillRightDip -gt $edgeMax) { $edgeMax = $probe.FillRightDip }
                }
                $holdingFrames += [pscustomobject]@{ Stall = $stallIndex; Idx = $frameIdx; Ms = $e2; Pct = $p2; EngineBytes = $(if ($eb) { ($eb -split '\|')[0] } else { '' }); FillRight = $(if ($probe) { $probe.FillRightDip } else { -1 }); MeanIn = $(if ($probe) { $probe.MeanLumaInFill } else { -1 }); DeltaFill = $dFill; DeltaTrack = $dTrack }
                $prevProbe = $probe
                if ($frameIdx -eq 0 -or $frameIdx -eq 8 -or $frameIdx -eq 16) { Save-Rg3Hero $probe ("holding{0}-f{1}" -f $stallIndex, $frameIdx) }
            }
            Add-Rg3Row 'holding' $e2 $p2 $b2 $s2 $true $workers.Count $stallIndex $probe $dFill $dTrack
            $samples++
            $frameIdx++
            Start-Sleep -Milliseconds $FrameMs
        }
        W ("STALL-HOLD index=$stallIndex frames=$frameIdx pctRange=$fpctMin~$fpctMax engineRange=" + $(if ($engineMin -ge 0) { "$engineMin~$engineMax (delta=" + ($engineMax - $engineMin) + ")" } else { 'n/a' }) + " fillRightRange=" + $(if ($edgeMin -lt 99999) { "$edgeMin~$edgeMax" } else { 'n/a' }) + " deltaFillSum=$dFillSum deltaTrackSum=$dTrackSum")
        $stallLog[-1] | Add-Member -NotePropertyName ClosePct -NotePropertyValue $fpctMax -Force
        $stallLog[-1] | Add-Member -NotePropertyName Frames -NotePropertyValue $frameIdx -Force
        $stallLog[-1] | Add-Member -NotePropertyName PctMin -NotePropertyValue $fpctMin -Force
        $stallLog[-1] | Add-Member -NotePropertyName PctMax -NotePropertyValue $fpctMax -Force
        $stallLog[-1] | Add-Member -NotePropertyName EdgeMin -NotePropertyValue $edgeMin -Force
        $stallLog[-1] | Add-Member -NotePropertyName EdgeMax -NotePropertyValue $edgeMax -Force
        $stallLog[-1] | Add-Member -NotePropertyName DeltaFillSum -NotePropertyValue $dFillSum -Force
        $stallLog[-1] | Add-Member -NotePropertyName DeltaTrackSum -NotePropertyValue $dTrackSum -Force
        $stallLog[-1] | Add-Member -NotePropertyName EngineMin -NotePropertyValue $engineMin -Force
        $stallLog[-1] | Add-Member -NotePropertyName EngineMax -NotePropertyValue $engineMax -Force
        $stallLog[-1] | Add-Member -NotePropertyName JobCommittedMin -NotePropertyValue $jcsMin -Force
        $stallLog[-1] | Add-Member -NotePropertyName JobCommittedMax -NotePropertyValue $jcsMax -Force
        $stallLog[-1] | Add-Member -NotePropertyName JobCommittedDelta -NotePropertyValue $(if ($jcsMin -ge 0) { $jcsMax - $jcsMin } else { -1 }) -Force
        $stallLog[-1] | Add-Member -NotePropertyName WorkerCountMax -NotePropertyValue $wcMax -Force
        $stallLog[-1] | Add-Member -NotePropertyName FrozenPids -NotePropertyValue ($frozenPids -join ',') -Force
        $stallLog[-1] | Add-Member -NotePropertyName FrozenCount -NotePropertyValue $frozenPids.Count -Force
        $r = Resume-Rg3Process -Id $frozenPids
        $stalledNow = $false
        $stallIndex++
        W ("STALL-CLOSE index=" + ($stallIndex - 1) + " resumed=$r")
        continue
    }

    $probe2 = $null
    Add-Rg3Row 'running' $elapsed $pct $byteTxt $state $false $workers.Count $stallIndex $probe2 -1 -1
    $samples++
    if ($pct -ge 99.9 -and $stallIndex -ge 1) { W 'REACHED-CEILING'; break }
    if ($pct -ge 100) { Start-Sleep -Milliseconds 1500; break }
}

# ---- safety: never leave a frozen worker behind (every PID we ever froze) ----
$allFrozen = @()
foreach ($x in $stallLog) {
    if ($x.PSObject.Properties.Name -contains 'FrozenPids' -and $x.FrozenPids) {
        foreach ($fp in ($x.FrozenPids -split ',')) { if ($fp) { $allFrozen += [int]$fp } }
    }
}
$allFrozen = @($allFrozen | Sort-Object -Unique)
if ($allFrozen.Count -gt 0) { Resume-Rg3Process -Id $allFrozen | Out-Null; W ("SAFETY-RESUME-FROZEN " + ($allFrozen -join ',')) }
$left = Get-Rg3WorkerPids -AppPid $appPid
if ($left.Count -gt 0) { Resume-Rg3Process -Id $left | Out-Null; W ("SAFETY-RESUME " + ($left -join ',')) }

$summary = @()
$summary += 'SCENARIO-G-HOLDING-SUMMARY'
$summary += "APP-PID=$appPid"
$summary += "METHOD=ntdll NtSuspendProcess on robocopy workers (PCMig app keeps running)"
$summary += "STALL-PLAN stallMs=$StallMs stallCount=$StallCount fromPct=$StallFromPct frameMs=$FrameMs"
$summary += "STALLS-OPENED=$stallIndex  SAMPLES=$samples"
foreach ($x in $stallLog) {
    $summary += ("  stall[{0}] openMs={1} openPct={2} suspend={3} frames={4} pctRange={5}~{6} engineRange={7}~{8} (engineDelta={9}) fillRightRange={10}~{11} deltaFillSum={12} deltaTrackSum={13} jobCommittedDelta={16} workerCountMax={17} frozenPids=[{14}] frozenCount={15}" -f `
        $x.Index, $x.OpenMs, $x.OpenPct, $x.SuspendReport, $x.Frames, $x.PctMin, $x.PctMax, $x.EngineMin, $x.EngineMax, ($x.EngineMax - $x.EngineMin), $x.EdgeMin, $x.EdgeMax, $x.DeltaFillSum, $x.DeltaTrackSum, $x.FrozenPids, $x.FrozenCount, $x.JobCommittedDelta, $x.WorkerCountMax)
}
$summary += 'VERDICT-TRUTH-FROZEN: engineDelta == 0 across the stall window means the ENGINE truth did not move.'
$summary += 'VERDICT-HEAD-FROZEN : fillRightRange width <= 2 DIP means the visible Head held its position.'
$summary += 'VERDICT-BAND-ALIVE : deltaTrackSum > 0 (luma AHEAD of the capsule changed) means motion continued while truth was frozen.'
$summary += 'VERDICT-NO-CREEP   : pctRange must stay within the display-filter catch-up bound, never a run of +1pp steps.'
$summary += "CSV=$csv"
$summary += "FRAMES=$(@($holdingFrames).Count)"
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'scenario-G-summary.txt') -Encoding UTF8
@($holdingFrames) | Export-Csv -LiteralPath (Join-Path $OutDir 'scenario-G-frames.csv') -NoTypeInformation -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }
Write-Output ("SCENARIO-G-OUT=" + $OutDir)