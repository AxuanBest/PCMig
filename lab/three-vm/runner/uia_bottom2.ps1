
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$p = Get-Process PCMig -ErrorAction SilentlyContinue | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$r = $win.Current.BoundingRectangle
Write-Output ('窗口高 ' + [int]$r.Height + '  底边 ' + [int]($r.Y + $r.Height))
$steps = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e }
}
$sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
foreach ($i in @(0,2)) {
  $steps[$i].GetCurrentPattern($sel).Select()
  Start-Sleep -Milliseconds 900
  Write-Output ('==== 导轨[' + $i + '] 底部 160px 内可见元素 ====')
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
    if ($e.Current.IsOffscreen) { continue }
    $b = $e.Current.BoundingRectangle
    if ([double]::IsInfinity($b.Y) -or $b.Y -lt ($r.Y + $r.Height - 160)) { continue }
    $tp = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($tp -ne 'Text' -and $tp -ne 'ProgressBar') { continue }
    Write-Output ('   ' + $tp.PadRight(12) + ' [' + $e.Current.Name + '] x=' + [string][int]$b.X + ' w=' + [string][int]$b.Width + ' h=' + [string][int]$b.Height)
  }
}
Write-Output 'BOTTOM_OK'
