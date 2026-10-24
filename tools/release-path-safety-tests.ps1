# PCMig 发版路径安全 —— 负向对照测试（AUD-03 / v0.5.5）
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-path-safety-tests.ps1 `
#     -Impl tools\release-path-safety.ps1
#   powershell ... -File tools\release-path-safety-tests.ps1 -Impl <v0.5.4 等价复现>.ps1
#
# 同一组断言跑两份实现：修复前应 FAIL、修复后应 PASS。
# 所有用例只在 %TEMP% 下建隔离目录，绝不触碰真实交付根目录。

param(
  [Parameter(Mandatory = $true)][string]$Impl
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Impl)) { Write-Output ('找不到实现文件：' + $Impl); exit 2 }
. (Resolve-Path -LiteralPath $Impl).Path

$root = Join-Path $env:TEMP ('pcmig-pathsafety-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
New-Item -ItemType Directory -Force -Path $root | Out-Null

$results = New-Object System.Collections.Generic.List[object]

function Add-Case([string]$id, [string]$name, [bool]$expectOk, [bool]$actualOk, [string]$why) {
  $pass = ($expectOk -eq $actualOk)
  $results.Add([pscustomobject]@{ Id = $id; Name = $name; Expect = $expectOk; Actual = $actualOk; Pass = $pass; Why = $why })
  $tag = if ($pass) { 'PASS' } else { 'FAIL' }
  Write-Output ('  [' + $tag + '] ' + $id + ' ' + $name + '  期望Ok=' + $expectOk + ' 实际Ok=' + $actualOk)
  if (-not $pass) { Write-Output ('         -> ' + $why) }
}

function New-Case([string]$id) {
  $d = Join-Path $root $id
  New-Item -ItemType Directory -Force -Path $d | Out-Null
  return $d
}
function Touch([string]$path) {
  $idx = $path.LastIndexOf('\')
  $dir = $path.Substring(0, $idx)
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  [IO.File]::WriteAllText($path, 'x', (New-Object Text.UTF8Encoding($false)))
}

Write-Output ('实现：' + (Resolve-Path -LiteralPath $Impl).Path)
Write-Output ('临时根：' + $root)
Write-Output ''

# ── N1 Portable 混入用户资料（有强身份文件，也有用户文件，无清单）──────────────
# 不变量：目录里有无法确认归属的条目 ⇒ 必须拒绝删除。
$d = New-Case 'N1'
$p = Join-Path $d 'Portable'
New-Item -ItemType Directory -Force -Path $p | Out-Null
Touch (Join-Path $p 'PCMig.WinUI.exe')
Touch (Join-Path $p 'PCMig.Core.dll')
Touch (Join-Path $p '客户合同-2026.docx')
$v = Test-PortableCleanupAllowed -DeliveryFull $d -PortableFull $p
Add-Case 'N1' 'Portable 混入用户资料' $false $v.Ok $v.Why

# ── N2 带方括号的真实目录名 ────────────────────────────────────────────────
# 不变量：路径按字面处理。含 [ ] 的交付根里放用户文件，必须拒绝删除。
# v0.5.4 用位置参数 Test-Path/Get-ChildItem，[客户] 被当字符类 ⇒ 看不到内容 ⇒ 误放行。
$d = New-Case 'N2'
$delivery2 = Join-Path $d '[客户]交付'
$p2 = Join-Path $delivery2 'Portable'
New-Item -ItemType Directory -Force -Path $p2 | Out-Null
Touch (Join-Path $p2 '年度报表.xlsx')
Touch (Join-Path $p2 '照片-2026.jpg')
$v = Test-PortableCleanupAllowed -DeliveryFull $delivery2 -PortableFull $p2
Add-Case 'N2' '带方括号目录名 + 用户文件' $false $v.Ok $v.Why

# ── N3 交付目录位于仓库之内（重叠）──────────────────────────────────────────
$d = New-Case 'N3'
$repo3 = Join-Path $d 'repo'
New-Item -ItemType Directory -Force -Path $repo3 | Out-Null
$probs = @(Test-ReleasePathSet -RepoFull $repo3 -DeliveryFull (Join-Path $repo3 'out') -WorkCopyFull (Join-Path $d 'wc'))
Add-Case 'N3' '交付目录位于仓库内' $false ($probs.Count -eq 0) ($probs -join ' ; ')

# ── N4 交付目录 == 工作副本 ────────────────────────────────────────────────
$d = New-Case 'N4'
$same = Join-Path $d 'same'
New-Item -ItemType Directory -Force -Path $same | Out-Null
$probs = @(Test-ReleasePathSet -RepoFull (Join-Path $d 'repo') -DeliveryFull $same -WorkCopyFull $same)
Add-Case 'N4' '交付目录与工作副本同一路径' $false ($probs.Count -eq 0) ($probs -join ' ; ')

# ── N5 有清单，但 Portable 里混入了清单外文件 ───────────────────────────────
$d = New-Case 'N5'
$p5 = Join-Path $d 'Portable'
New-Item -ItemType Directory -Force -Path $p5 | Out-Null
Touch (Join-Path $p5 'PCMig.WinUI.exe')
$manifest = ($p5 + '.manifest.txt')
[IO.File]::WriteAllLines($manifest, [string[]]@('PCMig.WinUI.exe'), (New-Object Text.UTF8Encoding($false)))
Touch (Join-Path $p5 '我自己放的文件.txt')
$v = Test-PortableCleanupAllowed -DeliveryFull $d -PortableFull $p5
Add-Case 'N5' '清单存在但出现清单外文件' $false $v.Ok $v.Why

# ── N6 正常产物（正向对照，防止"一律拒绝"也能全绿）──────────────────────────
$d = New-Case 'N6'
$p6 = Join-Path $d 'Portable'
New-Item -ItemType Directory -Force -Path $p6 | Out-Null
Touch (Join-Path $p6 'PCMig.WinUI.exe')
Touch (Join-Path $p6 'PCMig.Core.dll')
Touch (Join-Path $p6 'pcmig-cli.exe')
New-Item -ItemType Directory -Force -Path (Join-Path $p6 'matrix') | Out-Null
$v = Test-PortableCleanupAllowed -DeliveryFull $d -PortableFull $p6
Add-Case 'N6' '正常产物目录（应允许清理）' $true $v.Ok $v.Why

# ── N7 正常产物 + 清单完全一致 ─────────────────────────────────────────────
$d = New-Case 'N7'
$p7 = Join-Path $d 'Portable'
New-Item -ItemType Directory -Force -Path $p7 | Out-Null
Touch (Join-Path $p7 'PCMig.WinUI.exe')
Touch (Join-Path $p7 'PCMig.Core.dll')
$m7 = ($p7 + '.manifest.txt')
[IO.File]::WriteAllLines($m7, [string[]]@('PCMig.WinUI.exe', 'PCMig.Core.dll'), (New-Object Text.UTF8Encoding($false)))
$v = Test-PortableCleanupAllowed -DeliveryFull $d -PortableFull $p7
Add-Case 'N7' '清单与实际完全一致（应允许清理）' $true $v.Ok $v.Why

# ── N8 空 Portable ────────────────────────────────────────────────────────
$d = New-Case 'N8'
$p8 = Join-Path $d 'Portable'
New-Item -ItemType Directory -Force -Path $p8 | Out-Null
$v = Test-PortableCleanupAllowed -DeliveryFull $d -PortableFull $p8
Add-Case 'N8' '空 Portable 目录' $true $v.Ok $v.Why

# ── N9 交付根是盘符根 ──────────────────────────────────────────────────────
$v = Test-PortableCleanupAllowed -DeliveryFull 'I:\' -PortableFull 'I:\Portable'
Add-Case 'N9' '交付根是盘符根' $false $v.Ok $v.Why

# ── N10 顶层无任何 PCMig 身份文件 ─────────────────────────────────────────
$d = New-Case 'N10'
$p10 = Join-Path $d 'Portable'
New-Item -ItemType Directory -Force -Path $p10 | Out-Null
Touch (Join-Path $p10 '家庭照片.jpg')
Touch (Join-Path $p10 '简历.docx')
$v = Test-PortableCleanupAllowed -DeliveryFull $d -PortableFull $p10
Add-Case 'N10' '无任何 PCMig 身份文件' $false $v.Ok $v.Why

# ── N11 镜像目录位于交付目录之内（重叠）──────────────────────────────────
$d = New-Case 'N11'
$del11 = Join-Path $d 'delivery'
New-Item -ItemType Directory -Force -Path $del11 | Out-Null
$probs = @(Test-ReleasePathSet -RepoFull (Join-Path $d 'repo') -DeliveryFull $del11 -WorkCopyFull (Join-Path $d 'wc') -MirrorFull (Join-Path $del11 'mirror'))
Add-Case 'N11' '镜像目录位于交付目录内' $false ($probs.Count -eq 0) ($probs -join ' ; ')

# ── N12 通配符路径 ────────────────────────────────────────────────────────
$d = New-Case 'N12'
$probs = @(Test-ReleasePathSet -RepoFull (Join-Path $d 'repo') -DeliveryFull (Join-Path $d 'out*') -WorkCopyFull (Join-Path $d 'wc'))
Add-Case 'N12' '交付目录含通配符' $false ($probs.Count -eq 0) ($probs -join ' ; ')

# ── 汇总 ──────────────────────────────────────────────────────────────────
$pass = @($results | Where-Object { $_.Pass }).Count
$fail = @($results | Where-Object { -not $_.Pass }).Count
Write-Output ''
Write-Output ('===== 汇总：PASS=' + $pass + '  FAIL=' + $fail + '  共 ' + $results.Count + ' =====')
if ($fail -gt 0) {
  Write-Output '失败用例：'
  foreach ($r in ($results | Where-Object { -not $_.Pass })) {
    Write-Output ('  - ' + $r.Id + ' ' + $r.Name + '：' + $r.Why)
  }
}
try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } catch { }
if ($fail -gt 0) { exit 1 } else { exit 0 }
