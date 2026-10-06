
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
function Rect($e) { $b = $e.Current.BoundingRectangle; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
$jb = $combos[$combos.Count - 1]
Write-Output ('已有任务下拉: ' + (Rect $jb))
# 展开看选项
try { $jb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 700 } catch { Write-Output '  展开失败' }
$opts = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ListItem))) {
  if ($e.Current.Name -match 'JOB-') { $opts += $e.Current.Name }
}
Write-Output ('  可见任务项 ' + $opts.Count + ' 个:')
$opts | Select-Object -First 6 | ForEach-Object { Write-Output ('    · ' + $_) }
try { $jb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 300 } catch { }
$jb.SetFocus(); Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
Start-Sleep -Seconds 6
Write-Output '--- 左栏报错面板 ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 340 -and [int]$b.Y -gt 1100 -and $e.Current.Name -notmatch 'StepItem') {
    Write-Output ('  ' + (Rect $e) + ' :: ' + ($e.Current.Name -replace [char]13, ' / '))
  }
}
$n = 0
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 340 -and [int]$b.Y -gt 1180 -and [int]$b.Y -lt 1300 -and $n -lt 8 -and $e.Current.Name.Length -gt 3) {
    Write-Output ('  [报错行] ' + (Rect $e) + ' :: ' + ($e.Current.Name -replace [char]13, ' / '))
    $n++
  }
}
Write-Output '--- 结果页按钮 ---'
$items[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen) { continue }
  if ($e.Current.Name -match '^⚠ 尝试修复$|^✔ 验证完整性$|^↻ 恢复任务$') {
    Write-Output ('  ' + (Rect $e) + ' enabled=' + $e.Current.IsEnabled + ' :: ' + $e.Current.Name)
  }
}
Write-Output 'UIA19C_DONE'
