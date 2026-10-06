param(
  [string]$ReqFile = 'E:\PCMigLab\Staging\ctl\filereq.json',
  [string]$LogFile = 'E:\PCMigLab\Staging\ctl\log\labfile.log'
)
# PCMig lab host-side file helper (env management only).
# Runs elevated through the PCMigLabFileHelper scheduled task (same /RL HIGHEST mechanism
# as the resident ctl daemon) because the interactive filtered token cannot read the
# lab credential store. It never prints secret values.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
function Say($m) {
  $l = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m)
  Write-Host $l
  try { Add-Content -LiteralPath $LogFile -Value $l -Encoding UTF8 } catch { }
}
Say '=== labfile start ==='
$res = [ordered]@{ ok = $false; mode = ''; steps = @() }
try {
  $r = Get-Content -Raw -LiteralPath $ReqFile -Encoding UTF8 | ConvertFrom-Json
  $mode = [string]$r.mode
  $res.mode = $mode
  Say ("mode={0} vm={1}" -f $mode, $r.vm)

  $cred = Get-Content -Raw -LiteralPath 'E:\PCMigLab\Staging\.secrets\lab-credentials.json' | ConvertFrom-Json
  $guestUser = if ($r.guestUser) { [string]$r.guestUser } else { '.\labadmin' }
  $sec = ConvertTo-SecureString $cred.localAdministrator -AsPlainText -Force
  $psc = New-Object System.Management.Automation.PSCredential($guestUser, $sec)
  $s = $null
  foreach ($un in @($guestUser, '.\labadmin')) {
    try {
      $psc2 = New-Object System.Management.Automation.PSCredential($un, $sec)
      $s = New-PSSession -VMName ([string]$r.vm) -Credential $psc2 -ErrorAction Stop
      Say ("session opened as {0} : {1}" -f $un, $s.State)
      break
    } catch { $s = $null; Say ("session try failed for {0}" -f $un) }
  }
  if (-not $s) { throw 'PowerShell Direct session failed' }

  if ($r.hashPaths) {
    $paths = @($r.hashPaths)
    $h = Invoke-Command -Session $s -ArgumentList (, $paths) -ScriptBlock {
      param($ps)
      $o = @()
      foreach ($p in $ps) {
        if (Test-Path -LiteralPath $p) { $o += ("{0}`t{1}`t{2}" -f (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash, (Get-Item -LiteralPath $p).Length, $p) }
        else { $o += ("MISSING`t0`t{0}" -f $p) }
      }
      $o
    }
    foreach ($line in @($h)) { Say ("HASH " + $line); $res.steps += [string]$line }
  }

  foreach ($c in @($r.copy)) {
    $src = [string]$c.src
    $dst = [string]$c.dst
    if ($mode -eq 'push') {
      if (-not (Test-Path -LiteralPath $src)) { Say ("PUSH MISSING SRC " + $src); continue }
      Invoke-Command -Session $s -ArgumentList $dst -ScriptBlock {
        param($d)
        $parent = Split-Path -Parent $d
        if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
      } | Out-Null
      Copy-Item -ToSession $s -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
      $bytes = (Get-Item -LiteralPath $src).Length
      Say ("PUSH ok {0} -> {1} ({2} B)" -f $src, $dst, $bytes)
      $res.steps += ("PUSH {0} -> {1}" -f $src, $dst)
    } elseif ($mode -eq 'pull') {
      $parentHost = Split-Path -Parent $dst
      if ($parentHost -and -not (Test-Path -LiteralPath $parentHost)) { New-Item -ItemType Directory -Path $parentHost -Force | Out-Null }
      Copy-Item -FromSession $s -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
      $bytes = (Get-Item -LiteralPath $dst).Length
      Say ("PULL ok {0} -> {1} ({2} B)" -f $src, $dst, $bytes)
      $res.steps += ("PULL {0} -> {1}" -f $src, $dst)
    }
  }

  if ($r.hashPaths) {
    $h2 = Invoke-Command -Session $s -ArgumentList (, @($r.hashPaths)) -ScriptBlock {
      param($ps)
      $o = @()
      foreach ($p in $ps) {
        if (Test-Path -LiteralPath $p) { $o += ("{0}`t{1}`t{2}" -f (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash, (Get-Item -LiteralPath $p).Length, $p) }
        else { $o += ("MISSING`t0`t{0}" -f $p) }
      }
      $o
    }
    foreach ($line in @($h2)) { Say ("HASH-AFTER " + $line); $res.steps += [string]$line }
  }

  Remove-PSSession $s -ErrorAction SilentlyContinue
  $res.ok = $true
}
catch { Say ("FAILED: " + $_.Exception.Message); $res.error = $_.Exception.Message }
if ($r.evidenceFile) {
  try {
    $json = ($res | ConvertTo-Json -Depth 3)
    [IO.File]::WriteAllText([string]$r.evidenceFile, $json, (New-Object Text.UTF8Encoding($false)))
    Say ("evidence written: " + $r.evidenceFile)
  } catch { Say ("evidence write failed: " + $_.Exception.Message) }
}
Say ("=== labfile end ok={0} ===" -f $res.ok)