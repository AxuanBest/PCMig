# launch-app.ps1 - start the PCMig.WinUI produced by the SOLUTION-level Release build,
# and verify it is not older than the newest source file.
#
# Lesson (2026-10-05 RECOVERY GATE case02b): `dotnet build PCMig.sln -c Release` uses Platform=x64
# and writes to bin\x64\Release\... ; a project-level build writes to bin\Release\...
# When both exist, launching from bin\Release runs a STALE binary and a real fix looks ineffective.
# This script only launches the x64 path and prints the freshness comparison.
param(
    [int]$WaitSec = 10
)
$ErrorActionPreference = 'Stop'

$root = 'E:\Project\deepseek work\PCMig'
$exe  = Join-Path $root 'src\PCMig.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "EXE-MISSING $exe" }

$exeTime = (Get-Item -LiteralPath $exe).LastWriteTimeUtc
$srcNewest = Get-ChildItem (Join-Path $root 'src') -Recurse -Include '*.cs','*.xaml' |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
Write-Host "EXE=$exe"
Write-Host ("EXE-TIME={0}  NEWEST-SRC={1} ({2})" -f $exeTime.ToString('o'), $srcNewest.LastWriteTimeUtc.ToString('o'), $srcNewest.Name)
if ($srcNewest.LastWriteTimeUtc -gt $exeTime) {
    Write-Host 'STALE-WARNING: source is newer than exe - run dotnet build PCMig.sln -c Release --no-incremental first'
}

Get-Process PCMig.WinUI -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("KILL old PID=" + $_.Id); Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2

$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds $WaitSec
$live = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($null -eq $live) { throw 'APP-EXITED-EARLY' }
Write-Host ("STARTED PID={0} TITLE=[{1}]" -f $live.Id, $live.MainWindowTitle)
Write-Host ("PID-MATCHES-X64-PATH={0}" -f ($live.Path -ieq $exe))