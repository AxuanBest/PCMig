param(
    [Parameter(Mandatory=$true)][string]$Target,
    [switch]$Prepare
)
# Inspect the Step2 directory tree selection, set the target root, optionally click Prepare.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Host 'NO-WINDOW'; exit 1 }

Write-Host 'TREE-ROWS-BEGIN'
foreach ($e in (Get-RgAll $w)) {
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.TreeItem') { continue }
    $texts = @(); $cb = $null
    foreach ($c in $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($c.Current.ControlType.ProgrammaticName -eq 'ControlType.CheckBox' -and -not $cb) { $cb = $c }
        if ($c.Current.Name) { $texts += $c.Current.Name }
    }
    $tog = ''
    if ($cb) { $tp = $null; if ($cb.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { $tog = $tp.Current.ToggleState.ToString() } }
    Write-Host ("  ROW [" + ($texts -join ' | ') + "] check=" + $tog)
}
Write-Host 'TREE-ROWS-END'

$box = Find-RgAid $w 'TargetRootBox'
$mode = Set-RgText $box $Target
Write-Host ("SET-TARGET=" + $Target + " via " + $mode)

if ($Prepare) {
    Start-Sleep -Milliseconds 400
    $prep = Find-RgAid $w 'Step2.Prepare'
    Write-Host ("PREPARE-ENABLED=" + $prep.Current.IsEnabled)
    Invoke-RgClick $prep | Out-Null
    # wait for the plan to be produced (Start becomes enabled) or an error message
    $deadline = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 2
        $startBtn = Find-RgAid $w 'Step2.Start'
        $stateMsg = Get-RgText $w 'StateMessageText'
    } while ($startBtn -and -not $startBtn.Current.IsEnabled -and (Get-Date) -lt $deadline -and $stateMsg -notlike '*失败*' -and $stateMsg -notlike '*未通过*')
    Write-Host ("AFTER-PREPARE start-enabled=" + $startBtn.Current.IsEnabled)
    Write-Host ("AFTER-PREPARE state=[" + $stateMsg + "]")
    Write-Host ("AFTER-PREPARE stateLine=[" + (Get-RgText $w 'StateLineText') + "]")
}

Save-RgState $w 'E:\PCMigLab\Staging\recovery-gate\state-step2-prepared.txt'