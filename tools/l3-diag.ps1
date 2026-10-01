<#
  PCMig L3 —— VM 启动诊断（需管理员）
  ======================================
  目的：把"按空格没反应"的真实原因查清。只读为主，不做破坏性修改。

  输出：屏幕 + G:\PCMigLab\l3-diag-<时间>.txt（把该文件内容发回即可）

  用法：双击 l3-diag.cmd（自动请求 UAC）
  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$LabRoot = 'G:\PCMigLab',
  [string]$WinIso  = 'G:\PCMigLab\ISO\26100.32230.260111-0550.lt_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso',
  [string]$AnswerIso = 'G:\PCMigLab\answer\pcmigl3-answer.iso',
  [string]$MachinesCsv = 'DC01,FS01,CLIENT01'
)

$ErrorActionPreference = 'Continue'
$me = $PSCommandPath
if ([string]::IsNullOrEmpty($me)) { $me = $MyInvocation.MyCommand.Path }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host '正在请求管理员权限（请点“是”）…'
  try {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $me + '"'),
      '-LabRoot',('"' + $LabRoot + '"'),'-WinIso',('"' + $WinIso + '"'),'-AnswerIso',('"' + $AnswerIso + '"'),'-MachinesCsv',('"' + $MachinesCsv + '"'))
  } catch { Write-Host ('提升失败: ' + $_.Exception.Message) }
  exit 0
}

$out = @()
function L([string]$t) { Write-Host $t; $script:out += $t }
function H([string]$t) { L ''; L ('===== ' + $t + ' =====') }

L ('PCMig L3 启动诊断  ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
L ('管理员: ' + $isAdmin)

# ---- Hyper-V 服务 ----
H 'Hyper-V 服务'
foreach ($svc in 'vmms','vmcompute','HvHost') {
  $s = Get-Service $svc -ErrorAction SilentlyContinue
  if ($s) { L ('  ' + $svc + ' = ' + $s.Status) } else { L ('  ' + $svc + ' = 不存在') }
}

# ---- ISO 文件 ----
H 'ISO 文件'
foreach ($p in @($WinIso, $AnswerIso)) {
  if (Test-Path $p) {
    $i = Get-Item $p
    L ('  ✔ ' + $p)
    L ('      大小 ' + $i.Length + ' 字节 (' + [math]::Round($i.Length/1GB,2) + ' GB)  时间 ' + $i.LastWriteTime)
  } else {
    L ('  ✘ 不存在: ' + $p)
  }
}

# ---- 逐台 VM ----
$machines = @($MachinesCsv -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ -ne '' })
foreach ($m in $machines) {
  $name = 'PCMigLab-' + $m
  H ('VM ' + $name)
  $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
  if (-not $vm) { L '  ✘ VM 不存在'; continue }

  L ('  状态        : ' + $vm.State)
  L ('  代次        : ' + $vm.Generation)
  L ('  内存        : 启动 ' + [math]::Round($vm.MemoryStartup/1MB) + ' MB')
  L ('  CPU         : ' + $vm.ProcessorCount)
  L ('  自动启动动作: ' + $vm.AutomaticStartAction)

  # 固件
  try {
    $fw = Get-VMFirmware -VMName $name -ErrorAction Stop
    L ('  SecureBoot  : ' + $fw.SecureBoot)
    L ('  模板        : ' + $fw.SecureBootTemplate)
    L '  --- 启动顺序（从上到下）---'
    $k = 0
    foreach ($b in $fw.BootOrder) {
      $k++
      $line = '    ' + $k + '. '
      try { $line += [string]$b.Description } catch { }
      try { $line += '  [SwitchType=' + [string]$b.SwitchType + ']' } catch { }
      try { $line += '  [BootType=' + [string]$b.BootType + ']' } catch { }
      L $line
      # 若该项是 DVD，打印其路径
      try {
        if ($b.SwitchType -eq 'DvdDrive') {
          L ('         -> Controller ' + $b.ControllerNumber + ',' + $b.ControllerLocation + '  Path=' + $b.Path)
        }
      } catch { }
    }
    if ($k -eq 0) { L '    (空 —— 这就是问题：没有任何启动设备)' }
  } catch { L ('  ✘ 读固件失败: ' + $_.Exception.Message) }

  # DVD 驱动器
  L '  --- DVD 驱动器 ---'
  $dvds = @(Get-VMDvdDrive -VMName $name -ErrorAction SilentlyContinue)
  if ($dvds.Count -eq 0) { L '    ✘ 没有任何 DVD 驱动器（Gen2 需挂在 SCSI 控制器上）' }
  foreach ($d in $dvds) {
    $exists = $false
    if ($d.Path) { $exists = Test-Path $d.Path }
    L ('    Controller ' + $d.ControllerNumber + ',' + $d.ControllerLocation + '  Path=' + $d.Path + '  文件存在=' + $exists)
  }

  # 硬盘
  L '  --- 硬盘 ---'
  $hds = @(Get-VMHardDiskDrive -VMName $name -ErrorAction SilentlyContinue)
  foreach ($h in $hds) {
    $hex = Test-Path $h.Path
    $sz = 0
    if ($hex) { $sz = (Get-Item $h.Path).Length }
    L ('    Controller ' + $h.ControllerNumber + ',' + $h.ControllerLocation + '  Path=' + $h.Path)
    L ('      文件存在=' + $hex + '  当前大小=' + [math]::Round($sz/1MB) + ' MB（新盘应很小，装完会变大）')
  }

  # 网络
  $nad = @(Get-VMNetworkAdapter -VMName $name -ErrorAction SilentlyContinue)
  foreach ($a in $nad) { L ('  网卡: ' + $a.Name + '  Switch=' + $a.SwitchName + '  Status=' + $a.Status) }
}

# ---- 结论与建议 ----
H '诊断结论（自动判断）'
$dc = Get-VM -Name 'PCMigLab-DC01' -ErrorAction SilentlyContinue
if (-not $dc) {
  L '  ✘ PCMigLab-DC01 不存在 —— 需要先运行 l3-deploy.cmd -Execute 创建'
} else {
  $fw = $null
  try { $fw = Get-VMFirmware -VMName 'PCMigLab-DC01' -ErrorAction Stop } catch { }
  $dvds = @(Get-VMDvdDrive -VMName 'PCMigLab-DC01' -ErrorAction SilentlyContinue)
  $isoOk = @($dvds | Where-Object { $_.Path -and ($_.Path -ieq $WinIso) -and (Test-Path $_.Path) }).Count -gt 0
  $firstIsDvd = $false
  if ($fw -and $fw.BootOrder.Count -gt 0) {
    try { if ($fw.BootOrder[0].SwitchType -eq 'DvdDrive') { $firstIsDvd = $true } } catch { }
  }
  L ('  DVD 已挂安装 ISO      : ' + $isoOk)
  L ('  DVD 是第一个启动设备  : ' + $firstIsDvd)

  if (-not $isoOk) {
    L '  => 问题：安装 ISO 没挂上。请在 Hyper-V 管理器 → 设置 → SCSI 控制器 → DVD 驱动器 → 指向 ISO。'
    L '     或运行 l3-fixboot.cmd 自动修复。'
  } elseif (-not $firstIsDvd) {
    L '  => 问题：DVD 没在启动顺序第一位。请运行 l3-fixboot.cmd，或在 设置→固件 里把 DVD 上移。'
  } else {
    L '  => 配置看起来正确。若按空格仍无反应，多半是【连接窗口没有键盘焦点】：'
    L '     1) 打开 Hyper-V 连接窗口后，先用鼠标点一下窗口内部的黑色区域'
    L '     2) 再按空格（连按几次）'
    L '     3) 若仍无提示，说明从光驱引导的 3 秒窗口已过：'
    L '        请在"连接"窗口已打开且有焦点的情况下，点管理器的【启动】按钮，然后立刻连按空格'
  }
}

$f = Join-Path $LabRoot ('l3-diag-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
$out | Out-File -LiteralPath $f -Encoding utf8
L ''
L ('已写入: ' + $f)
L '请把上面这段（或该文件）内容发回。'
Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 10 } }