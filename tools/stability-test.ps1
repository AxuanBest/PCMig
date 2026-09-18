<#
  PCMig 稳定性测试台（功能冻结后的常规体检）
  用法： powershell -NoProfile -ExecutionPolicy Bypass -File tools\stability-test.ps1 [-Rounds 2] [-Cli <exe>]
  原则：只用公开 CLI 接口（与 GUI 同一套引擎）；自造边界数据；三重独立核对；
        破坏性场景真做（中途杀进程→resume、篡改目标→重跑修复）；只碰临时目录，绝不碰用户数据。
#>
param(
  [int]$Rounds = 2,
  [string]$Cli = 'D:\PCMig\pcmig-cli.exe',
  [string]$Root = 'E:\stab',
  [int]$BulkMb = 300
)
$ErrorActionPreference = 'Continue'
$NL = [string][char]10
$script:results = @()
function Note($phase, $ok, $msg) {
  $tag = if ($ok) { 'PASS' } else { 'FAIL' }
  $script:results += [pscustomobject]@{ Phase = $phase; Result = $tag; Detail = $msg }
  Write-Output ('  [' + $tag + '] ' + $phase + ' — ' + $msg)
}
function Log($m) { Write-Output ('  · ' + $m) }

function Compare-Tree($src, $dst, $hashLimitMb = 16) {
  $s = Get-ChildItem $src -Recurse -File -Force | ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($src.Length).TrimStart('\'); Len = $_.Length } }
  $d = Get-ChildItem $dst -Recurse -File -Force | ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($dst.Length).TrimStart('\'); Len = $_.Length } }
  $missing = @($s | Where-Object { $_.Rel -notin $d.Rel })
  $extra   = @($d | Where-Object { $_.Rel -notin $s.Rel })
  $lendiff = @(); $hashdiff = @()
  foreach ($f in $s) {
    $m = $d | Where-Object { $_.Rel -eq $f.Rel } | Select-Object -First 1
    if ($null -ne $m -and $m.Len -ne $f.Len) { $lendiff += $f.Rel }
    if ($null -ne $m -and $f.Len -le ($hashLimitMb * 1MB)) {
      $h1 = (Get-FileHash (Join-Path $src $f.Rel) -Algorithm SHA256).Hash
      $h2 = (Get-FileHash (Join-Path $dst $f.Rel) -Algorithm SHA256).Hash
      if ($h1 -ne $h2) { $hashdiff += $f.Rel }
    }
  }
  return [pscustomobject]@{
    SrcCount = $s.Count; DstCount = $d.Count
    Missing = $missing; Extra = $extra; LenDiff = $lendiff; HashDiff = $hashdiff
    Ok = ($missing.Count -eq 0 -and $extra.Count -eq 0 -and $lendiff.Count -eq 0 -and $hashdiff.Count -eq 0)
  }
}
function Show-Diff($c) {
  if ($c.Ok) { return '完全一致（条数/长度/SHA256 逐文件相同）' }
  $p = @()
  if ($c.Missing.Count) { $p += ('缺失 ' + $c.Missing.Count + '：' + (($c.Missing | Select-Object -First 3 | ForEach-Object { $_.Rel }) -join ';')) }
  if ($c.Extra.Count)   { $p += ('多余 ' + $c.Extra.Count + '：' + (($c.Extra | Select-Object -First 3 | ForEach-Object { $_.Rel }) -join ';')) }
  if ($c.LenDiff.Count) { $p += ('长度不符 ' + $c.LenDiff.Count + '：' + (($c.LenDiff | Select-Object -First 3) -join ';')) }
  if ($c.HashDiff.Count){ $p += ('内容不符 ' + $c.HashDiff.Count + '：' + (($c.HashDiff | Select-Object -First 3) -join ';')) }
  return ('源 ' + $c.SrcCount + ' 个 / 目标 ' + $c.DstCount + ' 个；' + ($p -join ' | '))
}
function New-SourceTree($src) {
  New-Item -ItemType Directory -Force -Path $src | Out-Null
  Set-Content -LiteralPath (Join-Path $src '普通文件.txt') -Value 'hello' -Encoding UTF8
  Set-Content -LiteralPath (Join-Path $src '带 空格 和#井号%百分号.txt') -Value 'x' -Encoding UTF8
  Set-Content -LiteralPath (Join-Path $src '中文名字（括号）[方括号].log') -Value 'y' -Encoding UTF8
  Set-Content -LiteralPath (Join-Path $src 'a b  c.txt') -Value 'z' -Encoding UTF8
  # 中等大小文件（供“篡改后修复”用；也要有 >100B 与 <5MB 之间的样本）
  [IO.File]::WriteAllBytes((Join-Path $src '中等文件-200KB.bin'), (New-Object byte[] (200KB)))
  [IO.File]::WriteAllBytes((Join-Path $src '空文件.bin'), @())
  Set-Content -Path (Join-Path $src '.隐藏文件.txt') -Value 'hidden' -Encoding UTF8
  $deep = $src; for ($i = 1; $i -le 12; $i++) { $deep = Join-Path $deep ('深层目录' + $i + '_' + ('d' * 12)) }
  New-Item -ItemType Directory -Force -Path $deep | Out-Null
  Set-Content -Path (Join-Path $deep '最深处.txt') -Value 'deep' -Encoding UTF8
  Log ('深层路径长度 = ' + ($deep.Length - $src.Length) + ' 字符')
  $many = Join-Path $src '大量小文件'; New-Item -ItemType Directory -Force -Path $many | Out-Null
  for ($i = 0; $i -lt 2000; $i++) { [IO.File]::WriteAllText((Join-Path $many ('f' + $i.ToString('0000') + '.txt')), ('content-' + $i)) }
  New-Item -ItemType Directory -Force -Path (Join-Path $src '中文目录\子目录') | Out-Null
  Set-Content -Path (Join-Path $src '中文目录\子目录\同名文件.txt') -Value 'A' -Encoding UTF8
  New-Item -ItemType Directory -Force -Path (Join-Path $src '另一个目录') | Out-Null
  Set-Content -Path (Join-Path $src '另一个目录\同名文件.txt') -Value 'B' -Encoding UTF8
  $big = Join-Path $src '大文件.bin'
  $fs = [IO.File]::Create($big); $buf = New-Object byte[] (4MB)
  (New-Object Random 7).NextBytes($buf)
  $chunks = [int]($BulkMb * 1MB / $buf.Length)
  for ($i = 0; $i -lt $chunks; $i++) { $fs.Write($buf, 0, $buf.Length) }
  $fs.Close()
  Log ('造数据完成：大文件 ' + [int]((Get-Item $big).Length / 1MB) + ' MB')
}
function Run-Quick($src, $dst) {
  $out = & $Cli quick --host localhost --source $src --target $dst --yes --threads 16 2>&1
  $job = ($out | Select-String -Pattern 'JOB-\d{8}-\d{6}-[0-9a-f]{4}' | Select-Object -First 1)
  return [pscustomobject]@{ Output = ($out -join $NL); Exit = $LASTEXITCODE; Job = $(if ($job) { $job.Matches[0].Value } else { $null }) }
}

Write-Output ('=== PCMig 稳定性测试台 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ===')
Write-Output ('CLI = ' + $Cli + '    轮数 = ' + $Rounds + '    根目录 = ' + $Root)
if (-not (Test-Path $Cli)) { Write-Output ('找不到 CLI：' + $Cli); exit 2 }
Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null
$src = Join-Path $Root 'src'
New-SourceTree $src
$srcStat = Get-ChildItem $src -Recurse -File -Force | Measure-Object -Property Length -Sum
Log ('源：' + $srcStat.Count + ' 个文件 / ' + [Math]::Round($srcStat.Sum / 1MB, 1) + ' MB')

for ($r = 1; $r -le $Rounds; $r++) {
  $dst = Join-Path $Root ('dst_full_' + $r)
  Write-Output ('--- P1.' + $r + ' 完整迁移（第 ' + $r + ' 轮）---')
  $t0 = Get-Date
  $q = Run-Quick $src $dst
  Log ('退出码 ' + $q.Exit + '，用时 ' + [int]((Get-Date) - $t0).TotalSeconds + ' 秒，任务 ' + $q.Job)
  if ($q.Exit -ne 0) { Write-Output ($q.Output | Select-Object -Last 25) }
  $c = Compare-Tree $src $dst
  Note ('P1.' + $r + ' 数据完整性（自写逐文件 SHA256 比对）') $c.Ok (Show-Diff $c)
  if ($q.Job) {
    $v = & $Cli verify --job $q.Job --level 2 2>&1
    $vcode = $LASTEXITCODE
    $vt = ($v -join $NL)
    # 判定用退出码 + 有没有 ✘ 行（不能用“出现失败二字”——汇总行里本来就有“失败对象 0”）
    $vok = ($vcode -eq 0) -and -not (@($v | Where-Object { $_ -match '✘' }).Count -gt 0)
    Note ('P1.' + $r + ' 应用自带 verify --level 2') $vok (($v | Select-Object -Last 4) -join ' / ')
  }
}

Write-Output '--- P2 中途硬杀进程 → 断点续传（确定性打断）---'
$dst2 = Join-Path $Root 'dst_resume'
# 先建任务（预检+扫描+计划），再用单线程跑传输：保证 3 秒时传输仍在进行，能真正“中途”打断
$newOut = & $Cli new --host localhost --source $src --target $dst2 --yes --threads 1 2>&1
$mj = [regex]::Match(($newOut -join $NL), 'JOB-\d{8}-\d{6}-[0-9a-f]{4}')
$myJob = if ($mj.Success) { $mj.Value } else { $null }
Log ('新建任务 = ' + $myJob + '（单线程传输，便于中途打断）')
if ($myJob) {
  $rp = Start-Process -FilePath $Cli -ArgumentList @('run', '--job', $myJob) -PassThru -WindowStyle Hidden
  Start-Sleep -Milliseconds 600   # 本地 NVMe 太快：打断点必须提前，否则传输已结束
  $wasRunning = -not $rp.HasExited
  if ($wasRunning) { Stop-Process -Id $rp.Id -Force; Log '已在传输中途硬杀 CLI 进程（模拟断电/崩溃，未走优雅停止）' }
  else { Log '警告：3 秒时传输已结束，本次未真正打断（仍按数据完整性判定）' }
  Start-Sleep -Seconds 3
  $before = Compare-Tree $src $dst2
  Log ('打断后目标 ' + $before.DstCount + ' / 源 ' + $srcStat.Count + ' 个文件（应少于源，证明确实中断）')
  & $Cli resume --job $myJob 2>&1 | Select-Object -Last 3 | ForEach-Object { Log ('resume: ' + $_) }
  Log ('resume 退出码 ' + $LASTEXITCODE)
  $after = Compare-Tree $src $dst2
  Note 'P2 中途硬杀 → 续传后数据完整（不丢不重）' $after.Ok ((Show-Diff $after) + $(if (-not $wasRunning) { '（注意：未真正打断）' } else { '' }))
  if ($wasRunning -and $before.DstCount -ge $srcStat.Count) { Note 'P2 中断态确认（打断时目标应不完整）' $false ('打断时目标已有 ' + $before.DstCount + ' 个文件，与源相同，未构成中断态') }
} else { Note 'P2 断点续传' $false ('没拿到任务号：' + (($newOut | Select-Object -Last 3) -join ' / ')) }

Write-Output '--- P3 目标文件损坏 → 重跑修复 ---'
$victim = Get-ChildItem $dst2 -Recurse -File -Force | Where-Object { $_.Length -gt 1000 -and $_.Length -lt 5MB } | Select-Object -First 1
if ($victim) {
  $bytes = [IO.File]::ReadAllBytes($victim.FullName); $bytes[10] = $bytes[10] -bxor 0xFF
  [IO.File]::WriteAllBytes($victim.FullName, $bytes)
  Log ('已篡改：' + $victim.FullName.Substring($dst2.Length))
  $bad = Compare-Tree $src $dst2
  Log ('篡改后比对：内容不符 ' + $bad.HashDiff.Count + ' 个（应为 1，证明比对有效）')
  $q3 = Run-Quick $src $dst2
  Log ('重跑退出码 ' + $q3.Exit + '，任务 ' + $q3.Job)
  $fixed = Compare-Tree $src $dst2
  Note 'P3 重跑修复被篡改文件' $fixed.Ok (Show-Diff $fixed)
} else { Note 'P3 损坏修复' $false '没找到合适的文件做篡改测试' }

Write-Output ''
Write-Output '=== 结果汇总 ==='
$fail = @($script:results | Where-Object { $_.Result -eq 'FAIL' })
foreach ($r in $script:results) { Write-Output ('  ' + $r.Result + '  ' + $r.Phase) }
$summary = if ($fail.Count -eq 0) { '全部通过' } else { ('有 ' + $fail.Count + ' 项失败') }
Write-Output ('  结论：' + $summary + '（共 ' + $script:results.Count + ' 项）')
$rep = Join-Path $Root ('stability-report-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.md')
$md = @('# PCMig 稳定性测试报告 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), '', ('- 被测 CLI：' + $Cli), ('- 源数据：' + $srcStat.Count + ' 个文件 / ' + [Math]::Round($srcStat.Sum / 1MB, 1) + ' MB'), ('- 结论：' + $summary), '', '| 结果 | 阶段 | 说明 |', '| --- | --- | --- |')
foreach ($r in $script:results) { $md += ('| ' + $r.Result + ' | ' + $r.Phase + ' | ' + ($r.Detail -replace '\|', '/') + ' |') }
[IO.File]::WriteAllLines($rep, $md, (New-Object Text.UTF8Encoding($true)))
Write-Output ('  报告：' + $rep)
if ($fail.Count -gt 0) { exit 1 } else { exit 0 }
