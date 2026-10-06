
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
function Rect($e) { $b = $e.Current.BoundingRectangle; if ($b.X -eq [double]::PositiveInfinity) { return 'x=∞' }; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) { if ($w.Current.Name -match 'PCMig 迁移工具') { $win = $w; break } }
Write-Output ('窗口: ' + $win.Current.Name + ' | ' + (Rect $win))
try {
  $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
  Write-Output ('  窗口状态=' + $wp.Current.WindowVisualState)
  if ($wp.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Normal) {
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep -Seconds 2
    Write-Output ('  已恢复为=' + $wp.Current.WindowVisualState + ' | ' + (Rect $win))
  }
} catch { Write-Output ('  窗口模式异常: ' + $_.Exception.Message) }
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1000
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
Write-Output ('第②页 ComboBox=' + $combos.Count)
$jb = $combos[$combos.Count - 1]
Write-Output ('  已有任务下拉 ' + (Rect $jb))
$jb.SetFocus(); Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
Start-Sleep -Seconds 8
Write-Output '--- 左栏报错区（x<400 且 y>窗口底部-260）---'
$wb = $win.Current.BoundingRectangle
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity -or $b.X -lt 0) { continue }
  if ([int]$b.X -lt 400 -and [int]$b.Y -gt ([int]$wb.Y + [int]$wb.Height - 300)) {
    Write-Output ('  ' + (Rect $e) + ' :: ' + ($e.Current.Name -replace [char]13, ' / '))
  }
}
Write-Output '--- 左栏列表行 ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity -or $b.X -lt 0) { continue }
  if ([int]$b.X -lt 400) {
    $kids = $e.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
    Write-Output ('  [列表] ' + (Rect $e) + ' 行数=' + $kids.Count)
    $n = 0
    foreach ($k in $kids) { if ($n -lt 6) { Write-Output ('      · ' + ($k.Current.Name -replace [char]13, ' | ')); $n++ } }
  }
}
Write-Output 'UIA20C_DONE'
