param([string]$Out = "E:\pcmig-test\uishot\shot.png", [string]$Exe = "D:\PCMig\PCMig.exe", [switch]$UseScreen)
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtra);' -Name Kbd -Namespace W
Add-Type -AssemblyName System.Drawing
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -Name Dpi -Namespace W
[W+Dpi]::SetProcessDPIAware() | Out-Null
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class S {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct RECT { public int L,T,R,B; }
}
"@

# --- ensure GUI running ---
$p = Get-Process PCMig -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) {
  Start-Process $Exe | Out-Null
  Start-Sleep -Seconds 10
  $p = Get-Process PCMig -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}
if (-not $p) { Write-Output "NO-WINDOW"; exit 1 }

$script:hwnd = [IntPtr]::Zero
$cb = [S+EnumProc]{ param($h,$l)
  $pp = 0; [S]::GetWindowThreadProcessId($h, [ref]$pp) | Out-Null
  if ($pp -eq $script:targetPid -and [S]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 256
    [S]::GetWindowText($h, $sb, 256) | Out-Null
    if ($sb.ToString() -match "PCMig") { $script:hwnd = $h }
  }
  return $true }
$script:targetPid = $p.Id
[S]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($script:hwnd -eq [IntPtr]::Zero) { Write-Output "NO-HWND"; exit 2 }
[S]::SetForegroundWindow($script:hwnd) | Out-Null
Start-Sleep -Milliseconds 1500
$rc = New-Object S+RECT
[S]::GetWindowRect($script:hwnd, [ref]$rc) | Out-Null
$w = $rc.R - $rc.L; $h = $rc.B - $rc.T
Write-Output ("WINDOW pid=" + $p.Id + " rect=" + $rc.L + "," + $rc.T + " size=" + $w + "x" + $h)

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$ok = $false
if (-not $UseScreen) { $hdc = $g.GetHdc(); $ok = [S]::PrintWindow($script:hwnd, $hdc, 2); $g.ReleaseHdc($hdc) }
if (-not $ok) {
  [W+Kbd]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
  [S]::SetForegroundWindow($script:hwnd) | Out-Null
  [W+Kbd]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 900
  $g.CopyFromScreen($rc.L, $rc.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
Write-Output ("CAPTURE-METHOD " + $(if ($ok) { "PrintWindow" } else { "CopyFromScreen" }))
Write-Output ("SESSION " + $env:SESSIONNAME)
Write-Output ("SHOT " + $Out)

# --- sample points (fractions of window) ---
function Px($fx, $fy) { $x = [int]($w * $fx); $y = [int]($h * $fy); $c = $bmp.GetPixel($x, $y); return ("(" + $x + "," + $y + ") RGB " + $c.R + "," + $c.G + "," + $c.B + " A" + $c.A) }
Write-Output ("SAMPLE titlebar  " + (Px 0.5 0.02))
Write-Output ("SAMPLE rail      " + (Px 0.06 0.5))
Write-Output ("SAMPLE railTop   " + (Px 0.06 0.12))
Write-Output ("SAMPLE mainLeft  " + (Px 0.3 0.5))
Write-Output ("SAMPLE mainMid   " + (Px 0.55 0.5))
Write-Output ("SAMPLE mainRight " + (Px 0.85 0.5))
Write-Output ("SAMPLE cornerTL  " + (Px 0.004 0.004))
Write-Output ("SAMPLE bottom    " + (Px 0.5 0.97))

# --- color histogram over a coarse grid ---
$h2 = @{}
for ($y = 0; $y -lt $h; $y += 17) {
  for ($x = 0; $x -lt $w; $x += 17) {
    $c = $bmp.GetPixel($x, $y)
    $k = "" + [int]($c.R/16) + "," + [int]($c.G/16) + "," + [int]($c.B/16)
    if ($h2.ContainsKey($k)) { $h2[$k] = $h2[$k] + 1 } else { $h2[$k] = 1 }
  }
}
Write-Output "TOP-COLORS (bucket 16, sample step 17):"
$h2.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object {
  $parts = $_.Key.Split(",")
  $r = [int]$parts[0]*16; $gg = [int]$parts[1]*16; $b = [int]$parts[2]*16
  Write-Output ("  RGB~" + $r + "," + $gg + "," + $b + "  count=" + $_.Value)
}
$bmp.Dispose()