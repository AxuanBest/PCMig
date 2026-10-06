param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$Out,
    [string]$Label = 'dataset'
)
# Independent-truth manifest: per-file SHA256 + bytes, sorted by relative path.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Root)) { throw "root not found: $Root" }

$rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$files = Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName
$rows = New-Object System.Collections.Generic.List[string]
$total = 0L
foreach ($f in $files) {
    $rel = $f.FullName.Substring($rootFull.Length + 1)
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    $rows.Add(("{0}`t{1}`t{2}" -f $h, $f.Length, $rel))
    $total += $f.Length
}
$dir = Split-Path $Out -Parent
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
Set-Content -LiteralPath $Out -Value $rows -Encoding UTF8
Write-Host ("MANIFEST-LABEL={0}" -f $Label)
Write-Host ("MANIFEST-ROOT={0}" -f $rootFull)
Write-Host ("MANIFEST-FILES={0}" -f $files.Count)
Write-Host ("MANIFEST-BYTES={0}" -f $total)
Write-Host ("MANIFEST-OUT={0}" -f $Out)