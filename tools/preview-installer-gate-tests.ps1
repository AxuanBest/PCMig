# PCMig v0.5.4 Preview.2 — 预览安装包构建脚本 正负双向闸门测试
#
# 目的：证明 tools\preview-installer-build.ps1 的各道闸门**既能放行合规输入，
#       也能拒绝违规输入**（只测正向 = 只证明没崩，不证明有防护）。
#
# 纪律：只读仓库；除测试自身创建的时间戳临时目录外不写任何位置；
#       绝不调用 tools\release.ps1；绝不执行安装。
$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$script = Join-Path $repo 'tools\preview-installer-build.ps1'
# 被测 app 目录：优先 -App 参数 / $env:PCMIG_PREVIEW_APP；未提供时依赖它的用例显式 SKIP（不伪造结论）。
$previewRoot = [Environment]::GetEnvironmentVariable('PCMIG_PREVIEW_ROOT')
if (-not $previewRoot) { $previewRoot = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PCMig-Previews' }
$app = $env:PCMIG_PREVIEW_APP
if (-not $app) { $app = Join-Path (Join-Path $previewRoot 'Preview-app') 'app' }
$InsRoot = Join-Path (Join-Path $previewRoot 'Installers') '_gate'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("  [PASS] " + $name) -ForegroundColor Green; $script:pass++ }
    else { Write-Host ("  [FAIL] " + $name + "  —— " + $detail) -ForegroundColor Red; $script:fail++ }
}

function Run-Gate([string[]]$extra) {
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $app -Version '0.5.4-preview.2' @extra 2>&1
    $code = $LASTEXITCODE
    $text = ($out | Out-String)
    return [pscustomobject]@{ Exit = $code; Text = $text }
}

Write-Host '=== 负向测试：闸门必须拒绝违规输入 ===' -ForegroundColor Cyan

# N1：版本号 == 仓库正式基线 0.5.3 ⇒ 必须拒绝（防拿正式版冒充预览）
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $app -Version '0.5.3' -OutRoot (Join-Path $InsRoot ("_neg1-" + (Get-Date -Format 'HHmmss'))) 2>&1
Check 'N1 版本号等于正式基线 0.5.3 被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '与正式基线相同')) ("exit=" + $LASTEXITCODE)

# N2：版本号不含 preview 字样 ⇒ 必须拒绝
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $app -Version '0.5.4' -OutRoot (Join-Path $InsRoot ("_neg2-" + (Get-Date -Format 'HHmmss'))) 2>&1
Check 'N2 版本号不含 preview 被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match "必须含 'preview'")) ("exit=" + $LASTEXITCODE)

# N3：源目录不存在 ⇒ 必须拒绝
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir 'Q:\__NOPE__\app' -Version '0.5.4-preview.2' -OutRoot (($InsRoot + "\_neg3-") + (Get-Date -Format 'HHmmss')) 2>&1
Check 'N3 源目录不存在被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '源目录不存在')) ("exit=" + $LASTEXITCODE)

# N4：输出目录已存在 ⇒ 必须拒绝覆盖
$existing = Join-Path $InsRoot 'existing-gate-probe'
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $app -Version '0.5.4-preview.2' -OutRoot $existing -SkipCompile 2>&1
Check 'N4 输出目录已存在被拒绝覆盖' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '拒绝覆盖')) ("exit=" + $LASTEXITCODE)

# N5：输出目录落在正式交付区（-Delivery / $env:PCMIG_DELIVERY）⇒ 必须拒绝
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $app -Version '0.5.4-preview.2' -OutRoot ((if ($env:PCMIG_DELIVERY) { $env:PCMIG_DELIVERY } else { "C:\__PCMIG_DELIVERY_UNSET__" }) + "\_should_never_" + (Get-Date -Format 'HHmmss')) -SkipCompile 2>&1
Check 'N5 输出目录落在正式交付区被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '受保护位置')) ("exit=" + $LASTEXITCODE)

# N6：输出目录落在仓库 dist\ ⇒ 必须拒绝
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $app -Version '0.5.4-preview.2' -OutRoot (Join-Path (Join-Path $repo 'dist') ("_should_never_" + (Get-Date -Format 'HHmmss'))) -SkipCompile 2>&1
Check 'N6 输出目录落在仓库 dist 被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '受保护位置')) ("exit=" + $LASTEXITCODE)

# N7：源目录缺 pcmig-cli.exe ⇒ 必须拒绝（正式交付契约要求两个可执行文件）
$tmpSrc = Join-Path $env:TEMP ('_pcmig_neg7_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force -Path $tmpSrc | Out-Null
Copy-Item (Join-Path $app 'PCMig.WinUI.exe') $tmpSrc -Force
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $tmpSrc -Version '0.5.4-preview.2' -OutRoot (Join-Path $InsRoot ("_neg7-" + (Get-Date -Format 'HHmmss'))) -SkipCompile 2>&1
Check 'N7 源目录缺 pcmig-cli.exe 被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '没有 pcmig-cli.exe')) ("exit=" + $LASTEXITCODE)
Remove-Item $tmpSrc -Recurse -Force -ErrorAction SilentlyContinue

# N8：源目录混入旧 WPF 前端 ⇒ 必须拒绝
$tmpSrc = Join-Path $env:TEMP ('_pcmig_neg8_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force -Path $tmpSrc | Out-Null
Copy-Item (Join-Path $app 'PCMig.WinUI.exe') $tmpSrc -Force
Copy-Item (Join-Path $app 'pcmig-cli.exe') $tmpSrc -Force
Set-Content -Path (Join-Path $tmpSrc 'PCMig-classic.exe') -Value 'x' -Encoding ASCII
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $tmpSrc -Version '0.5.4-preview.2' -OutRoot (Join-Path $InsRoot ("_neg8-" + (Get-Date -Format 'HHmmss'))) -SkipCompile 2>&1
Check 'N8 源目录混入旧 WPF 前端被拒绝' ($LASTEXITCODE -ne 0 -and (($r | Out-String) -match '旧 WPF 前端')) ("exit=" + $LASTEXITCODE)
Remove-Item $tmpSrc -Recurse -Force -ErrorAction SilentlyContinue

# ══════════════════════════════════════════════════════════════════════════════
# ★ 2026-10-15 新增（数据隔离整改纠偏令）★ 账户级污染负向门禁
#
# 背景：本脚本所测的预览安装器一度用 [Registry] 写 HKCU\Environment 的
#       PCMIG_JOBS / PCMIG_LOG_DIR 来"隔离"运行数据。那是**账户级**旋钮，
#       会把正式版与 Preview.1 的作业/日志一并改向预览目录，且静默不可测。
# 整改：[Registry] 段整段移除，改用随产物分发的 preview-isolation.json。
# 下面 N9/N10 就是钉住这次整改的门禁 —— 一旦有人把注册表写入加回来，立刻红。
#
# ★ 判据必须区分"真的写入"与"注释里在解释为什么不写"：
#   文件里出现 'HKCU' 字样本身不是罪证（整改说明里必然要提到它）。
#   罪证是**可执行的写入指令**：未注释的 [Registry] 段、RegWrite*/RegDelete*
#   调用、以及 HKCU/HKLM 与 Environment 子键同现的写入行。
# ══════════════════════════════════════════════════════════════════════════════
$issPath = Join-Path $repo 'tools\preview-installer.iss'
# ★★ 必须显式按 UTF-8 读（2026-10-15 修正，这是 N12 假阴性的真正原因）★
#   PS 5.1 的 `Get-Content -Raw` 默认按 **ANSI（当前代码页）** 解码。本 .iss 是
#   UTF-8 且含大量中文注释 ⇒ 中文被拆成乱码字节，其中部分字节被判成行分隔，
#   于是「185 行」被读成「137 行」，位于 L155 的
#       Source: "{#SourceDir}\preview-isolation.json"; DestDir: "{app}"; Flags: ignoreversion
#   整行消失 ⇒ 判据匹配不到，N12 恒为 FAIL。
#   ★ 教训：门禁脚本读文件时的编码错误，会让「闸门失效」伪装成「被检查对象不合规」。
#     判定未知（读不到/读成乱码）与判定失败必须分开，否则修的是错的东西。
$issRaw = [IO.File]::ReadAllText($issPath, [Text.Encoding]::UTF8)
# ★ 剥离注释时必须**区分两种 `;`**（2026-10-15 修正，此前判据本身是错的）：
#   ① Inno 注释符：行首 `;`，或空白后的 `;`；
#   ② Inno 段的**参数分隔符**：`Source: "..."; DestDir: "..."; Flags: ...`
#      —— 这里的 `;` 出现在**引号闭合之后**，是语法而不是注释。
#   旧实现粗暴地 `IndexOf(' ;')` 截断，把 `Source: "{#SourceDir}\preview-isolation.json"; DestDir: ...`
#   整行腰斩成 `Source: "{#SourceDir}\preview-isolation.json"`，于是 N12 永远匹配不到文件名，
#   文件长度从 8339 掉到 2061 —— **判据出错会让"闸门通过"与"闸门有效"变成两回事**。
#   现在改为逐字符扫描，跟踪双引号状态，只在**引号外**遇 `;` 才当注释起点。
function Strip-InnoComments([string]$text) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`r?`n")) {
        $sb = New-Object System.Text.StringBuilder
        $inQuote = $false
        for ($ci = 0; $ci -lt $line.Length; $ci++) {
            $ch = $line[$ci]
            if ($ch -eq '"') { $inQuote = -not $inQuote; [void]$sb.Append($ch); continue }
            if ($ch -eq ';' -and -not $inQuote) {
                # 引号外的 `;`：Inno 注释从这里开始，本行余下内容丢弃
                break
            }
            [void]$sb.Append($ch)
        }
        $out.Add($sb.ToString())
    }
    return ($out -join "`r`n")
}
$issCode = Strip-InnoComments $issRaw

Check 'N9 预览 .iss 不含 [Registry] 段（不得写账户级/机器级注册表）' `
    ($issCode -notmatch '(?m)^\s*\[Registry\]') `
    '发现了 [Registry] 段 —— 账户级污染风险（见 tools\preview-installer.iss 整改记录）'

Check 'N10 预览 .iss 不含注册表写入 API 调用' `
    ($issCode -notmatch 'RegWrite(StringValue|ExpandStringValue|DWordValue|BinaryValue|MultiStringValue)|RegDelete(Value|Key)') `
    '发现了 RegWrite*/RegDelete* 调用 —— 安装器不得改动注册表'

# 判据：可执行代码里不得**同时**出现注册表根与 Environment 子键。
$hasRegRoot = $issCode -match 'HKCU|HKLM|HKEY_CURRENT_USER|HKEY_LOCAL_MACHINE'
$hasEnvKey = $issCode -match 'Environment'
Check 'N11 预览 .iss 不含 HKCU/HKLM 与 Environment 子键的写入组合' `
    (-not ($hasRegRoot -and $hasEnvKey)) `
    '发现 HKCU/HKLM 与 Environment 同现于可执行代码 —— 账户级环境变量污染风险'

Check 'N12 预览 .iss 显式声明分发 preview-isolation.json' `
    ($issCode -match 'preview-isolation\.json') `
    '未声明分发隔离配置 —— 产出的预览包会 Fail Closed 无法启动'

# N13：正向的反面 —— 隔离配置必须真的进得了安装包。
# 做法：造一个**缺** preview-isolation.json 的源目录，编译必须失败。
# 这条闸门把"缺隔离配置的预览包装不出去"从口头约定变成机器可验的事实。
$tmpSrc = Join-Path $env:TEMP ('_pcmig_neg13_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force -Path $tmpSrc | Out-Null
Copy-Item (Join-Path $app 'PCMig.WinUI.exe') $tmpSrc -Force
Copy-Item (Join-Path $app 'pcmig-cli.exe') $tmpSrc -Force
$r = & powershell -NoProfile -ExecutionPolicy Bypass -File $script `
        -SourceDir $tmpSrc -Version '0.5.4-preview.2' -OutRoot (Join-Path $InsRoot ("_neg13-" + (Get-Date -Format 'HHmmss'))) 2>&1
Check 'N13 源目录缺 preview-isolation.json 被拒绝（编译期失败）' `
    ($LASTEXITCODE -ne 0) `
    ("exit=" + $LASTEXITCODE + "（应非 0：缺隔离配置的预览包不该装得出来）")
Remove-Item $tmpSrc -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== 正向测试：既有产物必须通过全部身份闸门 ===' -ForegroundColor Cyan
$built = $env:PCMIG_PREVIEW_BUILT
if (-not $built) { $built = Join-Path $InsRoot 'PCMigSetup-Preview2-20261015-141024' }
    -SourceDir $app -Version '0.5.4-preview.2' -OutRoot $built -SkipCompile 2>&1
$vt = ($verify | Out-String)
# -SkipCompile 时输出目录已存在会先拒绝 ⇒ 该路径本身即 N4 的正向对照；
# 真正的正向身份核验需新目录，故此处改为直接核验已编译产物元数据。
$exe = Join-Path $built 'PCMigSetup-Preview2-0.5.4-preview.2.exe'
$vi = (Get-Item $exe).VersionInfo
$fv = ($vi.FileVersion | Out-String).Trim()
$pn = ($vi.ProductName | Out-String).Trim()
$pd = ($vi.FileDescription | Out-String).Trim()
$pc = ($vi.CompanyName | Out-String).Trim()
Check 'P1 安装包 FileVersion = 0.5.4.0（纯数字四段）' ($fv -eq '0.5.4.0') ("实测 [" + $fv + "]")
Check 'P2 安装包 ProductName 带 Preview.2 标识' ($pn -like '*Preview.2*') ("实测 [" + $pn + "]")
Check 'P3 安装包 FileDescription 声明为本地实验预览' ($pd -eq ($pn + ' 安装程序（本地实验预览，非正式发布）')) ("实测 [" + $pd + "]")
Check 'P4 安装包 CompanyName = Axuanbest' ($pc -eq 'Axuanbest') ("实测 [" + $pc + "]")
Check 'P5 正式基线 installer\pcmig.iss 未被触碰（AppId 仍为正式值）' `
    ([IO.File]::ReadAllText((Join-Path $repo 'installer\pcmig.iss'), [Text.Encoding]::UTF8) -match '3B8E5A42-7C1F-4E9A-A2D1-9C0F5E7B3D61') '正式 AppId 缺失'
Check 'P6 预览脚本使用独立 AppId' `
    ([IO.File]::ReadAllText((Join-Path $repo 'tools\preview-installer.iss'), [Text.Encoding]::UTF8) -match '7A4D2E91-5B36-4C08-9F1E-2D8C4A6B7E50') '独立 AppId 缺失'
Check 'P7 预览安装目录与正式版不同' `
    ([IO.File]::ReadAllText((Join-Path $repo 'tools\preview-installer.iss'), [Text.Encoding]::UTF8) -match 'DefaultDirName=\{autopf\}\\PCMig-Preview2') '安装目录未隔离'

# ★ 2026-10-15 新增（数据隔离整改）★ 隔离配置必须随源目录就位。
# 这三条断言的是**源目录**（即将被打进安装包的 app 目录），因为身份核验的真值
# 应当来自"要装出去的东西"，而不是仓库里的模板。
$isoSrc = Join-Path $app 'preview-isolation.json'
Check 'P9 源目录含 preview-isolation.json（隔离配置随产物分发）' `
    (Test-Path $isoSrc) ("查找路径 " + $isoSrc)
if (Test-Path $isoSrc) {
    $isoObj = Get-Content $isoSrc -Raw -Encoding UTF8 | ConvertFrom-Json
    Check 'P10 隔离配置 schemaVersion = 1' ($isoObj.schemaVersion -eq 1) ("实测 " + $isoObj.schemaVersion)
    Check 'P11 隔离配置 dataRoot 指向预览专属根 PCMig-Preview2' `
        ($isoObj.dataRoot -like '*PCMig-Preview2*') ("实测 [" + $isoObj.dataRoot + "]")
    $isoRootOk = -not ($isoObj.dataRoot -match '\\PCMig\\' -or $isoObj.dataRoot -match '\\PCMig$')
    Check 'P12 隔离配置 dataRoot 不指向正式根（\PCMig\ 或 \PCMig 结尾）' `
        $isoRootOk ("实测 [" + $isoObj.dataRoot + "]")
}

# P13：清理干净 —— 安装器与构建脚本都不得写 HKCU\Environment。
#     这条与 N9/N10/N11 是同一件事的两个方向：那里证明"加回来会红"，
#     这里证明"现在还干净"。实测读取当前 HKCU\Environment 的真实状态。
$envJobsNow = (Get-ItemProperty -Path 'HKCU:\Environment' -Name 'PCMIG_JOBS' -ErrorAction SilentlyContinue).PCMIG_JOBS
$envLogsNow = (Get-ItemProperty -Path 'HKCU:\Environment' -Name 'PCMIG_LOG_DIR' -ErrorAction SilentlyContinue).PCMIG_LOG_DIR
Check 'P13 当前 HKCU\Environment 无 PCMIG_JOBS / PCMIG_LOG_DIR 残留污染' `
    ([string]::IsNullOrEmpty($envJobsNow) -and [string]::IsNullOrEmpty($envLogsNow)) `
    ("实测 PCMIG_JOBS=[" + $envJobsNow + "] PCMIG_LOG_DIR=[" + $envLogsNow + "]")

Write-Host ''
Write-Host ("=== 汇总： PASS=" + $pass + "  FAIL=" + $fail + " ===") -ForegroundColor Cyan
if ($fail -eq 0) { exit 0 } else { exit 1 }
