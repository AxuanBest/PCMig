
function DumpPage($idx, $title) {
  if ($idx -ge 0) {
    $sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
    $steps[$idx].GetCurrentPattern($sel).Select()
    Start-Sleep -Milliseconds 900
  }
  Write-Host ('==== ' + $title + ' ====')
  $elems = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $rows = @()
  foreach ($e in $elems) {
    if ($e.Current.IsOffscreen) { continue }
    $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($t -notin @('Button','Text','Edit','CheckBox','ComboBox','DataGrid','Tree','DataItem','ProgressBar')) { continue }
    $b = $e.Current.BoundingRectangle
    if ([double]::IsInfinity($b.X) -or [double]::IsInfinity($b.Y) -or [double]::IsNaN($b.X)) { continue }
    if ($b.Width -lt 1 -or $b.Height -lt 1) { continue }
    $n = $e.Current.Name.Replace([char]13, ' ').Replace([char]10, ' ').Trim()
    if ($n.Length -gt 42) { $n = $n.Substring(0, 42) + '~' }
    $dis = ''
    if (-not $e.Current.IsEnabled) { $dis = ' (X)' }
    $rows += [pscustomobject]@{ T = $t; N = $n; X = [int]$b.X; Y = [int]$b.Y; W = [int]$b.Width; H = [int]$b.Height; D = $dis }
  }
  Write-Host ('  可见元素 ' + $rows.Count + ' 个')
  foreach ($row in ($rows | Sort-Object Y, X)) {
    Write-Host ('  ' + $row.T.PadRight(10) + ' [' + $row.N + '] @' + $row.X + ',' + $row.Y + ' ' + $row.W + 'x' + $row.H + $row.D)
  }
  Write-Host ''
  return ,$rows
}
$p1 = DumpPage -1 'PAGE1 连接'
$p2 = DumpPage 1 'PAGE2 选择数据与目标'
$p3 = DumpPage 2 'PAGE3 迁移进度'
$p4 = DumpPage 3 'PAGE4 结果与校验'
