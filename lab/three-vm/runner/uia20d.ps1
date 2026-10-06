
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
function Rect($e) { $b = $e.Current.BoundingRectangle; if ($b.X -eq [double]::PositiveInfinity) { return 'x=∞' }; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) { if ($w.Current.Name -match 'PCMig 迁移工具') { $win = $w; break } }
Write-Output ('窗口 ' + (Rect $win))
Write-Output '--- 左栏面板区（x 250..760, y 900..1500）---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity -or $b.X -lt 0) { continue }
  if ([int]$b.X -ge 250 -and [int]$b.X -le 760 -and [int]$b.Y -ge 900 -and [int]$b.Y -le 1500) {
    $txt = $e.Current.Name
    if ([string]::IsNullOrWhiteSpace($txt)) { $txt = '<' + $e.Current.ControlType.ProgrammaticName + '>' }
    Write-Output ('  ' + (Rect $e) + ' ' + $e.Current.ControlType.ProgrammaticName + ' :: ' + ($txt -replace [char]13, ' / '))
  }
}
Write-Output 'UIA20D_DONE'
