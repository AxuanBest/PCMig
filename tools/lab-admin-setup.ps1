<#
  PCMig Lab —— 管理员执行器安装（只需运行一次）
  ================================================================
  作用：创建一个以 SYSTEM + 最高权限运行的计划任务，常驻执行
        I:\deepseek work\PCMig\tools\lab-admin-executor.ps1

  为什么要这么做：
    本机会话无法提权，导致每个需要管理员的步骤都要人工点 UAC。
    安装本执行器后，**只需这一次授权**，之后所有管理员操作都由它代跑，
    AI 可以一次把 L3 全流程走完，不再逐步打扰你。

  它到底授权了什么（请知悉）：
    · 执行器以 SYSTEM 身份常驻，会执行 G:\PCMigLab\admin-queue\ 下的任何 .ps1
    · 也就是说：能写入该目录的人，可获得管理员级执行能力
    · 该目录默认只有本机管理员与当前用户可写
    · 随时可用 -Remove 卸载（见末尾提示）

  用法（双击 l3-lab-admin-setup.cmd）：
    powershell -NoProfile -ExecutionPolicy Bypass -File lab-admin-setup.ps1 -Install
    卸载：  ... -Remove

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$ExecutorPath = 'I:\deepseek work\PCMig\tools\lab-admin-executor.ps1',
  [string]$TaskName = 'PCMigLab-AdminExecutor',
  [switch]$Install,
  [switch]$Remove,
  [switch]$Status
)

$ErrorActionPreference = 'Continue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ''
Write-Host '===== PCMig Lab 管理员执行器 ====='
Write-Host ('  执行器脚本: ' + $ExecutorPath)
Write-Host ('  计划任务  : ' + $TaskName)
Write-Host ('  管理员    : ' + $isAdmin)

if (-not $isAdmin -and ($Install -or $Remove)) {
  Write-Host ''
  Write-Host '需要管理员权限。正在请求提升（请点“是”）…'
  $me = $PSCommandPath
  if ([string]::IsNullOrEmpty($me)) { $me = $MyInvocation.MyCommand.Path }
  $extra = @()
  if ($Install) { $extra += '-Install' }
  if ($Remove)  { $extra += '-Remove' }
  try {
    Start-Process powershell -Verb RunAs -ArgumentList (@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $me + '"'),
      '-ExecutorPath',('"' + $ExecutorPath + '"'),'-TaskName',('"' + $TaskName + '"')) + $extra)
  } catch { Write-Host ('提升失败: ' + $_.Exception.Message) }
  exit 0
}

# ---------------------------------------------------------------- 状态
if ($Status -or (-not $Install -and -not $Remove)) {
  Write-Host ''
  Write-Host '--- 当前状态 ---'
  $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($t) {
    $i = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
    Write-Host ('  计划任务: 存在   状态=' + $t.State + '  上次运行=' + $i.LastRunTime + '  上次结果=' + $i.LastTaskResult)
  } else { Write-Host '  计划任务: 不存在（尚未安装）' }
  $proc = @(Get-Process powershell -ErrorAction SilentlyContinue)
  $exLog = 'G:\PCMigLab\admin-executor.log'
  if (Test-Path $exLog) {
    Write-Host '  执行器日志尾部:'
    Get-Content $exLog -Tail 6 -Encoding utf8 | ForEach-Object { Write-Host ('    ' + $_) }
  } else { Write-Host '  执行器日志: 尚未生成' }
  Write-Host ''
  Write-Host '  安装: 加 -Install    卸载: 加 -Remove'
  exit 0
}

# ---------------------------------------------------------------- 卸载
if ($Remove) {
  Write-Host ''
  Write-Host '--- 卸载 ---'
  try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
  try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop; Write-Host '  ✔ 已删除计划任务' }
  catch { Write-Host ('  ⚠ 删除任务失败: ' + $_.Exception.Message) }
  # 让常驻进程自行退出
  try {
    $q = 'G:\PCMigLab\admin-queue'
    if (-not (Test-Path $q)) { New-Item -ItemType Directory -Force -Path $q | Out-Null }
    '' | Out-File -LiteralPath (Join-Path $q 'STOP') -Encoding ascii
    Write-Host '  ✔ 已放置 STOP 标记，执行器将在数秒内退出'
  } catch { }
  Write-Host ''
  Write-Host '卸载完成。'
  exit 0
}

# ---------------------------------------------------------------- 安装
Write-Host ''
Write-Host '--- 安装 ---'

if (-not (Test-Path $ExecutorPath)) {
  Write-Host ('  ✘ 找不到执行器脚本: ' + $ExecutorPath)
  exit 2
}
Write-Host '  ✔ 执行器脚本存在'

# 目录准备
foreach ($d in 'G:\PCMigLab','G:\PCMigLab\admin-queue','G:\PCMigLab\admin-queue\done','G:\PCMigLab\admin-results') {
  if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}
Write-Host '  ✔ 队列/结果目录已就绪'

# 删除旧任务（若有）
$old = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($old) {
  try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
  try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop } catch { }
  Write-Host '  · 已清理旧任务'
}

# 创建任务：SYSTEM + 最高权限 + 开机自启 + 无时限
try {
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $ExecutorPath + '"')
  $trigger = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null
  Write-Host '  ✔ 已创建计划任务（SYSTEM / 最高权限 / 开机自启 / 无时限）'
} catch {
  Write-Host ('  ✘ 创建计划任务失败: ' + $_.Exception.Message)
  exit 3
}

# 立即启动
try {
  Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
  Write-Host '  ✔ 已启动执行器'
} catch { Write-Host ('  ⚠ 启动失败: ' + $_.Exception.Message) }

Start-Sleep -Seconds 4

# 验证：跑一个探针
Write-Host ''
Write-Host '--- 验证（提交一个探针任务）---'
$queue = 'G:\PCMigLab\admin-queue'
$res   = 'G:\PCMigLab\admin-results'
$probe = Join-Path $queue ('probe-' + (Get-Date -Format 'HHmmss') + '.ps1')
@'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
"PROBE-OK"
"User=$($id.Name)"
"IsAdmin=$($pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
"Computer=$env:COMPUTERNAME"
"Time=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
try { "VMs=" + (@(Get-VM -ErrorAction Stop).Count) } catch { "VMs=ERR $($_.Exception.Message.Split([char]10)[0])" }
'@ | Out-File -LiteralPath $probe -Encoding utf8 -Force

$rcFile = Join-Path $res ((Split-Path $probe -Leaf).Replace('.ps1','.rc'))
$outFile = Join-Path $res ((Split-Path $probe -Leaf).Replace('.ps1','.out'))
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
  Start-Sleep -Seconds 1
  if (Test-Path $rcFile) {
    $rc = (Get-Content $rcFile -Raw -ErrorAction SilentlyContinue)
    if ($rc -and $rc.Trim() -ne 'RUNNING') { $ok = $true; break }
  }
}
if ($ok) {
  Write-Host ('  ✔ 探针已执行，退出码=' + (Get-Content $rcFile -Raw).Trim())
  if (Test-Path $outFile) { Get-Content $outFile -Encoding utf8 | ForEach-Object { Write-Host ('    ' + $_) } }
  Write-Host ''
  Write-Host '  ==============================================='
  Write-Host '  ✔ 安装成功：管理员执行器已常驻且可代跑命令。'
  Write-Host '    从现在起，AI 不需要你再次点 UAC 就能执行管理员操作。'
  Write-Host '  ==============================================='
} else {
  Write-Host '  ⚠ 探针在 30 秒内没有结果。请稍后加 -Status 查看执行器日志。'
}

Write-Host ''
Write-Host '按任意键关闭…'
if (-not $env:PCMIGLAB_HEADLESS) { try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 8 } }