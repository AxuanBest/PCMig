<#
  PCMig Lab —— 通过管理员执行器运行命令（由 AI 或人调用，本身不需管理员）
  ========================================================================
  前置：已运行过 lab-admin-setup.ps1 -Install（执行器常驻）。

  用法（三种）：
    # 1) 执行一个已存在的脚本文件
    .\lab-admin-run.ps1 -ScriptFile 'G:\PCMigLab\scripts\run-orchestrate.ps1'

    # 2) 直接给一段 PowerShell 代码
    .\lab-admin-run.ps1 -Inline 'Get-VM | Select-Object Name,State'

    # 3) 从管道读代码
    'Get-VM' | .\lab-admin-run.ps1

  行为：写入队列 → 轮询结果 → 打印 stdout 并把退出码作为自身退出码返回。
  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$ScriptFile,
  [string]$Inline,
  [int]$TimeoutSec = 1800,
  [string]$Name,
  [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$base  = 'G:\PCMigLab'
$queue = Join-Path $base 'admin-queue'
$res   = Join-Path $base 'admin-results'
foreach ($d in @($base,$queue,$res)) { if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null } }

# ---- 组装要执行的代码 ----
$code = $null
if ($ScriptFile) {
  if (-not (Test-Path $ScriptFile)) { Write-Error ('找不到脚本: ' + $ScriptFile); exit 2 }
  $code = [IO.File]::ReadAllText($ScriptFile)
} elseif ($Inline) {
  $code = $Inline
} else {
  # 从管道读
  $stdin = @($input)
  if ($stdin.Count -gt 0) { $code = ($stdin -join [Environment]::NewLine) }
}
if ([string]::IsNullOrWhiteSpace($code)) { Write-Error '没有要执行的代码（用 -ScriptFile / -Inline / 管道）'; exit 2 }

# ---- 写入队列 ----
if ([string]::IsNullOrWhiteSpace($Name)) { $Name = 'cmd-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') }
$jobFile = Join-Path $queue ($Name + '.ps1')
$rcFile  = Join-Path $res   ($Name + '.rc')
$outFile = Join-Path $res   ($Name + '.out')
$errFile = Join-Path $res   ($Name + '.err')
Remove-Item $rcFile,$outFile,$errFile -Force -ErrorAction SilentlyContinue

# 带 BOM 写入（避免 VM/执行器侧中文乱码）
[IO.File]::WriteAllText($jobFile, $code, (New-Object Text.UTF8Encoding($true)))
if (-not $Quiet) { Write-Host ('· 已提交: ' + $Name) }

# ---- 轮询结果 ----
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$rc = $null
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 800
  if (Test-Path $rcFile) {
    $v = (Get-Content $rcFile -Raw -ErrorAction SilentlyContinue)
    if ($v -and $v.Trim() -ne 'RUNNING') { $rc = $v.Trim(); break }
  }
  # 若执行器不可用（长时间没人取走），提示
  if ((Get-Date) -gt $deadline) { break }
}

if ($null -eq $rc) {
  Write-Host ('✘ 超时（' + $TimeoutSec + 's）未拿到结果。可能管理员执行器未安装或未运行。')
  Write-Host '  安装：双击 tools\l3-lab-admin-setup.cmd'
  Write-Host '  状态：powershell -File tools\lab-admin-setup.ps1 -Status'
  exit 124
}

if (-not $Quiet) { Write-Host ('· 退出码: ' + $rc) }
if (Test-Path $outFile) { Get-Content $outFile -Encoding utf8 | ForEach-Object { Write-Output $_ } }
if (Test-Path $errFile) {
  $e = Get-Content $errFile -Encoding utf8
  if ($e) { Write-Host '--- stderr ---'; $e | ForEach-Object { Write-Host $_ } }
}

exit [int]$rc