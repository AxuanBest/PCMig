# round3-scenario-H.ps1 - PHASE G scenario H (Failed) end-to-end evidence on real hardware.
#
# Goal: make a REAL object failure happen in the running product (not a probe), then capture
#   * the control's Error semantics,
#   * that the Head RETAINS the last trusted value (never resets to 0, never jumps),
#   * that decoration never touches Value / Percent / ConfirmedBytes / Receipt / Verifier / JobState.
#
# Failure method (honest, fully reversible, no source fixture touched):
#   Mount a deliberately TINY VHDX (~2 GB) as the migration target. The plan still sees the
#   full 42 GB source, robocopy then genuinely runs out of space on the target volume
#   (ERROR_DISK_FULL) and objects fail for real. The VHD is dismounted and deleted
#   in a finally block, so nothing outside the throwaway VHD ever changes.
#
# ASCII only. Run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File round3-scenario-H.ps1

param(
    [string]$OutDir      = 'E:\PCMigLab\Staging\phH-failed',
    [string]$VhdPath     = 'E:\PCMigLab\Staging\phH-target.vhdx',
    [string]$MountPoint  = 'E:\PCMigLab\Staging\phH-target',
    [int]   $VhdSizeMB   = 4096,
    [int]   $SampleMs    = 400,
    [int]   $TimeoutSec  = 300,
    [switch]$Shot,
    [switch]$KeepVhd
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
$scriptsDir = 'E:\PCMigLab\Staging\recovery-gate'
. (Join-Path $scriptsDir 'uia-lib.ps1')
. (Join-Path $scriptsDir 'round3-proc-lib.ps1')

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$log = Join-Path $OutDir 'scenario-H.log'
Set-Content -LiteralPath $log -Value 'Round-3 PHASE G scenario H (Failed) - tiny-VHD disk-full method' -Encoding UTF8
$csv = Join-Path $OutDir 'scenario-H.csv'
Set-Content -LiteralPath $csv -Value 'utc,elapsedMs,phase,pct,bytes,stateline,statemsg,headXDip,hostW,maxLuma,fillPixels' -Encoding UTF8
function W { param([string]$m) $l = "$(Get-Date -Format 'HH:mm:ss.fff') $m"; Add-Content -LiteralPath $log -Value $l -Encoding UTF8; Write-Host $l }

function Get-RgTextSafe {
    param($Win, [string]$Aid, [int]$Retries = 3)
    for ($k = 0; $k -lt $Retries; $k++) {
        try { $v = Get-RgText $Win $Aid; return $v } catch { Start-Sleep -Milliseconds 250 }
    }
    return ''
}
function Get-RgAidSafe {
    param($Win, [string]$Aid, [int]$Retries = 3)
    for ($k = 0; $k -lt $Retries; $k++) {
        try { $e = Find-RgAid $Win $Aid; return $e } catch { Start-Sleep -Milliseconds 250 }
    }
    return $null
}
function Get-Rg3HJobState {
    param([string]$JobId = '')
    $d = Join-Path $env:ProgramData 'PCMig\Jobs'
    if (-not $JobId) {
        $latest = Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $latest) { return $null }
        $JobId = $latest.Name
    }
    $f = Join-Path (Join-Path $d $JobId) 'job-state.json'
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    try { $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json } catch { return $null }
    return [pscustomobject]@{ Id = $JobId; Phase = $j.phase; Percent = $j.percent; Committed = $j.completedBytes; Mtime = (Get-Item -LiteralPath $f).LastWriteTime }
}
function Get-PctFrom { param([string]$Text) if (-not $Text) { return -1 }; $m = [regex]::Match($Text, '([0-9]+(?:\.[0-9]+)?)\s*%'); if ($m.Success) { return [double]$m.Groups[1].Value }; return -1 }

function Get-Rg3HProbe {
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
    if ($Shot) { $bmp.Save((Join-Path $OutDir "hero-$script:shotTag.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $rect = New-Object System.Drawing.Rectangle(0, 0, $rw, $rh)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $rh)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data); $bmp.Dispose()
    $rightMost = -1; $fill = 0; $max = 0
    for ($i = 0; $i -lt $rw; $i++) {
        for ($j = 0; $j -lt $rh; $j++) {
            $o = $j * $stride + $i * 3
            $b = $bytes[$o]; $gg = $bytes[$o + 1]; $rr = $bytes[$o + 2]
            $l = $rr; if ($gg -gt $l) { $l = $gg }; if ($b -gt $l) { $l = $b }
            if ($l -gt $max) { $max = $l }
            if ($b -gt 150 -and ($b - $rr) -gt 50) { $fill++; if ($i -gt $rightMost) { $rightMost = $i } }
        }
    }
    return [pscustomobject]@{ HeadX = $(if ($rightMost -ge 0) { [double]($rightMost - $pad) } else { -1 }); HostW = [int]$r.Width; MaxLuma = $max; Fill = $fill }
}

function Add-HRow {
    param([string]$Phase, [int]$ElapsedMs, [double]$Pct, [string]$Bytes, [string]$State, [string]$Msg, $Probe)
    $hx = ''; $hw = ''; $mx = ''; $fp = ''
    if ($Probe) { $hx = $Probe.HeadX; $hw = $Probe.HostW; $mx = $Probe.MaxLuma; $fp = $Probe.Fill }
    Add-Content -LiteralPath $csv -Value (@(
        (Get-Date).ToUniversalTime().ToString('o'), $ElapsedMs, $Phase, $Pct, ($Bytes -replace ',', ''),
        ((($State -replace "`r?`n", ' / ')) -replace ',', ';'), ((($Msg -replace "`r?`n", ' / ')) -replace ',', ';'),
        $hx, $hw, $mx, $fp) -join ',') -Encoding UTF8
}

# ============================ SETUP: tiny target volume ============================
$vhdReady = $false
if (Test-Path -LiteralPath $MountPoint) { Remove-Item -LiteralPath $MountPoint -Force -Recurse -ErrorAction SilentlyContinue }
if (-not (Test-Path -LiteralPath $MountPoint)) { New-Item -ItemType Directory -Path $MountPoint -Force | Out-Null }
try {
    if (Test-Path -LiteralPath $VhdPath) { Remove-Item -LiteralPath $VhdPath -Force -ErrorAction SilentlyContinue }
    New-VHD -Path $VhdPath -SizeBytes ([int64]$VhdSizeMB * 1MB) -Dynamic -ErrorAction Stop | Out-Null
    $disk = Mount-VHD -Path $VhdPath -PassThru -ErrorAction Stop
    Start-Sleep -Seconds 2
    $dn = ($disk | Get-Disk).Number
    Initialize-Disk -Number $dn -PartitionStyle MBR -ErrorAction Stop
    $part = New-Partition -DiskNumber $dn -UseMaximumSize -ErrorAction Stop
    $dl = $null
    Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel 'PHH' -Confirm:$false -Force -ErrorAction Stop | Out-Null
    Add-PartitionAccessPath -DiskNumber $dn -PartitionNumber $part.PartitionNumber -AccessPath $MountPoint -ErrorAction Stop
    Start-Sleep -Seconds 2
    W ("VHD-READY path=$VhdPath sizeMB=$VhdSizeMB disk=$dn mountpoint=$MountPoint")
    $vhdReady = $true
} catch {
    W ("VHD-SETUP-FAILED " + $_.Exception.Message)
}

$app = Get-Process -Name 'PCMig.WinUI' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $app) { W 'NO-APP'; if ($vhdReady) { Dismount-VHD -Path $VhdPath -ErrorAction SilentlyContinue }; exit 1 }
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { W 'NO-WINDOW'; if ($vhdReady) { Dismount-VHD -Path $VhdPath -ErrorAction SilentlyContinue }; exit 1 }
W ("APP pid=" + $app.Id)

$targetDir = $MountPoint
$samples = 0; $sawError = $false; $failPct = -1
$pctSeen = @(); $errorStateText = ''; $errorMsgText = ''
$t0 = $null

try {
    # ---- point Step2 at the tiny volume and build the plan ----
    $nav = Find-RgAid $w 'Shell.Nav.Step2'
    if ($nav) { Invoke-RgClick $nav | Out-Null; Start-Sleep -Milliseconds 900 }
    $box = Find-RgAid $w 'TargetRootBox'
    if ($box) { Set-RgText $box $targetDir | Out-Null; W "SET-TARGET=$targetDir" } else { W 'TARGETBOX-MISSING' }
    Start-Sleep -Milliseconds 300
    $prep = Find-RgAid $w 'Step2.Prepare'
    if ($prep) { Invoke-RgClick $prep | Out-Null; W 'PREPARE-CLICKED'; Start-Sleep -Seconds 2 }
    $w = Get-RgWindow -TimeoutSec 15
    $msg = Get-RgText $w 'StateMessageText'
    W ("PLAN-STATE=[" + ($msg -replace "`r?`n", ' / ') + "]")

    $nav3 = Find-RgAid $w 'Shell.Nav.Step3'
    if ($nav3) { Invoke-RgClick $nav3 | Out-Null; Start-Sleep -Milliseconds 900 }
    $w = Get-RgWindow -TimeoutSec 15

    $start = Find-RgAid $w 'Shell.Transfer.Start'
    if (-not $start -or -not $start.Current.IsEnabled) { W 'START-NOT-ENABLED'; }
    else {
        Invoke-RgClick $start | Out-Null
        $t0 = Get-Date
        W 'START-CLICKED'
        while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
            Start-Sleep -Milliseconds $SampleMs
            $elapsed = [int]((Get-Date) - $t0).TotalMilliseconds
            $pct = Get-PctFrom (Get-RgTextSafe $w 'TotalPercentText')
            $byteTxt = Get-RgTextSafe $w 'TotalBytesText'
            $state = Get-RgTextSafe $w 'StateLineText'
            $msgTxt = Get-RgTextSafe $w 'StateMessageText'
            $js = Get-Rg3HJobState
            if ($js) { $script:lastJob = $js.Id }
            if ($pct -ge 0) { $pctSeen += $pct }
            $probe = $null
            $isErr = ($state -match ('Fail|Error|\u5931\u8d25|\u9519\u8bef')) -or ($msgTxt -match ('Fail|Error|\u5931\u8d25|\u9519\u8bef'))
            if ($isErr -and -not $sawError) {
                $sawError = $true; $failPct = $pct
                $errorStateText = $state; $errorMsgText = $msgTxt
                W ("FAIL-STATE-SEEN pct=$pct state=[" + ($state -replace "`r?`n", ' / ') + "] msg=[" + ($msgTxt -replace "`r?`n", ' / ') + "]")
                if ($Shot) {
                    # (reliable activation handled by uia-lib Get-RgWindow)
                    $script:shotTag = 'failed'
                    $probe = Get-Rg3HProbe $w
                }
            }
            Add-HRow 'running' $elapsed $pct $byteTxt $state $msgTxt $probe
            $samples++
            $btn = Get-RgAidSafe $w 'Shell.Transfer.Start'
            if ($elapsed -gt 12000 -and $btn -and $btn.Current.IsEnabled) { W 'START-RE-ENABLED (job settled)'; break }
            if ($state -match ('Completed|\u5df2\u5b8c\u6210') -and $elapsed -gt 12000) { W 'JOB-SETTLED'; break }
            if ($js -and $js.Phase -notin @('running', 'Running') -and $elapsed -gt 12000) { W ('JOB-PHASE-SETTLED phase=' + $js.Phase + ' pct=' + $js.Percent + ' committed=' + $js.Committed); break }
            if ($js -and $elapsed -gt 20000 -and ((Get-Date) - $js.Mtime).TotalSeconds -gt 12) { W ('JOB-STATE-STALE phase=' + $js.Phase + ' pct=' + $js.Percent + ' committed=' + $js.Committed + ' idleSec=' + [int]((Get-Date) - $js.Mtime).TotalSeconds); break }
        }
    }

    Start-Sleep -Milliseconds 1500
    Add-HRow 'final' $(if ($t0) { [int]((Get-Date) - $t0).TotalMilliseconds } else { 0 }) (Get-PctFrom (Get-RgTextSafe $w 'TotalPercentText')) (Get-RgTextSafe $w 'TotalBytesText') (Get-RgTextSafe $w 'StateLineText') (Get-RgTextSafe $w 'StateMessageText') $null
    $fjs = Get-Rg3HJobState
    if ($fjs) { W ('FINAL-JOB-STATE phase=' + $fjs.Phase + ' pct=' + $fjs.Percent + ' committed=' + $fjs.Committed) }

    # ---- engine-side failure evidence from the app log ----
    $logDir = Join-Path $env:ProgramData 'PCMig\Logs'
    $applog = Get-ChildItem -LiteralPath $logDir -Filter 'app-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $hits = @()
    if ($applog) {
        $hits = @(Select-String -LiteralPath $applog.FullName -Pattern ('Failed|\u5931\u8d25|\u9519\u8bef|disk|\u7a7a\u95f4\u4e0d\u8db3|Access is denied|RobocopyResult|Object.*(Fail|Error)') -ErrorAction SilentlyContinue | Select-Object -Last 80)
        $hits | ForEach-Object { $_.Line } | Set-Content -LiteralPath (Join-Path $OutDir 'applog-failure-lines.txt') -Encoding UTF8
    }
} finally {
    # ---- teardown, done SAFELY (a previous run hung the app by yanking the VHD while the app
    #      was still writing to it). Order matters:
    #        1) wait for the job to settle OR for the app to go idle
    #        2) if the app is still holding the volume, close the app FIRST (that releases every
    #           handle deterministically), then dismount the VHD, then relaunch the app
    #        3) only then delete the vhdx
    $wk = Get-Rg3WorkerPids -AppPid $app.Id
    if ($wk.Count -gt 0) { W ("TEARDOWN-KILL-WORKERS " + ($wk -join ',')); foreach ($k in $wk) { Stop-Process -Id $k -Force -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 2 }

    $settled = $false
    for ($i = 0; $i -lt 20; $i++) {
        $js2 = Get-Rg3HJobState
        $idle = $false
        try {
            $pp = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
            if ($pp) { $idle = $pp.Responding }
        } catch { }
        if ($js2 -and $js2.Phase -notin @('running', 'Running') -and ((Get-Date) - $js2.Mtime).TotalSeconds -gt 5) { $settled = $true; W ('TEARDOWN-SETTLED phase=' + $js2.Phase + ' pct=' + $js2.Percent + ' committed=' + $js2.Committed); break }
        Start-Sleep -Seconds 3
    }
    if (-not $settled) { W 'TEARDOWN-NOT-SETTLED (job kept running) - closing the app to release the volume' }

    $appClosed = $false
    try {
        $pp = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
        if ($pp -and $pp.Responding) {
            $pp.CloseMainWindow() | Out-Null
            Start-Sleep -Seconds 4
        }
        $pp = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
        if ($pp) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; $appClosed = $true }
        W ('TEARDOWN-APP-CLOSED force=' + $appClosed)
        Start-Sleep -Seconds 2
    } catch { W ('TEARDOWN-APP-CLOSE-ERR ' + $_.Exception.Message) }

    if ($vhdReady -and -not $KeepVhd) {
        try { Remove-PartitionAccessPath -DiskNumber $dn -PartitionNumber $part.PartitionNumber -AccessPath $MountPoint -ErrorAction SilentlyContinue } catch { }
        try { Dismount-VHD -Path $VhdPath -ErrorAction Stop; W 'VHD-DISMOUNTED' } catch { W ("VHD-DISMOUNT-FAILED " + $_.Exception.Message) }
        Start-Sleep -Seconds 1
        try { Remove-Item -LiteralPath $VhdPath -Force -ErrorAction Stop; W 'VHD-DELETED' } catch { W ("VHD-DELETE-FAILED " + $_.Exception.Message) }
    }

    # hand the app back to the user in a healthy state (it was closed to release the volume)
    try {
        $exe = 'E:\Project\deepseek work\PCMig\src\PCMig.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe'
        if (-not (Get-Process PCMig.WinUI -ErrorAction SilentlyContinue)) {
            $np = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
            Start-Sleep -Seconds 12
            $lp = Get-Process -Id $np.Id -ErrorAction SilentlyContinue
            if ($lp) { W ('APP-RELAUNCHED pid=' + $lp.Id + ' responding=' + $lp.Responding) } else { W 'APP-RELAUNCH-FAILED' }
        }
    } catch { W ('APP-RELAUNCH-ERR ' + $_.Exception.Message) }
}

$summary = @()
$summary += 'SCENARIO-H-FAILED-SUMMARY'
$summary += "APP-PID=" + $app.Id
$summary += "METHOD=tiny VHDX target volume ($VhdSizeMB MB) -> genuine disk-full object failure"
$summary += "VHD-READY=$vhdReady TARGET=$targetDir"
$summary += "SAMPLES=$samples"
$summary += "FAIL-STATE-SEEN=$sawError PCT-AT-FIRST-ERROR=$failPct"
$summary += "PCT-MIN=" + $(if ($pctSeen.Count) { ($pctSeen | Measure-Object -Minimum).Minimum } else { '' }) + " PCT-MAX=" + $(if ($pctSeen.Count) { ($pctSeen | Measure-Object -Maximum).Maximum } else { '' })
$summary += "ERROR-STATE=[" + ($errorStateText -replace "`r?`n", ' / ') + "]"
$summary += "ERROR-MSG=[" + ($errorMsgText -replace "`r?`n", ' / ') + "]"
$summary += "CSV=$csv"
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'scenario-H-summary.txt') -Encoding UTF8
$summary | ForEach-Object { Write-Host $_ }
Write-Output ("SCENARIO-H-OUT=" + $OutDir)