<#
  PCMig L3 —— 编排与验证（宿主机运行，需管理员）
  ==================================================
  解决一个必然出现的顺序问题：
    FS01 / CLIENT01 的首次开机跑在 DC01 域就绪【之前】，
    所以它们那两次"加域"必然失败。本脚本负责在 DC01 就绪后让它们重试。

  怎么做到"不用人进 VM"：
    用 **PowerShell Direct**（Hyper-V 从宿主机直接在 VM 内执行命令，不走网络、不需要 VM 内开远程）。

  它做的事（全程自动）：
    1. 等三台 VM 可被 PowerShell Direct 访问（轮询，带超时）
    2. 等 DC01 的 AD DS / DNS 服务就绪、域 PCMigLab.local 可查询
    3. 在 FS01 / CLIENT01 内重跑 C:\PCMigL3\run-setup.cmd（重试加域与配置）
    4. 采集三台的关键状态（计算机名/域/IP/服务/共享）写到证据文件
    5. 汇总

  用法（双击 l3-orchestrate.cmd）：
    powershell -NoProfile -ExecutionPolicy Bypass -File l3-orchestrate.ps1 -Execute
    干跑：不加 -Execute

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot  = 'G:\PCMigLab',
  [string]$DomainName = 'PCMigLab.local',
  [string]$AdminUser = 'Administrator',
  [string]$AdminPassword = '',
  [int]$WaitReadySec = 900,
  [int]$WaitDomainSec = 600,
  [switch]$Execute
)

$ErrorActionPreference = 'Continue'
$script:log = @()
$script:guardedValues = @()
function Say([string]$t){
  if ($null -eq $t) { $t = '' }
  foreach ($g in $script:guardedValues) {
    if (-not [string]::IsNullOrEmpty($g) -and $t.Contains($g)) {
      throw ('内部错误：拒绝输出包含已解析口令的内容（已阻止泄密）。')
    }
  }
  Write-Host $t; $script:log += $t
}
function Head([string]$t){ Say ''; Say ('===== ' + $t + ' =====') }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Head 'PCMig L3 编排与验证'
Say ('  域      : ' + $DomainName)
Say ('  账号    : ' + $AdminUser)
Say ('  等待上限: 就绪 ' + $WaitReadySec + 's / 域 ' + $WaitDomainSec + 's')
Say ('  Execute : ' + [bool]$Execute)
Say ('  管理员  : ' + $isAdmin)

if (-not $Execute) {
  Say ''
  Say '  干跑结束。实际执行请加 -Execute（需管理员）。'
  Say '  它将：等 VM 就绪 → 等 DC01 域就绪 → 让 FS01/CLIENT01 重试加域 → 采集状态。'
  exit 0
}
if (-not $isAdmin) { Say ''; Say '✘ 需要管理员（PowerShell Direct 需要 Hyper-V 管理权限）。请双击 l3-orchestrate.cmd。'; exit 5 }

# ---------------------------------------------------------------- 口令来源（安全加固）
# 管理员口令只从环境变量 PCMIGLAB_ADMIN_PASSWORD 读取；绝不内置默认值、绝不回显、绝不落盘。
$AdminPassword = $env:PCMIGLAB_ADMIN_PASSWORD
if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
  Say ''
  Say '✘ 未提供实验室管理员口令：请先设置环境变量 PCMIGLAB_ADMIN_PASSWORD（不落盘、不进命令行）后重试。本次未做任何修改，已中止。'
  exit 6
}
$AdminPassword = $AdminPassword.Trim()
$script:guardedValues += $AdminPassword

$cred = New-Object System.Management.Automation.PSCredential(
  $AdminUser, (ConvertTo-SecureString $AdminPassword -AsPlainText -Force))

# ---------------------------------------------------------------- 1. 等待 VM 可被 PowerShell Direct 访问
Head '1. 等待 VM 就绪（PowerShell Direct 可访问）'
$targets = @('PCMigLab-DC01','PCMigLab-FS01','PCMigLab-CLIENT01')
$ready = @{}
foreach ($t in $targets) { $ready[$t] = $false }

$deadline = (Get-Date).AddSeconds($WaitReadySec)
while ((Get-Date) -lt $deadline) {
  $allReady = $true
  foreach ($t in $targets) {
    if ($ready[$t]) { continue }
    $allReady = $false
    try {
      $r = Invoke-Command -VMName $t -Credential $cred -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop
      if ($r) { $ready[$t] = $true; Say ('  ✔ ' + $t + ' 就绪（VM 内主机名: ' + $r + '）') }
    } catch { }
  }
  if ($allReady) { break }
  $left = [int]($deadline - (Get-Date)).TotalSeconds
  if ($left % 30 -lt 5) { Say ('  · 仍在等待… 剩余 ' + $left + 's  （已就绪: ' + (@($ready.Keys | Where-Object { $ready[$_] }).Count) + '/3）') }
  Start-Sleep -Seconds 5
}

$notReady = @($targets | Where-Object { -not $ready[$_] })
if ($notReady.Count -gt 0) {
  Say ('  ⚠ 以下 VM 在超时内未就绪: ' + ($notReady -join ', '))
  Say '    （可能仍在 oobe/specialize 阶段。可稍后重跑本脚本。）'
}

# ---------------------------------------------------------------- 2. 等 DC01 域就绪
Head '2. 等待 DC01 域就绪'
$domainOk = $false
if ($ready['PCMigLab-DC01']) {
  $ddl = (Get-Date).AddSeconds($WaitDomainSec)
  while ((Get-Date) -lt $ddl) {
    try {
      $r = Invoke-Command -VMName 'PCMigLab-DC01' -Credential $cred -ScriptBlock {
        $out = [pscustomobject]@{ Domain=$null; NTDS=$null; DNS=$null; Users=0 }
        try { $d = Get-ADDomain -ErrorAction Stop; $out.Domain = $d.DNSRoot } catch { }
        try { $s = Get-Service NTDS -ErrorAction Stop; $out.NTDS = [string]$s.Status } catch { }
        try { $s2 = Get-Service DNS -ErrorAction Stop; $out.DNS = [string]$s2.Status } catch { }
        try { $out.Users = @(Get-ADUser -Filter "SamAccountName -like 'PCMig*'" -ErrorAction SilentlyContinue).Count } catch { }
        return $out
      } -ErrorAction Stop
      if ($r.Domain) {
        $domainOk = $true
        Say ('  ✔ 域已就绪: ' + $r.Domain + '  NTDS=' + $r.NTDS + '  DNS=' + $r.DNS + '  PCMig* 用户数=' + $r.Users)
        break
      } else { Say ('  · 域尚未就绪（NTDS=' + $r.NTDS + ' DNS=' + $r.DNS + '），继续等…') }
    } catch { Say ('  · 查询失败（可能 AD 正在初始化）: ' + $_.Exception.Message.Split([char]10)[0]) }
    Start-Sleep -Seconds 15
  }
  if (-not $domainOk) { Say '  ⚠ 域在超时内未就绪。FS01/CLIENT01 的加域可能仍会失败。' }
} else { Say '  ⚠ DC01 未就绪，跳过域等待。' }

# ---------------------------------------------------------------- 3. FS01 / CLIENT01 重试配置（重试加域）
Head '3. 让 FS01 / CLIENT01 重试加域与配置'
if ($domainOk) {
  foreach ($m in 'PCMigLab-FS01','PCMigLab-CLIENT01') {
    if (-not $ready[$m]) { Say ('  · ' + $m + ' 未就绪，跳过'); continue }
    try {
      Say ('  · 正在 ' + $m + ' 内重跑 run-setup.cmd（可能需要 1–3 分钟）…')
      $r = Invoke-Command -VMName $m -Credential $cred -ScriptBlock {
        $log = 'C:\PCMigLab-setup-retry.log'
        cmd.exe /c "C:\PCMigL3\run-setup.cmd" 2>&1 | Out-File $log -Encoding utf8
        $cs = Get-CimInstance Win32_ComputerSystem
        return [pscustomobject]@{
          ComputerName = $env:COMPUTERNAME
          PartOfDomain = $cs.PartOfDomain
          Domain       = $cs.Domain
          LogTail      = (@(Get-Content $log -Tail 6 -ErrorAction SilentlyContinue) -join ' | ')
        }
      } -ErrorAction Stop
      Say ('    ✔ ' + $r.ComputerName + '  已加域=' + $r.PartOfDomain + '  域=' + $r.Domain)
      Say ('      日志尾部: ' + $r.LogTail)
    } catch { Say ('    ✘ ' + $m + ' 重试失败: ' + $_.Exception.Message.Split([char]10)[0]) }
  }
} else { Say '  · 域未就绪，跳过重试（避免产生无意义的失败记录）' }

# ---------------------------------------------------------------- 4. 采集状态
Head '4. 三台状态采集'
$evidence = @()
foreach ($t in $targets) {
  if (-not $ready[$t]) { $evidence += [pscustomobject]@{ VM=$t; Ready=$false }; continue }
  try {
    $r = Invoke-Command -VMName $t -Credential $cred -ScriptBlock {
      $os = Get-CimInstance Win32_OperatingSystem
      $cs = Get-CimInstance Win32_ComputerSystem
      $ip = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress)
      $adm = @(Get-SmbShare -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\$$' } | Select-Object -ExpandProperty Name)
      $share = @(Get-SmbShare -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '\$$' -and $_.Name -notin @('IPC$') } | Select-Object -ExpandProperty Name)
      return [pscustomobject]@{
        ComputerName = $env:COMPUTERNAME
        Caption      = $os.Caption
        Build        = $os.BuildNumber
        IsEval       = ($os.Caption -match 'Evaluation')
        PartOfDomain = $cs.PartOfDomain
        Domain       = $cs.Domain
        IP           = ($ip -join ',')
        AdminShares  = ($adm -join ',')
        UserShares   = ($share -join ',')
        Explorer     = (Test-Path "$env:SystemRoot\explorer.exe")
      }
    } -ErrorAction Stop
    $evidence += $r
    Say ''
    Say ('  --- ' + $t + ' ---')
    Say ('    计算机名  : ' + $r.ComputerName)
    Say ('    系统      : ' + $r.Caption + '  Build ' + $r.Build + '  评估版=' + $r.IsEval)
    Say ('    Desktop体验: ' + $r.Explorer)
    Say ('    加域      : ' + $r.PartOfDomain + '  域=' + $r.Domain)
    Say ('    IP        : ' + $r.IP)
    Say ('    管理共享  : ' + $r.AdminShares)
    Say ('    用户共享  : ' + $r.UserShares)
  } catch { Say ('  ✘ ' + $t + ' 采集失败: ' + $_.Exception.Message.Split([char]10)[0]) }
}

# ---------------------------------------------------------------- 5. 汇总
Head '5. 汇总'
$dcOk  = @($evidence | Where-Object { $_.ComputerName -eq 'PCMigLab-DC01' -and $_.PartOfDomain } ).Count -gt 0
$fsOk  = @($evidence | Where-Object { $_.ComputerName -eq 'PCMigLab-FS01' -and $_.PartOfDomain }).Count -gt 0
$clOk  = @($evidence | Where-Object { $_.ComputerName -eq 'PCMigLab-CLIENT01' -and $_.PartOfDomain }).Count -gt 0
Say ('  DC01 已是域控      : ' + $dcOk)
Say ('  FS01 已加域        : ' + $fsOk)
Say ('  CLIENT01 已加域    : ' + $clOk)
Say ''
if ($dcOk -and $fsOk -and $clOk) {
  Say '  ✔ L3 环境就绪 —— 下一步可以跑 l3-scenarios.ps1 做 L3-01…L3-10 场景测试。'
} else {
  Say '  ⚠ 环境尚未完全就绪。若某台未加域，可稍后重跑本脚本（只补缺失部分，不会重复灌盘）。'
}

$out = Join-Path $LabRoot ('l3-orchestrate-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$evidence | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $out -Encoding utf8
$logOut = Join-Path $LabRoot ('l3-orchestrate-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$script:log | Out-File -LiteralPath $logOut -Encoding utf8
Say ('  证据: ' + $out)
Say ('  日志: ' + $logOut)
Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 10 } }