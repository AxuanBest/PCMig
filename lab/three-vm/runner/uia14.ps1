
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Get-Process PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process 'D:\PCMig\PCMig.exe' -PassThru
Start-Sleep -Seconds 18
if ($p.HasExited) { Write-Output ('闪退 ' + $p.ExitCode); exit 1 }
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Output ('交付 GUI: ' + $win.Current.Name + '  PID=' + $p.Id)
$steps = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e }
}
$sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
function BottomBar() {
  $out = @()
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
    if ($e.Current.IsOffscreen) { continue }
    $b = $e.Current.BoundingRectangle
    if ($b.Y -lt 1300) { continue }
    $tp = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($tp -eq 'Text' -or $tp -eq 'ProgressBar') { $out += ($tp + ' [' + $e.Current.Name + '] @x=' + [int]$b.X + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
  }
  return $out
}
$steps[0].GetCurrentPattern($sel).Select(); Start-Sleep -Milliseconds 800
Write-Output '--- ① 页底部状态栏（应含百分比） ---'
BottomBar | ForEach-Object { Write-Output ('   ' + $_) }
$steps[2].GetCurrentPattern($sel).Select(); Start-Sleep -Milliseconds 800
Write-Output '--- ③ 迁移页底部状态栏（不应有第二个百分比） ---'
BottomBar | ForEach-Object { Write-Output ('   ' + $_) }
Write-Output '--- ③ 迁移页关键文本（大百分比 / 对账行 / 卡片标题） ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen -or $e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
  $n = $e.Current.Name
  if ($n -and ($n -match '%|计划|实际|传输速度|预计剩余|对象进度|总体进度|已传|就绪')) {
    $b = $e.Current.BoundingRectangle
    Write-Output ('   [' + [int]$b.X + ',' + [int]$b.Y + ' ' + [int]$b.Width + 'x' + [int]$b.Height + '] ' + $n)
  }
}
Write-Output 'UI14_OK'
