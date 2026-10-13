<#
.SYNOPSIS
  ★ P1-10 ★ 启动/安装分段计时器：把"安装器双击→欢迎页""首启慢"变成可复验的毫秒表。

.DESCRIPTION
  两种模式：

    -Mode app    启动应用 exe，量「进程启动 → 主窗口出现」毫秒，并读取应用自己写的
                 startup-timeline.jsonl（距进程创建基线 + 各段毫秒 + phase-summary 汇总行）。

    -Mode setup  启动安装器，量「进程启动 → 安装器窗口出现」毫秒，并解析 /LOG 里的
                 PCMIG-STAGE 分段（需要用 ISCC /DSetupStageLog=1 编译的候选包），
                 **量到欢迎页就立即结束进程**：只到欢迎页，绝不执行安装。

  隔离纪律（本脚本存在的理由）：
    每次运行都用独立的 PCMIG_JOBS / PCMIG_STARTUP_DIR / PCMIG_LOG_DIR 临时目录，绝不往
    真实用户的 %ProgramData%\PCMig（任务、日志）里写任何东西。

  冷/热口径（如实写在输出里）：
    冷 = 该次运行前删掉本次隔离目录（应用自身状态是冷的）；热 = 紧接着连续启动。
    ★ 未清 OS 待机缓存（属内核级操作），因此这里的"冷"只代表**应用级冷启动**。★

.EXAMPLE
  .\tools\startup-timing.ps1 -Mode app -Path .\dist\app\PCMig.WinUI.exe -Runs 3 -OutDir $env:TEMP\p110-app

.EXAMPLE
  .\tools\startup-timing.ps1 -Mode setup -Path $env:TEMP\candidate\PCMigSetup-0.5.3-p110candidate.exe -Runs 3 -OutDir $env:TEMP\p110-setup
#>
[CmdletBinding()]
param(
    [ValidateSet('app', 'setup')]
    [string] $Mode = 'app',

    [Parameter(Mandatory = $true)]
    [string] $Path,

    [int] $Runs = 3,

    [string] $OutDir = (Join-Path $env:TEMP ('pcmig-startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),

    [int] $WindowTimeoutMs = 15000,

    [int] $TimelineTimeoutMs = 10000,

    # 每一轮都按"冷"处理（每轮前清空该轮隔离目录）。用于凑够"冷启动 ≥3 次"的口径。
    [switch] $AllCold
)

$ErrorActionPreference = 'Stop'
$Path = (Resolve-Path -LiteralPath $Path).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Save-Env([string]$name, [string]$value)
{
    $script:envBackup[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    if ($null -eq $value) { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
    else { Set-Item "env:$name" $value }
}

function Restore-Env
{
    foreach ($k in $script:envBackup.Keys) { Set-Item "env:$k" $script:envBackup[$k] }
}

$script:envBackup = @{}

function Invoke-TimedLaunch
{
    param([int]$Index, [string]$Kind, [string]$IsolatedRoot)

    New-Item -ItemType Directory -Force -Path $IsolatedRoot | Out-Null
    $jobs = Join-Path $IsolatedRoot 'jobs'
    $timeline = Join-Path $IsolatedRoot 'timeline'
    $logs = Join-Path $IsolatedRoot 'logs'
    New-Item -ItemType Directory -Force -Path $jobs, $timeline, $logs | Out-Null

    Save-Env 'PCMIG_JOBS' $jobs
    Save-Env 'PCMIG_STARTUP_DIR' $timeline
    Save-Env 'PCMIG_LOG_DIR' $logs

    $result = [ordered]@{
        Run            = $Index
        Kind           = $Kind
        Mode           = $Mode
        StartedUtc     = [DateTime]::UtcNow.ToString('o')
        ProcessStartLocal = $null
        WindowVisibleMs = $null
        ProcessToWelcomeMs = $null
        ExitedEarly    = $false
        ExitCode       = $null
        Timeline       = @()
        Summary        = $null
        StageLog       = @()
        Stages         = @()
        WindowProcess  = $null
        KilledProcesses = @()
    }

    $args = @()
    if ($Mode -eq 'setup')
    {
        $logFile = Join-Path $IsolatedRoot 'setup-stage.log'
        $args = @("/LOG=`"$logFile`"", '/NORESTART', '/SP-')
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    # ★ 2026-10-13 修复 ★ Windows PowerShell 5.1 下 `Start-Process -ArgumentList @()`（空集合）
    #   会抛 ParameterArgumentValidationError（"argument is null, empty…"），app 模式因此完全跑不起来。
    #   只有确实带了参数时才传 -ArgumentList。
    $proc = if ($args.Count -gt 0) { Start-Process -FilePath $Path -ArgumentList $args -PassThru }
            else { Start-Process -FilePath $Path -PassThru }
    $sw.Start()
    try { $result.ProcessStartLocal = $proc.StartTime.ToString('yyyy-MM-dd HH:mm:ss.fff') } catch { $result.ProcessStartLocal = $null }

    while ($sw.ElapsedMilliseconds -lt $WindowTimeoutMs)
    {
        if ($proc.HasExited -and $Mode -eq 'app') { $result.ExitedEarly = $true; break }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { $result.WindowVisibleMs = $sw.ElapsedMilliseconds; break }

        # ★ Inno 安装器会把自己解压成 %TEMP%\...\XXX.tmp 再运行**另一个进程**，
        #   所以"安装器窗口出现"要按窗口标题在同类进程里找，不能只看 Start-Process 拿到的那个 pid。
        if ($Mode -eq 'setup')
        {
            $ui = @(Get-Process -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowTitle -like '*PCMig*' -and $_.MainWindowHandle -ne 0 })
            if ($ui.Count -gt 0)
            {
                $result.WindowProcess = "$($ui[0].Id)/$($ui[0].ProcessName)"
                $result.WindowVisibleMs = $sw.ElapsedMilliseconds
                break
            }
        }
        Start-Sleep -Milliseconds 5
    }
    $sw.Stop()

    # 应用模式：等应用把 startup-timeline.jsonl 写全（有 phase-summary 即可）
    if ($Mode -eq 'app')
    {
        $tl = Join-Path $timeline 'startup-timeline.jsonl'
        $deadline = (Get-Date).AddMilliseconds($TimelineTimeoutMs)
        while ((Get-Date) -lt $deadline)
        {
            if (Test-Path -LiteralPath $tl)
            {
                $lines = @(Get-Content -LiteralPath $tl -ErrorAction SilentlyContinue)
                if ($lines | Where-Object { $_ -like '*phase-summary*' }) { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if (Test-Path -LiteralPath $tl)
        {
            $parsed = @()
            foreach ($line in (Get-Content -LiteralPath $tl -ErrorAction SilentlyContinue))
            {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                try { $parsed += ($line | ConvertFrom-Json) } catch { }
            }
            $result.Timeline = @($parsed | ForEach-Object {
                [pscustomobject]@{
                    Phase              = $_.Phase
                    Detail             = $_.Detail
                    MsSinceProcessStart = $_.MsSinceProcessStart
                    MsSinceBegin       = $_.MsSinceBegin
                }
            })
            $summary = @($parsed | Where-Object { $_.Phase -eq 'phase-summary' } | Select-Object -Last 1)
            if ($summary.Count -gt 0)
            {
                $result.Summary = [pscustomobject]@{
                    Count             = $summary[0].Count
                    TotalMs           = $summary[0].TotalMs
                    FirstInteractiveMs = $summary[0].FirstInteractiveMs
                    Stages            = @($summary[0].Stages)
                }
            }
        }
    }

    # 安装器模式：从 /LOG 里读 PCMIG-STAGE 分段
    if ($Mode -eq 'setup')
    {
        $logFile = Join-Path $IsolatedRoot 'setup-stage.log'
        for ($try = 0; $try -lt 40; $try++)
        {
            if ((Test-Path -LiteralPath $logFile) -and
                (Select-String -LiteralPath $logFile -Pattern 'PCMIG-STAGE page-1-rendered' -Quiet))
            { break }
            Start-Sleep -Milliseconds 50
        }
        if (Test-Path -LiteralPath $logFile)
        {
            $result.StageLog = @(Select-String -LiteralPath $logFile -Pattern 'PCMIG-STAGE' |
                ForEach-Object { $_.Line.Trim() })

            # ★ 关键口径 ★ 用 Inno 日志自己的毫秒时间戳 减去 进程创建时刻，
            #   得到"双击 → 欢迎页"的真实毫秒（比外部猜窗口句柄可靠得多）。
            $startTime = $null
            if ($result.ProcessStartLocal) { $startTime = [datetime]::ParseExact($result.ProcessStartLocal, 'yyyy-MM-dd HH:mm:ss.fff', $null) }
            $stages = @()
            foreach ($line in $result.StageLog)
            {
                if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(PCMIG-STAGE .*)$')
                {
                    $at = [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
                    $stages += [pscustomobject]@{
                        AtLocal            = $matches[1]
                        Stage              = $matches[2]
                        MsFromProcessStart = if ($startTime) { [math]::Round(($at - $startTime).TotalMilliseconds, 1) } else { $null }
                    }
                }
            }
            $result.Stages = $stages
            $welcome = @($stages | Where-Object { $_.Stage -like 'PCMIG-STAGE page-1-rendered*' } | Select-Object -First 1)
            if ($welcome.Count -gt 0) { $result.ProcessToWelcomeMs = $welcome[0].MsFromProcessStart }
        }
        $result.ExitCode = $null
    }

    # 量完就结束进程：安装器只到欢迎页，绝不进入安装。
    # ★ 必须按**进程名模式**杀，不能只杀 Start-Process 拿到的那个 pid ——
    #   Inno 会另起一个 %TEMP%\...\XXX.tmp 子进程，那才是真正显示向导的进程；
    #   只杀外层会把一个停在欢迎页的安装器留在机器上（实测踩到过 3 个）。
    $killed = @()
    $patterns = if ($Mode -eq 'setup') { @('PCMigSetup*') } else { @() }
    foreach ($pattern in $patterns)
    {
        foreach ($p in @(Get-Process -Name $pattern -ErrorAction SilentlyContinue))
        {
            $killed += "$($p.Id)/$($p.ProcessName)"
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $proc.HasExited)
    {
        $killed += "$($proc.Id)/$($proc.ProcessName)"
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $null = $proc.WaitForExit(10000)
    }
    $result.KilledProcesses = @($killed | Select-Object -Unique)
    if ($proc.HasExited) { $result.ExitCode = $proc.ExitCode }
    Restore-Env

    return [pscustomobject]$result
}

$all = @()
for ($i = 1; $i -le $Runs; $i++)
{
    $kind = if ($AllCold -or $i -eq 1) { 'cold(应用级)' } else { 'warm' }
    $root = Join-Path $OutDir ("run$i-" + $stamp)
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    if ($i -gt 1) { Start-Sleep -Milliseconds 800 }   # 让上一个进程彻底退出，避免互相干扰

    Write-Host ("[{0}/{1}] {2} …" -f $i, $Runs, $kind) -NoNewline
    $r = Invoke-TimedLaunch -Index $i -Kind $kind -IsolatedRoot $root
    $all += $r
    Write-Host (" 窗口 {0} ms{1}" -f $r.WindowVisibleMs, $(if ($r.Summary) { " / 首可交互 " + $r.Summary.FirstInteractiveMs + " ms" } elseif ($r.ProcessToWelcomeMs) { " / 双击→欢迎页 " + $r.ProcessToWelcomeMs + " ms" } else { '' }))
}

# 全局兜底：任何还活着的安装器一律结束（绝不允许留下停在欢迎页的安装器）。
$leftover = @(Get-Process -Name 'PCMigSetup*' -ErrorAction SilentlyContinue)
foreach ($p in $leftover)
{
    Write-Host ("兜底结束残留安装器进程：" + $p.Id + "/" + $p.ProcessName)
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

$jsonPath = Join-Path $OutDir "startup-timing-$Mode-$stamp.json"
$csvPath = Join-Path $OutDir "startup-timing-$Mode-$stamp.csv"
$all | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$rows = foreach ($r in $all)
{
    $perPhase = ($r.Timeline | ForEach-Object { "$($_.Phase)=$($_.MsSinceProcessStart)" }) -join ' '
    [pscustomobject]@{
        轮次            = $r.Run
        类型            = $r.Kind
        '进程→窗口(ms)' = $r.WindowVisibleMs
        '双击→欢迎页(ms)' = $r.ProcessToWelcomeMs
        '进程→首可交互(ms)' = if ($r.Summary) { $r.Summary.FirstInteractiveMs } else { $null }
        '距进程创建最后一段(ms)' = if ($r.Summary) { $r.Summary.TotalMs } else { $null }
        分段            = $perPhase
        安装器分段      = ($r.StageLog -join ' | ')
    }
}
$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

Write-Host ''
$rows | Format-Table -AutoSize
Write-Host "原始数据：$jsonPath"
Write-Host "表格：    $csvPath"
Write-Host ''
Write-Host '冷/热口径：冷 = 本次运行前删掉本次隔离目录（应用级冷）；热 = 紧接着连续启动。'
Write-Host '未清 OS 待机缓存（内核级操作），因此"冷"仅代表应用级冷启动。'
$all