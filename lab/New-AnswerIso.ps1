# 把 answer 目录打包成 ISO（Windows Setup 会扫描所有 CD/DVD 根目录的 autounattend.xml）
param(
    [string]$SourceDir = 'H:\deepseek work\PCMig\lab\answer',
    [string]$OutIso = 'H:\deepseek work\PCMig\lab\answer.iso'
)

$fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fsi.VolumeName = 'ANSWERS'
$fsi.FileSystemsToCreate = 7  # ISO9660 + Joliet + UDF
if (Test-Path $OutIso) { Remove-Item $OutIso -Force }
$fsi.Root.AddTree($SourceDir, $false)
$result = $fsi.CreateResultImage()

# 把 COM IStream 落成文件
$istream = [System.Runtime.InteropServices.ComTypes.IStream]$result.ImageStream
$stat = New-Object System.Runtime.InteropServices.ComTypes.STATSTG
$istream.Stat([ref]$stat, 1)
$len = [int]$stat.cbSize
$buffer = New-Object byte[] $len
$istream.Read($buffer, $len, [System.IntPtr]::Zero) | Out-Null
[System.IO.File]::WriteAllBytes($OutIso, $buffer)
"answer.iso 已生成: $([math]::Round((Get-Item $OutIso).Length/1KB,1)) KB"
