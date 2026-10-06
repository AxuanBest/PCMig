
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$p = Get-Process PCMig -ErrorAction SilentlyContinue | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$w = $win.Current.BoundingRectangle
Write-Output ('窗口: top=' + [int]$w.Y + ' height=' + [int]$w.Height + ' bottom=' + [int]($w.Y+$w.Height) + ' width=' + [int]$w.Width)
function R($e) { $b = $e.Current.BoundingRectangle; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen) { continue }
  $tp = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
  $n = $e.Current.Name
  if ($tp -eq 'ProgressBar') { Write-Output ('  [细进度条] ' + (R $e)) }
  if ($n -match 'Axuanbest') { Write-Output ('  [署名] ' + (R $e) + '  ' + ($n -replace [char]13, ' / ')) }
  if ($n -eq '就绪') { Write-Output ('  [状态消息] ' + (R $e)) }
  if ($n -eq '0 B / 0 B') { Write-Output ('  [数据量] ' + (R $e)) }
  if ($tp -eq 'Button' -and $n -match '开始迁移') { Write-Output ('  [按钮] ' + (R $e) + ' ' + $n) }
  if ($tp -eq 'ListItem') { Write-Output ('  [导轨项] ' + (R $e) + ' ' + ($n.Substring(0, [Math]::Min(20, $n.Length)))) }
}
Write-Output 'MEASURE_DONE'
