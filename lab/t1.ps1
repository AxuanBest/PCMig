
$AE='x'; $TS='y'; $CTypeOf={param($z) 'ct'}; $CLOG='c'
function W([string]$s) { Write-Host $s }
function NameOf($el) { 'n' }
function RectOf($el) { 'r' }
$wins = @()
$p = @{ Id = 5 }
foreach ($w in $wins) { W("  win: name='$(NameOf($w))' class='$($w.Current.ClassName)' type=$($CTypeOf($w)) rect=$(RectOf($w))") }
Write-Host 'LINE75 OK'
