param(
    [Parameter(Mandatory=$true)][string]$Out,
    [string]$StoreId = '',
    [string]$Pattern = 'Pause|Resume|TRN-0|UI\.Action',
    [string]$SinceUtc = ''
)
# Recovery Gate: collect diagnostics truth for one case (events + session + incidents).
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'PCMig\Diagnostics'
if ($StoreId) { $store = Join-Path $root $StoreId }
else {
    $cand = Get-ChildItem -LiteralPath $root -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $cand) { Write-Host 'DIAG-NO-STORE'; exit 1 }
    $store = $cand.FullName
}
$outDir = Split-Path -Parent $Out
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$evDir = Join-Path $store 'events'
$lines = @()
foreach ($f in (Get-ChildItem -LiteralPath $evDir -File | Sort-Object Name)) { $lines += (Get-Content -LiteralPath $f.FullName) }
Write-Host ("DIAG-STORE=" + $store)
Write-Host ("DIAG-EVENT-LINES=" + $lines.Count)

$since = $null
if ($SinceUtc) { $since = [datetime]::Parse($SinceUtc).ToUniversalTime() }
$rows = New-Object System.Collections.ArrayList
$hist = @{}
foreach ($l in $lines) {
    if (-not $l) { continue }
    try { $j = $l | ConvertFrom-Json } catch { continue }
    $ts = [datetime]::Parse($j.timestampUtc).ToUniversalTime()
    if ($since -and $ts -lt $since) { continue }
    $name = $j.eventName
    if (-not $hist.ContainsKey($name)) { $hist[$name] = 0 }
    $hist[$name]++
    if ($name -notmatch $Pattern) { continue }
    $pl = $j.payload
    $extra = @()
    foreach ($k in 'mode','objectId','milliseconds','waitedMs','outcome','reason','phase','message','actionId','expectationStepId','eventName','kind','status','detail') {
        if ($pl -and ($pl.PSObject.Properties.Name -contains $k)) { $extra += ($k + '=' + ([string]$pl.$k -replace "`r?`n", ' ')) }
    }
    $null = $rows.Add([pscustomobject]@{
        utc = $ts.ToString('HH:mm:ss.fff'); eventName = $name; eventCode = $j.eventCode; level = $j.level
        deliveryClass = $j.deliveryClass; payloadName = $j.payloadName; detail = ($extra -join ' ')
    })
}
$rows | ForEach-Object { ($_.utc, $_.eventName, $_.eventCode, $_.level, $_.deliveryClass, $_.payloadName, $_.detail) -join "`t" } |
    Set-Content -LiteralPath $Out -Encoding UTF8
Write-Host ("DIAG-MATCHED=" + $rows.Count + " OUT=" + $Out)
Write-Host '--- top event names ---'
$hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 18 | ForEach-Object { Write-Host ("  " + $_.Value + "  " + $_.Key) }
if (Test-Path -LiteralPath (Join-Path $store 'session.json')) { Copy-Item -LiteralPath (Join-Path $store 'session.json') -Destination (Join-Path $outDir 'diag-session.json') -Force }
$inc = Join-Path $store 'incidents'
if (Test-Path -LiteralPath $inc) {
    foreach ($f in (Get-ChildItem -LiteralPath $inc -File)) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $outDir ('diag-' + $f.Name)) -Force }
    $open = @()
    foreach ($f in (Get-ChildItem -LiteralPath $inc -File)) {
        foreach ($l in (Get-Content -LiteralPath $f.FullName)) { if ($l) { try { $j = $l | ConvertFrom-Json; $open += ($j.RuleId + '/' + $j.Status + '/' + $j.Severity) } catch {} } }
    }
    Write-Host ("DIAG-INCIDENTS=" + $open.Count + " " + ($open -join ' | '))
}
Write-Host ("DIAG-STORE-ID=" + (Split-Path -Leaf $store))