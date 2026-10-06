param(
    [string]$ShareMatch = 'PCMIG-RG',
    [switch]$SkipNav
)
# Select the given share in Step1, then navigate to Step2 and dump the page state.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'

$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Host 'NO-WINDOW'; exit 1 }

$target = $null
foreach ($e in (Get-RgAll $w)) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ListItem') { continue }
    $texts = @()
    foreach ($c in $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($c.Current.Name) { $texts += $c.Current.Name }
    }
    $joined = ($texts -join ' | ')
    if ($joined -like "*$ShareMatch*") { $target = $e; Write-Host ("FOUND-ITEM texts=[" + $joined + "]"); break }
}

if (-not $target) {
    Write-Host ("NO-ITEM-MATCHING " + $ShareMatch)
    foreach ($e in (Get-RgAll $w)) {
        if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.ListItem') { continue }
        $texts = @()
        foreach ($c in $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($c.Current.Name) { $texts += $c.Current.Name } }
        Write-Host ("  ITEM [" + ($texts -join ' | ') + "]")
    }
    exit 2
}

# scroll the item into view then click it
$si = $null
if ($target.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$si)) { $si.ScrollIntoView(); Start-Sleep -Milliseconds 400 }
Invoke-RgClick $target | Out-Null
Start-Sleep -Milliseconds 700
Write-Host ("AFTER-CLICK step1status=[" + (Get-RgText $w 'Step1.StatusText') + "]")
Save-RgState $w 'E:\PCMigLab\Staging\recovery-gate\state-step1-selected.txt'

if ($SkipNav) { Write-Host 'SKIP-NAV'; exit 0 }

$nav = Find-RgAid $w 'Shell.Nav.Step2'
Invoke-RgClick $nav | Out-Null
Start-Sleep -Seconds 3
Write-Host 'STEP2-DUMP-BEGIN'
foreach ($e in (Get-RgAll $w)) {
    $aid = $e.Current.AutomationId
    $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''
    $nm = ($e.Current.Name -replace "`r?`n", ' / ')
    $r = $e.Current.BoundingRectangle
    if ([double]::IsInfinity($r.X)) { continue }
    if ($aid -or $ct -in @('Button','CheckBox','Edit','TreeItem')) {
        if ($nm.Length -gt 80) { $nm = $nm.Substring(0, 80) + '...' }
        Write-Host ("  EL type=$ct aid=$aid name=[$nm] x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height) enabled=$($e.Current.IsEnabled)")
    }
}
Write-Host 'STEP2-DUMP-END'
Save-RgState $w 'E:\PCMigLab\Staging\recovery-gate\state-step2-initial.txt'