<#
  PCMig Lab —— 管理员执行器（由计划任务以 SYSTEM 最高权限运行）
  =========================================================================
  为什么需要它：
    本 DSH 会话的 shell 是 Medium 完整性级别，无法提权；
    而 L3 的绝大多数操作（Hyper-V / VHD / 计划任务 / bcdboot / dism）都需要管理员。
    每次都要用户点 UAC，等于把自动化推回给人。

  它做什么：
    以 SYSTEM + 最高权限常驻，持续扫描队列目录里提交的 .ps1，
    执行之，并把 stdout/stderr/退出码写到结果目录。

  目录约定（都在 G:\PCMigLab 下）：
    admin-queue\       待执行脚本（*.ps1）
    admin-queue\done\  已执行完的归档
    admin-results\     结果（<同名>.out / <同名>.rc / <同名>.err）
                       .rc 取值：RUNNING / 退出码 / 9001(执行异常) / 9002(超时强杀)
    admin-executor.log 执行器自身日志

  关键健壮性设计（v2，2026-09-19）：
    1. 启动子进程前注入 $env:PCMIGLAB_HEADLESS=1 —— 子脚本据此跳过"按任意键"暂停。
       （v1 教训：子脚本的 ReadKey 在无人值守下永久阻塞，把执行器一起挂死。）
    2. 每个作业有超时（默认 1800s），超时强制终止并记 9002，执行器不会永久卡住。
    3. STOP 标记可让执行器优雅退出。

  ⚠ 必须 UTF-8 带 BOM。
#>

param(
  [int]$JobTimeoutSec = 1800,
  [int]$PollSeconds   = 2
)

$ErrorActionPreference = 'Continue'

$base     = 'G:\PCMigLab'
$queue    = Join-Path $base 'admin-queue'
$doneDir  = Join-Path $queue 'done'
$resDir   = Join-Path $base 'admin-results'
$selfLog  = Join-Path $base 'admin-executor.log'
$stopFlag = Join-Path $queue 'STOP'

foreach ($d in @($base, $queue, $doneDir, $resDir)) {
  if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}

function Log([string]$m) {
  try {
    ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $m) |
      Out-File -LiteralPath $selfLog -Append -Encoding utf8
  } catch { }
}

Log ('=== 执行器启动(v2)  PID=' + $PID + '  User=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name + '  JobTimeout=' + $JobTimeoutSec + 's ===')
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log ('IsAdmin=' + $isAdmin)

# 让子脚本知道处于无人值守环境（关键：避免 ReadKey 阻塞）
$env:PCMIGLAB_HEADLESS = '1'

while ($true) {
  if (Test-Path $stopFlag) {
    Log 'STOP 标记出现，退出'
    Remove-Item $stopFlag -Force -ErrorAction SilentlyContinue
    break
  }

  $jobs = @()
  try {
    $jobs = @(Get-ChildItem -LiteralPath $queue -Filter '*.ps1' -File -ErrorAction SilentlyContinue | Sort-Object Name)
  } catch { }

  if ($jobs.Count -eq 0) { Start-Sleep -Seconds $PollSeconds; continue }

  foreach ($j in $jobs) {
    $name    = $j.BaseName
    $outFile = Join-Path $resDir ($name + '.out')
    $rcFile  = Join-Path $resDir ($name + '.rc')
    $errFile = Join-Path $resDir ($name + '.err')

    Log ('执行: ' + $j.Name)
    'RUNNING' | Out-File -LiteralPath $rcFile -Encoding ascii

    $proc = $null
    try {
      $argLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $j.FullName + '"'
      $proc = Start-Process -FilePath 'powershell.exe' `
                -ArgumentList $argLine `
                -PassThru -NoNewWindow `
                -RedirectStandardOutput $outFile -RedirectStandardError $errFile

      $finished = $proc.WaitForExit($JobTimeoutSec * 1000)
      if (-not $finished) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        '9002' | Out-File -LiteralPath $rcFile -Encoding ascii
        Log ('超时 ' + $JobTimeoutSec + 's，已强制终止: ' + $j.Name)
      } else {
        ([string]$proc.ExitCode) | Out-File -LiteralPath $rcFile -Encoding ascii
        Log ('完成: ' + $j.Name + '  exit=' + $proc.ExitCode)
      }
    } catch {
      ('EXEC-ERROR: ' + $_.Exception.Message) | Out-File -LiteralPath $errFile -Append -Encoding utf8
      '9001' | Out-File -LiteralPath $rcFile -Encoding ascii
      Log ('执行异常: ' + $j.Name + ' : ' + $_.Exception.Message)
    }

    try {
      Move-Item -LiteralPath $j.FullName -Destination (Join-Path $doneDir $j.Name) -Force -ErrorAction SilentlyContinue
    } catch {
      try { Remove-Item -LiteralPath $j.FullName -Force -ErrorAction SilentlyContinue } catch { }
    }
  }

  Start-Sleep -Seconds $PollSeconds
}

Log '=== 执行器退出 ==='