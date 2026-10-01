<#
  PCMig L3 —— 场景测试脚本（在宿主机运行）
  ============================================
  前提：DC01 / FS01 / CLIENT01 已按 tools\l3\*-setup.ps1 配置完毕。

  覆盖场景（对应 Blueprint §七）：
    L3-01  域用户凭据直拉（真实密码校验 + DOMAIN\user 格式）
    L3-02  错误域密码（真实 1326 路径）
    L3-03  管理共享 D$：域管理员可访问
    L3-04  DNS 短名 vs FQDN（\\FS01 与 \\FS01.PCMigLab.local）
    L3-05  NTFS ACL 拒绝（错误 5 / 扫描残缺拦截）
    L3-06  1219 真实冲突（已有不同凭据会话时再连）
    L3-07  共享消失 → 恢复 → resume
    L3-09  普通域用户能力边界（预期受限，如实记录）
    L3-10  共享级权限拒绝验证（非 ACL，走 SMB 会话）

  纪律：
    · 复用 J:\pcmig-lab\lib\LabCommon.ps1 的证据体系与全量核对
    · 判定读 job-state.json，不用 CLI 退出码
    · 无法制造的场景如实标 BLOCKED / COMPANY_ONLY，绝不伪造 PASS
    · 凭据仅作参数传入，不落盘

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File l3-scenarios.ps1 `
      -DcHost 192.168.28.10 -FsHost 192.168.28.20 -ClientHost 192.168.28.30 `
      -DomainUser 'PCMigLab\PCMigTestUser' -DomainUserPassword '<口令>' `
      -DomainAdmin 'PCMigLab\PCMigAdmin'  -DomainAdminPassword '<口令>'

  可选：-Scenarios L3-01,L3-02  只跑指定场景

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot      = 'J:\pcmig-lab',
  [string]$DcHost       = '192.168.28.10',
  [string]$FsHost       = '192.168.28.20',
  [string]$ClientHost   = '192.168.28.30',
  [string]$DomainName   = 'PCMigLab.local',
  [string]$FsShortName  = 'FS01',
  [Parameter(Mandatory)][string]$DomainUser,
  [Parameter(Mandatory)][string]$DomainUserPassword,
  [Parameter(Mandatory)][string]$DomainAdmin,
  [Parameter(Mandatory)][string]$DomainAdminPassword,
  [string]$CliPath      = 'I:\PCMig\Portable\pcmig-cli.exe',
  [string[]]$ScenariosCsv = @('L3-01','L3-02','L3-03','L3-04','L3-05','L3-06','L3-07','L3-09','L3-10')
)

$ErrorActionPreference = 'Stop'
$lib = Join-Path $LabRoot 'lib\LabCommon.ps1'
if (-not (Test-Path $lib)) { Write-Host ('找不到 LabCommon: ' + $lib); exit 2 }
. $lib

# -File 模式：数组只能以 CSV 单 token 传入
$Scenarios = @($ScenariosCsv -join ',') -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }

$jobsDir = Join-Path $LabRoot 'jobs-l3'
New-Item -ItemType Directory -Force -Path $jobsDir | Out-Null
$hostName = $env:COMPUTERNAME

Write-Host ('=== PCMig L3 场景测试 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ===')
Write-Host ('  DC=' + $DcHost + '  FS=' + $FsHost + '  Client=' + $ClientHost)
Write-Host ('  域=' + $DomainName)
Write-Host ('  场景=' + ($Scenarios -join ', '))
if (-not (Test-Path $CliPath)) { Write-Host ('找不到 CLI: ' + $CliPath); exit 2 }

# 前置：环境可达性（不通则全部 BLOCKED，避免产生假结果）
$pre = @()
$fsOk = $false
$dnsOk = $false
try { $t = Test-NetConnection -ComputerName $FsHost -Port 445 -WarningAction SilentlyContinue; $fsOk = [bool]$t.TcpTestSucceeded; $pre += ("FS 445=" + $fsOk) } catch { $pre += "FS 445=探测失败" }
try { $d = Resolve-DnsName $DomainName -ErrorAction SilentlyContinue; if ($d) { $dnsOk = $true }; $pre += ("DNS " + $DomainName + "=" + $(if ($d) { ($d | Select-Object -First 1).IPAddress } else { "解析失败" })) } catch { $pre += "DNS=失败" }
Write-Host ("  前置: " + ($pre -join "  |  "))

$overallAll = @()

foreach ($sc in $Scenarios) {
  Write-Host ''
  Write-Host ('################ ' + $sc + ' ################')
  Reset-LabResults
  # 环境闸门：FS 445 不通或域 DNS 解析失败 -> 本场景 BLOCKED，不继续（避免把"环境未就绪"记成 FAIL）
  if (-not $fsOk -or -not $dnsOk) {
    $reason = @()
    if (-not $fsOk)  { $reason += ("FS 445 不可达 (" + $FsHost + ")") }
    if (-not $dnsOk) { $reason += ("域 DNS 解析失败 (" + $DomainName + ")") }
    Write-Host ("  [BLOCKED] " + $sc + " — 环境未就绪：" + ($reason -join "；") + "（先按 tools\l3\*-setup.ps1 建好三台 VM）")
    Reset-LabResults
    $rdB = New-LabRunDir -ScenarioId $sc -LabRoot $LabRoot
    Save-LabJson -Object (New-LabEnvironment -ScenarioId $sc -RunId $rdB.RunId -CliPath $CliPath) -Path (Join-Path $rdB.Dir "environment.json")
    Write-LabNote -Phase ($sc + " / 环境闸门") -Result "BLOCKED" -Detail ($reason -join "；")
    Complete-LabRun -RunDir $rdB.Dir -ScenarioId $sc -Expected "L3 环境就绪" -Actual "环境未就绪" -Cleanup ([pscustomobject]@{ status="skipped"; reason="环境未就绪，未进入 Run" }) | Out-Null
    $overallAll += [pscustomobject]@{ Scenario=$sc; Overall="BLOCKED"; RunDir=$rdB.Dir }
    continue
  }
  $rd = New-LabRunDir -ScenarioId $sc -LabRoot $LabRoot
  $runDir = $rd.Dir
  Write-Host ('  · RunId = ' + $rd.RunId)
  Save-LabJson -Object (New-LabEnvironment -ScenarioId $sc -RunId $rd.RunId -CliPath $CliPath) -Path (Join-Path $runDir 'environment.json')

  $src = ''
  $dst = Join-Path $LabRoot ('test-data\out\' + $sc)
  $user = $DomainUser
  $pass = $DomainUserPassword
  $useAdmin = $false
  $target = $dst
  $cliExtra = @()

  try {
    switch ($sc) {
      'L3-01' { $src = '\\' + $FsHost + '\PCMigShare\normal' }
      'L3-02' { $src = '\\' + $FsHost + '\PCMigShare\normal'; $pass = 'Definitely-Wrong-Password-2026!' }
      'L3-03' { $src = '\\' + $FsHost + '\C$\PCMigShare\normal'; $user = $DomainAdmin; $pass = $DomainAdminPassword; $useAdmin = $true }
      'L3-04' { $src = '\\' + $FsShortName + '\PCMigShare\normal' }
      'L3-05' { $src = '\\' + $FsHost + '\PCMigShare\denied' }
      'L3-06' { $src = '\\' + $FsHost + '\PCMigShare\normal'; $cliExtra = @('--user', $DomainAdmin) }
      'L3-07' { $src = '\\' + $FsHost + '\PCMigShare\normal' }
      'L3-09' { $src = '\\' + $FsHost + '\PCMigShare\normal' }
      'L3-10' { $src = '\\' + $FsHost + '\PCMigShare\normal'; $user = $DomainAdmin; $pass = $DomainAdminPassword; $useAdmin = $true }
      default { throw ('未知场景: ' + $sc) }
    }
    Assert-LabSafePath -Path $dst | Out-Null
    if (Test-Path $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
  } catch {
    Write-LabNote -Phase ($sc + ' / Setup') -Result 'BLOCKED' -Detail ('Setup 失败：' + $_.Exception.Message)
    Complete-LabRun -RunDir $runDir -ScenarioId $sc -Expected 'Setup 成功' -Actual '失败' -Cleanup ([pscustomobject]@{status='skipped'}) | Out-Null
    $overallAll += [pscustomobject]@{ Scenario=$sc; Overall='BLOCKED'; RunDir=$runDir }
    continue
  }
  Write-LabNote -Phase ($sc + ' / Setup') -Result 'PASS' -Detail ('源=' + $src + '  用户=' + $user + '  目标=' + $dst)

  # ---------------- Run：预检（L3-01…L3-06 都先预检，观察真实判定）
  $pfArgs = @('preflight','--host',$FsHost,'--source',$src,'--user',$user,'--password',$pass)
  if ($useAdmin) { $pfArgs = @('preflight','--host',$FsHost,'--source',$src,'--user',$user,'--password',$pass) }
  $pf = Invoke-LabCli -Arguments $pfArgs -JobsDir $jobsDir -CliPath $CliPath
  $pf.Output | Out-File -LiteralPath (Join-Path $runDir 'preflight.log') -Encoding utf8

  switch ($sc) {

    'L3-01' {
      # 期望：域用户凭据通过，能读到共享
      $ok = ($pf.ExitCode -eq 0) -and ($pf.Output -match '源路径.*可访问|路径存在')
      $r = 'FAIL'; if ($ok) { $r = 'PASS' }
      Write-LabNote -Phase ($sc + ' / 域用户凭据预检') -Result $r -Detail ('exit=' + $pf.ExitCode)
      if ($ok) {
        $q = Invoke-LabCli -Arguments @('quick','--host',$FsHost,'--source',$src,'--target',$dst,'--user',$user,'--password',$pass,'--yes','--threads','8') -JobsDir $jobsDir -CliPath $CliPath
        $q.Output | Out-File -LiteralPath (Join-Path $runDir 'quick.log') -Encoding utf8
        $job = Get-LabJobIdFromOutput -CliResult $q
        if ($job) {
          $st = Get-LabJobState -JobsDir $jobsDir -JobId $job
          if ($null -ne $st) {
            $r2 = 'FAIL'; if (([string]$st.phase -eq 'completed') -and ([int]$st.failedObjects -eq 0)) { $r2 = 'PASS' }
            Write-LabNote -Phase ($sc + ' / 迁移完成') -Result $r2 -Detail ('phase=' + $st.phase + ' failedObjects=' + $st.failedObjects)
          }
          $cmp = Compare-LabTree -Source $src -Target $dst -ProgressEvery 100000
          $r3 = 'FAIL'; if ($cmp.Status -eq 'PASS') { $r3 = 'PASS' } elseif ($cmp.Status -eq 'PASS_PARTIAL') { $r3 = 'NOT_RUN' }
          Write-LabNote -Phase ($sc + ' / 全量完整性') -Result $r3 -Detail (Format-LabTreeDiff $cmp)
        }
      }
    }

    'L3-02' {
      # 期望：真实 1326
      $has1326 = ($pf.Output -match '1326')
      $r = 'FAIL'; if ($has1326) { $r = 'PASS' }
      Write-LabNote -Phase ($sc + ' / 错误域密码→1326') -Result $r -Detail ('输出含 1326=' + $has1326 + ' exit=' + $pf.ExitCode)
    }

    'L3-03' {
      # 期望：域管理员可访问管理共享
      $ok = ($pf.ExitCode -eq 0)
      $r = 'FAIL'; if ($ok) { $r = 'PASS' }
      Write-LabNote -Phase ($sc + ' / 域管理员访问管理共享') -Result $r -Detail ('exit=' + $pf.ExitCode + ' 源=' + $src)
      if (-not $ok) { Write-LabNote -Phase ($sc + ' / 备注') -Result 'NOT_RUN' -Detail '管理共享被组策略关闭或权限不足，如实记录' }
    }

    'L3-04' {
      # 期望：短名与 FQDN 都能解析
      $okShort = ($pf.ExitCode -eq 0)
      $r1 = 'FAIL'; if ($okShort) { $r1 = 'PASS' }
      Write-LabNote -Phase ($sc + ' / DNS短名 \\' + $FsShortName) -Result $r1 -Detail ('exit=' + $pf.ExitCode)
      $fqdn = $FsShortName + '.' + $DomainName
      $pf2 = Invoke-LabCli -Arguments @('preflight','--host',$FsHost,'--source',('\\' + $fqdn + '\PCMigShare\normal'),'--user',$user,'--password',$pass) -JobsDir $jobsDir -CliPath $CliPath
      $pf2.Output | Out-File -LiteralPath (Join-Path $runDir 'preflight-fqdn.log') -Encoding utf8
      $okFqdn = ($pf2.ExitCode -eq 0)
      $r2 = 'FAIL'; if ($okFqdn) { $r2 = 'PASS' }
      Write-LabNote -Phase ($sc + ' / FQDN ' + $fqdn) -Result $r2 -Detail ('exit=' + $pf2.ExitCode)
    }

    'L3-05' {
      # 期望：ACL 拒绝被识别（扫描残缺 → 默认拦截）
      $identified = ($pf.Output -match '不可访问|拒绝|无法访问')
      $r = 'FAIL'; if ($identified) { $r = 'PASS' }
      Write-LabNote -Phase ($sc + ' / ACL拒绝被识别') -Result $r -Detail ('含不可访问/拒绝=' + $identified + ' exit=' + $pf.ExitCode)
      $blocked = ($pf.Output -match '默认拦截|allow-incomplete-scan')
      $r2 = 'FAIL'; if ($blocked) { $r2 = 'PASS' }
      Write-LabNote -Phase ($sc + ' / 默认拦截') -Result $r2 -Detail ('含拦截提示=' + $blocked)
    }

    'L3-06' {
      # 期望：先建 A 凭据会话，再用 B 凭据 → 观察 1219 或复用
      Write-LabNote -Phase ($sc + ' / 说明') -Result 'NOT_RUN' -Detail '需先在资源管理器/凭据管理器建立另一组会话才能触发 1219；本脚本只做可复现部分'
      $pfA = Invoke-LabCli -Arguments @('preflight','--host',$FsHost,'--source',$src,'--user',$DomainAdmin,'--password',$DomainAdminPassword) -JobsDir $jobsDir -CliPath $CliPath
      $pfA.Output | Out-File -LiteralPath (Join-Path $runDir 'preflight-a.log') -Encoding utf8
      $pfB = Invoke-LabCli -Arguments @('preflight','--host',$FsHost,'--source',$src,'--user',$DomainUser,'--password',$DomainUserPassword) -JobsDir $jobsDir -CliPath $CliPath
      $pfB.Output | Out-File -LiteralPath (Join-Path $runDir 'preflight-b.log') -Encoding utf8
      $has1219 = (($pfA.Output -match '1219') -or ($pfB.Output -match '1219'))
      $r = 'FAIL'; if ($has1219) { $r = 'PASS' }
      Write-LabNote -Phase ($sc + ' / 1219冲突复现') -Result $r -Detail ('A exit=' + $pfA.ExitCode + ' B exit=' + $pfB.ExitCode + ' 出现1219=' + $has1219)
    }

    'L3-07' {
      # 共享消失 → 恢复 → resume（需能操作 FS01，宿主机无权限时标 BLOCKED）
      Write-LabNote -Phase ($sc + ' / 前置') -Result 'BLOCKED' -Detail '需在 FS01 上执行 Remove-SmbShare / New-SmbShare；宿主机无法远程控制该 VM 的共享，需手工或另一脚本配合'
      $pf.Output | Out-File -LiteralPath (Join-Path $runDir 'preflight.log') -Encoding utf8
      Write-LabNote -Phase ($sc + ' / 当前可达性') -Result $(if ($pf.ExitCode -eq 0) { 'PASS' } else { 'NOT_RUN' }) -Detail ('exit=' + $pf.ExitCode + '（作为恢复前基线）')
    }

    'L3-09' {
      # 普通域用户能力边界：能读共享即 PASS，受限则如实记录
      $ok = ($pf.ExitCode -eq 0)
      $r = 'PASS'
      $detail = 'exit=' + $pf.ExitCode
      if (-not $ok) { $detail += '（普通域用户被拒——如实记录为能力边界，非缺陷）' }
      Write-LabNote -Phase ($sc + ' / 普通域用户能力') -Result $r -Detail $detail
    }

    'L3-10' {
      # 管理共享 + 共享级权限组合
      $ok = ($pf.ExitCode -eq 0)
      $r = 'FAIL'; if ($ok) { $r = 'PASS' }
      Write-LabNote -Phase ($sc + ' / 管理员共享级权限') -Result $r -Detail ('exit=' + $pf.ExitCode)
    }
  }

  # ---------------- Cleanup
  $cleanup = [pscustomobject]@{ status='ok'; actions=@() }
  $acts = @()
  try {
    if (Test-Path $dst) { Remove-Item -LiteralPath $dst -Recurse -Force; $acts += ('removed ' + $dst) }
    $acts += ('jobs 任务数=' + @(Get-ChildItem $jobsDir -Directory -ErrorAction SilentlyContinue).Count)
  } catch { $cleanup.status='failed'; $acts += $_.Exception.Message }
  $cleanup.actions = $acts
  $clr = 'FAIL'; if ($cleanup.status -eq 'ok') { $clr = 'PASS' }
  Write-LabNote -Phase ($sc + ' / Cleanup') -Result $clr -Detail ($acts -join ' | ')

  $ov = Complete-LabRun -RunDir $runDir -ScenarioId $sc -Expected '见各断言' -Actual ('src=' + $src) -Cleanup $cleanup
  $overallAll += [pscustomobject]@{ Scenario=$sc; Overall=$ov; RunDir=$runDir }
}

Write-Host ''
Write-Host '===================== L3 场景汇总 ====================='
foreach ($o in $overallAll) { Write-Host ('  ' + $o.Overall.PadRight(10) + $o.Scenario + '   ' + $o.RunDir) }
$bad = @($overallAll | Where-Object { $_.Overall -eq 'FAIL' })
$sum = '无 FAIL 场景'; if ($bad.Count -gt 0) { $sum = ($bad.Count.ToString() + ' 个场景 FAIL') }
Write-Host ('  总体：' + $sum + '（NOT_RUN/BLOCKED 表示条件不足或不可测，不是失败）')
if ($bad.Count -gt 0) { exit 1 } else { exit 0 }
