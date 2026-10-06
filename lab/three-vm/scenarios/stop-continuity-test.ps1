param(
  [string]$OutDir = 'E:\PCMigLab\Staging\phStop',
  [double]$RunSec = 2.0,
  [int]$SampleCount = 30,
  [int]$SampleMs = 200,
  [switch]$NoStart
)
$ErrorActionPreference = 'Continue'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
$OutDir = $OutDir.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$w = Get-RgWindow -TimeoutSec 30
if ($null -eq $w) { Write-Host 'NO-WINDOW'; exit 1 }

$script:rows = @()

function Read-Triple {
  $p = Get-RgText $w 'TotalPercentText'
  $b = Get-RgText $w 'TotalBytesText'
  $s = Get-RgText $w 'StateLineText'
  return @{ p = $p; b = $b; s = $s }
}

function Write-Row {
  param([string]$Tag)
  $t = Read-Triple
  $script:rows += [pscustomobject]@{
    utc     = (Get-Date).ToString('HH:mm:ss.fff')
    tag     = $Tag
    percent = $t.p
    bytes   = $t.b
    state   = $t.s
  }
  Write-Host ("SAM {0} tag={1} p=[{2}] b=[{3}] s=[{4}]" -f (Get-Date).ToString('HH:mm:ss.fff'), $Tag, $t.p, $t.b, $t.s)
}

Write-Row 'before-start'

if (-not $NoStart) {
  $startEl = Find-RgAid $w 'Shell.Transfer.Start' -TimeoutSec 10
  if ($null -ne $startEl) { Invoke-RgClick $startEl | Out-Null; Write-Host 'START-CLICKED' }
  else { Write-Host 'NO-START' }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $RunSec) {
  Write-Row 'running'
  Start-Sleep -Milliseconds $SampleMs
}

$stopBefore = Read-Triple
Write-Host ("STOP-BEFORE p=[{0}] b=[{1}]" -f $stopBefore.p, $stopBefore.b)

$stopEl = Find-RgAid $w 'Shell.Transfer.Stop' -TimeoutSec 5
if ($null -ne $stopEl) { Invoke-RgClick $stopEl | Out-Null; Write-Host 'STOP-CLICKED' }
else { Write-Host 'NO-STOP' }

for ($i = 1; $i -le $SampleCount; $i++) {
  Start-Sleep -Milliseconds $SampleMs
  Write-Row ("stop+{0}" -f $i)
}

$csv = Join-Path $OutDir 'stop-samples.csv'
$script:rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csv
Write-Host ("CSV " + $csv)
Write-Host 'STOP-TEST-DONE'