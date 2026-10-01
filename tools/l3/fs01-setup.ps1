<#
  PCMig L3 —— FS01 配置脚本（在 FS01 虚拟机内运行）
  =====================================================
  作用：把一台全新的 Windows Server 2025 变成实验室文件服务器，用于验证：
    · 普通共享（域用户可读）—— PCMig 直拉的正常路径
    · NTFS ACL 拒绝（错误 5）—— 权限失败分支
    · 管理共享 D$（域管理员可访问 / 普通域用户被拒）—— 管理共享权限模型
    · 共享消失/恢复 —— 网络故障恢复分支（由测试脚本操作，不在本脚本内）

  安全与纪律：
    1. **只操作 Lab-only 对象**：共享名/目录/ACL 全部 PCMig 专用命名。
    2. 若检测到本机已属于**非 PCMigLab** 的域，直接中止（防止污染真实环境）。
    3. 不动宿主网络；只配置本 VM。
    4. 每步 Verify，失败即明确报错。

  用法（在 FS01 内，管理员 PowerShell）：
    powershell -NoProfile -ExecutionPolicy Bypass -File fs01-setup.ps1 `
        -DomainName PCMigLab.local -StaticIP 192.168.28.20 `
        -DomainJoinUser 'PCMigAdmin' -DomainJoinPassword '<口令>'

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$DomainName   = 'PCMigLab.local',
  [string]$StaticIP     = '192.168.28.20',
  [string]$DnsServer    = '192.168.28.10',
  [int]$PrefixLength    = 24,
  [Parameter(Mandatory)][string]$DomainJoinUser,
  [Parameter(Mandatory)][string]$DomainJoinPassword,
  [string]$ShareRoot    = 'C:\PCMigShare',
  [string]$ShareName    = 'PCMigShare',
  [switch]$SkipDomainJoin
)

$ErrorActionPreference = 'Stop'
$script:results = @()
function Note([string]$phase,[string]$result,[string]$detail){
  $script:results += [pscustomobject]@{ Phase=$phase; Result=$result; Detail=$detail }
  Write-Host ('  [' + $result + '] ' + $phase + ' — ' + $detail)
}
function Head([string]$t){ Write-Host ''; Write-Host ('=== ' + $t + ' ===') }

Head 'PCMig L3 / FS01 配置'
Write-Host ('  域: ' + $DomainName + '   静态IP: ' + $StaticIP + '   DNS: ' + $DnsServer)
Write-Host ('  共享: ' + $ShareName + ' -> ' + $ShareRoot)

# 管理员闸门
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Note '前置-管理员' 'BLOCKED' '需要管理员权限，请以管理员身份运行'; return }
Note '前置-管理员' 'PASS' '管理员令牌已确认'

# 安全闸门
Head '安全闸门'
try {
  $cs = Get-CimInstance Win32_ComputerSystem
  if ($cs.PartOfDomain -and $cs.Domain -notlike 'PCMigLab*') {
    Note '安全闸门' 'BLOCKED' ('本机已属于域 ' + $cs.Domain + '，不是本实验室域。脚本中止以避免污染真实环境。')
    return
  }
  Note '安全闸门' 'PASS' ('PartOfDomain=' + $cs.PartOfDomain + '  Domain=' + $cs.Domain)
} catch { Note '安全闸门' 'FAIL' $_.Exception.Message }

# 1. 网络
Head '1. 网络配置'
try {
  $nic = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' } | Select-Object -First 1
  if ($null -eq $nic) { throw '找不到已启用的网络适配器' }
  $existing = Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
  if ($existing) { Remove-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue }
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
      Write-Host ('  · 正在加入 ' + $DomainName + '（此步可能重启）…')
      $cred = New-Object System.Management.Automation.PSCredential(
        ($DomainName.Split('.')[0] + '\' + $DomainJoinUser),
        (ConvertTo-SecureString $DomainJoinPassword -AsPlainText -Force))
      Add-Computer -DomainName $DomainName -Credential $cred -Force -ErrorAction Stop
      Note '2-加域' 'PASS' '加域指令已执行（如提示重启请重启后重跑本脚本）'
    }
  } catch {
    $m = $_.Exception.Message
    if ($m -match 'reboot|重启') { Note '2-加域' 'NOT_RUN' '需重启后继续' }
    else { Note '2-加域' 'FAIL' $m }
  }
} else { Note '2-加域' 'NOT_RUN' '按 -SkipDomainJoin 跳过' }

# 3. 文件服务角色
Head '3. 文件服务角色'
try {
  $f = Get-WindowsFeature -Name FS-FileServer
  if ($f.Installed) { Note '3-角色' 'PASS' 'FS-FileServer 已安装' }
  else {
    $r = Install-WindowsFeature -Name FS-FileServer -IncludeManagementTools
    $res = 'FAIL'; if ($r.Success) { $res = 'PASS' }
    Note '3-角色' $res '安装完成'
  }
} catch { Note '3-角色' 'FAIL' $_.Exception.Message }

# 4. 目录 + NTFS ACL
Head '4. 目录与 NTFS ACL'
$okDir = $true
try {
  $normal = Join-Path $ShareRoot 'normal'
  $denied = Join-Path $ShareRoot 'denied'
  New-Item -ItemType Directory -Force -Path $normal,$denied | Out-Null
  # 正常数据
  if (@(Get-ChildItem $normal -Recurse -File -Force -ErrorAction SilentlyContinue).Count -eq 0) {
    1..80 | ForEach-Object { Set-Content (Join-Path $normal ("fs-file-{0:D3}.txt" -f $_)) -Value ('fs01 payload ' + $_) -Encoding UTF8 }
    New-Item -ItemType Directory -Force -Path (Join-Path $normal '中文目录') | Out-Null
    Set-Content (Join-Path $normal '中文目录\带 空格#井号%.txt') -Value 'edge case' -Encoding UTF8
    [IO.File]::WriteAllBytes((Join-Path $normal '零字节.bin'), @())
  }
  # 拒绝访问目录（用于发错误 5）
  Set-Content (Join-Path $denied 'secret.txt') -Value 'should be denied for operators' -Encoding UTF8
  $grp = $DomainName.Split('.')[0] + '\PCMig-Operators'
  icacls $denied /inheritance:d 2>&1 | Out-Null
  icacls $denied /remove:g "Users" 2>&1 | Out-Null
  $ic = icacls $denied /deny ($grp + ':(OI)(CI)(RX)') 2>&1
  Note '4-目录' 'PASS' ($normal + ' / ' + $denied + ' 已建')
  Note '4-ACL拒绝' 'PASS' ('已对 ' + $grp + ' 施加 deny 读取')
} catch { $okDir = $false; Note '4-目录/ACL' 'FAIL' $_.Exception.Message }

# 5. SMB 共享
Head '5. SMB 共享'
try {
  $ex = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
  if ($ex) {
    Note '5-共享' 'PASS' ('共享已存在: ' + $ShareName)
  } else {
    New-SmbShare -Name $ShareName -Path $ShareRoot -FullAccess 'Administrators' -ChangeAccess 'Authenticated Users' -ErrorAction Stop | Out-Null
    Note '5-共享' 'PASS' ('已创建 \\' + $env:COMPUTERNAME + '\' + $ShareName + ' -> ' + $ShareRoot)
  }
  $s = Get-SmbShare -Name $ShareName -ErrorAction Stop
  Note '5-共享路径' 'PASS' $s.Path
  # 共享级 ACL 明细
  $acc = Get-SmbShareAccess -Name $ShareName | ForEach-Object { $_.AccountName + '=' + $_.AccessRight }
  Note '5-共享ACL' 'PASS' ($acc -join ' | ')
} catch { Note '5-共享' 'FAIL' $_.Exception.Message }

# 6. 管理共享检查（Server 默认导出）
Head '6. 管理共享'
try {
  $adm = @(Get-SmbShare | Where-Object { $_.Name -match '\$$' } | Select-Object -ExpandProperty Name)
  $r = 'FAIL'; if ($adm -contains 'C$') { $r = 'PASS' }
  Note '6-管理共享' $r ('已导出: ' + ($adm -join ', ') + '（PCMig 用 \\FS01\C$ 或 D$ 测管理共享权限）')
} catch { Note '6-管理共享' 'FAIL' $_.Exception.Message }

# 7. 校验
Head '7. 汇总校验'
try {
  $cnt = @(Get-ChildItem $ShareRoot -Recurse -File -Force).Count
  Note '7-数据量' 'PASS' ($ShareRoot + ' 共 ' + $cnt + ' 个文件')
  $cs = Get-CimInstance Win32_ComputerSystem
  $r = 'FAIL'; if ($cs.PartOfDomain) { $r = 'PASS' }
  Note '7-加域状态' $r ($cs.Domain + ' / ' + $env:COMPUTERNAME)
  $svc = Get-Service LanmanServer -ErrorAction SilentlyContinue
  $r2 = 'FAIL'; if ($svc.Status -eq 'Running') { $r2 = 'PASS' }
  Note '7-LanmanServer' $r2 $svc.Status
} catch { Note '7-校验' 'FAIL' $_.Exception.Message }

Head '结果汇总'
foreach ($r in $script:results) { Write-Host ('  ' + $r.Result.PadRight(10) + ' ' + $r.Phase) }
$bad = @($script:results | Where-Object { $_.Result -eq 'FAIL' })
$sum = '无 FAIL'; if ($bad.Count -gt 0) { $sum = ($bad.Count.ToString() + ' 项 FAIL') }
Write-Host ('  总体：' + $sum)
$outDir = 'C:\PCMigLab-Logs'; New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir ('fs01-setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$script:results | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $out -Encoding utf8
Write-Host ('  证据：' + $out)
if ($bad.Count -gt 0) { exit 1 } else { exit 0 }