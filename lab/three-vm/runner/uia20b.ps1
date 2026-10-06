
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
  if ($w.Current.Name -match 'PCMig') { $win = $w; break }
}
Write-Output ('窗口: ' + $win.Current.Name + '  ' + (Rect $win))
function Rect($e) { $b = $e.Current.BoundingRectangle; if ($b.X -eq [double]::PositiveInfinity) { return 'x=∞' }; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
# 关掉可能开着的下拉
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 500
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1000
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
$jb = $combos[$combos.Count - 1]
Write-Output ('已有任务下拉 ' + (Rect $jb))
$jb.SetFocus(); Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
Start-Sleep -Seconds 8
Write-Output '--- 左栏全部文本（x<400）---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 400 -and $e.Current.Name -notmatch '^StepItem') {
    Write-Output ('  ' + (Rect $e) + ' :: ' + ($e.Current.Name -replace [char]13, ' / '))
  }
}
Write-Output '--- 左栏列表 ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 400) {
    $kids = $e.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
    Write-Output ('  [列表] ' + (Rect $e) + ' 行数=' + $kids.Count)
    $n = 0
    foreach ($k in $kids) { if ($n -lt 6) { Write-Output ('      · ' + ($k.Current.Name -replace [char]13, ' | ')); $n++ } }
  }
}
Write-Output 'UIA20B_DONE'
