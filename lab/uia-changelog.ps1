
param(
  [string]$Exe = 'D:\PCMig\PCMig.exe',
  [int]$StartWaitSec = 40,
  [switch]$SkipClick,
  [string]$Out = 'E:\deepseek work\PCMig\lab\uia-out.txt'
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient | Out-Null
Add-Type -AssemblyName UIAutomationTypes | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$CLOG = [string]([char]0x66F4 + [char]0x65B0 + [char]0x65E5 + [char]0x5FD7)
$lines = New-Object System.Collections.Generic.List[string]
function W([string]$s) { $script:lines.Add($s); Write-Host $s }
function NameOf($el) { try { return [string]$el.Current.Name } catch { return '<err>' } }
function CTypeOf($el) { try { return [string]$el.Current.ControlType.ProgrammaticName } catch { return '<err>' } }
function RectOf($el) { try { $r = $el.Current.BoundingRectangle; return ("{0},{1},{2},{3}" -f [int]$r.X,[int]$r.Y,[int]$r.Width,[int]$r.Height) } catch { return '' } }
function OffOf($el) { try { return [string]$el.Current.IsOffscreen } catch { return '<err>' } }
function DumpTree($el, [int]$depth, [int]$maxDepth) {
  if ($depth -gt $maxDepth) { return }
  $pad = '  ' * $depth
  W($pad + (CTypeOf $el) + " | name='" + (NameOf $el) + "' | rect=" + (RectOf $el) + " | off=" + (OffOf $el))
  try { $kids = $el.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) } catch { return }
  foreach ($k in $kids) { DumpTree $k ($depth+1) $maxDepth }
}
W("=== START $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') exe=$Exe skipClick=$SkipClick ===")
W('--- crash logs before ---')
Get-ChildItem 'C:\ProgramData\PCMig\Logs' -Filter 'crash-*' -ErrorAction SilentlyContinue | ForEach-Object { W($_.FullName + ' ' + $_.Length) }
Get-Process -Name PCMig -ErrorAction SilentlyContinue | ForEach-Object { W("killing old PCMig pid=" + $_.Id); $_.Kill() }
Start-Sleep -Milliseconds 1500
$logFile = Join-Path 'C:\ProgramData\PCMig\Logs' ("app-" + (Get-Date -Format 'yyyyMMdd') + ".log")
$logLen0 = 0
try { $logLen0 = (Get-Item $logFile).Length } catch { }
$p = Start-Process -FilePath $Exe -PassThru
W("started pid=" + $p.Id)
$h = 0
for ($i = 0; $i -lt ($StartWaitSec * 2); $i++) {
  Start-Sleep -Milliseconds 500
  $p.Refresh()
  if ($p.HasExited) { W("PROCESS EXITED early, code=" + $p.ExitCode); break }
  if ($p.MainWindowHandle -ne 0) { $h = $p.MainWindowHandle; break }
}
W("main window handle=" + $h)
if ($h -eq 0) { W('NO MAIN WINDOW') }
if ((-not $SkipClick) -and ($h -ne 0)) {
  $root = $AE::FromHandle([intptr]$h)
  W("main window name='" + (NameOf $root) + "'")
  W('--- all buttons in main window ---')
  $allBtns = $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)))
  foreach ($b in $allBtns) { W("  button name='" + (NameOf $b) + "' autoId='" + $b.Current.AutomationId + "' enabled=" + $b.Current.IsEnabled + " offscreen=" + $b.Current.IsOffscreen + " rect=" + (RectOf $b)) }
  $btnCond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $CLOG)
  $btn = $root.FindFirst($TS::Descendants, $btnCond)
  if ($btn -eq $null) {
    W('TARGET BUTTON NOT FOUND by Name')
  } else {
    W("button found: name='" + (NameOf $btn) + "' autoId='" + $btn.Current.AutomationId + "' enabled=" + $btn.Current.IsEnabled + " offscreen=" + $btn.Current.IsOffscreen + " rect=" + (RectOf $btn) + " hwnd=" + $btn.Current.NativeWindowHandle)
    try {
      $ip = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
      $ip.Invoke()
      W('INVOKE OK')
    } catch {
      W("INVOKE FAILED: " + $_.Exception.GetType().FullName + ": " + $_.Exception.Message)
      W($_.Exception.StackTrace)
    }
  }
  Start-Sleep -Seconds 3
  $p.Refresh()
  if ($p.HasExited) { W("PROCESS EXITED after click, code=" + $p.ExitCode) }
  $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
  $wins = $null
  try { $wins = $AE::RootElement.FindAll($TS::Children, $pidCond) } catch { W("window enumeration failed: " + $_.Exception.Message) }
  $cnt = 'n/a'; if ($wins) { $cnt = $wins.Count }
  W("--- top-level windows of pid " + $p.Id + ": " + $cnt + " ---")
  if ($wins) { foreach ($w in $wins) { W("  win: name='" + (NameOf $w) + "' class='" + $w.Current.ClassName + "' type=" + (CTypeOf $w) + " off=" + (OffOf $w) + " rect=" + (RectOf $w)) } }
  $clogWin = $null
  if ($wins) { foreach ($w in $wins) { if ((NameOf $w) -like ("*" + $CLOG + "*")) { $clogWin = $w } } }
  if ($clogWin -eq $null) {
    W('CHANGELOG WINDOW NOT FOUND')
  } else {
    W('--- CHANGELOG WINDOW TREE (depth 3) ---')
    DumpTree $clogWin 0 3
    $list = $clogWin.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::List)))
    if ($list -eq $null) { W('LIST NOT FOUND') } else {
      $items = $list.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))
      W("LIST ITEM COUNT = " + $items.Count)
      $n = 0
      foreach ($it in $items) { $n++; if (($n -le 4) -or ($n -ge ($items.Count-1))) { W("  item[" + $n + "] name='" + (NameOf $it) + "' selected=" + $it.GetCurrentPropertyValue([System.Windows.Automation.SelectionItemPattern]::IsSelectedProperty)) } }
    }
  }
}
Start-Sleep -Seconds 1
W('--- new log lines ---')
try {
  $fs = [System.IO.File]::Open($logFile, 'Open', 'Read', 'ReadWrite')
  $fs.Seek($logLen0, 'Begin') | Out-Null
  $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
  $txt = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
  if ($txt.Length -gt 20000) { $txt = $txt.Substring($txt.Length - 20000) }
  W($txt)
} catch { W("log read failed: " + $_.Exception.Message) }
W('--- crash logs after ---')
Get-ChildItem 'C:\ProgramData\PCMig\Logs' -Filter 'crash-*' -ErrorAction SilentlyContinue | ForEach-Object { W($_.FullName + ' ' + $_.Length) }
W('=== END ===')
$lines -join [Environment]::NewLine | Set-Content -Path $Out -Encoding UTF8
Write-Host ("WROTE " + $Out)
