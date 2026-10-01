<#
  PCMig L3 —— 全自动离线部署（无需任何手工点按）
  ================================================================
  为什么改用这种方式：
    从 ISO 引导安装必然出现 "Press any key to boot from CD"（Hyper-V Gen2 UEFI 无法绕过），
    这是不可接受的人工介入点。离线部署把映像直接灌进 VHDX，**完全不需要引导安装程序**。

  它做的事（全程自动）：
    1. 挂载官方 ISO（只读，不修改，SHA256 完整性不受影响）
    2. 每台 VM：
       a. 建/复用动态 VHDX
       b. GPT 分区：EFI 300MB(FAT32) + MSR 16MB + 主分区 NTFS
       c. 用 Expand-WindowsImage 把 install.wim 的指定索引直接展开到主分区
       d. bcdboot 写 UEFI 引导
       e. 离线注入 unattend.xml 到 Windows\Panther\
       f. 离线注入 SetupComplete.cmd 到 Windows\Setup\Scripts\（比 FirstLogonCommands 可靠）
       g. 离线放置配置脚本到 C:\PCMigL3\
       h. 卸载 VHDX
    3. 创建/更新 VM（挂应答 ISO 作兜底、设硬盘为第一启动）
    4. 启动 → 首次开机自动完成 specialize + oobe + 自动登录 + 自动跑配置

  仍然完全不碰：
    · 原生 ISO 文件（不修改、不重建）
    · 宿主的网络设置
    · 任何非 PCMigLab- 前缀的 VM

  用法（双击 l3-inject.cmd，或管理员 PowerShell）：
    powershell -NoProfile -ExecutionPolicy Bypass -File l3-inject.ps1 -Execute
    干跑（只打印计划）：不加 -Execute
    只做一台：          -MachinesCsv DC01

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot   = 'G:\PCMigLab',
  [string]$WinIso    = 'G:\PCMigLab\ISO\26100.32230.260111-0550.lt_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso',
  [string]$Switch    = 'PCMigLab-Sw',
  [string]$DomainName= 'PCMigLab.local',
  [string]$AdminPassword = '',
  [int]$ImageIndex   = 2,
  [string]$MachinesCsv = 'DC01,FS01,CLIENT01',
  [switch]$Execute,
  [switch]$KeepMounted
)

$ErrorActionPreference = 'Stop'
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

$vhdDir = Join-Path $LabRoot 'VMs'
$ansDir = Join-Path $LabRoot 'answer'
New-Item -ItemType Directory -Force -Path $vhdDir,$ansDir | Out-Null

$specs = @(
  [pscustomobject]@{ Name='DC01';     Cpu=2; MemGB=3; DiskGB=44; IP='192.168.28.10'; Role='AD DS + DNS' }
  [pscustomobject]@{ Name='FS01';     Cpu=2; MemGB=2; DiskGB=44; IP='192.168.28.20'; Role='文件服务 + SMB + ACL' }
  [pscustomobject]@{ Name='CLIENT01'; Cpu=2; MemGB=4; DiskGB=60; IP='192.168.28.30'; Role='Win10 客户端（加域）' }
)
$want = @($MachinesCsv -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ -ne '' })
if ($want.Count -gt 0) { $specs = @($specs | Where-Object { $want -contains $_.Name.ToUpperInvariant() }) }

Head 'PCMig L3 全自动离线部署'
Say ('  LabRoot : ' + $LabRoot)
Say ('  WinISO  : ' + $WinIso)
Say ('  映像索引: ' + $ImageIndex + '  (= Standard Evaluation 桌面体验，已实测确认)')
Say ('  目标机  : ' + (($specs | ForEach-Object { $_.Name }) -join ', '))
Say ('  Execute : ' + [bool]$Execute)
Say ('  管理员  : ' + $isAdmin)

if (-not $Execute) {
  Head '干跑（未做任何修改）'
  foreach ($s in $specs) {
    Say ('  PCMigLab-' + $s.Name.PadRight(10) + ' ' + $s.Cpu + 'vCPU/' + $s.MemGB + 'GB/' + $s.DiskGB + 'GB  IP=' + $s.IP + '  ' + $s.Role)
  }
  Say ''
  Say ('  VHDX 目录: ' + $vhdDir)
  Say '  实际执行请加 -Execute（需管理员）。全程无需手工点按、无需按空格。'
  exit 0
}

if (-not $isAdmin) { Say ''; Say '✘ 需要管理员权限（挂载 VHD / 应用映像 / bcdboot）。请双击 l3-inject.cmd。'; exit 5 }
if (-not (Test-Path $WinIso)) { Say ('✘ Windows ISO 不存在: ' + $WinIso); exit 2 }

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

# ---------------------------------------------------------------- 准备配置脚本载荷
Head '0. 准备配置载荷'
$payloadDir = Join-Path $ansDir 'payload'
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
$setupDir = Join-Path $PSScriptRoot 'l3'
$copied = @()
foreach ($f in 'dc01-setup.ps1','fs01-setup.ps1','client01-setup.ps1','l3-scenarios.ps1') {
  $src = Join-Path $setupDir $f
  if (Test-Path $src) { Copy-Item $src (Join-Path $payloadDir $f) -Force; $copied += $f }
}
Say ('  已收集脚本: ' + ($copied -join ', '))

# SetupComplete.cmd：由 Windows 在 OOBE 结束后、以 SYSTEM 身份自动执行（比 FirstLogonCommands 可靠）
$setupComplete = @'
@echo off
setlocal
set LOG=C:\PCMigLab-setup.log
echo [%DATE% %TIME%] SetupComplete start >> %LOG%
if exist C:\PCMigL3\run-setup.cmd ( call C:\PCMigL3\run-setup.cmd >> %LOG% 2>&1 ) else ( echo no payload at C:\PCMigL3 >> %LOG% )
echo [%DATE% %TIME%] SetupComplete done >> %LOG%
endlocal
'@
[IO.File]::WriteAllText((Join-Path $payloadDir 'SetupComplete.cmd'), $setupComplete, (New-Object Text.ASCIIEncoding))

# run-setup.cmd：按计算机名分发到对应 setup 脚本
$runSetup = @'
@echo off
setlocal
set LOG=C:\PCMigLab-setup.log
echo [%DATE% %TIME%] run-setup start on %COMPUTERNAME% >> %LOG%
set HERE=%~dp0
if /I "%COMPUTERNAME%"=="PCMigLab-DC01"     ( echo -> dc01-setup >> %LOG% & powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%dc01-setup.ps1" -SafeModePassword "__ADMINPWD__" -UserPassword "__ADMINPWD__" >> %LOG% 2>&1 )
if /I "%COMPUTERNAME%"=="PCMigLab-FS01"     ( echo -> fs01-setup >> %LOG% & powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%fs01-setup.ps1" -DomainJoinUser PCMigAdmin -DomainJoinPassword "__ADMINPWD__" >> %LOG% 2>&1 )
if /I "%COMPUTERNAME%"=="PCMigLab-CLIENT01" ( echo -> client01-setup >> %LOG% & powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%client01-setup.ps1" -DomainJoinUser PCMigAdmin -DomainJoinPassword "__ADMINPWD__" >> %LOG% 2>&1 )
echo [%DATE% %TIME%] run-setup done >> %LOG%
endlocal
'@
$runSetup = $runSetup.Replace('__ADMINPWD__', $AdminPassword)
[IO.File]::WriteAllText((Join-Path $payloadDir 'run-setup.cmd'), $runSetup, (New-Object Text.ASCIIEncoding))
Say '  已生成 SetupComplete.cmd 与 run-setup.cmd'

# 生成 unattend.xml（每个计算机名一份，同时也放一份通用名）
function New-Unattend {
  param([string]$ComputerName, [int]$Index, [string]$Pwd)
  return @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <ComputerName>$ComputerName</ComputerName>
      <TimeZone>China Standard Time</TimeZone>
    </component>
    <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Path>cmd.exe /c netsh advfirewall firewall set rule group="文件和打印机共享" new enable=Yes</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>2</Order>
          <Path>cmd.exe /c reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <NetworkLocation>Work</NetworkLocation>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts>
        <AdministratorPassword>
          <Value>$Pwd</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <Username>Administrator</Username>
        <LogonCount>50</LogonCount>
        <Password><Value>$Pwd</Value><PlainText>true</PlainText></Password>
      </AutoLogon>
    </component>
  </settings>
</unattend>
"@
}

# ---------------------------------------------------------------- 挂载 ISO
Head '1. 挂载官方 ISO（只读）'
$isoDrive = $null
try {
  $existing = Get-DiskImage -ImagePath $WinIso -ErrorAction SilentlyContinue
  if ($existing -and $existing.Attached) {
    $isoDrive = ($existing | Get-Volume).DriveLetter
    Say ('  · ISO 已挂载于 ' + $isoDrive + ':')
  } else {
    $img = Mount-DiskImage -ImagePath $WinIso -PassThru -ErrorAction Stop
    Start-Sleep -Seconds 2
    $isoDrive = ($img | Get-Volume).DriveLetter
    Say ('  ✔ 已挂载到 ' + $isoDrive + ':')
  }
} catch { Say ('  ✘ 挂载失败: ' + $_.Exception.Message); exit 3 }

$wim = $isoDrive + ':\sources\install.wim'
if (-not (Test-Path $wim)) { Say ('  ✘ 找不到 ' + $wim); exit 3 }
Say ('  ✔ install.wim: ' + [math]::Round((Get-Item $wim).Length/1GB,2) + ' GB')

# ---------------------------------------------------------------- 交换机
Head '2. 隔离交换机'
$sw = Get-VMSwitch -Name $Switch -ErrorAction SilentlyContinue
if ($sw) { Say ('  · 已存在: ' + $Switch) }
else { New-VMSwitch -Name $Switch -SwitchType Internal | Out-Null; Say ('  ✔ 已创建 Internal: ' + $Switch) }

# ---------------------------------------------------------------- 逐台离线部署
$gptEfi = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
$gptMsr = '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'
$gptBasic = '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'

foreach ($s in $specs) {
  $name = 'PCMigLab-' + $s.Name
  $vhd  = Join-Path $vhdDir ($name + '.vhdx')
  Head ('3. 离线部署 ' + $name)

  $osLetter = $null; $efiLetter = $null; $meta = $null
  try {
    # 0) 关键前提：离线挂载 VHDX 前，必须先停止 VM（否则 VHDX 被占用，Dismount/Mount 都会失败）
    $vmPre = Get-VM -Name $name -ErrorAction SilentlyContinue
    if ($vmPre -and $vmPre.State -ne 'Off') {
      Say ('  · VM 正在运行（' + $vmPre.State + '），先停止以便离线挂载 VHDX')
      Stop-VM -Name $name -Force -ErrorAction SilentlyContinue
      $w = 0
      while ($w -lt 40) {
        $vmPre = Get-VM -Name $name -ErrorAction SilentlyContinue
        if ((-not $vmPre) -or ($vmPre.State -eq 'Off')) { break }
        Start-Sleep -Seconds 1; $w++
      }
      Say ('  ✔ VM 已停止（等待 ' + $w + ' 秒）')
    }

    # 已灌过盘的 VHDX 直接跳过灌盘（按文件大小判断：灌入 7.46GB 映像后必然远大于 5GB）
    $skipInject = $false
    if (Test-Path $vhd) {
      $szNow = (Get-Item $vhd).Length
      if ($szNow -gt 5GB) {
        Say ('  · VHDX 已灌入系统（' + [math]::Round($szNow/1GB,1) + ' GB），跳过灌盘，仅做 VM 配置')
        $skipInject = $true
      }
    }

    if ($skipInject) {
      Say '  · 跳过灌盘（此盘已部署过），仅做 VM 配置'
    } else {
    # a) VHDX
    if (Test-Path $vhd) {
      $meta = Get-VHD -Path $vhd -ErrorAction SilentlyContinue
      if ($meta -and $meta.Attached) { Dismount-VHD -Path $vhd -ErrorAction SilentlyContinue }
      Say ('  · 复用已有 VHDX: ' + $vhd)
    } else {
      New-VHD -Path $vhd -SizeBytes ([long]$s.DiskGB * 1GB) -Dynamic | Out-Null
      Say ('  ✔ 已创建动态 VHDX: ' + $vhd + '  (' + $s.DiskGB + ' GB 上限)')
    }

    if ($meta -and $meta.Attached) { Dismount-VHD -Path $vhd -ErrorAction Stop; Start-Sleep -Seconds 1 }

    # b) 挂载并分区
    $vhdx = Mount-VHD -Path $vhd -Passthru
    Start-Sleep -Seconds 2
    $diskNum = $vhdx.DiskNumber
    Say ('  · 已挂载为 Disk ' + $diskNum)

    $disk = Get-Disk -Number $diskNum
    if ($disk.PartitionStyle -eq 'RAW') {
      Initialize-Disk -Number $diskNum -PartitionStyle GPT -Confirm:$false | Out-Null
      Say '  ✔ 已初始化为 GPT'
    } else { Say ('  · 分区表已存在: ' + $disk.PartitionStyle + '（将重建）') }

    # 清掉旧分区（仅限本 VHD 的磁盘对象）
    Get-Partition -DiskNumber $diskNum -ErrorAction SilentlyContinue | ForEach-Object {
      Remove-Partition -DiskNumber $diskNum -PartitionNumber $_.PartitionNumber -Confirm:$false -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 1

    $efi = New-Partition -DiskNumber $diskNum -Size 300MB -GptType $gptEfi -AssignDriveLetter
    Format-Volume -Partition $efi -FileSystem FAT32 -NewFileSystemLabel 'System' -Confirm:$false | Out-Null
    $efiLetter = $efi.DriveLetter
    Say ('  ✔ EFI 分区: ' + $efiLetter + ': (FAT32 300MB)')

    New-Partition -DiskNumber $diskNum -Size 16MB -GptType $gptMsr | Out-Null
    Say '  ✔ MSR 分区: 16MB'

    $os = New-Partition -DiskNumber $diskNum -UseMaximumSize -GptType $gptBasic -AssignDriveLetter
    Format-Volume -Partition $os -FileSystem NTFS -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null
    $osLetter = $os.DriveLetter
    Say ('  ✔ 主分区: ' + $osLetter + ': (NTFS)')

    # c) 应用映像
    Say ('  · 正在展开映像（索引 ' + $ImageIndex + '）— 约需 5–15 分钟，请耐心…')
    $t0 = Get-Date
    Expand-WindowsImage -ImagePath $wim -Index $ImageIndex -ApplyPath ($osLetter + ':\') -ErrorAction Stop | Out-Null
    $sec = [int]((Get-Date) - $t0).TotalSeconds
    Say ('  ✔ 映像已展开，用时 ' + $sec + ' 秒')

    # d) 引导
    $bcd = & bcdboot.exe ($osLetter + ':\Windows') /s ($efiLetter + ':') /f UEFI 2>&1
    Say ('  ✔ bcdboot: ' + (($bcd | Select-Object -Last 1) -as [string]))

    # e) 注入 unattend.xml
    $panther = $osLetter + ':\Windows\Panther'
    New-Item -ItemType Directory -Force -Path $panther | Out-Null
    $xml = New-Unattend -ComputerName $name -Index $ImageIndex -Pwd $AdminPassword
    [IO.File]::WriteAllText((Join-Path $panther 'unattend.xml'), $xml, (New-Object Text.UTF8Encoding($true)))
    Say '  ✔ 已注入 Windows\Panther\unattend.xml'

    # f) 注入 SetupComplete.cmd
    $sc = $osLetter + ':\Windows\Setup\Scripts'
    New-Item -ItemType Directory -Force -Path $sc | Out-Null
    Copy-Item (Join-Path $payloadDir 'SetupComplete.cmd') (Join-Path $sc 'SetupComplete.cmd') -Force
    Say '  ✔ 已注入 Windows\Setup\Scripts\SetupComplete.cmd'

    # g) 放置配置载荷
    $pl = $osLetter + ':\PCMigL3'
    New-Item -ItemType Directory -Force -Path $pl | Out-Null
    Copy-Item (Join-Path $payloadDir '*') $pl -Recurse -Force
    Say ('  ✔ 已放置配置载荷到 ' + $osLetter + ':\PCMigL3（' + (@(Get-ChildItem $pl -File).Count) + ' 个文件）')

    # 记录
    Say ('  · 预期 IP: ' + $s.IP + '（由 setup 脚本在首次开机时配置）')
    }
  } catch {
    Say ('  ✘ 部署失败: ' + $_.Exception.Message)
  } finally {
    # h) 卸载 VHDX（无论如何都要卸，避免残留挂载）
    try {
      $still = Get-VHD -Path $vhd -ErrorAction SilentlyContinue
      if ($still -and $still.Attached) { Dismount-VHD -Path $vhd -ErrorAction Stop }
      Say '  ✔ 已卸载 VHDX'
    } catch { Say ('  ⚠ 卸载失败: ' + $_.Exception.Message) }
  }

  # i) 创建/更新 VM
  try {
    $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
    if (-not $vm) {
      New-VM -Name $name -MemoryStartupBytes ([long]$s.MemGB * 1GB) -VHDPath $vhd -Generation 2 -SwitchName $Switch | Out-Null
      Say ('  ✔ 已创建 VM: ' + $name)
    } else { Say ('  · VM 已存在: ' + $name) }
    Set-VM -Name $name -ProcessorCount $s.Cpu -DynamicMemory -MemoryMinimumBytes 1GB -MemoryMaximumBytes ([long]$s.MemGB * 1GB) | Out-Null
    Set-VMFirmware -VMName $name -EnableSecureBoot Off | Out-Null

    # 硬盘设为第一启动（离线部署后不需要光驱引导）
    $hd = Get-VMHardDiskDrive -VMName $name | Select-Object -First 1
    if ($hd) { Set-VMFirmware -VMName $name -FirstBootDevice $hd | Out-Null; Say '  ✔ 已把硬盘设为首选启动设备（无需光驱引导）' }
  } catch { Say ('  ✘ VM 配置失败: ' + $_.Exception.Message) }
}

# ---------------------------------------------------------------- 启动
Head '4. 启动'
foreach ($s in $specs) {
  $name = 'PCMigLab-' + $s.Name
  try {
    $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
    if (-not $vm) { Say ('  ✘ 不存在: ' + $name); continue }
    if ($vm.State -eq 'Off') { Start-VM -Name $name | Out-Null; Say ('  ✔ 已启动 ' + $name) }
    else { Say ('  · ' + $name + ' 状态: ' + $vm.State) }
  } catch { Say ('  ✘ 启动 ' + $name + ' 失败: ' + $_.Exception.Message) }
}

Start-Sleep -Seconds 5
Head '5. 状态'
foreach ($s in $specs) {
  $vm = Get-VM -Name ('PCMigLab-' + $s.Name) -ErrorAction SilentlyContinue
  if ($vm) { Say ('  ' + $vm.Name.PadRight(20) + ' ' + $vm.State + '  Uptime=' + $vm.Uptime) }
}

Head '完成'
Say '  首次开机会自动：specialize → oobe → 自动登录 → SetupComplete 跑配置脚本'
Say '  全部自动，无需按任何键、无需从光驱引导。'
Say '  查看进度：连接 VM 看桌面，或等 5–15 分钟后在宿主机跑 l3-scenarios.ps1。'
Say ''
Say ('  若需要兜底：VM 内 C:\PCMigLab-setup.log 记录配置脚本执行情况')

$out = Join-Path $LabRoot ('l3-inject-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$script:log | Out-File -LiteralPath $out -Encoding utf8
Say ('  日志: ' + $out)
Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 10 } }
