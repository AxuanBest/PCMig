# UIA helper library for Recovery Gate driving (ASCII only). Dot-source it.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace RgNative -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
'@

$script:RgMouseLeftDown = 0x0002
$script:RgMouseLeftUp   = 0x0004

function Get-RgWindow {
    param([int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $p = Get-Process -Name 'PCMig.WinUI' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($p) {
            [RgNative.Mouse]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
            return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Get-RgAll {
    param($Root)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-RgAid {
    param($Root, [string]$Aid, [int]$TimeoutSec = 0)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
        $el = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        if ($TimeoutSec -gt 0) { Start-Sleep -Milliseconds 300 }
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Find-RgName {
    param($Root, [string]$Pattern, [int]$TimeoutSec = 5)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($e in (Get-RgAll $Root)) {
            if ($e.Current.Name -like $Pattern) { return $e }
        }
        if ($TimeoutSec -gt 0) { Start-Sleep -Milliseconds 300 }
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Get-RgRect {
    param($El)
    return $El.Current.BoundingRectangle
}

function Invoke-RgClick {
    param($El, [int]$Clicks = 1)
    if (-not $El) { throw 'Invoke-RgClick: element is null' }
    $r = $El.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2)
    $y = [int]($r.Y + $r.Height / 2)
    [RgNative.Mouse]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 90
    for ($i = 0; $i -lt $Clicks; $i++) {
        [RgNative.Mouse]::mouse_event($script:RgMouseLeftDown, 0, 0, 0, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [RgNative.Mouse]::mouse_event($script:RgMouseLeftUp, 0, 0, 0, [System.UIntPtr]::Zero)
        if ($i -lt $Clicks - 1) { Start-Sleep -Milliseconds 120 }
    }
    return @{ x = $x; y = $y }
}

function Set-RgText {
    param($El, [string]$Text)
    if (-not $El) { throw 'Set-RgText: element is null' }
    $vp = $null
    if ($El.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
        $vp.SetValue($Text)
        return 'VALUEPATTERN'
    }
    Invoke-RgClick $El | Out-Null
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait($Text)
    return 'SENDKEYS'
}

function Get-RgText {
    param($Root, [string]$Aid)
    $el = Find-RgAid $Root $Aid
    if (-not $el) { return $null }
    $vp = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
    return $el.Current.Name
}

function Get-RgAidList {
    param($Root)
    $out = @()
    foreach ($e in (Get-RgAll $Root)) {
        $aid = $e.Current.AutomationId
        if ($aid) { $out += $aid }
    }
    return $out
}

function Save-RgState {
    param($Root, [string]$Path)
    $lines = New-Object System.Collections.Generic.List[string]
    $all = Get-RgAll $Root
    $lines.Add("ELEMENTS $($all.Count)  at $(Get-Date -Format 'HH:mm:ss.fff')")
    foreach ($e in $all) {
        $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''
        $aid = $e.Current.AutomationId
        $nm = ($e.Current.Name -replace "`r?`n", ' / ')
        if ($nm.Length -gt 120) { $nm = $nm.Substring(0, 120) + '...' }
        $r = $e.Current.BoundingRectangle
        if ([double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Y) -or [double]::IsInfinity($r.Width)) {
            $lines.Add("EL type=$ct aid=$aid name=[$nm] OFFSCREEN enabled=$($e.Current.IsEnabled)")
            continue
        }
        if (-not $aid -and $ct -notin @('Button','ListItem','TreeItem','Edit')) { continue }
        $lines.Add("EL type=$ct aid=$aid name=[$nm] x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height) enabled=$($e.Current.IsEnabled)")
    }
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Add-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}