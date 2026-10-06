param(
    [Parameter(Mandatory=$true)][string]$CaseName,
    [Parameter(Mandatory=$true)][string]$Share,
    [Parameter(Mandatory=$true)][string]$TargetRoot,
    [string]$Host_ = 'localhost',
    [switch]$ReuseTarget
)
# Recovery Gate: drive Step1 -> Step2 -> Prepare for one case and print the resulting job id.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Host 'NO-WINDOW'; exit 1 }

if ((Test-Path -LiteralPath $TargetRoot) -and -not $ReuseTarget) {
    $c = (Get-ChildItem -LiteralPath $TargetRoot -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($c -gt 0) { Write-Host ("TARGET-NOT-EMPTY files=" + $c + " path=" + $TargetRoot); exit 3 }
}

# --- Step 1: connect ---
Invoke-RgClick (Find-RgAid $w 'Shell.Nav.Step1') | Out-Null
Start-Sleep -Seconds 2
Set-RgText (Find-RgAid $w 'Step1.HostInput') $Host_ | Out-Null
Start-Sleep -Milliseconds 300
Invoke-RgClick (Find-RgAid $w 'Step1.Connect') | Out-Null
Start-Sleep -Seconds 5
Write-Host ("STEP1-STATUS=[" + (Get-RgText $w 'Step1.StatusText') + "]")

# --- Step 1: share selection (exactly the requested share) ---
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'E:\PCMigLab\Staging\recovery-gate\step1-set-shares.ps1' -Include $Share -NavStep2 | ForEach-Object { Write-Host $_ }
Start-Sleep -Seconds 2

# --- Step 2: tree rows + target + prepare ---
$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'E:\PCMigLab\Staging\recovery-gate\step2-prepare.ps1' -Target $TargetRoot -Prepare
$out | ForEach-Object { Write-Host $_ }
$text = ($out -join "`n")
$m = [regex]::Match($text, 'JOB-\d{8}-\d{6}-[0-9a-f]{4}')
if ($m.Success) { Write-Host ("JOBID=" + $m.Value + " CASE=" + $CaseName) } else { Write-Host ("JOBID-NOT-FOUND CASE=" + $CaseName) }