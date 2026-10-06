
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) { if ($w.Current.Name -match 'PCMig 迁移工具') { $win = $w; break } }
$wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
if ($wp.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Normal) { $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal); Start-Sleep -Seconds 2 }
$win.SetFocus(); Start-Sleep -Milliseconds 800
$b = $win.Current.BoundingRectangle
$sc = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output ('屏幕 ' + $sc.Width + 'x' + $sc.Height + '  窗口 ' + [int]$b.X + ',' + [int]$b.Y + ' ' + [int]$b.Width + 'x' + [int]$b.Height)
function Shot($x, $y, $w, $h, $file) {
  if ($x -lt 0) { $x = 0 }; if ($y -lt 0) { $y = 0 }
  if ($x + $w -gt $sc.Width) { $w = $sc.Width - $x }
  if ($y + $h -gt $sc.Height) { $h = $sc.Height - $y }
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output ('  已存 ' + $file + '  (' + $w + 'x' + $h + ') 起于 ' + $x + ',' + $y)
}
Shot ([int]$b.X - 8) ([int]$b.Y - 40) 720 130 'E:\deepseek work\shot-titlebar.png'
Shot 0 ($sc.Height - 70) $sc.Width 70 'E:\deepseek work\shot-taskbar.png'
Write-Output 'SHOTS_DONE'
