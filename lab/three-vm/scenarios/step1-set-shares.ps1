param(
    [Parameter(Mandatory=$true)][string[]]$Include,
    [switch]$NavStep2
)
# Set Step1 share checkboxes so that exactly the -Include shares are checked.
# Uses TogglePattern (no mouse) so it is not affected by scroll/virtualization.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Host 'NO-WINDOW'; exit 1 }

$rows = @()
foreach ($e in (Get-RgAll $w)) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ListItem') { continue }
    $name = $null; $cb = $null
    foreach ($c in $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $ct = $c.Current.ControlType.ProgrammaticName
        if ($ct -eq 'ControlType.CheckBox' -and -not $cb) { $cb = $c }
        elseif ($ct -eq 'ControlType.Text' -and $c.Current.Name -and -not $name -and $c.Current.Name -notlike '\\*') { $name = $c.Current.Name }
    }
    if ($name) { $rows += [pscustomobject]@{ Name = $name; Check = $cb } }
}

foreach ($r in $rows) {
    $want = $Include -contains $r.Name
    if (-not $r.Check) { Write-Host ("ROW " + $r.Name + " NO-CHECKBOX"); continue }
    $tp = $null
    if (-not $r.Check.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { Write-Host ("ROW " + $r.Name + " NO-TOGGLE"); continue }
    $cur = $tp.Current.ToggleState.ToString()
    $isOn = ($cur -eq 'On')
    if ($isOn -ne $want) { $tp.Toggle(); Start-Sleep -Milliseconds 350; $cur2 = $tp.Current.ToggleState.ToString() } else { $cur2 = $cur }
    Write-Host ("ROW " + $r.Name + " was=" + $cur + " want=" + $want + " now=" + $cur2)
}

Start-Sleep -Milliseconds 500
Write-Host ("STEP1-STATUS=[" + (Get-RgText $w 'Step1.StatusText') + "]")

if ($NavStep2) {
    $nav = Find-RgAid $w 'Shell.Nav.Step2'
    Invoke-RgClick $nav | Out-Null
    Start-Sleep -Seconds 3
    Write-Host 'STEP2-AIDS-BEGIN'
    $aids = Get-RgAidList $w
    $aids | Sort-Object -Unique | ForEach-Object { Write-Host ("  AID $_") }
    Write-Host 'STEP2-AIDS-END'
    Save-RgState $w 'E:\PCMigLab\Staging\recovery-gate\state-step2-after-select.txt'
}