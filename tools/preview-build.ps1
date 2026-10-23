<#
.SYNOPSIS
    PCMig 本地预览（Preview）隔离构建脚本 —— 版本号经 MSBuild 参数注入，不改仓库默认配置。

.DESCRIPTION
    ★ 2026-10-15 新增（修复 R7 共享版本污染）★

    问题：v0.5.4 Preview.2 曾把 src\PCMig.WinUI\PCMig.WinUI.csproj 的
    <Version> / <InformationalVersion> 直接改成 0.5.4-preview.2。但
    tools\release.ps1（L114-115 与 L132-135）会读这两个节点做正式发版自查 ⇒
    仓库里常驻 Preview 版本号，会把正式版的版本身份带偏。

    方案：仓库里的 <Version> 永远只表示**正式版本基线**（当前 0.5.3）。
    Preview 身份改由本脚本在命令行注入：
        dotnet publish ... -p:Version=<preview 版本> -p:InformationalVersion=<同值>
    MSBuild 命令行属性优先级高于 csproj 内的属性 ⇒ 注入生效但不落盘、不进 git diff。

    验收真值来源：MainWindow.xaml.cs 的 ApplyDisplayVersion() 在运行时从程序集读
    AssemblyInformationalVersion 驱动标题栏三处版本串 ⇒ 界面显示必然跟随实际构建，
    不会与 csproj 脱节。**不得**以"XAML 里 Patch 成功"代替版本验证（见下方 -Verify）。

.PARAMETER Version
    预览版本号（如 0.5.4-preview.2）。**不得**与正式基线相同，否则等同于拿正式版冒充预览。

.PARAMETER OutRoot
    产物根目录。默认写到 <预览根>\PCMig-v<Version>-<时间戳>\；预览根取 $env:PCMIG_PREVIEW_ROOT，未设则用当前用户桌面下的 PCMig-Previews。
    每次调用都新建唯一时间戳目录，**绝不覆盖**既有预览产物。

.PARAMETER SkipBuild
    只跑身份核验，不重新构建（用于复核既有产物）。

.PARAMETER IsolationDataRoot
    ★ 2026-10-15 新增（数据隔离整改）★ 预览版运行数据根，写入 app 目录下的
    preview-isolation.json。默认 %LocalAppData%\PCMig-Preview2。
    **只有**预览身份（程序集 InformationalVersion 含 -preview）的程序会读它；
    正式产物完全不读，因此本文件的存在不影响正式版行为。
    传 "" 表示不生成隔离配置（仅供对照实验，正常构建不要这么用）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\preview-build.ps1 -Version 0.5.4-preview.2

.NOTES
    本脚本只做本地构建与只读核验：
      · 不调用 tools\release.ps1
      · 不生成安装器、不安装、不覆盖正式安装与正式交付目录
      · 不 commit / push / tag / release
    正式发布流程（release.ps1）保持不变，本脚本是它的**旁路**，不是替代。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutRoot,
    [switch]$SkipBuild,
    [string]$IsolationDataRoot = '%LocalAppData%\PCMig-Preview2'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot          # tools\ 的父目录 = 仓库根
$csproj = Join-Path $repo 'src\PCMig.WinUI\PCMig.WinUI.csproj'
$exeName = 'PCMig.WinUI.exe'

function Fail($msg) { Write-Host ("[预览构建] 失败：" + $msg) -ForegroundColor Red; exit 1 }
function Info($msg) { Write-Host ("[预览构建] " + $msg) }

# ── 闸门 1：版本号不得等于 csproj 里的正式基线 ────────────────────────────────
if (-not (Test-Path $csproj)) { Fail ("找不到 " + $csproj) }
$csprojText = [IO.File]::ReadAllText($csproj, [Text.Encoding]::UTF8)
$m = [regex]::Match($csprojText, '<Version>([^<]+)</Version>')
if (-not $m.Success) { Fail 'csproj 里没有 <Version> 节点（release.ps1 会因此中止，请先补上）' }
$baseline = $m.Groups[1].Value.Trim()
Info ("csproj 正式基线版本 = " + $baseline)
if ($Version -eq $baseline) {
    Fail ("注入版本与正式基线相同（" + $Version + "）—— 那就不是预览构建，请传不同的预览版本号")
}

# ── 闸门 2：产物目录（新建唯一时间戳目录，绝不覆盖既有产物）────────────────────
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutRoot)) {
    $previewRoot = [Environment]::GetEnvironmentVariable('PCMIG_PREVIEW_ROOT')
    if (-not $previewRoot) { $previewRoot = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PCMig-Previews' }
    $OutRoot = Join-Path $previewRoot ("PCMig-v" + $Version + "-" + $stamp)
}
if (Test-Path $OutRoot) { Fail ("目标目录已存在，拒绝覆盖：" + $OutRoot) }
$appDir = Join-Path $OutRoot 'app'
$cliDir = Join-Path $OutRoot 'cli'
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Info ("产物目录 = " + $OutRoot)

# ── 构建 ──────────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Info ("开始构建并注入版本 " + $Version + " …")
    # 注意：不能用 $args（PowerShell 自动变量，只读）——用 $msbuildArgs。
    # ★ 坑（2026-10-15）：`& dotnet @msbuildArgs`（数组 splat）会被拼成含空格的多参数，
    #   MSBuild 报 "MSB1008: 只能指定一个项目"。改成拼单条命令字符串交给 cmd /c 执行。
    $verArgs = '-p:Version=' + $Version + ' -p:InformationalVersion=' + $Version
    $winuiCmd = 'dotnet publish "' + $csproj + '" -c Release -p:Platform=x64 --self-contained true ' + $verArgs + ' -o "' + $appDir + '" --nologo -v minimal'
    Info '· publish WinUI 主界面'
    cmd /c $winuiCmd
    if ($LASTEXITCODE -ne 0) { Fail ("WinUI dotnet publish 退出码 " + $LASTEXITCODE) }

    # ★ 2026-10-15 补：正式交付契约要求**两个可执行文件**（PCMig.WinUI.exe + pcmig-cli.exe，
    #   见 tools\release.ps1:265-278 与 installer\pcmig.iss 的 [Icons]）。早期预览构建只 publish
    #   了 WinUI，导致安装器 [Icons] 引用 pcmig-cli.exe 时源文件缺失。这里补齐 CLI。
    $cliProj = Join-Path $repo 'src\PCMig.Cli\PCMig.Cli.csproj'
    if (Test-Path $cliProj) {
        $cliCmd = 'dotnet publish "' + $cliProj + '" -c Release -r win-x64 --self-contained true ' +
                  '-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ' + $verArgs +
                  ' -o "' + $cliDir + '" --nologo -v minimal'
        Info '· publish CLI（pcmig-cli.exe，对齐正式交付契约）'
        cmd /c $cliCmd
        if ($LASTEXITCODE -ne 0) { Fail ("CLI dotnet publish 退出码 " + $LASTEXITCODE) }
        $cliExe = Join-Path $cliDir 'pcmig.exe'
        if (Test-Path $cliExe) {
            Copy-Item $cliExe (Join-Path $appDir 'pcmig-cli.exe') -Force
            Info '  → 已并入 app\pcmig-cli.exe'
        } else {
            Fail ("CLI publish 产物里没有 pcmig.exe：" + $cliDir)
        }
    } else {
        Fail ("找不到 CLI 工程：" + $cliProj)
    }

    # 旧 WPF 前端不得进入预览交付物（与 release.ps1:278 同一纪律）
    foreach ($legacy in @('PCMig-classic.exe', 'PCMig.Gui.exe')) {
        if (Test-Path (Join-Path $appDir $legacy)) {
            Fail ("产物里出现了旧 WPF 前端 " + $legacy + " —— v0.5.1 起不得进入交付物")
        }
    }
}

# ── ★ 2026-10-15 新增（数据隔离整改）★ 生成随产物分发的隔离配置 ──────────────
# 为什么放在 app 目录而不是写注册表/环境变量：
#   之前预览安装器写 HKCU\Environment 的 PCMIG_JOBS / PCMIG_LOG_DIR，那是**账户级**旋钮，
#   会把正式版与 Preview.1 的作业和日志一并改向预览目录（静默、不可测、卸载异常就残留）。
#   改为随产物分发后：只有预览产物自己的目录里有这个文件 ⇒ 正式版天然不受影响，
#   与"怎么启动"无关，卸载随安装目录消失，且**每条行为都能被进程内测试断言**。
# 判定身份看程序集 InformationalVersion 是否含 "-preview"，不看文件名/目录名。
# 完整语义与 Fail Closed 设计见 src\PCMig.Core\Util\PreviewIsolation.cs。
if (-not [string]::IsNullOrWhiteSpace($IsolationDataRoot)) {
    $isoPath = Join-Path $appDir 'preview-isolation.json'
    $isoRoot = $IsolationDataRoot.Replace('\', '\\')
    $isoJson = @(
        '{'
        '  "schemaVersion": 1,'
        '  "dataRoot": "' + $isoRoot + '",'
        '  "note": "PCMig 预览版运行数据根。仅当程序集 InformationalVersion 含 -preview 时生效；正式产物不读本文件。不得指向 %ProgramData%\\PCMig 或 %LocalAppData%\\PCMig。"'
        '}'
    ) -join "`r`n"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($isoPath, $isoJson + "`r`n", $utf8NoBom)
    Info ("隔离配置已写入：" + $isoPath)
    Info ("  dataRoot = " + $IsolationDataRoot)
} else {
    Info '★ 注意：本次构建**未**生成 preview-isolation.json（IsolationDataRoot 为空）——'
    Info '  该产物在预览身份下会 Fail Closed 拒绝启动，仅供对照实验使用。'
}

# ── 核验：以**产物实际元数据**为真值，不以 XAML/源码声明为准 ──────────────────
$exe = Join-Path $appDir $exeName
if (-not (Test-Path $exe)) { Fail ("产物里没有 " + $exe) }

$vi = (Get-Item $exe).VersionInfo
Write-Host ''
Info '================= 身份核验（读产物实际元数据）================='
Info ("EXE            = " + $exe)
Info ("FileVersion    = " + $vi.FileVersion)
Info ("ProductVersion = " + $vi.ProductVersion)

# 从 ProductVersion 里取出主版本段（可能带 +<commit> 后缀）与期望值比对
$pv = $vi.ProductVersion
$pvMain = if ($pv -and $pv.Contains('+')) { $pv.Substring(0, $pv.IndexOf('+')) } else { $pv }
$okPv = ($pvMain -eq $Version)

# ── csproj 基线未被改动（这是"预览版不会污染正式版配置"的核心保证）──────────
$okBaseline = (Select-String -Path $csproj -Pattern ('<Version>' + [regex]::Escape($baseline) + '</Version>') -Quiet) -eq $true

# ── 读程序集本身，核对 AssemblyInformationalVersion（这才是 UI 真值来源）────
# 注意：这里只做**字节流粗筛**。它在 UTF-8 解码下命中，只能说明该串在文件里出现过，
# 不能证明它确实写进了 AssemblyInformationalVersion 属性槽（可能来自别处的元数据）。
# 因此本项判定语义限定为"程序集内含目标版本串"，精确的版本真值以 ProductVersion 与
# 运行期 ApplyDisplayVersion() 实测为准（后者见 UIA 取证）。
$coreDll = Join-Path $appDir 'PCMig.WinUI.dll'
$okInfo = $null
if (Test-Path $coreDll) {
    $bytes = [IO.File]::ReadAllBytes($coreDll)
    # 同时按 UTF-8 与 UTF-16LE 找（.NET 的 #US 字符串堆是 UTF-16LE）
    $okInfo = ([Text.Encoding]::UTF8.GetString($bytes)).Contains($Version) -or
              ([Text.Encoding]::Unicode.GetString($bytes)).Contains($Version)
} else {
    # 单文件/裁剪产物可能没有独立 dll，退回用 exe 字节流
    $bytes = [IO.File]::ReadAllBytes($exe)
    $okInfo = ([Text.Encoding]::UTF8.GetString($bytes)).Contains($Version) -or
              ([Text.Encoding]::Unicode.GetString($bytes)).Contains($Version)
}

$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
$files = Get-ChildItem $appDir -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)

# ── ★ 产物指纹：不能只报 exe 的哈希 ──────────────────────────────────────────
# 踩过的坑（2026-10-15）：PCMig.WinUI.exe 是 **apphost 存根**（原生启动器，不含托管代码），
# 所有业务逻辑都在同目录的 .dll 里。两次内容不同的构建会得到**完全相同的 exe 哈希**
# （实测两次不同构建的 exe 均为 EF86B984…），因此单独报 exe 哈希会让身份核验**看似通过而实际失效**。
# 真正的产物身份必须覆盖托管程序集。
$managedNames = @('PCMig.Core.dll', 'PCMig.WinUI.dll', 'PCMig.Diagnostics.dll', 'PCMig.Diagnostics.Abstractions.dll')
$managedHash = @{}
$fingerprintInput = ''
foreach ($n in $managedNames) {
    $p = Join-Path $appDir $n
    if (Test-Path $p) {
        $h = (Get-FileHash $p -Algorithm SHA256).Hash
        $managedHash[$n] = $h
        $fingerprintInput += ($n + ':' + $h + ';')
    }
}
# ── 必需程序集：必须是**指名**的这两个，不能只看"找到几个" ────────────────────
# 踩过的坑（2026-10-15 负向测试）：最初写成 `$managedHash.Count -ge 2`，结果在一个
# **故意删掉 Core.dll 与 WinUI.dll** 的目录里，只剩 Diagnostics + Diagnostics.Abstractions
# 也满足"≥2 个"，判定照样 PASS —— 指纹在缺失关键程序集时仍然"可用"，闸门形同虚设。
# 指纹的意义在于覆盖承载业务逻辑的那两个程序集，因此必须点名断言。
$essentialNames = @('PCMig.Core.dll', 'PCMig.WinUI.dll')
$missingEssential = @($essentialNames | Where-Object { -not $managedHash.ContainsKey($_) })
$okManaged = ($missingEssential.Count -eq 0)

# 指纹 = 所有托管程序集哈希的再哈希（稳定、可跨构建比较）
$fingerprint = $null
if ($fingerprintInput.Length -gt 0) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $fpBytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintInput))
    $fingerprint = ($fpBytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

Info ("SHA256(EXE)    = " + $sha + "   ← apphost 存根，两次不同构建也会相同，不可作为身份依据")
foreach ($k in $managedHash.Keys) {
    Info ("SHA256(" + $k + ") = " + $managedHash[$k])
}
Info ("构建指纹       = " + $fingerprint + "   ← 覆盖托管程序集，真正的产物身份")
Info ("文件数 / 体积  = " + $files.Count + " / " + $totalMb + " MB")
Info ''
Write-Host ''
Info '================= 判定 ================='
$allOk = $true
if ($okPv) { Info ("ProductVersion 含 " + $Version + "  ……… PASS") }
else { Info ("ProductVersion 含 " + $Version + "  ……… FAIL（实测 " + $pv + "）"); $allOk = $false }

if ($okInfo -eq $true) { Info ("程序集内含 " + $Version + " 串  ……… PASS") }
elseif ($okInfo -eq $false) { Info ("程序集内含 " + $Version + " 串  ……… FAIL"); $allOk = $false }
else { Info ("程序集内含 " + $Version + " 串  ……… UNKNOWN（读不出，不猜）") }

    Info ("csproj 基线仍为 " + $baseline + "，未被本脚本改动  ……… " + $(if ($okBaseline) { 'PASS' } else { 'FAIL' }))
    if (-not $okBaseline) {
        Write-Warning ("csproj 里的 <Version>" + $baseline + "</Version> 基线不见了 —— 说明本脚本（或别的操作）改动了公共版本配置。")
        $allOk = $false
    }

# 托管程序集指纹必须覆盖必需程序集，否则「身份核验」其实是空的
if ($okManaged) { Info ("托管程序集指纹可用（必需程序集齐全，共 " + $managedHash.Count + " 个）  ……… PASS") }
else { Info ("托管程序集指纹可用  ……… FAIL（缺少必需程序集：" + ($missingEssential -join ', ') + "，无法确立产物身份）"); $allOk = $false }

# ── ★ 2026-10-15 新增（数据隔离整改）★ 隔离配置必须随产物发出且合法 ──────────
# 判据有四条，缺一不可：
#   ① 文件存在          —— 缺了它，预览版会 Fail Closed 拒绝启动（这是有意设计）
#   ② schemaVersion=1   —— 运行时严格校验，版本不符即拒绝
#   ③ dataRoot 存在且含 PCMig-Preview<N> —— 确认指向预览专属根而非兜底值
#      （2026-10-17 放宽：原判据写死 PCMig-Preview2，导致 Preview.3 独立根被判 FAIL；
#        现按 PCMig-Preview + 数字识别任意预览线根名，仍拒绝正式根与兜底值）
#   ④ dataRoot 不含 \PCMig\ 正式根片段  —— 静态兜底，运行时会再判一次
# 第 ④ 条只是静态粗筛（权威判定在 PreviewIsolation.Evaluate + PreviewIsolationTests），
# 这里是为了让"打错路径"在构建期就暴露，而不是等用户启动时才被 Fail Closed 拦住。
$isoPath = Join-Path $appDir 'preview-isolation.json'
$okIso = $false
$isoDetail = ''
if (Test-Path $isoPath) {
    try {
        $isoObj = Get-Content $isoPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $sv = $isoObj.schemaVersion
        $dr = $isoObj.dataRoot
        if ($sv -ne 1) { $isoDetail = 'schemaVersion=' + $sv + '（应为 1）' }
        elseif ([string]::IsNullOrWhiteSpace($dr)) { $isoDetail = 'dataRoot 为空' }
        elseif ($dr -notmatch 'PCMig-Preview[0-9]') { $isoDetail = 'dataRoot 未指向预览专属根：' + $dr }
        elseif ($dr -match '\\PCMig\\' -or $dr -match '\\PCMig$') { $isoDetail = 'dataRoot 指向正式根：' + $dr }
        else { $okIso = $true; $isoDetail = $dr }
    } catch {
        $isoDetail = 'JSON 解析失败：' + $_.Exception.Message
    }
} else {
    $isoDetail = '文件不存在'
}
if ($okIso) { Info ("隔离配置 preview-isolation.json 合法（dataRoot=" + $isoDetail + "）  ……… PASS") }
else { Info ("隔离配置 preview-isolation.json  ……… FAIL（" + $isoDetail + "）"); $allOk = $false }

Write-Host ''
Info ("产物： " + $exe)
Info ("SHA256(EXE，不具身份区分度)： " + $sha)
Info ("构建指纹（托管程序集）： " + $fingerprint)

if ($allOk) { exit 0 } else { exit 1 }
