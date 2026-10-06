
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$p = Get-Process PCMig -ErrorAction SilentlyContinue | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$steps = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e }
}
$sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
foreach ($i in @(0,1,2,3)) {
  $steps[$i].GetCurrentPattern($sel).Select()
  Start-Sleep -Milliseconds 800
  Write-Output ('==== 导轨[' + $i + '] 底部栏可见元素 ====')
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
    if ($e.Current.IsOffscreen) { continue }
    $b = $e.Current.BoundingRectangle
    if ($b.Y -lt 1300 -or $b.Y -gt 100000) { continue }
    $tp = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($tp -ne 'Text' -and $tp -ne 'ProgressBar') { continue }
    $x = if ([double]::IsInfinity($b.X)) { '∞' } else { [string][int]$b.X }
    $w = if ([double]::IsInfinity($b.Width)) { '∞' } else { [string][int]$b.Width }
    Write-Output ('   ' + $tp + ' [' + $e.Current.Name + '] x=' + $x + ' w=' + $w)
  }
}
Write-Output 'BOTTOM_OK'
