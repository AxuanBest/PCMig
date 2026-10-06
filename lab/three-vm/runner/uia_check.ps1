
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = 'E:\deepseek work\PCMig\src\PCMig.Gui\bin\Release\net8.0-windows\PCMig.exe'
Get-Process PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"; $env:Path = "$env:DOTNET_ROOT;" + $env:Path
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16
if ($p.HasExited) { Write-Output ('启动失败 ExitCode=' + $p.ExitCode); exit 1 }
$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output '未找到窗口'; exit 1 }
$r = $win.Current.BoundingRectangle
Write-Output ('窗口: ' + $win.Current.Name + ' 尺寸 ' + [int]$r.Width + 'x' + [int]$r.Height)
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ('控件总数: ' + $all.Count)
$steps = @()
foreach ($e in $all) { if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e } }
Write-Output ('导轨条目: ' + $steps.Count)
foreach ($s in $steps) {
  $b = $s.Current.BoundingRectangle
  $nm = $s.Current.Name.Replace([char]13, ' ').Replace([char]10, ' ')
  Write-Output ('   [' + $nm + ']  ' + [int]$b.X + ',' + [int]$b.Y + '  ' + [int]$b.Width + 'x' + [int]$b.Height)
}
Write-Output ''

function DumpPage($idx, $title) {
  if ($idx -ge 0) {
    $sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
    $steps[$idx].GetCurrentPattern($sel).Select()
    Start-Sleep -Milliseconds 900
  }
  Write-Output ('==== ' + $title + ' ====')
  $elems = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $rows = @()
  foreach ($e in $elems) {
    if ($e.Current.IsOffscreen) { continue }
    $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($t -notin @('Button','Text','Edit','CheckBox','ComboBox','DataGrid','Tree','ListItem','DataItem','ProgressBar')) { continue }
    $b = $e.Current.BoundingRectangle
    $n = $e.Current.Name.Replace([char]13, ' ').Replace([char]10, ' ').Trim()
    if ($n.Length -gt 44) { $n = $n.Substring(0, 44) + '~' }
    $dis = ''
    if (-not $e.Current.IsEnabled) { $dis = ' (禁用)' }
    $rows += [pscustomobject]@{ T = $t; N = $n; X = [int]$b.X; Y = [int]$b.Y; W = [int]$b.Width; H = [int]$b.Height; D = $dis }
  }
  Write-Output ('  可见元素 ' + $rows.Count + ' 个')
  foreach ($row in ($rows | Sort-Object Y, X)) {
    Write-Output ('  ' + $row.T.PadRight(10) + ' [' + $row.N + '] @' + $row.X + ',' + $row.Y + ' ' + $row.W + 'x' + $row.H + $row.D)
  }
  Write-Output ''
  return ,$rows
}
$p1 = DumpPage -1 'PAGE1 connect'
$p2 = DumpPage 1 'PAGE2 select'
$p3 = DumpPage 2 'PAGE3 transfer'
$p4 = DumpPage 3 'PAGE4 result'

$r1 = $p1 | Where-Object { $_.W -gt 0 -and $_.H -gt 0 }
$bad = 0
for ($i = 0; $i -lt $r1.Count; $i++) {
  for ($j = $i + 1; $j -lt $r1.Count; $j++) {
    $a = $r1[$i]
    $b = $r1[$j]
    $ix = [Math]::Max(0, [Math]::Min($a.X + $a.W, $b.X + $b.W) - [Math]::Max($a.X, $b.X))
    $iy = [Math]::Max(0, [Math]::Min($a.Y + $a.H, $b.Y + $b.H) - [Math]::Max($a.Y, $b.Y))
    $small = [Math]::Min($a.W * $a.H, $b.W * $b.H)
    if ($small -gt 0 -and (($ix * $iy) / $small) -gt 0.55) {
      Write-Output ('  [OVERLAP] ' + [int](100 * $ix * $iy / $small) + ' pct : [' + $a.N + '] <-> [' + $b.N + ']')
      $bad++
    }
  }
}
Write-Output ('页面1重叠告警: ' + $bad)
Write-Output ('窗口底部 y=' + [int]($r.Y + $r.Height))
Write-Output 'UIA_DONE'
