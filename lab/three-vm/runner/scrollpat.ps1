
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
function Rct($e) { $b = $e.Current.BoundingRectangle; if ($b.X -eq [double]::PositiveInfinity -or $b.X -lt 0) { return $null }; return $b }
function RS($e) { $b = Rct $e; if ($b -eq $null) { return 'inf' }; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) { if ($w.Current.Name -match 'PCMig 迁移工具') { $win = $w; break } }
$wb = Rct $win
Add-Type -AssemblyName System.Windows.Forms
$items = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List)).FindAll([System.Windows.Automation.TreeScope]::Children, $all)
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
$combos[$combos.Count - 1].SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
Start-Sleep -Seconds 9
$wb = Rct $win
$railRight = [int]$wb.X + 430; $panelTop = [int]$wb.Y + 660
$lst = $null
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  $b = Rct $e
  if ($b -eq $null) { continue }
  if ([int]$b.X -le $railRight -and [int]$b.Y -ge $panelTop) { $lst = $e }
}
if ($lst -eq $null) { Write-Output '未找到报错列表——先选一个带失败记录的任务'; exit }
function ReasonRect {
  $best = $null; $bestLen = 0
  foreach ($e in $lst.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
    if ($e.Current.Name.Length -gt $bestLen) { $bestLen = $e.Current.Name.Length; $best = $e }
  }
  return $best
}
Write-Output ('报错列表 ' + (RS $lst))
$sp = $lst.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
Write-Output ('  可垂直滚动=' + $sp.Current.VerticallyScrollable + '  垂直滚动位置=' + $sp.Current.VerticalScrollPercent + '%  视口比例=' + $sp.Current.VerticalViewSize)
$r1 = ReasonRect
Write-Output ('  滚前 原因文本 y=' + [int]$r1.Current.BoundingRectangle.Y + '（高 ' + [int]$r1.Current.BoundingRectangle.Height + '）')
$sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
Start-Sleep -Milliseconds 800
$r2 = ReasonRect
Write-Output ('  程序滚动后 原因文本 y=' + [int]$r2.Current.BoundingRectangle.Y)
Write-Output ('  垂直滚动位置=' + $sp.Current.VerticalScrollPercent + '%')
$d = [int]$r1.Current.BoundingRectangle.Y - [int]$r2.Current.BoundingRectangle.Y
Write-Output ('  >>> 内容上移 ' + $d + ' 像素')
# 复位并再滚一次，确认可双向
$sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeDecrement)
Start-Sleep -Milliseconds 600
$r3 = ReasonRect
Write-Output ('  回滚后 y=' + [int]$r3.Current.BoundingRectangle.Y + '  垂直滚动位置=' + $sp.Current.VerticalScrollPercent + '%')
Write-Output 'SCROLLPATTERN_DONE'
