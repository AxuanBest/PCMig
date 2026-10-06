param(
    [string]$OutDir = 'E:\PCMigLab\Evidence\Trust-Critical-Recovery\recovery-gate\case04-verify',
    [string]$ClickAid = '',
    [int]$WaitAfterClickSec = 10
)
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$root = Get-RgWindow
if (-not $root) { throw 'NO-WINDOW' }

function Dump-Control([string]$label, [System.Windows.Automation.ControlType]$ct) {
    Write-Host "=== $label ==="
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $all) {
        $r = $b.Current.BoundingRectangle
        $off = ([double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Width))
        $geo = if ($off) { 'OFFSCREEN' } else { "x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height)" }
        Write-Host ("  {0} name=[{1}] aid=[{2}] enabled={3} {4}" -f $ct.ProgrammaticName.Replace('ControlType.',''), ($b.Current.Name -replace "`r?`n",' / '), $b.Current.AutomationId, $b.Current.IsEnabled, $geo)
    }
}

# 导航到第 4 步（结果与校验）
$nav = Find-RgAid $root 'Shell.Nav.Step4'
if (-not $nav) { throw 'NAV-STEP4-NOT-FOUND' }
Invoke-RgClick $nav | Out-Null
Start-Sleep -Seconds 3
Dump-Control 'STEP4-BUTTONS' ([System.Windows.Automation.ControlType]::Button)
Dump-Control 'STEP4-TEXTS'   ([System.Windows.Automation.ControlType]::Text)

if ($ClickAid) {
    $t = Find-RgAid $root $ClickAid
    if (-not $t) { throw "CLICK-AID-NOT-FOUND $ClickAid" }
    Write-Host ("--- clicking {0} (enabled={1}) ---" -f $ClickAid, $t.Current.IsEnabled)
    Invoke-RgClick $t | Out-Null
    Start-Sleep -Seconds $WaitAfterClickSec
    Dump-Control "AFTER-$ClickAid-BUTTONS" ([System.Windows.Automation.ControlType]::Button)
    Dump-Control "AFTER-$ClickAid-TEXTS"   ([System.Windows.Automation.ControlType]::Text)
    $list = Find-RgAid $root 'Shell.Nav.Step4'
    Write-Host "STEP4-STILL-PRESENT=$([bool]$list)"
}

Save-RgState -Root $root -Path (Join-Path $OutDir 'step4-state.txt')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'E:\PCMigLab\Staging\tc-batch5\shot-fixed.ps1' -Out (Join-Path $OutDir 'step4.png') | Write-Host
Write-Host 'CASE04-DUMP-DONE'