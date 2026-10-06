param(
    [string]$Stamp = '20261005',
    [string]$RepoRoot = 'E:\Project\deepseek work\PCMig'
)
$ErrorActionPreference = 'Stop'

$desk = Join-Path $env:USERPROFILE 'Desktop'
$out = Join-Path $desk '源码包'

# 安全断言：目标必须正好是 Desktop\源码包
if ((Split-Path -Parent $out) -ne $desk -or (Split-Path -Leaf $out) -ne '源码包') {
    throw "拒绝执行：目标路径异常 $out"
}
if (Test-Path -LiteralPath $out) {
    Get-ChildItem -LiteralPath $out -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $out -Force | Out-Null
}
Write-Host "OUT=$out"

$chgDir = Join-Path $out '01-本次改动文件'
$tmpDir = Join-Path $env:TEMP ('pcmig-src-' + $Stamp)
New-Item -ItemType Directory -Path $chgDir, $tmpDir -Force | Out-Null

Push-Location $RepoRoot
$ga = @('-c', 'core.quotepath=false')

# 本次改动 = 已跟踪但相对 HEAD 有改动 ∪ 未跟踪新文件（排除测试结果目录）
$modified = @(& git @ga diff --name-only HEAD)
$untracked = @(& git @ga ls-files --others --exclude-standard | Where-Object { $_ -notmatch 'TestResults' })
$changed = @($modified + $untracked | Where-Object { $_ -and $_.Trim() -ne '' } | Sort-Object -Unique)

# 完整源码清单 = 全部 tracked ∪ 未跟踪新文件
$tracked = @(& git @ga ls-files)
$allSrc = @($tracked + $untracked | Where-Object { $_ -and $_.Trim() -ne '' } | Sort-Object -Unique)
$head = (& git rev-parse HEAD).Trim()
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
Pop-Location

Write-Host ("CHANGED=" + $changed.Count)
Write-Host ("ALLSRC=" + $allSrc.Count)

function Copy-KeepTree([string]$rel, [string]$root) {
    $src = Join-Path $RepoRoot ($rel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $src)) { Write-Host "SKIP-NOTFOUND $rel"; return $false }
    $dst = Join-Path $root ($rel -replace '/', '\')
    $parent = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    if (Test-Path -LiteralPath $src -PathType Container) { return $false }
    Copy-Item -LiteralPath $src -Destination $dst -Force
    return $true
}

# 1) 本次改动文件（保持相对路径）
$n1 = 0
foreach ($rel in $changed) { if (Copy-KeepTree $rel $chgDir) { $n1++ } }
Write-Host ("COPIED-CHANGED=" + $n1)

# 2) 完整源码（镜像到临时目录后压缩）
$n2 = 0
foreach ($rel in $allSrc) { if (Copy-KeepTree $rel $tmpDir) { $n2++ } }
Write-Host ("COPIED-ALL=" + $n2)
$zip = Join-Path $out ("03-PCMig-源码-v0.5.0-" + $Stamp + ".zip")
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($tmpDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $tmpDir -Recurse -Force
Write-Host ("ZIP-MB=" + [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2))

# 3) 本次改动补丁（已跟踪文件的 diff；新增文件见 01 目录）
Push-Location $RepoRoot
$diffFile = Join-Path $out '02-本次改动.diff'
& git @ga diff HEAD --binary | Out-File -LiteralPath $diffFile -Encoding utf8
$statFile = Join-Path $out '04-改动文件清单.txt'
$lines = @()
$lines += "PCMig v0.5.0 UI Closure 返修 · 改动与源码包"
$lines += ("生成时间(本地): " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines += ("仓库: " + $RepoRoot)
$lines += ("HEAD: " + $head)
$lines += ("分支: " + $branch)
$lines += ""
$lines += "== 一、本次未提交改动（相对 HEAD），共 $($changed.Count) 项 =="
$lines += "[状态] 类型  文件"
$numstat = @(& git @ga diff --numstat HEAD)
$numMap = @{}
foreach ($l in $numstat) {
    $p = $l -split "`t"
    if ($p.Count -ge 3) { $numMap[$p[2]] = ($p[0] + "/" + $p[1]) }
}
foreach ($rel in $changed) {
    $isNew = $untracked -contains $rel
    $kind = if ($isNew) { 'NEW ' } else { 'MOD ' }
    $delta = if ($numMap.ContainsKey($rel)) { $numMap[$rel] } else { '-' }
    $lines += ("{0} {1} {2}  (+{3})" -f '    ', $kind, $rel, $delta)
}
$lines += ""
$lines += "== 二、完整源码包内容（src/tests/docs/tools/lab/matrix/installer + 根文件） =="
$lines += ("tracked 文件: " + $tracked.Count)
$lines += ("未跟踪新增: " + $untracked.Count)
$lines += ("合计: " + $allSrc.Count)
Pop-Location
$lines | Out-File -LiteralPath $statFile -Encoding utf8
Write-Host "MANIFEST-DONE"
Write-Host "PACK-SOURCE-DONE"