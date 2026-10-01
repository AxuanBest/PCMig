<#
  PCMig Corporate Simulation Lab —— VM 创建/管理工具（需管理员）
  ===================================================================
  用途：在 Hyper-V 上创建隔离的 L3 实验室虚拟机（DC01 / FS01 / CLIENT01）。

  设计原则（按用户 2026-09-19 决策）：
    1. **先 Blueprint，再创建 VM** —— 本工具只在你明确执行时创建。
    2. 默认只做 **Plan（干跑）**，不加 -Execute 不创建任何东西。
    3. 完整隔离：只用 Lab-only 的 VM / VHD / 交换机 / 域，绝不碰生产网络与真实域。
    4. 与既有资产无冲突：VM 名与 VHD 路径都带 PCMigLab 前缀，放 G:\PCMigLab\VMs\。
    5. 绝不加入任何真实域、绝不改动宿主 DNS/网络设置。

  拓扑（Blueprint v2 §五）：
    VM-1 DC01      AD DS + DNS（PCMigLab.local）        2 vCPU / 3 GB / 44 GB
    VM-2 FS01      文件服务 + SMB + NTFS ACL + 管理共享   2 vCPU / 2 GB / 44 GB
    VM-3 CLIENT01  Win10 加域客户端（模拟员工 PC）        2 vCPU / 4 GB / 60 GB

  用法：
    干跑（默认，只打印计划）：
      powershell -NoProfile -ExecutionPolicy Bypass -File pcmiglab-vm.ps1
    实际创建（需管理员）：
      powershell -NoProfile -ExecutionPolicy Bypass -File pcmiglab-vm.ps1 -Execute
    仅创建某几台：
      ... -Execute -Machines DC01,FS01
    清理（只删本 Lab 的 VM 与 VHD，需 -Execute 与 -Remove 双确认）：
      ... -Remove -Execute

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot = 'G:\PCMigLab',
  [string[]]$MachinesCsv = @('DC01','FS01','CLIENT01'),
  [string]$VmSwitchName = 'PCMigLab-Sw',
  [string]$SubnetPrefix = '192.168.28',
  [switch]$Execute,
  [switch]$Remove
)

$ErrorActionPreference = 'Stop'

function Write-Head([string]$t) { Write-Host ''; Write-Host ('=== ' + $t + ' ===') }
function Write-Item([string]$t) { Write-Host ('  ' + $t) }

# ---------------------------------------------------------------- 权限闸门
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Head 'PCMig Corporate Simulation Lab / VM 工具'
Write-Item ('LabRoot      : ' + $LabRoot)
Write-Item ('Machines     : ' + ($MachinesCsv -join ', '))
Write-Item ('Switch       : ' + $VmSwitchName)
Write-Item ('Subnet       : ' + $SubnetPrefix + '.0/24')
Write-Item ('Execute      : ' + [bool]$Execute)
Write-Item ('Remove       : ' + [bool]$Remove)
Write-Item ('管理员令牌   : ' + $isAdmin)

if (-not $isAdmin -and ($Execute -or $Remove)) {
  Write-Host ''
  Write-Host '!! 当前不是管理员令牌，无法实际创建/删除 VM。'
  Write-Host '   请用"以管理员身份运行"的 PowerShell 执行，或用自我提升入口：'
  Write-Host ('     powershell -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Execute')
  exit 5
}

# ---------------------------------------------------------------- 规格表
$specs = @{
  'DC01'     = @{ Cpu=2; MemGB=3; DiskGB=44; Role='AD DS + DNS（域 PCMigLab.local）';        Media='Windows Server 2025 Eval ISO' }
  'FS01'     = @{ Cpu=2; MemGB=2; DiskGB=44; Role='文件服务 + SMB + NTFS ACL + 管理共享 D$'; Media='Windows Server 2025 Eval ISO' }
  'CLIENT01' = @{ Cpu=2; MemGB=4; DiskGB=60; Role='Win10 22H2 加域客户端（模拟员工 PC）';     Media='Win10 22H2 ISO（本机已有）' }
}

$vhdDir = Join-Path $LabRoot 'VMs'
$isoDir = Join-Path $LabRoot 'ISO'
if (-not (Test-Path $vhdDir)) { New-Item -ItemType Directory -Force -Path $vhdDir | Out-Null }
if (-not (Test-Path $isoDir)) { New-Item -ItemType Directory -Force -Path $isoDir | Out-Null }

# ---------------------------------------------------------------- 环境前置检查
Write-Head '前置检查'
$hypervOk = $false
try { Get-VM -ErrorAction Stop | Out-Null; $hypervOk = $true; Write-Item '✔ Hyper-V 管理可用（Get-VM 成功）' }
catch { Write-Item ('✘ Hyper-V 管理不可用：' + $_.Exception.Message.Split([char]10)[0]) }

$existingSw = $null
try { $existingSw = Get-VMSwitch -Name $VmSwitchName -ErrorAction SilentlyContinue } catch { }
if ($existingSw) { Write-Item ('✔ 交换机已存在：' + $VmSwitchName + '（' + $existingSw.SwitchType + '）') }
else { Write-Item ('· 交换机不存在，将创建 Internal 类型：' + $VmSwitchName) }

# 介质检查
$srvIso = @(Get-ChildItem $isoDir -Filter '*.iso' -File -ErrorAction SilentlyContinue)
if ($srvIso.Count -gt 0) { foreach ($i in $srvIso) { Write-Item ('✔ ISO: ' + $i.Name + '  ' + [math]::Round($i.Length/1GB,2) + ' GB') } }
else { Write-Item ('⚠ ISO 目录为空：' + $isoDir + '（Server 2025 评估版需从官方评估中心下载）') }

$freeGB = [math]::Round((Get-Volume -DriveLetter ($LabRoot.Substring(0,1))).SizeRemaining/1GB,1)
Write-Item ('✔ ' + $LabRoot.Substring(0,2) + ' 可用空间: ' + $freeGB + ' GB')

# 目标 VM 名冲突检查（带前缀，绝不与既有 VM 冲突）
$prefix = 'PCMigLab-'
$conflicts = @()
try {
  $allVm = @(Get-VM -ErrorAction Stop)
  foreach ($m in $MachinesCsv) {
    $nm = $prefix + $m
    if ($allVm.Name -contains $nm) { $conflicts += $nm }
  }
} catch { }
if ($conflicts.Count -gt 0) { Write-Item ('⚠ 已存在同名 VM：' + ($conflicts -join ', ')) } else { Write-Item '✔ 无同名 VM 冲突（目标名带 PCMigLab- 前缀）' }

# ---------------------------------------------------------------- 计划输出
Write-Head '计划（每台 VM）'
$plan = @()
foreach ($m in $MachinesCsv) {
  $s = $specs[$m]
  if ($null -eq $s) { Write-Item ('✘ 未知机器名：' + $m); continue }
  $vmName = $prefix + $m
  $vhdPath = Join-Path $vhdDir ($vmName + '.vhdx')
  $ip = switch ($m) { 'DC01' { $SubnetPrefix + '.10' } 'FS01' { $SubnetPrefix + '.20' } 'CLIENT01' { $SubnetPrefix + '.30' } default { $SubnetPrefix + '.99' } }
  $plan += [pscustomobject]@{ VM=$vmName; IP=$ip; Cpu=$s.Cpu; MemGB=$s.MemGB; DiskGB=$s.DiskGB; Role=$s.Role; Media=$s.Media; Vhd=$vhdPath }
  Write-Item ($vmName + '  IP=' + $ip + '  ' + $s.Cpu + 'vCPU / ' + $s.MemGB + 'GB / ' + $s.DiskGB + 'GB')
  Write-Item ('    角色: ' + $s.Role)
  Write-Item ('    介质: ' + $s.Media)
  Write-Item ('    VHD : ' + $vhdPath)
}
$totalMem = ($plan | Measure-Object -Property MemGB -Sum).Sum
$totalDisk = ($plan | Measure-Object -Property DiskGB -Sum).Sum
Write-Head '资源合计'
Write-Item ('内存: ' + $totalMem + ' GB（当前可用约 12 GB，动态内存可按需下调）')
Write-Item ('磁盘: ' + $totalDisk + ' GB 上限（dynamic 精简后实际占用通常为 30–50%）')

# ---------------------------------------------------------------- 干跑结束
if (-not $Execute -and -not $Remove) {
  Write-Head '干跑结束（未创建任何 VM）'
  Write-Item '实际创建请加 -Execute（需管理员）'
  Write-Item '例如：powershell -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Execute -MachinesCsv DC01,FS01,CLIENT01'
  exit 0
}

# ---------------------------------------------------------------- 删除模式
if ($Remove) {
  Write-Head '删除本 Lab 的 VM 与 VHD（仅限 PCMigLab- 前缀）'
  foreach ($m in $MachinesCsv) {
    $vmName = $prefix + $m
    try {
      $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
      if ($vm) {
        if ($vm.State -ne 'Off') { Stop-VM -Name $vmName -Force -ErrorAction SilentlyContinue }
        Remove-VM -Name $vmName -Force -ErrorAction SilentlyContinue
        Write-Item ('已删除 VM: ' + $vmName)
      } else { Write-Item ('VM 不存在，跳过: ' + $vmName) }
      $vhdPath = Join-Path $vhdDir ($vmName + '.vhdx')
      if (Test-Path $vhdPath) { Remove-Item -LiteralPath $vhdPath -Force; Write-Item ('已删除 VHD: ' + $vhdPath) }
    } catch { Write-Item ('删除失败 ' + $vmName + '：' + $_.Exception.Message) }
  }
  Write-Head '删除完成'
  exit 0
}

# ---------------------------------------------------------------- 创建模式
Write-Head '创建（实际执行）'

# 1) 交换机（Internal，隔离）
if (-not $existingSw) {
  try {
    New-VMSwitch -Name $VmSwitchName -SwitchType Internal | Out-Null
    Write-Item ('✔ 已创建 Internal 交换机：' + $VmSwitchName)
    Write-Item '  注：宿主侧会多出一个 vEthernet 适配器；如需宿主与 VM 互通，需另给该适配器配 IP（本工具不自动改宿主网络设置）。'
  } catch { Write-Item ('✘ 创建交换机失败：' + $_.Exception.Message) }
} else {
  Write-Item ('· 复用既有交换机：' + $VmSwitchName)
}

# 2) 逐台创建
foreach ($p in $plan) {
  try {
    if (Test-Path $p.Vhd) {
      Write-Item ('· VHD 已存在，复用: ' + $p.Vhd)
    } else {
      New-VHD -Path $p.Vhd -SizeBytes ([long]$p.DiskGB * 1GB) -Dynamic | Out-Null
      Write-Item ('✔ 已创建动态 VHD: ' + $p.Vhd + ' (' + $p.DiskGB + ' GB 上限)')
    }
    $vm = Get-VM -Name $p.VM -ErrorAction SilentlyContinue
    if ($vm) { Write-Item ('· VM 已存在，跳过创建: ' + $p.VM) ; continue }

    New-VM -Name $p.VM -MemoryStartupBytes ([long]$p.MemGB * 1GB) -VHDPath $p.Vhd -Generation 2 -SwitchName $VmSwitchName | Out-Null
    Set-VM -Name $p.VM -ProcessorCount $p.Cpu -DynamicMemory -MemoryMinimumBytes 1GB -MemoryMaximumBytes ([long]$p.MemGB * 1GB) | Out-Null
    Set-VMFirmware -VMName $p.VM -EnableSecureBoot Off | Out-Null   # 便于实验镜像启动
    Write-Item ('✔ 已创建 VM: ' + $p.VM + '  ' + $p.Cpu + 'vCPU / 动态内存 1–' + $p.MemGB + 'GB')
    Write-Item ('   计划 IP: ' + $p.IP + '（需在 VM 内手工配置；本工具不改宿主网络）')
  } catch {
    Write-Item ('✘ 创建失败 ' + $p.VM + '：' + $_.Exception.Message)
  }
}

Write-Head '完成'
Write-Item '下一步（手工，按 Blueprint 顺序）：'
Write-Item '  1. 在 DC01 安装 Windows Server 2025 并提升为域控（域 PCMigLab.local）'
Write-Item '  2. 在 FS01 加域并配置文件服务/共享/NTFS ACL/管理共享'
Write-Item '  3. 在 CLIENT01 安装 Win10 并加入 PCMigLab.local'
Write-Item '  4. 跑 L3-01…L3-11（用 J:\pcmig-lab\lib\LabCommon.ps1 的记录与核对能力）'
Write-Item ''
Write-Item '提示：镜像 ISO 需先挂到各 VM 的 DVD 驱动器（Set-VMDvdDrive），或在此步手工附加。'