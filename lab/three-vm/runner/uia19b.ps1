
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

# 第 2 页（选择数据与目标）里有"已有任务"下拉
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
Write-Output ('第2页 ComboBox 数=' + $combos.Count)
for ($i = 0; $i -lt $combos.Count; $i++) { Write-Output ('  [' + $i + '] ' + (Rect $combos[$i]) + ' name=' + $combos[$i].Current.Name) }
$target = $null
foreach ($c in $combos) { if ($c.Current.Name -match '任务|JOB' -or $c.Current.BoundingRectangle.Width -gt 400) { $target = $c; break } }
if ($target -eq $null -and $combos.Count -gt 0) { $target = $combos[0] }
if ($target -ne $null) {
  Write-Output ('  选中下拉: ' + (Rect $target) + ' name=' + $target.Current.Name)
  $target.SetFocus()
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
  Start-Sleep -Seconds 5
  Write-Output '  已按 DOWN 选中一个已有任务'
} else { Write-Output '  未找到可用下拉' }

Write-Output '--- 左栏报错面板（应出现条数与报错行）---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 320 -and $e.Current.Name -match '报错|暂无|个人制作') {
    Write-Output ('  ' + (Rect $e) + ' :: ' + ($e.Current.Name -replace [char]13, ' / '))
  }
}
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 320) {
    $kids = $e.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
    Write-Output ('  [左栏报错列表] ' + (Rect $e) + ' 子项=' + $kids.Count)
    $n = 0
    foreach ($k in $kids) { if ($n -lt 4) { Write-Output ('      · ' + ($k.Current.Name -replace [char]13, ' | ')); $n++ } }
  }
}
Write-Output '--- 结果页按钮状态（载入任务后应可用）---'
$items[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen) { continue }
  if ($e.Current.Name -match '^⚠ 尝试修复$|^✔ 验证完整性$|^强制覆盖$') {
    Write-Output ('  ' + (Rect $e) + ' enabled=' + $e.Current.IsEnabled + ' :: ' + $e.Current.Name)
  }
}
Write-Output 'UIA19B_DONE'
