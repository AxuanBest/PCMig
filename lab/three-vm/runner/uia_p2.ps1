
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
