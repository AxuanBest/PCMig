
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$all = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
function Cond($t) { New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t) }
$p = Get-Process PCMig | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Output ('窗口: ' + $win.Current.Name)
function Rect($e) { $b = $e.Current.BoundingRectangle; return ('x=' + [int]$b.X + ' y=' + [int]$b.Y + ' w=' + [int]$b.Width + ' h=' + [int]$b.Height) }
function SpeedTexts {
  $out = @()
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
    if ($e.Current.IsOffscreen) { continue }
    if ($e.Current.Name -match '^[\d.]+ (B|KB|MB|GB)/s$') { $out += $e.Current.Name }
  }
  return ($out -join ' | ')
}
function NicBytes { return (Get-NetAdapterStatistics | ForEach-Object { $_.ReceivedBytes + $_.SentBytes } | Measure-Object -Sum).Sum }

Write-Output '--- ① 底栏网速控件结构 ---'
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::Text))) {
  if ($e.Current.IsOffscreen) { continue }
  $b = $e.Current.BoundingRectangle
  if ([int]$b.Y -gt 1330 -and [int]$b.X -gt 250 -and [int]$b.X -lt 700) { Write-Output ('  ' + (Rect $e) + ' :: ' + $e.Current.Name) }
}
Write-Output ('  网上所有速率文本: ' + (SpeedTexts))

Write-Output '--- ② 空闲 3 秒：GUI 显示 vs 独立 API ---'
$a1 = NicBytes; Start-Sleep -Seconds 3; $a2 = NicBytes
Write-Output ('  Get-NetAdapterStatistics: ' + [math]::Round(($a2 - $a1) / 3 / 1KB, 1) + ' KB/s')
Write-Output ('  GUI 底栏显示: ' + (SpeedTexts))

Write-Output '--- ③ 制造真实流量（下载 60MB）后同时读数 ---'
$uri = 'https://speed.cloudflare.com/__down?bytes=60000000'
$out = Join-Path $env:TEMP 'pcmig_spd.bin'
$dl = Start-Process powershell -ArgumentList '-NoProfile','-Command',('Invoke-WebRequest -Uri "' + $uri + '" -OutFile "' + $out + '" -UseBasicParsing') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
for ($i = 1; $i -le 5; $i++) {
  $b1 = NicBytes; Start-Sleep -Seconds 2; $b2 = NicBytes
  $api = [math]::Round(($b2 - $b1) / 2 / 1MB, 2)
  Write-Output ('  [' + $i + '] 独立 API=' + $api + ' MB/s   GUI=' + (SpeedTexts))
}
if ($dl -and -not $dl.HasExited) { $dl | Stop-Process -Force }
if (Test-Path $out) { Write-Output ('  下载文件大小: ' + [math]::Round((Get-Item $out).Length / 1MB, 1) + ' MB'); Remove-Item $out -Force }

Write-Output '--- ④ 迁移页卡片 / 结果页按钮与复选框 ---'
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (Cond $CT::List))
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $all)
Write-Output ('  导轨项数=' + $items.Count)
$items[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen) { continue }
  if ($e.Current.Name -match '传输速度|任务实测|网卡实时') { Write-Output ('  [③页] ' + (Rect $e) + ' :: ' + $e.Current.Name) }
}
$items[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900
foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)) {
  if ($e.Current.IsOffscreen) { continue }
  if ($e.Current.Name -match '尝试修复|强制覆盖|验证完整性') {
    $extra = ''
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.CheckBox') {
      try { $extra = ' ToggleState=' + $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { $extra = ' ToggleState=?' }
    }
    Write-Output ('  [④页] ' + (Rect $e) + ' enabled=' + $e.Current.IsEnabled + $extra + ' :: ' + $e.Current.Name)
  }
}
Write-Output 'UIA19_DONE'
