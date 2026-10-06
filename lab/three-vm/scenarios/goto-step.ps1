param([Parameter(Mandatory=$true)][ValidateSet('Step1','Step2','Step3','Step4')][string]$Step, [int]$WaitSec = 3)
# Recovery Gate helper: click a left-nav step and wait for the page to settle.
$ErrorActionPreference = 'Stop'
. 'E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1'
$w = Get-RgWindow -TimeoutSec 20
if (-not $w) { Write-Host 'NO-WINDOW'; exit 1 }
$nav = Find-RgAid $w ("Shell.Nav." + $Step)
if (-not $nav) { Write-Host ("NO-NAV " + $Step); exit 2 }
Invoke-RgClick $nav | Out-Null
Start-Sleep -Seconds $WaitSec
Write-Host ("NAV-CLICKED " + $Step)