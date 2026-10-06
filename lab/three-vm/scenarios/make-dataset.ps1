param(
    [Parameter(Mandatory=$true)][string]$Root,
    [ValidateSet('small','mixed','large','tiny','smallmany','single8')][string]$Profile = 'mixed',
    [int]$Seed = 20261005
)
# Recovery Gate dataset builder (ASCII only, deterministic content).
# Profiles:
#   tiny  : ~40 MB  (quick smoke)
#   small : ~300 MB (many small files only)
#   mixed : ~3.6 GB (1500 small files + 3 large files)  -> Recovery Gate case 1/2
#   large : ~3.2 GB (3 large files + 20 small)          -> Recovery Gate case 3
$ErrorActionPreference = 'Stop'

if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Path $Root | Out-Null

function New-PatternFile {
    param([string]$Path, [long]$Bytes, [int]$SeedValue)
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $buf = New-Object byte[] (1MB)
    $rnd = New-Object System.Random $SeedValue
    $rnd.NextBytes($buf)
    $fs = [System.IO.File]::Create($Path)
    try {
        $left = $Bytes
        while ($left -gt 0) {
            $n = [int][Math]::Min($left, $buf.Length)
            $fs.Write($buf, 0, $n)
            $left -= $n
        }
    } finally { $fs.Dispose() }
}

$smallCount = 0; $largeCount = 0
switch ($Profile) {
    'tiny'  { $smallCount = 40;   $largeCount = 0 }
    'small' { $smallCount = 1500; $largeCount = 0 }
    'mixed' { $smallCount = 1500; $largeCount = 3 }
    'large' { $smallCount = 20;   $largeCount = 3 }
    'smallmany' { $smallCount = 12000; $largeCount = 0 }   # ~1.2 GB in 12k small files (bulk pass window)
    'single8'   { $smallCount = 4;     $largeCount = 2 }   # 8 GB + 2 GB single files (single-file pause window)
}

# ---- small files: 40 directories, 4 KB .. 200 KB each ----
$rnd = New-Object System.Random $Seed
$dirs = 1..40 | ForEach-Object { Join-Path $Root ("dir{0:d2}" -f $_) }
foreach ($d in $dirs) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
for ($i = 0; $i -lt $smallCount; $i++) {
    $d = $dirs[$i % $dirs.Count]
    $kb = 4 + $rnd.Next(0, 197)
    $sub = ''
    if ($i % 7 -eq 0) { $sub = 'sub'; $p = Join-Path $d $sub; if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p | Out-Null } }
    $name = "file{0:d4}_{1}.bin" -f $i, $kb
    $full = if ($sub) { Join-Path (Join-Path $d $sub) $name } else { Join-Path $d $name }
    New-PatternFile -Path $full -Bytes ([long]$kb * 1KB) -SeedValue ($Seed + $i)
}

# ---- large files ----
$largeSpecs = @()
if ($Profile -eq 'mixed') { $largeSpecs = @(@{n='big-a.bin';gb=1.2}, @{n='big-b.bin';gb=1.1}, @{n='big-c.bin';gb=1.0}) }
if ($Profile -eq 'large') { $largeSpecs = @(@{n='big-a.bin';gb=1.2}, @{n='big-b.bin';gb=1.1}, @{n='big-c.bin';gb=1.0}) }
if ($Profile -eq 'single8') { $largeSpecs = @(@{n='big-huge.bin';gb=8}, @{n='big-tail.bin';gb=2}) }
$bigDir = Join-Path $Root 'big'
if ($largeSpecs.Count -gt 0) { New-Item -ItemType Directory -Path $bigDir -Force | Out-Null }
$k = 0
foreach ($s in $largeSpecs) {
    $bytes = [long]([double]$s.gb * 1GB)
    New-PatternFile -Path (Join-Path $bigDir $s.n) -Bytes $bytes -SeedValue ($Seed + 1000 + $k)
    $k++
}

# ---- summary ----
$files = Get-ChildItem -LiteralPath $Root -Recurse -File
$total = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("DATASET-ROOT={0}" -f $Root)
Write-Host ("DATASET-PROFILE={0}" -f $Profile)
Write-Host ("DATASET-FILES={0}" -f $files.Count)
Write-Host ("DATASET-BYTES={0}" -f $total)
Write-Host ("DATASET-GIB={0}" -f ([math]::Round($total / 1GB, 3)))