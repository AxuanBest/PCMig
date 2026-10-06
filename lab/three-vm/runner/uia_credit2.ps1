
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$p = Get-Process PCMig -ErrorAction SilentlyContinue | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$w = $win.Current.BoundingRectangle
Write-Output ('窗口: top=' + [int]$w.Y + ' height=' + [int]$w.Height + ' bottom=' + [int]($w.Y+$w.Height) + ' width=' + [int]$w.Width)
function GetRect($e) { $b = $e.Current.BoundingRectangle; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height + ' bottom=' + [int]($b.Y+$b.Height)) }
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen) { continue }
  $tp = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
  $n = $e.Current.Name
  if ($tp -eq 'ProgressBar') { Write-Output ('  [细进度条] ' + (GetRect $e)) }
  if ($n -match 'Axuanbest') { Write-Output ('  [署名] ' + (GetRect $e) + '  ' + ($n -replace [char]13, ' / ')) }
  if ($n -eq '就绪') { Write-Output ('  [状态消息] ' + (GetRect $e)) }
  if ($n -eq '0 B / 0 B') { Write-Output ('  [数据量] ' + (GetRect $e)) }
  if ($tp -eq 'Button' -and $n -match '开始迁移') { Write-Output ('  [按钮] ' + (GetRect $e) + ' ' + $n) }
  if ($tp -eq 'ListItem' -and $n -match 'StepItem') { Write-Output ('  [导轨项] ' + (GetRect $e)) }
}
Write-Output 'MEASURE_DONE'
