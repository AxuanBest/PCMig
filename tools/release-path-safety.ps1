# PCMig 发版路径安全（AUD-03 / v0.5.5）
#
# 为什么单独成文件：交付目录、工作副本、镜像目录都会被**递归删除**，
# 归属判断与路径断言一旦出错就是不可挽回的数据丢失。把它们放在这里，
# 一是 release.ps1 与测试脚本共用同一份实现（不会"测的和跑的不是一套逻辑"），
# 二是可以在 -GateOnly 阶段就验证，不必等构建跑完才发现路径配错。
#
# 本文件被 tools\release.ps1 dot-source，也被 tools\release-path-safety-tests.ps1 dot-source。
# 只定义函数，不做任何 IO 副作用。

# 归一化：展开环境变量 → 全路径 → 去掉尾部斜杠。
# 含通配符（* 或 ?）的输入不是"路径"，直接标记成不可用，由调用方中止。
function Get-FullPath([string]$p) {
  if ([string]::IsNullOrWhiteSpace($p)) { return '' }
  $expanded = [Environment]::ExpandEnvironmentVariables($p)
  if ($expanded -match '[*?]') { return '<<WILDCARD:' + $expanded + '>>' }
  try {
    return [IO.Path]::GetFullPath($expanded).TrimEnd('\', '/')
  } catch {
    return '<<INVALID:' + $expanded + '>>'
  }
}

function Test-IsWildcard([string]$p) { return ($p -like '<<WILDCARD:*>>') }
function Test-IsInvalidPath([string]$p) { return ($p -like '<<INVALID:*>>') }

# $child 是否位于 $parent 之内（含相等）。两者都必须是归一化全路径。
function Test-IsUnder([string]$child, [string]$parent) {
  if ([string]::IsNullOrEmpty($child) -or [string]::IsNullOrEmpty($parent)) { return $false }
  if ($child.Equals($parent, [StringComparison]::OrdinalIgnoreCase)) { return $true }
  return $child.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

# 交付/工作副本/镜像/仓库之间的重叠检查。
# 返回 problems 数组；为空表示通过。
function Test-ReleasePathSet {
  param(
    [Parameter(Mandatory = $true)][string]$RepoFull,
    [Parameter(Mandatory = $true)][string]$DeliveryFull,
    [Parameter(Mandatory = $true)][string]$WorkCopyFull,
    [string]$MirrorFull = ''
  )
  $problems = New-Object System.Collections.Generic.List[string]
  # 入参统一归一化：调用方可能传相对路径、含环境变量、含通配符或 8.3 短名。
  # 归一化放在函数内部，保证"无论谁来调，口径都一样"。
  $RepoFull = Get-FullPath $RepoFull
  $DeliveryFull = Get-FullPath $DeliveryFull
  $WorkCopyFull = Get-FullPath $WorkCopyFull
  if ($MirrorFull) { $MirrorFull = Get-FullPath $MirrorFull }
  $set = @(
    [pscustomobject]@{ N = '仓库'; V = $RepoFull },
    [pscustomobject]@{ N = '交付目录'; V = $DeliveryFull },
    [pscustomobject]@{ N = '工作副本'; V = $WorkCopyFull })
  if ($MirrorFull) { $set += [pscustomobject]@{ N = '镜像目录'; V = $MirrorFull } }

  foreach ($x in $set) {
    if (Test-IsWildcard $x.V) { $problems.Add($x.N + '含通配符（* 或 ?），本脚本不接受通配路径：' + $x.V) }
    elseif (Test-IsInvalidPath $x.V) { $problems.Add($x.N + '不是合法路径：' + $x.V) }
    elseif ($x.V -match '^[A-Za-z]:$') { $problems.Add($x.N + '不能是盘符根：' + $x.V) }
  }
  if ($problems.Count -gt 0) { return $problems.ToArray() }

  for ($i = 0; $i -lt $set.Count; $i++) {
    for ($j = 0; $j -lt $set.Count; $j++) {
      if ($i -eq $j) { continue }
      if (Test-IsUnder $set[$i].V $set[$j].V) {
        $problems.Add('路径重叠：' + $set[$i].N + '（' + $set[$i].V + '）位于 ' + $set[$j].N + '（' + $set[$j].V + '）之内。交付/工作副本/镜像会被递归清理，重叠会把一侧的内容删掉，必须分开。')
      }
    }
  }
  return $problems.ToArray()
}

# ── Portable 管理清单 ──────────────────────────────────────────────────────
# 清单落在 <交付根>\Portable.manifest.txt（Portable 的**同级**）：
# 既不会被 Portable 的递归清理删掉，也不会被误当成 Portable 的内容。
function Get-ManifestPath([string]$portableFull) { return ($portableFull + '.manifest.txt') }

# 枚举目录下全部条目，返回相对路径（正斜杠归一为反斜杠）。
# ★ 不能用「入参字符串长度」去 Substring：Get-ChildItem 返回的 FullName 是**长路径**，
#   而入参可能是 8.3 短名（如 C:\Users\PCMIG-~1\...）或大小写不同的写法，长度一差就把
#   相对路径切错（实测会把 Portable\PCMig.Core.dll 切成 table\PCMig.Core.dll）。
#   必须以 Get-Item 解析出来的真实 FullName 为基准。
function Get-RelativeRows([string]$rootFull) {
  $item = Get-Item -LiteralPath $rootFull -Force -ErrorAction SilentlyContinue
  if (-not $item) { return @() }
  $base = $item.FullName.TrimEnd('\')
  return @(Get-ChildItem -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName.Substring($base.Length).TrimStart('\') })
}

function Write-PortableManifest([string]$portableFull) {
  $manifest = Get-ManifestPath $portableFull
  $rows = @(Get-RelativeRows $portableFull | Sort-Object)
  [IO.File]::WriteAllLines($manifest, [string[]]$rows, (New-Object Text.UTF8Encoding($false)))
  return $rows.Count
}

# 无清单时的**引导白名单**：仅用于首次升级（旧版本目录还没有 manifest）。
# 顶层名字必须全部落在 PCMig 产物形态内，且必须存在强身份文件；否则拒绝清理。
function Test-PortableOwnershipBootstrap([string]$portableFull) {
  $strongMarkers = @('PCMig.WinUI.exe', 'pcmig-cli.exe')
  $top = @(Get-ChildItem -LiteralPath $portableFull -Force -ErrorAction SilentlyContinue)
  $names = @($top | ForEach-Object { $_.Name })
  $hit = @($strongMarkers | Where-Object { $names -contains $_ })
  if ($hit.Count -eq 0) {
    return [pscustomobject]@{ Ok = $false; Why = ('顶层找不到任何 PCMig 强身份文件（' + ($strongMarkers -join ' / ') + '）') }
  }
  $allowedExact = @('matrix', 'runtimes', '更新日志.txt', '使用说明.txt', 'licenses', 'LICENSE', 'NOTICE')
  $allowedPattern = @('PCMig.*', 'pcmig-*', '*.dll', '*.exe', '*.json', '*.pdb', '*.txt', '*.xml', '*.config', '*.pri', '*.winmd')
  $unknown = @()
  foreach ($n in $names) {
    if ($allowedExact -contains $n) { continue }
    $matched = $false
    foreach ($pat in $allowedPattern) { if ($n -like $pat) { $matched = $true; break } }
    if (-not $matched) { $unknown += $n }
  }
  if ($unknown.Count -gt 0) {
    return [pscustomobject]@{ Ok = $false; Why = ('顶层存在无法确认归属的条目：' + ($unknown -join ', ')) }
  }
  return [pscustomobject]@{ Ok = $true; Why = ('强身份=' + ($hit -join ',') + '；顶层 ' + $names.Count + ' 项全部符合产物形态') }
}

# Portable 递归删除的总前置判断。返回 Ok/Why/Detail，不删除任何东西。
# 调用方（release.ps1 的闸门 −4c 与构建期清理）都用它，保证判断口径唯一。
function Test-PortableCleanupAllowed {
  param(
    [Parameter(Mandatory = $true)][string]$DeliveryFull,
    [Parameter(Mandatory = $true)][string]$PortableFull
  )
  if ([string]::IsNullOrWhiteSpace($DeliveryFull)) {
    return [pscustomobject]@{ Ok = $false; Why = '交付目录为空，拒绝 Portable 递归清理（可能误删用户文件）'; Detail = '' }
  }
  if ($DeliveryFull -match '^[A-Za-z]:$') {
    return [pscustomobject]@{ Ok = $false; Why = ('交付目录是盘符根 ' + $DeliveryFull + '，拒绝 Portable 递归清理'); Detail = '' }
  }
  # 对**实际解析出来的路径**断言，而不是拿两个同源表达式互相比（v0.5.4 那条断言恒真）。
  # 注意：PS 5.1 的 Split-Path 不支持 -LiteralPath 与 -Parent/-Leaf 组合（报 AmbiguousParameterSet），
  # 而 -Path 会把 [ ] 当通配符。这里直接用字符串运算，天然按字面处理。
  $idx = $PortableFull.LastIndexOf('\')
  if ($idx -lt 0) {
    return [pscustomobject]@{ Ok = $false; Why = ('Portable 路径没有父目录，拒绝清理：' + $PortableFull); Detail = '' }
  }
  $parent = $PortableFull.Substring(0, $idx)
  $leaf = $PortableFull.Substring($idx + 1)
  if (-not $parent.Equals($DeliveryFull, [StringComparison]::OrdinalIgnoreCase)) {
    return [pscustomobject]@{ Ok = $false; Why = ('Portable 的父目录不等于交付根，拒绝清理：parent=' + $parent + ' delivery=' + $DeliveryFull); Detail = '' }
  }
  if ($leaf -ne 'Portable') {
    return [pscustomobject]@{ Ok = $false; Why = ('Portable 末级目录名不是 Portable，拒绝清理：' + $leaf); Detail = '' }
  }
  if (-not (Test-Path -LiteralPath $PortableFull)) {
    return [pscustomobject]@{ Ok = $true; Why = 'Portable 尚不存在，无需清理'; Detail = 'absent' }
  }
  $item = Get-Item -LiteralPath $PortableFull -Force
  if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    return [pscustomobject]@{ Ok = $false; Why = ('Portable 是重解析点 / 链接，拒绝递归删除：' + $PortableFull); Detail = '' }
  }
  if (-not $item.PSIsContainer) {
    return [pscustomobject]@{ Ok = $false; Why = ('Portable 是文件而不是目录，拒绝递归删除：' + $PortableFull); Detail = '' }
  }
  $entries = @(Get-ChildItem -LiteralPath $PortableFull -Force)
  if ($entries.Count -eq 0) {
    return [pscustomobject]@{ Ok = $true; Why = 'Portable 为空目录'; Detail = 'empty' }
  }
  $manifestPath = Get-ManifestPath $PortableFull
  if (Test-Path -LiteralPath $manifestPath) {
    $declared = @([IO.File]::ReadAllLines($manifestPath) | Where-Object { $_.Trim() -ne '' })
    $declaredSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($d in $declared) { [void]$declaredSet.Add($d.Trim()) }
    $actual = @(Get-RelativeRows $PortableFull)
    $unowned = @($actual | Where-Object { -not $declaredSet.Contains($_) })
    if ($unowned.Count -gt 0) {
      return [pscustomobject]@{
        Ok = $false
        Why = ('Portable 目录里存在清单外条目（' + $unowned.Count + ' 项），无法确认归属，拒绝递归删除并保留原状：' + [char]10 + '  - ' + (($unowned | Select-Object -First 10) -join ([char]10 + '  - ')))
        Detail = 'manifest-unowned'
      }
    }
    return [pscustomobject]@{ Ok = $true; Why = ('清单 ' + $declared.Count + ' 条，实际 ' + $actual.Count + ' 条，清单外 0 条'); Detail = 'manifest-ok' }
  }
  $own = Test-PortableOwnershipBootstrap $PortableFull
  if (-not $own.Ok) {
    return [pscustomobject]@{ Ok = $false; Why = ('Portable 无管理清单且无法确认归属，拒绝递归删除并保留原状：' + $own.Why); Detail = 'bootstrap-reject' }
  }
  return [pscustomobject]@{ Ok = $true; Why = ('无清单，引导白名单通过：' + $own.Why); Detail = 'bootstrap-ok' }
}
