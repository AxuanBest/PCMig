<#
  PCMig L3 —— 诊断并修复 VM 启动顺序（需管理员）
  ==================================================
  症状：Hyper-V 报 "The boot loader did not load an operating system"，
        列出 1. SCSI Disk (0,0) / 2. Network Adapter —— 说明 DVD 不在启动序列里。

  本脚本只做：
    1. 报告每台 Lab VM 的 DVD 挂载情况与启动顺序
    2. 若 DVD 未挂载 -> 挂上（安装 ISO 到 DVD1，应答 ISO 到 DVD2）
    3. 把 DVD1 设为首选启动设备
    4. 再次报告确认

  用法：双击 l3-fixboot.cmd（自动请求 UAC）
        或管理员 PowerShell：
        powershell -NoProfile -ExecutionPolicy Bypass -File "I:\deepseek work\PCMig\tools\l3-fixboot.ps1"

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot  = 'G:\PCMigLab',
  [string]$WinIso   = 'G:\PCMigLab\ISO\26100.32230.260111-0550.lt_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso',
  [string]$AnswerIso= 'G:\PCMigLab\answer\pcmigl3-answer.iso',
  [string]$MachinesCsv = 'DC01,FS01,CLIENT01',
  [switch]$StartAfterFix
)

$ErrorActionPreference = 'Continue'
$me = $PSCommandPath
if ([string]::IsNullOrEmpty($me)) { $me = $MyInvocation.MyCommand.Path }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host '当前不是管理员，正在请求提升（请点“是”）…'
  try {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $me + '"'),
      '-LabRoot',('"' + $LabRoot + '"'),'-WinIso',('"' + $WinIso + '"'),'-AnswerIso',('"' + $AnswerIso + '"'),
      '-MachinesCsv',('"' + $MachinesCsv + '"'))
    Write-Host '已请求提升，请看新窗口。'
  } catch { Write-Host ('提升失败: ' + $_.Exception.Message) }
  exit 0
}

Write-Host ''
Write-Host '=== PCMig L3 启动顺序诊断与修复 ==='
Write-Host ('  管理员: True   时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Host ('  安装ISO: ' + $WinIso + '  存在=' + (Test-Path $WinIso))
Write-Host ('  应答ISO: ' + $AnswerIso + '  存在=' + (Test-Path $AnswerIso))

$machines = @($MachinesCsv -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ -ne '' })

foreach ($m in $machines) {
  $name = 'PCMigLab-' + $m
  Write-Host ''
  Write-Host ('################ ' + $name + ' ################')
  $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
  if (-not $vm) { Write-Host '  ✘ VM 不存在，跳过'; continue }
  Write-Host ('  状态: ' + $vm.State + '   代次: ' + $vm.Generation)

  # --- 当前启动顺序 ---
  Write-Host '  --- 当前启动顺序 ---'
  try {
    $order = Get-VMFirmware -VMName $name -ErrorAction Stop
    $i = 0
    foreach ($d in $order.BootOrder) {
      $i++
      $desc = $d.SwitchType
      try { $desc = $d.Description } catch { }
      Write-Host ('    ' + $i + '. ' + $desc)
    }
    if ($i -eq 0) { Write-Host '    (空)' }
  } catch { Write-Host ('    ✘ 读取失败: ' + $_.Exception.Message) }

  # --- 当前 DVD 挂载 ---
  Write-Host '  --- 当前 DVD 挂载 ---'
  $dvds = @(Get-VMDvdDrive -VMName $name -ErrorAction SilentlyContinue)
  if ($dvds.Count -eq 0) { Write-Host '    ✘ 没有任何 DVD 驱动器' }
  else {
    foreach ($d in $dvds) {
      Write-Host ('    Controller ' + $d.ControllerNumber + ',' + $d.ControllerLocation + '  Path=' + $d.Path)
    }
  }

  # --- 修复：确保 DVD1=安装ISO, DVD2=应答ISO ---
  Write-Host '  --- 修复 ---'
  try {
    # 找空闲的 DVD 位置（Gen2 只有 SCSI，最多 4 个位置 0..3）
    $free = @()
    for ($loc = 0; $loc -le 3; $loc++) {
      $ex = Get-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation $loc -ErrorAction SilentlyContinue
      if (-not $ex) { $free += $loc }
    }
    Write-Host ('    空闲 DVD 位置: ' + ($free -join ', '))

    # DVD1（安装 ISO）：优先复用已在位的，否则用第一个空闲位
    $dvd1 = $dvds | Where-Object { $_.Path -and ($_.Path -ieq $WinIso) } | Select-Object -First 1
    if (-not $dvd1) {
      $dvd1 = $dvds | Select-Object -First 1
      if ($dvd1) {
        Set-VMDvdDrive -VMName $name -ControllerNumber $dvd1.ControllerNumber -ControllerLocation $dvd1.ControllerLocation -Path $WinIso -ErrorAction Stop
        Write-Host ('    ✔ 已把安装 ISO 挂到 (' + $dvd1.ControllerNumber + ',' + $dvd1.ControllerLocation + ')')
      } elseif ($free.Count -gt 0) {
        $dvd1 = Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation $free[0] -Path $WinIso -Passthru -ErrorAction Stop
        $free = @($free | Where-Object { $_ -ne $free[0] })
        Write-Host ('    ✔ 已新增 DVD 并挂安装 ISO 到 (' + $dvd1.ControllerNumber + ',' + $dvd1.ControllerLocation + ')')
      } else { Write-Host '    ✘ 无可用 DVD 位置' }
    } else {
      Write-Host ('    · 安装 ISO 已在 (' + $dvd1.ControllerNumber + ',' + $dvd1.ControllerLocation + ')')
    }

    # DVD2（应答 ISO）
    if ((Test-Path $AnswerIso) -and $dvd1) {
      $hasAns = @($dvds | Where-Object { $_.Path -and ($_.Path -ieq $AnswerIso) }).Count -gt 0
      if (-not $hasAns -and $free.Count -gt 0) {
        Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation $free[0] -Path $AnswerIso -ErrorAction SilentlyContinue | Out-Null
        Write-Host ('    ✔ 已新增 DVD 并挂应答 ISO 到 (0,' + $free[0] + ')')
      } elseif ($hasAns) { Write-Host '    · 应答 ISO 已挂载' }
      else { Write-Host '    ⚠ 无空闲位置挂应答 ISO（应答文件将不可用，可在装完后手工运行 l3\run-setup.cmd）' }
    }

    # --- 设 DVD1 为首选启动设备 ---
    if ($dvd1) {
      Set-VMFirmware -VMName $name -FirstBootDevice $dvd1 -ErrorAction Stop
      Write-Host '    ✔ 已把 DVD 设为首选启动设备'
    }

    # --- 再次确认 ---
    Write-Host '  --- 修复后启动顺序 ---'
    $o2 = Get-VMFirmware -VMName $name
    $j = 0
    foreach ($d in $o2.BootOrder) {
      $j++
      $desc = ''
      try { $desc = $d.Description } catch { $desc = [string]$d.SwitchType }
      Write-Host ('    ' + $j + '. ' + $desc)
    }

    if ($StartAfterFix) {
      if ($vm.State -eq 'Off') { Start-VM -Name $name | Out-Null; Write-Host '    ✔ 已启动' }
      else { Write-Host ('    · 当前状态: ' + $vm.State) }
    }
  } catch {
    Write-Host ('    ✘ 修复失败: ' + $_.Exception.Message)
  }
}

Write-Host ''
Write-Host '=== 完成 ==='
Write-Host '请在 Hyper-V 管理器里连接该 VM，若无窗口请点“启动”。'
Write-Host '看到 "Press any key to boot from CD or DVD..." 时按一下空格。'
Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 8 } }