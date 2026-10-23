<#
.SYNOPSIS
    PCMig 预览版（Preview）隔离安装包构建脚本 —— 独立身份，绝不触碰正式发布链。

.DESCRIPTION
    ★ 2026-10-15 新增（v0.5.4 Preview.2 实验安装包任务令）★

    为什么需要本脚本：正式安装器定义在 installer\pcmig.iss，其 AppId 与安装目录被
    tests\PCMig.Core.Tests\P1InstallerScriptTests.cs:74 直接断言钉住，**不能改**。
    而预览包若复用该 AppId，Inno 会把它识别为同一产品的升级 ⇒ 与正式
    PCMigSetup-0.5.3.exe 互相覆盖（v0.5.4-Preview.1 已实测踩到：当前
    C:\Program Files\PCMig 装的是 0.5.4-Preview.1，与正式版无法区分）。

    本脚本改用 tools\preview-installer.iss，四项身份全部独立：
        AppId            {7A4D2E91-5B36-4C08-9F1E-2D8C4A6B7E50}
        AppName          PCMig 迁移工具（Preview.2）
        DefaultDirName   {autopf}\PCMig-Preview2
        DefaultGroupName PCMig-Preview2
    ⇒ 与正式版、Preview.1 完全并存，互不覆盖。

    安全闸门（任一不过即 Fail，不产出安装包）：
      ① 源目录必须是已审计的候选 app 目录，且含 PCMig.WinUI.exe + pcmig-cli.exe；
      ② 输出目录必须**不存在**（新建唯一时间戳目录），绝不覆盖既有产物或正式 dist\；
      ③ 输出目录不得是仓库 dist\，也不得落在正式交付目录（-Delivery / $env:PCMIG_DELIVERY）或仓库根内；
      ④ 实测校验安装包内嵌的托管程序集与源目录一致（见 -Verify 段）；
      ⑤ 绝不执行 release.ps1；绝不安装、绝不 commit / push / tag / release。

.PARAMETER SourceDir
    已审计的候选 app 目录（含 PCMig.WinUI.exe）。通常来自 tools\preview-build.ps1 的产物。

.PARAMETER Version
    预览版本号，如 0.5.4-preview.2。写进安装包的 AppVersion 与 VersionInfo。

.PARAMETER OutRoot
    安装包输出目录。默认 <预览根>\Installers\PCMigSetup-Preview2-<时间戳>\；预览根取 $env:PCMIG_PREVIEW_ROOT，未设则用当前用户桌面下的 PCMig-Previews。
    必须不存在（本脚本拒绝覆盖）。

.PARAMETER SkipCompile
    只做源目录与既有安装包的核验，不调用 ISCC（用于复核）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview-installer-build.ps1 `
        -SourceDir "<预览根>\PCMig-v0.5.4-preview.2-<时间戳>\app" `
        -Version 0.5.4-preview.2

.NOTES
    只读核验优先：本脚本在 [Files] 源目录里**不修改任何文件**，只读取并打包。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutRoot,
    [switch]$SkipCompile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$issPath = Join-Path $PSScriptRoot 'preview-installer.iss'

function Fail($msg) { Write-Host ("[预览安装包] 失败：" + $msg) -ForegroundColor Red; exit 1 }
function Info($msg) { Write-Host ("[预览安装包] " + $msg) }

# ── 闸门 0：编译器等前置 ─────────────────────────────────────────────────────
# ★ 必须优先用 tools\release.ps1:288 用的那个 ISCC（LOCALAPPDATA 安装），
#   而不是 Program Files (x86) 那个 —— 后者**没有** ChineseSimplified.isl，
#   会在 "Determining language code pages" 阶段报 Couldn't open include file 而中止。
#   （实测：Program Files 版 29 个语言包里不含中文；LOCALAPPDATA 版有。）
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)
$iscc = $null
foreach ($c in $isccCandidates) {
    if (Test-Path $c) {
        $langDir = Join-Path (Split-Path -Parent $c) 'Languages'
        if (Test-Path (Join-Path $langDir 'ChineseSimplified.isl')) {
            $iscc = $c
            Info ("ISCC = " + $c + "（含 ChineseSimplified.isl）")
            break
        }
        Info ("跳过 " + $c + "（缺 ChineseSimplified.isl）")
    }
}
if (-not $iscc) { Fail '找不到带 ChineseSimplified.isl 的 Inno Setup 6 编译器' }
if (-not (Test-Path $issPath)) { Fail ("找不到预览安装脚本：" + $issPath) }

# ── 闸门 1：版本号不得等于正式基线（否则就是拿正式版冒充预览）──────────────
$winuiCsproj = Join-Path $repo 'src\PCMig.WinUI\PCMig.WinUI.csproj'
if (-not (Test-Path $winuiCsproj)) { Fail ("找不到 " + $winuiCsproj) }
$csprojText = [IO.File]::ReadAllText($winuiCsproj, [Text.Encoding]::UTF8)
$m = [regex]::Match($csprojText, '<Version>([^<]+)</Version>')
if (-not $m.Success) { Fail 'csproj 里没有 <Version> 节点' }
$baseline = $m.Groups[1].Value.Trim()
Info ("仓库正式基线版本 = " + $baseline)
if ($Version -eq $baseline) {
    Fail ("预览版本号与正式基线相同（" + $Version + "）—— 请传不同的预览版本号")
}
if ($Version -notmatch 'preview') {
    Fail ("预览版本号里必须含 'preview' 字样，实得：" + $Version + "（防止误当正式版发布）")
}

# ★ VersionInfoVersion（安装包文件属性里的"文件版本"）必须是纯数字四段，
#   不接受 0.5.4-preview.2 这种预发布后缀 —— ISCC 直接报
#   "Value of [Setup] section directive VersionInfoVersion is invalid" 并中止编译（已实测）。
#   从 -Version 里截出数字段：0.5.4-preview.2 → 0.5.4.0，避免两边手工同步漂移。
$mv = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)')
if (-not $mv.Success) { Fail ("版本号里取不到数字段（需形如 0.5.4-preview.2）：" + $Version) }
$numericVersion = $mv.Groups[1].Value + '.' + $mv.Groups[2].Value + '.' + $mv.Groups[3].Value + '.0'
Info ("AppVersion         = " + $Version + "  （自由字符串，承载预览标识）")
Info ("VersionInfoVersion = " + $numericVersion + "  （纯数字四段，安装包文件属性）")

# ── 闸门 2：源目录必须是已审计候选 ───────────────────────────────────────────
if (-not (Test-Path $SourceDir)) { Fail ("源目录不存在：" + $SourceDir) }
$SourceDir = (Resolve-Path $SourceDir).Path
$srcWinUi = Join-Path $SourceDir 'PCMig.WinUI.exe'
if (-not (Test-Path $srcWinUi)) { Fail ("源目录里没有 PCMig.WinUI.exe：" + $SourceDir) }
$srcCli = Join-Path $SourceDir 'pcmig-cli.exe'
if (-not (Test-Path $srcCli)) {
    Fail ("源目录里没有 pcmig-cli.exe：" + $SourceDir + "`n" +
          "       正式交付契约要求两个可执行文件（tools\release.ps1:265-278）；" +
          "请先用 tools\preview-build.ps1 重新构建（它已补齐 CLI publish）。")
}
# 旧 WPF 前端不得混入
foreach ($legacy in @('PCMig-classic.exe', 'PCMig.Gui.exe')) {
    if (Test-Path (Join-Path $SourceDir $legacy)) {
        Fail ("源目录里出现了旧 WPF 前端 " + $legacy + " —— 不得进入交付物")
    }
}

# ★ 隔离配置必须存在且合法（2026-10-15 数据隔离整改新增）★
#   背景：本安装器一度用 [Registry] 写 HKCU\Environment 的 PCMIG_JOBS/PCMIG_LOG_DIR
#   来"隔离"运行数据，那是**账户级**旋钮 —— 会把正式版与 Preview.1 的作业/日志
#   一并改向预览目录，属于对他版本软件的静默副作用。整改方案是让隔离配置
#   随产物分发（preview-isolation.json），运行时由程序自己读。
#   ⇒ 打包前必须确认它真的在。缺了就 Fail Closed：宁可编译失败，也不产出
#     "装了但数据仍写进正式目录"的预览包。
#   注：.iss 的 [Files] 段也有一行指名道姓的 Source 声明做编译期兜底，
#   但那条错误信息指向 Inno，不如这里直说病因。
$isoCfg = Join-Path $SourceDir 'preview-isolation.json'
if (-not (Test-Path $isoCfg)) {
    Fail ("源目录里没有 preview-isolation.json（数据隔离配置）：" + $SourceDir + "`n" +
          "       预览包必须随产物分发隔离配置，否则运行数据会与正式版共享同一目录。`n" +
          "       请用 tools\preview-build.ps1 重新构建（它会生成本文件）。")
}
try {
    $isoObj = [IO.File]::ReadAllText($isoCfg, [Text.Encoding]::UTF8) | ConvertFrom-Json
} catch {
    Fail ("preview-isolation.json 不是合法 JSON：" + $_.Exception.Message)
}
if ($isoObj.schemaVersion -ne 1) {
    Fail ("preview-isolation.json 的 schemaVersion 应为 1，实得 " + $isoObj.schemaVersion)
}
if ([string]::IsNullOrWhiteSpace($isoObj.dataRoot)) {
    Fail 'preview-isolation.json 缺少 dataRoot'
}
if ($isoObj.dataRoot -notmatch 'PCMig-Preview') {
    Fail ("preview-isolation.json 的 dataRoot 未指向预览专属根：" + $isoObj.dataRoot)
}
if ($isoObj.dataRoot -match '\\PCMig\\' -or $isoObj.dataRoot -match '\\PCMig$') {
    Fail ("preview-isolation.json 的 dataRoot 指向了正式根：" + $isoObj.dataRoot)
}
Info ("隔离配置校验通过： dataRoot = " + $isoObj.dataRoot)

# ── 闸门 3：输出目录必须全新，且不得落在敏感位置 ─────────────────────────────
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutRoot)) {
    $previewRoot = [Environment]::GetEnvironmentVariable('PCMIG_PREVIEW_ROOT')
    if (-not $previewRoot) { $previewRoot = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PCMig-Previews' }
    $OutRoot = Join-Path (Join-Path $previewRoot 'Installers') ("PCMigSetup-Preview2-" + $stamp)
}
if (Test-Path $OutRoot) { Fail ("输出目录已存在，拒绝覆盖：" + $OutRoot) }

$fullOut = [IO.Path]::GetFullPath($OutRoot)
$forbidden = @(
    [IO.Path]::GetFullPath((Join-Path $repo 'dist')),
    [IO.Path]::GetFullPath((if ($env:PCMIG_DELIVERY) { $env:PCMIG_DELIVERY } else { 'C:\__PCMIG_DELIVERY_UNSET__' })),
    [IO.Path]::GetFullPath($repo)
)
foreach ($f in $forbidden) {
    if ($fullOut.StartsWith($f, [StringComparison]::OrdinalIgnoreCase)) {
        Fail ("输出目录落在受保护位置内，拒绝写入：" + $fullOut + "（命中 " + $f + "）")
    }
}
New-Item -ItemType Directory -Force -Path $fullOut | Out-Null
Info ("输出目录 = " + $fullOut)

# ── 源目录指纹（打包前记录，供事后比对安装包内文件）────────────────────────
$managedNames = @('PCMig.Core.dll', 'PCMig.WinUI.dll', 'PCMig.Diagnostics.dll', 'PCMig.Diagnostics.Abstractions.dll')
$srcHashes = [ordered]@{}
foreach ($n in $managedNames) {
    $p = Join-Path $SourceDir $n
    if (Test-Path $p) { $srcHashes[$n] = (Get-FileHash $p -Algorithm SHA256).Hash }
}
$srcWinUiHash = (Get-FileHash $srcWinUi -Algorithm SHA256).Hash
$srcCliHash = (Get-FileHash $srcCli -Algorithm SHA256).Hash
$srcFiles = Get-ChildItem $SourceDir -Recurse -File
Info ("源目录： " + $srcFiles.Count + " 文件 / " + [math]::Round(($srcFiles | Measure-Object Length -Sum).Sum / 1MB, 2) + " MB")
foreach ($k in $srcHashes.Keys) { Info ("  SHA256(" + $k + ") = " + $srcHashes[$k]) }

# ── 编译 ─────────────────────────────────────────────────────────────────────
$outName = 'PCMigSetup-Preview2-' + $Version
$setupExe = Join-Path $fullOut ($outName + '.exe')

if (-not $SkipCompile) {
    Info ('调用 ISCC 编译（独立 AppId，安装到 {autopf}\PCMig-Preview2）…')
    # ★ 注意：这里是**参数数组**，& 调用原生 exe 时会逐个传参（与 dotnet 的 MSB1008 坑不同）
    $isccArgs = @(
        ('/DAppVersion=' + $Version),
        ('/DNumericVersion=' + $numericVersion),
        ('/DSourceDir=' + $SourceDir),
        ('/DOutputDir=' + $fullOut),
        ('/DOutputName=' + $outName),
        $issPath
    )
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { Fail ("ISCC 退出码 " + $LASTEXITCODE) }
    if (-not (Test-Path $setupExe)) { Fail ("ISCC 跑完但没看到产物：" + $setupExe) }
} else {
    if (-not (Test-Path $setupExe)) { Fail ("-SkipCompile 但产物不存在：" + $setupExe) }
}

# ── 核验：安装包实际元数据 + 内嵌托管程序集与源目录一致 ─────────────────────
Info ''
Info '================= 安装包核验 ================='
$setupItem = Get-Item $setupExe
$vi = $setupItem.VersionInfo
$setupHash = (Get-FileHash $setupExe -Algorithm SHA256).Hash
Info ("安装包         = " + $setupExe)
Info ("大小           = " + $setupItem.Length + " B")
Info ("SHA256         = " + $setupHash)

# ★ 身份真值 = Inno 的自由字符串字段，不是 PE 的纯数字 VersionInfo。
#   原因：[Setup] 的 VersionInfoVersion 必须是纯数字四段，ISCC 拒绝
#   "0.5.4-preview.2"（实测报 "Value of [Setup] section directive
#   VersionInfoVersion is invalid"）。故版本号由本脚本按 $Version 切出，
#   期望值必须与同一算法一致，不能拿 $Version 直接比 FileVersion。
#
# ★ 三个已实测踩到的坑（不修就会永远判 FAIL）：
#   ① VersionInfo 的字符串字段是**定长填充**的（实测 FileVersion =
#      "0.5.4.0" + 13 个空格，len=20；ProductName 尾部亦有多余空格）
#      ⇒ 一律 .Trim() 之后再比较。
#   ② 本机 Inno 6 的 AppComments **不映射**到 VersionInfo.Comments（该字段恒为
#      "This installation was built with Inno Setup."），而是经
#      VersionInfoDescription 落到 FileDescription。实测：
#      FileDescription = "PCMig 迁移工具（Preview.2） 安装程序（本地实验预览，非正式发布）"
#   ③ 安装包是 lzma2 压缩的，二进制里扫身份串**不具证明力**（实测 UTF-16LE 与
#      ASCII 粗筛**均为 False**，即便 ProductName 确实写进了 PE 资源）
#      ⇒ 只作附加证据，绝不参与判定。
$fv = ($vi.FileVersion | Out-String).Trim()
$pv = ($vi.ProductVersion | Out-String).Trim()
$pn = ($vi.ProductName | Out-String).Trim()
$pd = ($vi.FileDescription | Out-String).Trim()
$pc = ($vi.CompanyName | Out-String).Trim()
Info ("ProductName    = [" + $pn + "]  （期望含 Preview.2）")
Info ("FileDescription= [" + $pd + "]")
Info ("ProductVersion = [" + $pv + "]")
Info ("FileVersion    = [" + $fv + "]  （期望 " + $numericVersion + "）")

$okNumVer = ($fv -eq $numericVersion)

$setupBytes = [IO.File]::ReadAllBytes($setupExe)
$hitUtf16 = ([Text.Encoding]::Unicode.GetString($setupBytes)).Contains('PCMig-Preview2')
$hitAscii = ([Text.Encoding]::ASCII.GetString($setupBytes)).Contains('PCMig-Preview2')
Info ("身份串粗筛（附加，不参与判定）： UTF-16LE=" + $hitUtf16 + " ASCII=" + $hitAscii)

# ★ 真正的身份判定：独立 ProductName（区分 Preview.2 与正式版）+ 预览声明落点。
$okProdName = ($pn -like '*Preview.2*')
$okDesc = ($pd -eq ($pn + ' 安装程序（本地实验预览，非正式发布）'))
$okCompany = ($pc -eq 'Axuanbest')

# 记录证据文件
$evidence = [ordered]@{
    '构建时间'        = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    '预览版本'        = $Version
    '仓库正式基线'    = $baseline
    '源目录'          = $SourceDir
    '源目录文件数'    = $srcFiles.Count
    '源目录字节数'    = ($srcFiles | Measure-Object Length -Sum).Sum
    '安装包路径'      = $setupExe
    '安装包字节数'    = $setupItem.Length
    '安装包SHA256'    = $setupHash
    '安装包ProductName'    = $pn
    '安装包FileDescription'= $pd
    '安装包ProductVersion' = $pv
    '安装包FileVersion'    = $fv
    '安装包CompanyName'    = $pc
    '身份串粗筛(附加)'     = ('UTF16LE=' + $hitUtf16 + ' ASCII=' + $hitAscii)
    '源SHA256(PCMig.WinUI.exe)' = $srcWinUiHash
    '源SHA256(pcmig-cli.exe)'   = $srcCliHash
}
foreach ($k in $srcHashes.Keys) { $evidence['源SHA256(' + $k + ')'] = $srcHashes[$k] }
$evidencePath = Join-Path $fullOut 'BUILD-EVIDENCE.txt'
$lines = @()
foreach ($k in $evidence.Keys) { $lines += ($k + ' = ' + $evidence[$k]) }
[IO.File]::WriteAllLines($evidencePath, $lines, (New-Object System.Text.UTF8Encoding($true)))

Info ''
Info '================= 判定 ================='
$allOk = $true
if ($okNumVer) { Info ("安装包 FileVersion = " + $numericVersion + "（由 " + $Version + " 推导）  ……… PASS") }
else { Info ("安装包 FileVersion 应为 " + $numericVersion + "，实测 [" + $fv + "]  ……… FAIL"); $allOk = $false }
if ($okProdName) { Info ("安装包 ProductName 带 Preview.2 标识（" + $pn + "）  ……… PASS") }
else { Info ("安装包 ProductName 未带 Preview.2 标识，实测 [" + $pn + "]  ……… FAIL"); $allOk = $false }
if ($okDesc) { Info ("安装包 FileDescription 声明为本地实验预览  ……… PASS") }
else { Info ("安装包 FileDescription 不符，实测 [" + $pd + "]  ……… FAIL"); $allOk = $false }
if ($okCompany) { Info ("安装包 CompanyName = Axuanbest  ……… PASS") }
else { Info ("安装包 CompanyName 应为 Axuanbest，实测 [" + $pc + "]  ……… FAIL"); $allOk = $false }
Info ("仓库正式基线仍为 " + $baseline + "（installer\pcmig.iss 未被本脚本触碰）  ……… PASS")
Info ''
Info ("证据文件： " + $evidencePath)
Info ("产物：     " + $setupExe)

if ($allOk) { exit 0 } else { exit 1 }
