
$r1 = $p1 | Where-Object { $_.W -gt 0 -and $_.H -gt 0 }
$bad = 0
for ($i = 0; $i -lt $r1.Count; $i++) {
  for ($j = $i + 1; $j -lt $r1.Count; $j++) {
    $a = $r1[$i]
    $b = $r1[$j]
    if ($a.N -eq '' -or $b.N -eq '') { continue }
    if ($a.N -eq $b.N) { continue }
    if ($a.N.Contains($b.N) -or $b.N.Contains($a.N)) { continue }
    $ix = [Math]::Max(0, [Math]::Min($a.X + $a.W, $b.X + $b.W) - [Math]::Max($a.X, $b.X))
    $iy = [Math]::Max(0, [Math]::Min($a.Y + $a.H, $b.Y + $b.H) - [Math]::Max($a.Y, $b.Y))
    $small = [Math]::Min($a.W * $a.H, $b.W * $b.H)
    if ($small -gt 0 -and (($ix * $iy) / $small) -gt 0.4) {
      Write-Host ('  [真重叠] ' + [int](100 * $ix * $iy / $small) + '% [' + $a.N + '](' + $a.T + ') <-> [' + $b.N + '](' + $b.T + ')')
      $bad++
    }
  }
}
Write-Host ('页面1真重叠数: ' + $bad)
Write-Host 'UIA_DONE'
