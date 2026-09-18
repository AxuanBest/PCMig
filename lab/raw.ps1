
$p = 'E:\deepseek work\PCMig\src\PCMig.Gui\MainWindow.xaml.cs'
$t = [System.IO.File]::ReadAllText($p)
$lines = $t -split "\r?\n"
for ($i = 63; $i -lt 81; $i++) {
  $ln = $lines[$i]
  $cps = ($ln.ToCharArray() | ForEach-Object { [int]$_ }) -join ','
  Write-Host ("{0}| {1}" -f ($i+1), $ln)
  Write-Host ("   CP: " + $cps)
}
Write-Host ("BOM: " + (([System.IO.File]::ReadAllBytes($p))[0..2] -join ','))
