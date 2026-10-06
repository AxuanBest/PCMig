
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = 'E:\deepseek work\PCMig\src\PCMig.Gui\bin\Release\net8.0-windows\PCMig.exe'
Get-Process PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 16
if ($p.HasExited) { Write-Output ('启动失败 ExitCode=' + $p.ExitCode); exit 1 }
$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output '未找到窗口'; exit 1 }
$r = $win.Current.BoundingRectangle
Write-Output ('窗口: ' + $win.Current.Name + ' 尺寸 ' + [int]$r.Width + 'x' + [int]$r.Height)
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ('控件总数: ' + $all.Count)
$steps = @()
foreach ($e in $all) { if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $steps += $e } }
Write-Output ('导轨条目: ' + $steps.Count)
foreach ($s in $steps) {
  $b = $s.Current.BoundingRectangle
  $nm = $s.Current.Name.Replace([char]13, ' ').Replace([char]10, ' ')
  Write-Output ('   [' + $nm + ']  ' + [int]$b.X + ',' + [int]$b.Y + '  ' + [int]$b.Width + 'x' + [int]$b.Height)
}
Write-Output ''
