
param(
  [Parameter(Mandatory = $true)][string]$Path,
  [string]$OutputPath = (Join-Path $env:TEMP 'paste-ocr.txt')
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation1' }).Count
# 取 AsTask 泛型方法（名称匹配，避免反引号）
$m = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.FullName -like '*IAsyncOperation*' })[0]
function Await($op, $type) {
  $t = $m.MakeGenericMethod($type).Invoke($null, @($op))
  $t.Wait(-1) | Out-Null
  return $t.Result
}
Write-Output ('可用 OCR 语言: ' + (([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag }) -join ', '))
$eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($eng -eq $null) { $eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('zh-Hans-CN')) }
if ($eng -eq $null) { Write-Output '无可用 OCR 引擎'; exit }
Write-Output ('引擎语言: ' + $eng.RecognizerLanguage.LanguageTag)
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
Write-Output ('图片尺寸: ' + $decoder.PixelWidth + 'x' + $decoder.PixelHeight)
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$res = Await ($eng.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
Write-Output ('--- OCR 行数 ' + $res.Lines.Count + ' ---')
$txt = ($res.Lines | ForEach-Object { $_.Text }) -join [char]10
# 脱敏：疑似密钥/口令一律打码
$txt = $txt -replace '(?i)(apikey|api_key|token|password|passwd|sk-)[^\s]{6,}', '$1=***'
$txt = $txt -replace '\b[A-Za-z0-9+/]{32,}={0,2}\b', '***'
$txt = $txt -replace '(SRC-PC-2|<口令已脱敏>|<口令已脱敏>)', '***'
[IO.File]::WriteAllText($OutputPath, $txt, (New-Object Text.UTF8Encoding($true)))
Write-Output $txt
Write-Output 'OCR_DONE'
