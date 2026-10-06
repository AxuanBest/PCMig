
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
$w = $win.Current.BoundingRectangle
Write-Output ('窗口: ' + [int]$w.X + ',' + [int]$w.Y + ' ' + [int]$w.Width + 'x' + [int]$w.Height + ' 标题=' + $win.Current.Name)
function Rect($e) { $b = $e.Current.BoundingRectangle; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
function DumpTexts($label, $pattern) {
  Write-Output ('--- ' + $label + ' ---')
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
    if ($e.Current.IsOffscreen) { continue }
    $n = $e.Current.Name
    if ($n -match $pattern) { Write-Output ('  ' + (Rect $e) + ' :: ' + ($n -replace [char]13, ' / ')) }
  }
}
function DumpButtons($pattern) {
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Button))) {
    if ($e.Current.IsOffscreen) { continue }
    if ($e.Current.Name -match $pattern) {
      Write-Output ('  [按钮] ' + (Rect $e) + ' enabled=' + $e.Current.IsEnabled + ' :: ' + $e.Current.Name)
    }
  }
}
DumpTexts '启动态：报错面板 / 署名 / 状态' '报错提示|暂无报错|个人制作|^就绪'
DumpButtons '尝试修复|验证完整性|恢复'

Write-Output '--- 用键盘选中「已有任务」触发报错条目 ---'
$combo = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::ComboBox))
if ($combo -ne $null) {
  Write-Output ('  ComboBox: ' + (Rect $combo) + ' enabled=' + $combo.Current.IsEnabled)
  $combo.SetFocus()
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
  Start-Sleep -Seconds 4
  Write-Output '  已发送 DOWN 键'
} else { Write-Output '  未找到 ComboBox（可能不在第 1 页）' }

DumpTexts '选中任务后：报错面板' '报错提示|暂无报错|个人制作|^就绪|个对象'
DumpButtons '尝试修复'
Write-Output '--- 列表/滚动容器 ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ([int]$b.X -lt 320) { Write-Output ('  [左栏列表] ' + (Rect $e) + ' 子项=' + $e.FindAll([System.Windows.Automation.TreeScope]::Children, $all).Count) }
}
Write-Output 'UIA18_DONE'
