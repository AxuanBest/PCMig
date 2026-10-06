
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$p = Get-Process PCMig -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output 'GUI 未运行'; exit 1 }
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$steps = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e }
}
$sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
$steps[1].GetCurrentPattern($sel).Select()
Start-Sleep -Milliseconds 800
$combos = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox') { $combos += $e }
}
Write-Output ('ComboBox 数: ' + $combos.Count)
$combo = $combos[$combos.Count - 1]
$b = $combo.Current.BoundingRectangle
Write-Output ('已有任务下拉 @x=' + [int]$b.X + ' y=' + [int]$b.Y)
$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 1200
$items = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and -not $e.Current.IsOffscreen) { $items += $e }
}
Write-Output ('候选 ' + $items.Count + ' 个:')
foreach ($it in $items) { Write-Output ('   - ' + $it.Current.Name) }
$target = $items[0]
foreach ($it in $items) { if ($it.Current.Name -match '7fc7') { $target = $it } }
Write-Output ('选中: ' + $target.Current.Name)
$target.GetCurrentPattern($sel).Select()
Start-Sleep -Seconds 4
$steps[2].GetCurrentPattern($sel).Select()
Start-Sleep -Milliseconds 900
Write-Output '---- 迁移页文本 ----'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen -or $e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
  $n = $e.Current.Name
  if ($n) { Write-Output ('   [' + [int]$e.Current.BoundingRectangle.X + ',' + [int]$e.Current.BoundingRectangle.Y + '] ' + $n) }
}
Write-Output 'PREVIEW_DONE'
