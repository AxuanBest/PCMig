
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace W -Name U -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
'@
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
$wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
Write-Output ('窗口状态=' + $wp.Current.WindowVisualState)
if ($wp.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Normal) {
  $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
  Start-Sleep -Seconds 3
}
Write-Output ('窗口=' + $win.Current.Name + ' 状态=' + $wp.Current.WindowVisualState + ' ' + (RS $win))
[W.U]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 800

$steps = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))
$items = $steps.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
Write-Output ('导轨项=' + $items.Count)
$items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 1000
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
Write-Output ('下拉数=' + $combos.Count)
$jb = $combos[$combos.Count - 1]
try { $jb.SetFocus(); Start-Sleep -Milliseconds 500; [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Write-Output '  已发 DOWN' }
catch { Write-Output ('  SetFocus 失败: ' + $_.Exception.Message) }
Start-Sleep -Seconds 9
$wb = Rct $win
$railRight = [int]$wb.X + 430; $panelTop = [int]$wb.Y + 660
$lst = $null
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  $b = Rct $e
  if ($b -eq $null) { continue }
  if ([int]$b.X -le $railRight -and [int]$b.Y -ge $panelTop) { $lst = $e }
}
if ($lst -eq $null) {
  Write-Output '未找到报错列表；左栏现有元素：'
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
    $b = Rct $e
    if ($b -eq $null) { continue }
    if ([int]$b.X -le $railRight -and [int]$b.Y -ge $panelTop) { Write-Output ('   ' + (RS $e) + ' :: ' + $e.Current.ControlType.ProgrammaticName + ' ' + $e.Current.Name.Substring(0, [Math]::Min(30, $e.Current.Name.Length))) }
  }
  exit
}
function ReasonRect {
  $best = $null; $bestLen = 0
  foreach ($e in $lst.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) { if ($e.Current.Name.Length -gt $bestLen) { $bestLen = $e.Current.Name.Length; $best = $e } }
  return $best
}
Write-Output ('报错列表 ' + (RS $lst))
$sp = $lst.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
Write-Output ('  可垂直滚动=' + $sp.Current.VerticallyScrollable + ' 位置=' + $sp.Current.VerticalScrollPercent + '% 视口占比=' + $sp.Current.VerticalViewSize)
$r1 = ReasonRect
Write-Output ('  滚前 原因 y=' + [int]$r1.Current.BoundingRectangle.Y + ' 高=' + [int]$r1.Current.BoundingRectangle.Height)
$sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
Start-Sleep -Milliseconds 900
$r2 = ReasonRect
Write-Output ('  大格下滚后 y=' + [int]$r2.Current.BoundingRectangle.Y + ' 位置=' + $sp.Current.VerticalScrollPercent + '%  → 上移 ' + ([int]$r1.Current.BoundingRectangle.Y - [int]$r2.Current.BoundingRectangle.Y) + ' px')
$sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeDecrement)
Start-Sleep -Milliseconds 700
$r3 = ReasonRect
Write-Output ('  回滚后 y=' + [int]$r3.Current.BoundingRectangle.Y + ' 位置=' + $sp.Current.VerticalScrollPercent + '%')
Write-Output 'SCROLLPAT2_DONE'
