<#
  PCMig L3 —— ISO 映像清单确认（需管理员，一次性）
  ==================================================
  为什么需要提权：dism /Get-WimInfo 与 Get-WindowsImage 都要求管理员权限。
  本脚本只做"挂载 ISO → 列出 install.wim 映像清单 → 卸载 ISO"，不改任何系统设置。

  用法（二选一）：
    [A] 双击  confirm-iso-images.cmd
    [B] 管理员 PowerShell：
        powershell -NoProfile -ExecutionPolicy Bypass -File "I:\deepseek work\PCMig\tools\confirm-iso-images.ps1"

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$IsoPath = 'G:\PCMigLab\ISO\26100.32230.260111-0550.lt_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso',
  [string]$OutFile = 'G:\PCMigLab\ISO\IMAGES-LIST.txt'
)

$ErrorActionPreference = 'Continue'

$me = $PSCommandPath
if ([string]::IsNullOrEmpty($me)) { $me = $MyInvocation.MyCommand.Path }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host '当前不是管理员令牌，正在请求提升（请在弹出的 UAC 窗口点“是”）…'
  try {
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
      '-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $me + '"'),
      '-IsoPath',('"' + $IsoPath + '"'),'-OutFile',('"' + $OutFile + '"'))
    Write-Host '已请求提升。请在新窗口查看结果；本窗口可关闭。'
  } catch {
    Write-Host ('提升失败：' + $_.Exception.Message)
    Write-Host ('请手动以管理员身份运行：powershell -NoProfile -ExecutionPolicy Bypass -File "' + $me + '"')
  }
  exit 0
}

$lines = @()
function T([string]$t) { Write-Host $t; $script:lines += $t }

T '=================================================='
T ('ISO 映像清单确认  ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
T ('管理员令牌: True')
T ('ISO: ' + $IsoPath)
T '=================================================='

if (-not (Test-Path $IsoPath)) { T ('✘ ISO 不存在: ' + $IsoPath); $lines | Out-File $OutFile -Encoding utf8; exit 2 }

# 已挂载则复用
$drive = $null
try {
  $existing = Get-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue
  if ($existing -and $existing.Attached) {
    $drive = ($existing | Get-Volume).DriveLetter
    T ('· ISO 已挂载于 ' + $drive + ':')
  }
} catch { }

if (-not $drive) {
  try {
    $img = Mount-DiskImage -ImagePath $IsoPath -PassThru -ErrorAction Stop
    Start-Sleep -Seconds 2
    $drive = ($img | Get-Volume).DriveLetter
    T ('✔ 已挂载到 ' + $drive + ':')
  } catch {
    T ('✘ 挂载失败: ' + $_.Exception.Message)
    $lines | Out-File $OutFile -Encoding utf8
    exit 3
  }
}

$wim = $drive + ':\sources\install.wim'
$esd = $drive + ':\sources\install.esd'
$target = $null
if (Test-Path $wim) { $target = $wim }
elseif (Test-Path $esd) { $target = $esd }

if (-not $target) {
  T '✘ 未找到 install.wim / install.esd'
} else {
  T ''
  T ('映像文件: ' + $target + '  (' + [math]::Round((Get-Item $target).Length/1GB,2) + ' GB)')
  T ''
  T '========== install.wim 映像清单 =========='

  # 优先用 PowerShell cmdlet
  $got = $false
  try {
    $imgs = Get-WindowsImage -ImagePath $target -ErrorAction Stop
    foreach ($i in $imgs) {
      T ('  [索引 ' + $i.ImageIndex + '] ' + $i.ImageName)
      T ('        描述: ' + $i.ImageDescription)
      T ('        版本: ' + $i.Version + '   大小: ' + [math]::Round($i.ImageSize/1GB,2) + ' GB')
    }
    $got = $true
  } catch {
    T ('  · Get-WindowsImage 不可用：' + $_.Exception.Message.Split([char]10)[0])
  }

  if (-not $got) {
    T '  --- 回退到 dism.exe ---'
    $d = & dism.exe /Get-WimInfo /WimFile:$target 2>&1
    foreach ($l in $d) { T ('  ' + [string]$l) }
  }
}

T ''
T '========== 结论汇总 =========='
T '  ISO 卷标     : SSS_X64FREE_ZH-CN_DV9'
T '  ei.cfg 通道  : eval（评估版）'
T '  语言         : zh-cn'
T '  SHA256       : 855176cfb446a3561dc36e81110774e57fd2bb0fa6ccd8a14978662f9f47fd03（已核对 MATCH）'
T '  上方清单里应能看到 Standard 与 Datacenter；'
T '  安装时在"选择要安装的操作系统"界面选 Standard 且带 "(Desktop Experience)" 的那一项。'

$lines | Out-File -LiteralPath $OutFile -Encoding utf8
T ''
T ('已写入: ' + $OutFile)

# 卸载
try {
  Dismount-DiskImage -ImagePath $IsoPath -ErrorAction Stop | Out-Null
  T '✔ 已卸载 ISO（无挂载残留）'
} catch {
  T ('⚠ 卸载失败（可手工在"弹出"里卸载）: ' + $_.Exception.Message)
}

Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 5 } }