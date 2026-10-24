# PCMig 发布候选（RC）构建脚本 —— v0.5.5-rc.1 起使用
#
# 用途：从**当前工作副本或干净检出**产出一个与正式版、预览版、v0.5.4-rc.1 全部隔离的
#       RC 安装包。四项身份（AppId / 安装目录 / 数据根 / 快捷方式名）由
#       tools\rc-installer-v055.iss 与随产物分发的 preview-isolation.json 共同决定。
#
# 为什么不用 tools\preview-build.ps1：
#   该脚本的隔离配置自检把 dataRoot 限定为 `PCMig-Preview[0-9]`（见其 :280），
#   而 RC 链的数据根是 %LocalAppData%\PCMig-RC1-v055 —— 会被它自己的闸门判 FAIL。
#   它属于 Preview 链，改它会牵动已发布的 Preview.3~.7 行为，因此另起本脚本。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\rc-build.ps1
#   powershell ... -File tools\rc-build.ps1 -Version 0.5.5-rc.1 -SkipBuild
#
# 纪律：不安装、不启动、不 commit / push / tag / release；绝不写仓库 dist\；
#       绝不产出 dist\PCMigSetup-<正式版本>.exe。

param(
    [string]$Version = '0.5.5-rc.1',
    [string]$NumericVersion = '0.5.5.0',
    [string]$RepoRoot = '',
    [string]$OutDir = 'I:\Project\PCMig\ReleaseCandidates\v0.5.5-rc.1',
    [string]$IsolationDataRoot = '%LocalAppData%\PCMig-RC1-v055',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path }
$repoPath = (Resolve-Path -LiteralPath $RepoRoot).Path

function Info([string]$m) { Write-Output ('[rc] ' + $m) }
function Fail([string]$m) { Write-Output ('[rc] ✗ ' + $m); exit 1 }

function Run([string]$label, [string]$exe, [string[]]$argv) {
    Info ('→ ' + $label)
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { Fail ($label + ' 失败（退出码 ' + $LASTEXITCODE + '）') }
}

# ── 0. 前置检查 ─────────────────────────────────────────────────────────────
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) { Fail ('找不到 Inno Setup 6 编译器：' + $iscc) }
$issPath = Join-Path $repoPath 'tools\rc-installer-v055.iss'
if (-not (Test-Path -LiteralPath $issPath)) { Fail ('找不到安装脚本：' + $issPath) }

# 输出目录必须是 RC 专用目录，且不得落在仓库内（防止污染工作树 / 误覆盖正式交付根）
$outFull = [IO.Path]::GetFullPath($OutDir)
if (-not ($outFull -match '\\ReleaseCandidates\\v0\.5\.5-rc\.1$')) {
    Fail ('输出目录必须是 ...\ReleaseCandidates\v0.5.5-rc.1（收到：' + $outFull + '）')
}
if ($outFull.StartsWith($repoPath, [StringComparison]::OrdinalIgnoreCase)) {
    Fail ('输出目录不得位于仓库内：' + $outFull)
}

$stageRoot = Join-Path $env:TEMP ('pcmig-rc-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$cliDir = Join-Path $stageRoot 'cli'
$appDir = Join-Path $stageRoot 'app'
New-Item -ItemType Directory -Force -Path $cliDir, $appDir | Out-Null

Info ('仓库      = ' + $repoPath)
Info ('版本      = ' + $Version + '（定长版本 ' + $NumericVersion + '）')
Info ('暂存根    = ' + $stageRoot)
Info ('输出目录  = ' + $outFull)
Info ('数据根    = ' + $IsolationDataRoot)

# ── 1. 发布 CLI（单文件）与 WinUI（自包含）─────────────────────────────────
# ★ 版本参数必须先拼成变量再进数组：PS 5.1 里 @( ..., '-p:Version=' + $Version, ... )
#   不会把 + 作用于该元素，MSBuild 会收到裸的 '-p:Version=' 与游离的 '0.5.5-rc.1'，
#   报 MSB1008「只能指定一个项目」（2026-10-24 实测踩过）。
$pVer = '-p:Version=' + $Version
$pFileVer = '-p:FileVersion=' + $NumericVersion
$pInfoVer = '-p:InformationalVersion=' + $Version

if (-not $SkipBuild) {
    Run 'publish CLI' 'dotnet' @(
        'publish', 'src\PCMig.Cli\PCMig.Cli.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true',
        $pVer, $pFileVer, $pInfoVer,
        '-o', $cliDir, '--nologo', '-v', 'minimal')
    Run 'publish WinUI' 'dotnet' @(
        'publish', 'src\PCMig.WinUI\PCMig.WinUI.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:Platform=x64',
        $pVer, $pFileVer, $pInfoVer,
        '-o', $appDir, '--nologo', '-v', 'minimal')
} else {
    Info '★ -SkipBuild：跳过编译，直接使用暂存目录已有内容（仅用于重打包）'
}

# ── 2. 组装 app 目录 ────────────────────────────────────────────────────────
$cliExe = Join-Path $cliDir 'pcmig.exe'
if (-not (Test-Path -LiteralPath $cliExe)) { Fail ('CLI 产物缺失：' + $cliExe) }
Copy-Item -LiteralPath $cliExe -Destination (Join-Path $appDir 'pcmig-cli.exe') -Force

foreach ($doc in @('使用说明.txt', '更新日志.txt')) {
    $srcDoc = Join-Path $repoPath ('docs\' + $doc)
    if (-not (Test-Path -LiteralPath $srcDoc)) { Fail ('文档缺失：' + $srcDoc) }
    Copy-Item -LiteralPath $srcDoc -Destination (Join-Path $appDir $doc) -Force
}

# 旧 WPF 前端不得进入交付物（与正式链同一纪律）
foreach ($legacy in @('PCMig-classic.exe', 'PCMig.exe')) {
    if (Test-Path -LiteralPath (Join-Path $appDir $legacy)) {
        Fail ('app 目录里出现旧 WPF 前端 ' + $legacy + ' —— 只允许 PCMig.WinUI.exe 与 pcmig-cli.exe')
    }
}

# ── 3. 写隔离配置（RC 身份的数据根，唯一事实来源）──────────────────────────
$isoPath = Join-Path $appDir 'preview-isolation.json'
$isoRoot = $IsolationDataRoot.Replace('\', '\\')
$isoJson = @(
    '{'
    '  "schemaVersion": 1,'
    '  "dataRoot": "' + $isoRoot + '",'
    '  "note": "PCMig 发布候选（RC）运行数据根。仅当程序集 InformationalVersion 含 -rc. 时生效；正式产物不读本文件。不得指向 %ProgramData%\\PCMig、%LocalAppData%\\PCMig 或 v0.5.4-rc.1 的数据根。"'
    '}'
) -join "`r`n"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($isoPath, $isoJson + "`r`n", $utf8NoBom)
Info ('隔离配置已写入：' + $isoPath)

# ── 4. 产物元数据核验（以文件真值为准，不看源码声明）──────────────────────
$winuiExe = Join-Path $appDir 'PCMig.WinUI.exe'
if (-not (Test-Path -LiteralPath $winuiExe)) { Fail ('产物里没有 PCMig.WinUI.exe') }
$vi = (Get-Item -LiteralPath $winuiExe).VersionInfo
Info ('PCMig.WinUI.exe  ProductVersion=' + $vi.ProductVersion + '  FileVersion=' + $vi.FileVersion)
if ($vi.ProductVersion -notlike ($Version + '*')) {
    Fail ('PCMig.WinUI.exe 的 ProductVersion 是「' + $vi.ProductVersion + '」，应含「' + $Version + '」')
}
$cliInfo = (Get-Item -LiteralPath (Join-Path $appDir 'pcmig-cli.exe')).VersionInfo
Info ('pcmig-cli.exe    ProductVersion=' + $cliInfo.ProductVersion)
if ($cliInfo.ProductVersion -notlike ($Version + '*')) {
    Fail ('pcmig-cli.exe 的 ProductVersion 是「' + $cliInfo.ProductVersion + '」，应含「' + $Version + '」')
}

# ── 5. 编译安装包 ──────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $outFull | Out-Null
$outputName = 'PCMigSetup-' + $Version
Run 'ISCC' $iscc @(
    ('/DAppVersion=' + $Version),
    ('/DNumericVersion=' + $NumericVersion),
    ('/DSourceDir=' + $appDir),
    ('/DOutputDir=' + $outFull),
    ('/DOutputName=' + $outputName),
    $issPath)

# ── 6. 安装包核验 ──────────────────────────────────────────────────────────
$setupExe = Join-Path $outFull ($outputName + '.exe')
if (-not (Test-Path -LiteralPath $setupExe)) { Fail ('安装包未生成：' + $setupExe) }
$setupItem = Get-Item -LiteralPath $setupExe
$hash = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash
Info ('安装包  = ' + $setupItem.FullName)
Info ('  大小  = ' + $setupItem.Length + ' B')
Info ('  SHA256= ' + $hash)
Info ('  FileVersion=' + $setupItem.VersionInfo.FileVersion + '  ProductVersion=' + $setupItem.VersionInfo.ProductVersion)

$deliverRoot = 'I:\Project\PCMig'
if ($setupExe.StartsWith($deliverRoot, [StringComparison]::OrdinalIgnoreCase) -and
    -not $setupExe.StartsWith((Join-Path $deliverRoot 'ReleaseCandidates'), [StringComparison]::OrdinalIgnoreCase)) {
    Fail '安装包被写进正式交付根（非 ReleaseCandidates）—— 立即中止'
}

Info '完成。未安装、未启动、未 commit / push / tag。'
