
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Output ('窗口: ' + $win.Current.Name)
function Rect($e) { $b = $e.Current.BoundingRectangle; if ($b.X -eq [double]::PositiveInfinity) { return 'x=∞' }; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
$jb = $combos[$combos.Count - 1]
$jb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 800
$picked = $null
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ListItem))) {
  if ($e.Current.Name -match 'JOB-20260915-142742-c790') { $picked = $e; break }
}
if ($picked -eq $null) { Write-Output '未找到目标任务项'; }
else {
  Write-Output ('选中: ' + $picked.Current.Name)
  $picked.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Seconds 6
}
Write-Output '--- 左栏报错面板（应显示条数 + 失败行）---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 340 -and [int]$b.Y -gt 1160 -and [int]$b.Y -lt 1340) {
    Write-Output ('  ' + (Rect $e) + ' :: ' + ($e.Current.Name -replace [char]13, ' / '))
  }
}
Write-Output '--- 报错列表容器与行数 ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.X -eq [double]::PositiveInfinity) { continue }
  if ([int]$b.X -lt 340 -and [int]$b.Y -gt 1150) {
    $kids = $e.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
    Write-Output ('  [列表] ' + (Rect $e) + ' 行数=' + $kids.Count)
    $n = 0
    foreach ($k in $kids) { if ($n -lt 5) { Write-Output ('      · ' + ($k.Current.Name -replace [char]13, ' | ')); $n++ } }
  }
}
Write-Output '--- 底栏网速 ---'
$sp = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  if ($e.Current.Name -match '^[\d.]+ (B|KB|MB|GB)/s$') { $sp += $e.Current.Name }
}
Write-Output ('  速率文本: ' + ($sp -join ' | '))
Write-Output 'UIA20_DONE'
