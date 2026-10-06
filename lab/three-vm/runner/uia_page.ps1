
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = 'E:\deepseek work\PCMig\src\PCMig.Gui\bin\Release\net8.0-windows\PCMig.exe'
Get-Process PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 15
if ($p.HasExited) { Write-Output ('启动失败 ' + $p.ExitCode); exit 1 }
$AE = [System.Windows.Automation.AutomationElement]
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
function VisibleTitle() {
  $titles = @()
  $els = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $els) {
    if ($e.Current.IsOffscreen) { continue }
    $n = $e.Current.Name
    if ($n -match '^[①②③④] ') { $titles += $n }
  }
  if ($titles.Count -eq 0) { return '(无页面标题可见!)' }
  return ($titles -join ' | ')
}
$steps = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e }
}
Write-Output ('启动时可见页面: ' + (VisibleTitle))
Write-Output ('导轨选中索引: ' + (($steps | ForEach-Object { if ($_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { 1 } else { 0 } }) -join ','))
$sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
foreach ($i in 0..3) {
  $steps[$i].GetCurrentPattern($sel).Select()
  Start-Sleep -Milliseconds 700
  Write-Output ('点击导轨[' + $i + '] 后可见页面: ' + (VisibleTitle))
}
Write-Output 'CHECK_DONE'
