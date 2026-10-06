
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = 'E:\deepseek work\PCMig\src\PCMig.Gui\bin\Release\net8.0-windows\PCMig.exe'
Get-Process PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16
if ($p.HasExited) { Write-Output ('启动失败 ' + $p.ExitCode); exit 1 }
$AE = [System.Windows.Automation.AutomationElement]
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Output ('窗口: ' + $win.Current.Name)

function Texts() {
  $out = @()
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.IsOffscreen) { continue }
    if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Text') { continue }
    $n = $e.Current.Name
    if ($n -and $n.Length -lt 120) { $out += $n }
  }
  return $out
}

# 切到迁移页（③）
$steps = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e }
}
$steps[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 600

# 选一个已有任务（下拉里的第一个 = 最新）
$combo = $null
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox') { $combo = $e }
}
if ($combo -eq $null) { Write-Output '未找到已有任务下拉'; exit 1 }
$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 900
$items = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and -not $e.Current.IsOffscreen) { $items += $e }
}
Write-Output ('下拉候选 ' + $items.Count + ' 个:')
foreach ($it in $items) { Write-Output ('   - ' + $it.Current.Name) }
$target = $null
foreach ($it in $items) { if ($it.Current.Name -match '7fc7') { $target = $it } }
if ($target -eq $null -and $items.Count -gt 0) { $target = $items[0] }
Write-Output ('选中: ' + $target.Current.Name)
$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 3
Write-Output '---- 迁移页显示的文本 ----'
foreach ($t in (Texts)) { Write-Output ('   ' + $t) }
Write-Output '---- 底部状态栏 ----'
$pb = @()
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ($b.Y -gt 1000) {
    $tp = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($tp -in @('Text','ProgressBar','Button')) { $pb += ($tp + ': ' + $e.Current.Name + ' @x=' + [int]$b.X + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
  }
}
foreach ($x in $pb) { Write-Output ('   ' + $x) }
Write-Output 'PREVIEW_DONE'
