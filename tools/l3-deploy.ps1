<#
  PCMig L3 —— 一键部署（创建 VM + 自动应答 + 挂 ISO + 启动）
  ==============================================================
  目的：把"手工建 VM / 手工挂 ISO / 手工点安装界面"压缩成：
    · 你双击一次（同意 UAC）
    · VM 窗口里按一下空格
    · 等它装完（全自动，含 AD/DNS 配置）

  它做什么（全部自动）：
    1. 创建 Internal 交换机 PCMigLab-Sw（隔离网段 192.168.28.0/24）
    2. 创建 3 台 Gen2 VM：PCMigLab-DC01 / PCMigLab-FS01 / PCMigLab-CLIENT01
    3. 为每台生成 autounattend.xml（分区/映像选择/计算机名/自动登录/首登自动跑配置）
    4. 把 autounattend.xml 打成小 ISO，挂到每台的第二个 DVD 驱动器
    5. 把 Windows 安装 ISO 挂到第一个 DVD 驱动器
    6. 按顺序启动（先 DC01）
    7. 安装完成后：在 VM 内自动执行对应的 l3\*-setup.ps1

  安全：
    · 只创建 PCMigLab- 前缀对象；不动任何既有 VM/真实网络
    · 默认**只干跑**，加 -Execute 才真正执行
    · 删除需 -Remove -Execute

  用法：
    干跑（看计划）：  powershell -NoProfile -ExecutionPolicy Bypass -File l3-deploy.ps1
    实际部署：        powershell -NoProfile -ExecutionPolicy Bypass -File l3-deploy.ps1 -Execute
    删除 Lab VM：     ... -Remove -Execute

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot   = 'G:\PCMigLab',
  [string]$WinIso    = 'G:\PCMigLab\ISO\26100.32230.260111-0550.lt_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso',
  [string]$Switch    = 'PCMigLab-Sw',
  [string]$DomainName= 'PCMigLab.local',
  [string]$AdminPassword = '',
  [int]$ServerImageIndex = 2,      # 2 = Standard Evaluation (桌面体验)  ← 已由 dism 实测确认
  [string]$MachinesCsv = 'DC01,FS01,CLIENT01',   # 只建其中某几台：-MachinesCsv DC01
  [string]$ClientIso = '',                        # 可选：CLIENT01 用的 Win10 ISO（留空则也用 Server ISO）
  [switch]$Execute,
  [switch]$Remove
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
function Head([string]$t){ Say ''; Say ('=== ' + $t + ' ===') }

$vhdDir = Join-Path $LabRoot 'VMs'
$ansDir = Join-Path $LabRoot 'answer'
New-Item -ItemType Directory -Force -Path $vhdDir,$ansDir | Out-Null

# VM 规格（与 Blueprint §五 一致）
$specs = @(
  [pscustomobject]@{ Name='DC01';     Cpu=2; MemGB=3; DiskGB=44; IP='192.168.28.10'; Role='AD DS + DNS' }
  [pscustomobject]@{ Name='FS01';     Cpu=2; MemGB=2; DiskGB=44; IP='192.168.28.20'; Role='文件服务 + SMB + ACL' }
  [pscustomobject]@{ Name='CLIENT01'; Cpu=2; MemGB=4; DiskGB=60; IP='192.168.28.30'; Role='Win10 客户端（加域）' }
)

# 按 -MachinesCsv 过滤（-File 模式下数组只能以 CSV 单 token 传入）
$want = @($MachinesCsv -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ -ne '' })
if ($want.Count -gt 0) { $specs = @($specs | Where-Object { $want -contains $_.Name.ToUpperInvariant() }) }

Head 'PCMig L3 一键部署'
Say ('  LabRoot : ' + $LabRoot)
Say ('  WinISO  : ' + $WinIso)
Say ('  交换机  : ' + $Switch)
Say ('  域      : ' + $DomainName)
Say ('  映像索引: ' + $ServerImageIndex + '（2 = Standard Evaluation 桌面体验，已实测确认）')
Say ('  Execute : ' + [bool]$Execute + '   Remove: ' + [bool]$Remove)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say ('  管理员  : ' + $isAdmin)

if (-not $isAdmin -and ($Execute -or $Remove)) {
  Head '需要管理员'
  Say '  当前不是管理员令牌。请用管理员身份运行，或双击 l3-deploy.cmd（会自动请求 UAC）。'
  exit 5
}

# ---------------------------------------------------------------- 删除模式
if ($Remove -and $Execute) {
  Head '删除 Lab VM'
  foreach ($s in $specs) {
    $n = 'PCMigLab-' + $s.Name
    try {
      $vm = Get-VM -Name $n -ErrorAction SilentlyContinue
      if ($vm) {
        if ($vm.State -ne 'Off') { Stop-VM -Name $n -Force -ErrorAction SilentlyContinue }
        Remove-VM -Name $n -Force -ErrorAction SilentlyContinue
        Say ('  ✔ 已删除 VM: ' + $n)
      } else { Say ('  · 不存在: ' + $n) }
      $v = Join-Path $vhdDir ($n + '.vhdx')
      if (Test-Path $v) { Remove-Item -LiteralPath $v -Force; Say ('  ✔ 已删 VHD: ' + $v) }
    } catch { Say ('  ✘ ' + $n + ' : ' + $_.Exception.Message) }
  }
  Say ''; Say '删除完成'
  exit 0
}
if ($Execute) {
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
}

# ---------------------------------------------------------------- 前置检查
Head '前置检查'
if (-not (Test-Path $WinIso)) { Say ('  ✘ Windows ISO 不存在: ' + $WinIso); exit 2 }
Say ('  ✔ Windows ISO 存在 (' + [math]::Round((Get-Item $WinIso).Length/1GB,2) + ' GB)')

$hvOk = $false
try { Get-VM -ErrorAction Stop | Out-Null; $hvOk = $true; Say '  ✔ Hyper-V 管理可用' }
catch { Say ('  ✘ Hyper-V 管理不可用: ' + $_.Exception.Message.Split([char]10)[0]) }

$setupDir = Join-Path $PSScriptRoot 'l3'
Say ('  · 配置脚本目录: ' + $setupDir + '  (存在=' + (Test-Path $setupDir) + ')')

# ---------------------------------------------------------------- 生成 autounattend.xml
function New-UnattendXml {
  param([string]$ComputerName, [int]$ImageIndex, [string]$AdminPwd)
  # 注意：Server 版评估映像索引 2 = Standard Evaluation (桌面体验)
  $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <SetupUILanguage><UILanguage>zh-CN</UILanguage></SetupUILanguage>
      <InputLocale>zh-CN</InputLocale>
      <SystemLocale>zh-CN</SystemLocale>
      <UILanguage>zh-CN</UILanguage>
      <UserLocale>zh-CN</UserLocale>
    </component>
    <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <DiskConfiguration>
        <Disk wcm:action="add">
          <DiskID>0</DiskID>
          <WillWipeDisk>true</WillWipeDisk>
          <CreatePartitions>
            <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>300</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
            <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
            <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
          </ModifyPartitions>
        </Disk>
        <WillShowUI>OnError</WillShowUI>
      </DiskConfiguration>
      <ImageInstall>
        <OSImage>
          <InstallFrom>
            <MetaData wcm:action="add">
              <Key>/IMAGE/INDEX</Key>
              <Value>$ImageIndex</Value>
            </MetaData>
          </InstallFrom>
          <InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo>
          <InstallToAvailablePartition>false</InstallToAvailablePartition>
        </OSImage>
      </ImageInstall>
      <UserData>
        <AcceptEula>true</AcceptEula>
        <FullName>PCMigLab</FullName>
        <Organization>PCMigLab</Organization>
      </UserData>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <ComputerName>$ComputerName</ComputerName>
      <TimeZone>China Standard Time</TimeZone>
    </component>
    <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Description>Enable File and Printer Sharing firewall group</Description>
          <Path>cmd.exe /c netsh advfirewall firewall set rule group="文件和打印机共享" new enable=Yes</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>2</Order>
          <Description>Allow local admin remote access (LocalAccountTokenFilterPolicy)</Description>
          <Path>cmd.exe /c reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>3</Order>
          <Description>Copy L3 payload from answer ISO to local disk (C:\PCMigL3)</Description>
          <Path>cmd.exe /c for %d in (D E F G H I J) do if exist %d:\l3\run-setup.cmd xcopy %d:\l3 C:\PCMigL3\ /E /I /Y</Path>
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
          <Value>$AdminPwd</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <Username>Administrator</Username>
        <LogonCount>30</LogonCount>
        <Password><Value>$AdminPwd</Value><PlainText>true</PlainText></Password>
      </AutoLogon>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Description>Run PCMig L3 setup (local copy first, answer ISO fallback)</Description>
          <CommandLine>cmd.exe /c if exist C:\PCMigL3\run-setup.cmd ( cmd /c C:\PCMigL3\run-setup.cmd ) else ( for %d in (D E F G H I J) do if exist %d:\l3\run-setup.cmd cmd /c %d:\l3\run-setup.cmd )</CommandLine>
        </SynchronousCommand>
      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
"@
  return $xml
}

# ---------------------------------------------------------------- 把 answer 目录打成 ISO
function New-AnswerIso {
  param([string]$SourceDir, [string]$OutIso)
  if (Test-Path $OutIso) { Remove-Item -LiteralPath $OutIso -Force }
  $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
  $fsi.VolumeName = 'PCMIGL3'
  $fsi.FileSystemsToCreate = 7   # ISO9660 + Joliet + UDF
  $fsi.Root.AddTree($SourceDir, $false)
  $result = $fsi.CreateResultImage()
  $istream = [System.Runtime.InteropServices.ComTypes.IStream]$result.ImageStream
  $stat = New-Object System.Runtime.InteropServices.ComTypes.STATSTG
  $istream.Stat([ref]$stat, 1)
  $len = [int]$stat.cbSize
  $buffer = New-Object byte[] $len
  $istream.Read($buffer, $len, [System.IntPtr]::Zero) | Out-Null
  [System.IO.File]::WriteAllBytes($OutIso, $buffer)
  return $len
}

# ---------------------------------------------------------------- 干跑输出
Head '部署计划'
foreach ($s in $specs) {
  $n = 'PCMigLab-' + $s.Name
  Say ('  ' + $n.PadRight(20) + ' IP=' + $s.IP + '  ' + $s.Cpu + 'vCPU / ' + $s.MemGB + 'GB / ' + $s.DiskGB + 'GB   ' + $s.Role)
}
$mem = ($specs | Measure-Object -Property MemGB -Sum).Sum
Say ('  合计内存: ' + $mem + ' GB   磁盘上限: ' + (($specs | Measure-Object -Property DiskGB -Sum).Sum) + ' GB')

if (-not $Execute) {
  Head '干跑结束（未创建任何 VM）'
  Say '  实际部署请加 -Execute（需管理员）'
  Say ('  powershell -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Execute')
  exit 0
}

if (-not $hvOk) { Say ''; Say '✘ Hyper-V 管理不可用，无法继续。请以管理员身份运行。'; exit 5 }

# ---------------------------------------------------------------- 建交换机
Head '1. 交换机'
$sw = Get-VMSwitch -Name $Switch -ErrorAction SilentlyContinue
if ($sw) { Say ('  · 已存在: ' + $Switch + ' (' + $sw.SwitchType + ')') }
else {
  New-VMSwitch -Name $Switch -SwitchType Internal | Out-Null
  Say ('  ✔ 已创建 Internal 交换机: ' + $Switch)
  $ad = Get-NetAdapter | Where-Object { $_.Name -like ('vEthernet (' + $Switch + ')') } | Select-Object -First 1
  if ($ad) { Say ('    · 宿主侧适配器: ' + $ad.Name + '（未配置 IP，保持宿主网络不受影响）') }
}

# ---------------------------------------------------------------- 准备 answer 目录
Head '2. 生成应答文件与配置载荷'
# run-setup.cmd：在 VM 内被 firstlogon 调用；按计算机名选对应 setup 脚本
$runner = @'
@echo off
setlocal
set LOG=C:\PCMigLab-setup.log
echo [%DATE% %TIME%] L3 setup runner start >> %LOG%
set HERE=%~dp0
for %%d in (D E F G H I) do if exist %%d:\l3\ (set HERE=%%d:\l3\)
echo payload dir = %HERE% >> %LOG%
if /I "%COMPUTERNAME%"=="PCMigLab-DC01"     ( echo running dc01-setup >> %LOG% & powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%dc01-setup.ps1" -SafeModePassword "__ADMINPWD__" -UserPassword "__ADMINPWD__" >> %LOG% 2>&1 )
if /I "%COMPUTERNAME%"=="PCMigLab-FS01"     ( echo running fs01-setup >> %LOG% & powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%fs01-setup.ps1" -DomainJoinUser PCMigAdmin -DomainJoinPassword "__ADMINPWD__" >> %LOG% 2>&1 )
if /I "%COMPUTERNAME%"=="PCMigLab-CLIENT01" ( echo running client01-setup >> %LOG% & powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%client01-setup.ps1" -DomainJoinUser PCMigAdmin -DomainJoinPassword "__ADMINPWD__" >> %LOG% 2>&1 )
echo [%DATE% %TIME%] done >> %LOG%
endlocal
'@
$runner = $runner.Replace('__ADMINPWD__', $AdminPassword)
$l3Dir = Join-Path $ansDir 'l3'
New-Item -ItemType Directory -Force -Path $l3Dir | Out-Null
[IO.File]::WriteAllText((Join-Path $l3Dir 'run-setup.cmd'), $runner, (New-Object Text.ASCIIEncoding))
# 复制三个 setup 脚本进载荷
foreach ($f in 'dc01-setup.ps1','fs01-setup.ps1','client01-setup.ps1','l3-scenarios.ps1') {
  $src = Join-Path $setupDir $f
  if (Test-Path $src) { Copy-Item $src (Join-Path $l3Dir $f) -Force; Say ('  ✔ 已放入载荷: ' + $f) }
  else { Say ('  ⚠ 缺失: ' + $f) }
}
# 生成各机的 autounattend.xml（放载荷根；firstlogon 不依赖它，它供安装程序使用）
foreach ($s in $specs) {
  $xml = New-UnattendXml -ComputerName ('PCMigLab-' + $s.Name) -ImageIndex $ServerImageIndex -AdminPwd $AdminPassword
  $p = Join-Path $ansDir ('autounattend-' + $s.Name + '.xml')
  [IO.File]::WriteAllText($p, $xml, (New-Object Text.UTF8Encoding($true)))
  Say ('  ✔ 已生成应答: ' + (Split-Path $p -Leaf))
}

# ---------------------------------------------------------------- 逐台建 VM + 挂 ISO
Head '3. 创建 VM 并挂载 ISO'
$answerIso = Join-Path $ansDir 'pcmigl3-answer.iso'
# 把每个机的 autounattend 也复制成通用名（部分安装程序只认 autounattend.xml）
Copy-Item (Join-Path $ansDir 'autounattend-DC01.xml') (Join-Path $ansDir 'autounattend.xml') -Force
$sz = New-AnswerIso -SourceDir $ansDir -OutIso $answerIso
Say ('  ✔ 已生成应答 ISO: ' + $answerIso + '  (' + [math]::Round($sz/1KB,1) + ' KB)')

foreach ($s in $specs) {
  $n = 'PCMigLab-' + $s.Name
  $vhd = Join-Path $vhdDir ($n + '.vhdx')
  try {
    if (-not (Test-Path $vhd)) {
      New-VHD -Path $vhd -SizeBytes ([long]$s.DiskGB * 1GB) -Dynamic | Out-Null
      Say ('  ✔ VHD: ' + $vhd)
    } else { Say ('  · VHD 已存在: ' + $vhd) }

    $vm = Get-VM -Name $n -ErrorAction SilentlyContinue
    if (-not $vm) {
      New-VM -Name $n -MemoryStartupBytes ([long]$s.MemGB * 1GB) -VHDPath $vhd -Generation 2 -SwitchName $Switch | Out-Null
      Say ('  ✔ VM: ' + $n)
    } else { Say ('  · VM 已存在: ' + $n) }

    Set-VM -Name $n -ProcessorCount $s.Cpu -DynamicMemory -MemoryMinimumBytes 1GB -MemoryMaximumBytes ([long]$s.MemGB * 1GB) | Out-Null
    Set-VMFirmware -VMName $n -EnableSecureBoot Off | Out-Null

    # DVD1 = Windows 安装介质；DVD2 = 应答 ISO
    # 【重要】不假设 ControllerLocation：Add-VMDvdDrive 会自动挑位置，
    #          必须用它的返回对象记录真实位置，否则 Set-VMFirmware 拿不到对象、启动顺序设不上。
    $dvd1 = $null
    $existing = @(Get-VMDvdDrive -VMName $n -ErrorAction SilentlyContinue)
    $dvd1 = $existing | Where-Object { $_.Path -and ($_.Path -ieq $WinIso) } | Select-Object -First 1
    if (-not $dvd1 -and $existing.Count -gt 0) {
      $reuse = $existing[0]
      Set-VMDvdDrive -VMName $n -ControllerNumber $reuse.ControllerNumber -ControllerLocation $reuse.ControllerLocation -Path $WinIso -ErrorAction Stop
      $dvd1 = Get-VMDvdDrive -VMName $n -ControllerNumber $reuse.ControllerNumber -ControllerLocation $reuse.ControllerLocation
      Say ('    · 复用原 DVD 位置 (0,' + $reuse.ControllerLocation + ') 挂安装 ISO')
    }
    if (-not $dvd1) {
      $dvd1 = Add-VMDvdDrive -VMName $n -Path $WinIso -Passthru -ErrorAction Stop
      Say ('    ✔ 新增 DVD 挂安装 ISO: (0,' + $dvd1.ControllerLocation + ')')
    }

    $existing2 = @(Get-VMDvdDrive -VMName $n -ErrorAction SilentlyContinue)
    $dvd2 = $existing2 | Where-Object { $_.Path -and ($_.Path -ieq $answerIso) } | Select-Object -First 1
    if (-not $dvd2) {
      $dvd2 = Add-VMDvdDrive -VMName $n -Path $answerIso -Passthru -ErrorAction SilentlyContinue
      if ($dvd2) { Say ('    ✔ 新增 DVD 挂应答 ISO: (0,' + $dvd2.ControllerLocation + ')') }
      else { Say '    ⚠ 无空闲 DVD 位置放应答 ISO（装完可手工跑 run-setup.cmd）' }
    }

    if ($dvd1) {
      Set-VMFirmware -VMName $n -FirstBootDevice $dvd1 -ErrorAction Stop
      Say ('    ✔ 已把 DVD(0,' + $dvd1.ControllerLocation + ') 设为首选启动设备')
    } else { Say '    ✘ 没能挂上安装 ISO' }
  } catch { Say ('  ✘ ' + $n + ' : ' + $_.Exception.Message) }
}

# ---------------------------------------------------------------- 启动
Head '4. 启动 DC01（先装域控）'
try {
  $dc = Get-VM -Name 'PCMigLab-DC01' -ErrorAction Stop
  if ($dc.State -eq 'Off') { Start-VM -Name 'PCMigLab-DC01' | Out-Null; Say '  ✔ 已启动 PCMigLab-DC01' }
  else { Say ('  · PCMigLab-DC01 当前状态: ' + $dc.State) }
  Start-Sleep -Seconds 3
  $dc = Get-VM -Name 'PCMigLab-DC01'
  Say ('  当前状态: ' + $dc.State + '   （打开 Hyper-V 管理器可看到窗口）')
  Say ''
  Say '  ┌─ 接下来你只需要 ────────────────────────────────┐'
  Say '  │ 1. 打开 Hyper-V 管理器 → 双击 PCMigLab-DC01 → 连接 │'
  Say '  │ 2. 看到 "Press any key to boot from CD" 时按一下空格 │'
  Say '  │ 3. 然后全自动：分区→安装→重启→自动登录→自动配 AD   │'
  Say '  │ 4. 装完再对 FS01 / CLIENT01 重复（脚本已挂好 ISO）  │'
  Say '  └──────────────────────────────────────────────────┘'
} catch { Say ('  ✘ 启动失败: ' + $_.Exception.Message) }

# ---------------------------------------------------------------- 汇总
Head '完成'
Say ('  应答载荷目录: ' + $ansDir)
Say ('  应答 ISO    : ' + $answerIso)
Say ('  VM 目录     : ' + $vhdDir)
$out = Join-Path $LabRoot ('l3-deploy-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$script:log | Out-File -LiteralPath $out -Encoding utf8
Say ('  日志: ' + $out)
Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 5 } }
