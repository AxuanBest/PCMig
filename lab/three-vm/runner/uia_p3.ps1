
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
