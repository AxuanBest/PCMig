# round3-proc-lib.ps1 - process freeze helpers for Round-3 PHASE G evidence.
# Requirement: a REAL "truth stops advancing" window (network stall analog).
# Suspend-Process is unavailable here, so we P/Invoke ntdll NtSuspendProcess / NtResumeProcess.
# ASCII only. UTF-8 with BOM not required (pure ASCII) but harmless.

$script:Rg3Nt = @'
[DllImport("ntdll.dll", SetLastError=true)]
public static extern uint NtSuspendProcess(System.IntPtr hProcess);
[DllImport("ntdll.dll", SetLastError=true)]
public static extern uint NtResumeProcess(System.IntPtr hProcess);
[DllImport("kernel32.dll", SetLastError=true)]
public static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32.dll", SetLastError=true)]
public static extern bool CloseHandle(System.IntPtr h);
'@

if (-not ('Rg3.Nt' -as [type])) {
    Add-Type -Namespace Rg3 -Name Nt -MemberDefinition $script:Rg3Nt
}

function Get-Rg3Descendant {
    # Return all descendant PIDs of $RootPid (including deep children).
    param([int]$RootPid)
    $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId, ParentProcessId, Name
    $out = New-Object System.Collections.Generic.List[object]
    $frontier = @($RootPid)
    $seen = @{}
    $seen[$RootPid] = $true
    while ($frontier.Count -gt 0) {
        $next = New-Object System.Collections.Generic.List[int]
        foreach ($p in $all) {
            if ($frontier -contains [int]$p.ParentProcessId -and -not $seen[[int]$p.ProcessId]) {
                $seen[[int]$p.ProcessId] = $true
                $out.Add($p)
                $next.Add([int]$p.ProcessId)
            }
        }
        $frontier = $next.ToArray()
    }
    return $out
}

function Get-Rg3WorkerPids {
    # PIDs of robocopy.exe under the PCMig tree. These are the byte movers:
    # freezing them stops target growth while the app itself keeps animating.
    param([int]$AppPid)
    return @(Get-Rg3Descendant -RootPid $AppPid | Where-Object { $_.Name -ieq 'robocopy.exe' } | ForEach-Object { [int]$_.ProcessId })
}

function Suspend-Rg3Process {
    param([int[]]$Id)
    $ok = 0
    foreach ($pid_ in $Id) {
        $h = [Rg3.Nt]::OpenProcess(0x0800, $false, $pid_)   # PROCESS_SUSPEND_RESUME
        if ($h -eq [System.IntPtr]::Zero) { continue }
        $st = [Rg3.Nt]::NtSuspendProcess($h)
        [Rg3.Nt]::CloseHandle($h) | Out-Null
        if ($st -eq 0) { $ok++ }
    }
    return $ok
}

function Resume-Rg3Process {
    param([int[]]$Id)
    $ok = 0
    foreach ($pid_ in $Id) {
        $h = [Rg3.Nt]::OpenProcess(0x0800, $false, $pid_)
        if ($h -eq [System.IntPtr]::Zero) { continue }
        $st = [Rg3.Nt]::NtResumeProcess($h)
        [Rg3.Nt]::CloseHandle($h) | Out-Null
        if ($st -eq 0) { $ok++ }
    }
    return $ok
}

function Set-Rg3Foreground {
    # The ONLY reliable way found on this machine to get a real WinUI 3 picture:
    # MinimizeAll() through the Shell.Application COM object, then un-minimize the target.
    # SetForegroundWindow / SetWindowPos(HWND_TOPMOST) / PrintWindow all silently yield
    # a stale or wrong window on this box (documented in the Round-3 handover).
    # NOTE: the parameter must NOT be named $Pid - "Pid" is a read-only automatic
    # variable in PowerShell (same trap family as $host). Using it makes every call
    # die with "Cannot overwrite variable Pid because it is read-only or constant."
    param([int]$ProcessId)
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p -or $p.MainWindowHandle -eq 0) { return $false }
    try {
        $shell = New-Object -ComObject Shell.Application
        $shell.MinimizeAll()
        Start-Sleep -Milliseconds 220
        $shell.UndoMinimizeAll()
        Start-Sleep -Milliseconds 420
    } catch {
        return $false
    }
    return $true
}

function Get-Rg3JobState {
    # Authoritative engine truth straight from the job archive (NOT the approximate UI text).
    # This is what proves a real stall: committedBytes / percent / phase must not move.
    param([string]$JobId = '')
    $d = Join-Path $env:ProgramData 'PCMig\Jobs'
    if (-not $JobId) {
        $latest = Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $latest) { return $null }
        $JobId = $latest.Name
    }
    $p = Join-Path (Join-Path $d $JobId) 'job-state.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    for ($i = 0; $i -lt 4; $i++) {
        try {
            $j = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
            return [pscustomobject]@{ Id = $JobId; Phase = $j.phase; Percent = $j.percent; Committed = $j.completedBytes; Failed = $j.failedCount; Pause = $j.pauseState }
        } catch { Start-Sleep -Milliseconds 80 }
    }
    return $null
}

function Get-Rg3TargetBytes {
    # Cheap target growth probe: sum of file sizes under $Root (non-recursive per dir is not enough,
    # so we use a real walk but keep it bounded by -MaxFiles).
    param([string]$Root, [int]$MaxFiles = 200)
    if (-not (Test-Path -LiteralPath $Root)) { return -1 }
    $f = Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First $MaxFiles
    $sum = 0L
    foreach ($x in $f) { $sum += $x.Length }
    return $sum
}