<#
  PCMig L3 —— CLIENT01 配置脚本（在 CLIENT01 虚拟机内运行）
  =========================================================
  作用：把一台 Windows 10 22H2 客户端变成"模拟普通员工 PC"：
    · 加入 PCMigLab.local
    · 配置静态 IP + DNS 指向 DC01
    · 造用户数据（供 PCMig 从宿主机直拉；对齐 State Plane 的 C:\Users 场景）
    · 验证：能在该客户端上运行 PCMig（验证普通域用户下的能力边界）

  安全与纪律：
    1. 只加入 **PCMigLab.local**；检测到已属于其它域则中止。
    2. 不动宿主网络。
    3. 每步 Verify。

  用法（在 CLIENT01 内，管理员 PowerShell）：
    powershell -NoProfile -ExecutionPolicy Bypass -File client01-setup.ps1 `
        -DomainName PCMigLab.local -StaticIP 192.168.28.30 `
        -DomainJoinUser PCMigAdmin -DomainJoinPassword '<口令>'

  可选：
    -PcmigExe <路径>   把 PCMig 可执行文件放到本机（默认从 \\DC01\C$\PCMigLab 取，取不到则跳过）

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$DomainName  = 'PCMigLab.local',
  [string]$StaticIP    = '192.168.28.30',
  [string]$DnsServer   = '192.168.28.10',
  [int]$PrefixLength   = 24,
  [Parameter(Mandatory)][string]$DomainJoinUser,
  [Parameter(Mandatory)][string]$DomainJoinPassword,
  [string]$UserDataRoot = 'C:\PCMigUserData',
  [switch]$SkipDomainJoin
)

$ErrorActionPreference = 'Stop'
$script:results = @()
function Note([string]$phase,[string]$result,[string]$detail){
  $script:results += [pscustomobject]@{ Phase=$phase; Result=$result; Detail=$detail }
  Write-Host ('  [' + $result + '] ' + $phase + ' — ' + $detail)
}
function Head([string]$t){ Write-Host ''; Write-Host ('=== ' + $t + ' ===') }

Head 'PCMig L3 / CLIENT01 配置'
Write-Host ('  域: ' + $DomainName + '   静态IP: ' + $StaticIP + '   DNS: ' + $DnsServer)
Write-Host ('  用户数据根: ' + $UserDataRoot)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Note '前置-管理员' 'BLOCKED' '需要管理员权限，请以管理员身份运行'; return }
Note '前置-管理员' 'PASS' '管理员令牌已确认'

# 安全闸门
Head '安全闸门'
try {
  $cs = Get-CimInstance Win32_ComputerSystem
  if ($cs.PartOfDomain -and $cs.Domain -notlike 'PCMigLab*') {
    Note '安全闸门' 'BLOCKED' ('本机已属于域 ' + $cs.Domain + '，非本实验室域，脚本中止。')
    return
  }
  Note '安全闸门' 'PASS' ('PartOfDomain=' + $cs.PartOfDomain + '  Domain=' + $cs.Domain)
} catch { Note '安全闸门' 'FAIL' $_.Exception.Message }

# 1. 网络
Head '1. 网络配置'
try {
  $nic = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' } | Select-Object -First 1
  if ($null -eq $nic) { throw '找不到已启用的网络适配器' }
  $ex = Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
  if ($ex) { Remove-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue }
  New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress $StaticIP -PrefixLength $PrefixLength | Out-Null
  Set-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ServerAddresses $DnsServer
  Note '1-网络' 'PASS' ($nic.Name + ' -> ' + $StaticIP + '，DNS=' + $DnsServer)
} catch { Note '1-网络' 'FAIL' $_.Exception.Message }

# 2. 加域
Head '2. 加入域'
if (-not $SkipDomainJoin) {
  try {
    $cs = Get-CimInstance Win32_ComputerSystem
    if ($cs.PartOfDomain -and $cs.Domain -ieq $DomainName) {
      Note '2-加域' 'PASS' ('已加入 ' + $DomainName)
    } else {
      Write-Host ('  · 正在加入 ' + $DomainName + '（此步会重启）…')
      $cred = New-Object System.Management.Automation.PSCredential(
        ($DomainName.Split('.')[0] + '\' + $DomainJoinUser),
        (ConvertTo-SecureString $DomainJoinPassword -AsPlainText -Force))
      Add-Computer -DomainName $DomainName -Credential $cred -Force -ErrorAction Stop
      Note '2-加域' 'PASS' '加域指令已执行（请重启后重跑本脚本继续）'
    }
  } catch {
    $m = $_.Exception.Message
    if ($m -match 'reboot|重启') { Note '2-加域' 'NOT_RUN' '需重启后继续' } else { Note '2-加域' 'FAIL' $m }
  }
} else { Note '2-加域' 'NOT_RUN' '按 -SkipDomainJoin 跳过' }

# 3. 用户数据（对齐真实员工 PC 的 C:\Users 结构）
Head '3. 用户数据'
try {
  $dirs = @('Desktop','Documents','Pictures','Downloads','AppData\Roaming\DemoApp')
  foreach ($d in $dirs) { New-Item -ItemType Directory -Force -Path (Join-Path $UserDataRoot $d) | Out-Null }
  $desk = Join-Path $UserDataRoot 'Desktop'
  if (@(Get-ChildItem $desk -File -Force -ErrorAction SilentlyContinue).Count -eq 0) {
    1..30 | ForEach-Object { Set-Content (Join-Path $desk ("桌面文件-{0:D2}.txt" -f $_)) -Value ('client01 desktop ' + $_) -Encoding UTF8 }
    Set-Content (Join-Path $desk '会议纪要 2026.txt') -Value '中文名 + 空格' -Encoding UTF8
    [IO.File]::WriteAllBytes((Join-Path $desk '空文件.bin'), @())
    Set-Content (Join-Path $UserDataRoot 'Documents\项目计划（内部）.txt') -Value 'docs' -Encoding UTF8
    Set-Content (Join-Path $UserDataRoot 'AppData\Roaming\DemoApp\settings.json') -Value '{"demo":true}' -Encoding UTF8
  }
  $cnt = @(Get-ChildItem $UserDataRoot -Recurse -File -Force).Count
  Note '3-用户数据' 'PASS' ($UserDataRoot + ' 共 ' + $cnt + ' 个文件（含中文名/空格/零字节/AppData 结构）')
} catch { Note '3-用户数据' 'FAIL' $_.Exception.Message }

# 4. PCMig 可执行检查（用于 L3-09 普通域用户能力边界）
Head '4. PCMig 可执行检查'
try {
  $p = Get-Command pcmig -ErrorAction SilentlyContinue
  if ($p) { Note '4-PCMig' 'PASS' ('PATH 中找到: ' + $p.Source) }
  else {
    Note '4-PCMig' 'NOT_RUN' '本机 PATH 中无 pcmig；可把 Portable 版拷入后重测（L3-09 需要）'
  }
} catch { Note '4-PCMig' 'FAIL' $_.Exception.Message }

# 5. 校验
Head '5. 汇总校验'
try {
  $cs = Get-CimInstance Win32_ComputerSystem
  $r = 'FAIL'; if ($cs.PartOfDomain -and $cs.Domain -ieq $DomainName) { $r = 'PASS' }
  Note '5-加域状态' $r ($cs.Domain + ' / ' + $env:COMPUTERNAME)
  $dns = Resolve-DnsName $DomainName -ErrorAction SilentlyContinue
  $r2 = 'FAIL'; if ($dns) { $r2 = 'PASS' }
  Note '5-DNS解析' $r2 ((($dns | Select-Object -First 1).IPAddress) -join ',')
  $dclist = @()
  try { $dclist = @(Get-ADDomainController -Discover -ErrorAction SilentlyContinue | Select-Object -ExpandProperty HostName) } catch { }
  if ($dclist.Count -gt 0) { Note '5-发现DC' 'PASS' ($dclist -join ', ') } else { Note '5-发现DC' 'NOT_RUN' '未发现 DC（如刚加域需重启后重跑）' }
} catch { Note '5-校验' 'FAIL' $_.Exception.Message }

Head '结果汇总'
foreach ($r in $script:results) { Write-Host ('  ' + $r.Result.PadRight(10) + ' ' + $r.Phase) }
$bad = @($script:results | Where-Object { $_.Result -eq 'FAIL' })
$sum = '无 FAIL'; if ($bad.Count -gt 0) { $sum = ($bad.Count.ToString() + ' 项 FAIL') }
Write-Host ('  总体：' + $sum)
$outDir = 'C:\PCMigLab-Logs'; New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir ('client01-setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$script:results | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $out -Encoding utf8
Write-Host ('  证据：' + $out)
if ($bad.Count -gt 0) { exit 1 } else { exit 0 }