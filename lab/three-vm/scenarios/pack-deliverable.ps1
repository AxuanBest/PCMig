param(
    [string]$Stamp = '20261005',
    [string]$RepoRoot = 'E:\Project\deepseek work\PCMig',
    [string]$Stage = 'E:\PCMigLab\Staging'
)
$ErrorActionPreference = 'Stop'

# 1) 目标目录：桌面「新建文件夹 (4)」+ 日期子目录（只复制，不改源）
$desk = Join-Path $env:USERPROFILE 'Desktop'
$base = Get-ChildItem -LiteralPath $desk -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '新建文件夹*' } | Select-Object -First 1
if (-not $base) {
    $p = Join-Path $desk '新建文件夹 (4)'
    New-Item -ItemType Directory -Path $p -Force | Out-Null
    $base = Get-Item -LiteralPath $p
}
$out = Join-Path $base.FullName ("PCMig-UI-Closure-PHASE-ABCD-" + $Stamp)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$ev = Join-Path $out 'evidence'
$sc = Join-Path $out 'scripts'
New-Item -ItemType Directory -Path $ev, $sc -Force | Out-Null
Write-Host "OUT=$out"

$copied = @()
function Copy-One([string]$src, [string]$dstDir) {
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $dstDir -Force
        $script:copied += (Join-Path $dstDir (Split-Path -Leaf $src))
    } else {
        Write-Host "MISSING $src"
    }
}

# 2) 报告
Copy-One (Join-Path $RepoRoot 'docs\UI-CLOSURE-PHASE-ABCD-20261005.md') $out

# 3) 证据：截图
Copy-One (Join-Path $Stage 'phD-hero\hero-zoom.png') $ev
Copy-One (Join-Path $Stage 'phD-hero\hero-zoom2.png') $ev
Copy-One (Join-Path $Stage 'phDdone\statcards-v3.png') $ev
Copy-One (Join-Path $Stage 'phD-metric\running-00.png') $ev

# 4) 证据：CSV / 日志
Copy-One (Join-Path $Stage 'phD-run\frames.csv') $ev
Copy-One (Join-Path $Stage 'phC-5hz\frames.csv') $ev
Copy-One (Join-Path $Stage 'phC-big\frames.csv') $ev
Copy-One (Join-Path $Stage 'phDB\resume-samples.csv') $ev
Copy-One (Join-Path $Stage 'phDB\pause-resume.log') $ev
Copy-One (Join-Path $Stage 'phDB\pause-resume-summary.txt') $ev
Copy-One (Join-Path $Stage 'phDB\progress-transitions.jsonl') $ev

# 5) 证据：帧序列抽样（每 6 张取 1 张，最多 12 张）
$framesDir = Join-Path $Stage 'phD-run\frames'
if (Test-Path -LiteralPath $framesDir) {
    $fr = Join-Path $ev 'frames-sample'
    New-Item -ItemType Directory -Path $fr -Force | Out-Null
    $all = Get-ChildItem -LiteralPath $framesDir -Filter *.png | Sort-Object Name
    $i = 0
    foreach ($f in $all) {
        if ($i % 6 -eq 0 -and $i -lt 72) { Copy-Item -LiteralPath $f.FullName -Destination $fr -Force }
        $i++
    }
    Write-Host ("FRAMES-TOTAL=" + $all.Count)
}

# 6) 复现脚本
$rg = Join-Path $Stage 'recovery-gate'
foreach ($s in 'launch-app.ps1', 'ensure-step3.ps1', 'pause-resume-continuity.ps1',
               'capture-progress-frames.ps1', 'uia-lib.ps1', 'dump-controls.ps1',
               'step1-connect.ps1', 'step1-set-shares.ps1', 'step1-select-and-step2.ps1',
               'step2-prepare.ps1', 'goto-step.ps1') {
    Copy-One (Join-Path $rg $s) $sc
}

# 7) 源码变更清单（工作树状态，不提交）
$git = Join-Path $out 'git-status.txt'
Push-Location $RepoRoot
& git rev-parse HEAD            | Out-File -LiteralPath $git -Encoding utf8
& git rev-parse --abbrev-ref HEAD | Out-File -LiteralPath $git -Encoding utf8 -Append
& git status --porcelain        | Out-File -LiteralPath $git -Encoding utf8 -Append
& git diff --stat               | Out-File -LiteralPath $git -Encoding utf8 -Append
Pop-Location
Write-Host ("GIT-STATUS=" + (Test-Path -LiteralPath $git))

# 8) 清单 + SHA256 校验
$manifest = Join-Path $out '清单.txt'
$lines = @()
$lines += "PCMig v0.5.0 UI Closure 返修 · PHASE A-D 交付包"
$lines += ("生成时间(本地): " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines += ("仓库: " + $RepoRoot)
foreach ($l in @(& powershell -NoProfile -Command "Set-Location '$RepoRoot'; git rev-parse HEAD; git rev-parse --abbrev-ref HEAD")) {
    $lines += ("GIT: " + $l)
}
$lines += ""
$lines += "== 文件清单 =="
$files = Get-ChildItem -LiteralPath $out -Recurse -File | Sort-Object FullName
foreach ($f in $files) {
    $rel = $f.FullName.Substring($out.Length + 1)
    $lines += ("{0}`t{1}`t{2}" -f $rel, $f.Length, $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
}
$lines += ""
$lines += ("文件总数: " + $files.Count)
$lines | Out-File -LiteralPath $manifest -Encoding utf8

$sha = Join-Path $out 'SHA256.txt'
$shaLines = @()
foreach ($f in Get-ChildItem -LiteralPath $out -Recurse -File | Where-Object { $_.Name -ne 'SHA256.txt' } | Sort-Object FullName) {
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    $rel = $f.FullName.Substring($out.Length + 1)
    $shaLines += ("{0}  {1}" -f $h, $rel)
}
$shaLines | Out-File -LiteralPath $sha -Encoding utf8

Write-Host ("COPIED=" + $copied.Count)
Write-Host ("FILES=" + $files.Count)
Write-Host ("SHA-LINES=" + $shaLines.Count)
Write-Host "PACK-DONE"