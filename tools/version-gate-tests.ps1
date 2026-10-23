# PCMig 版本身份闸门 —— 自测脚本（正向 + 负向）。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File "tools\version-gate-tests.ps1"
# 退出码：0 = 全部通过；1 = 有失败。
#
# ★ 存在的理由（缺陷 E · R2 2026-10-17）★
#   旧 tools\release.ps1 的两条"运行时版本真值闸门"恒不失败（详见 tools/version-gate.ps1 文件头）。
#   把它们抽成独立函数后，必须用**能失败的用例**证明它们真的会失败 ——
#   否则只是把"恒真断言"搬了个家。本脚本因此对每条检查都至少配一个反向用例。
#
# 纪律：本脚本只在自己的临时目录里造假仓库，**不碰真实仓库**，也**不运行 release.ps1**。

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        $script:pass++
        Write-Output ('  [PASS] ' + $name)
    } else {
        $script:fail++
        Write-Output ('  [FAIL] ' + $name + '  <-- ' + $detail)
    }
}

function Write-Text([string]$path, [string]$text) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

$root = Join-Path $env:TEMP ('pcmig-version-gate-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
. (Join-Path $PSScriptRoot 'version-gate.ps1')

$Ver = '0.5.4'
$nl = [string][char]10

# ── 夹具：一个"全对"的最小仓库 ────────────────────────────────────────────────
$good = Join-Path $root 'good'
Write-Text (Join-Path $good 'src\PCMig.WinUI\MainWindow.xaml.cs') (
    'private void ApplyDisplayVersion()' + $nl +
    '{' + $nl +
    '    var informational = System.Reflection.CustomAttributeExtensions' + $nl +
    '        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)' + $nl +
    '        ?.InformationalVersion;' + $nl +
    '}' + $nl)
Write-Text (Join-Path $good 'src\PCMig.WinUI\PCMig.WinUI.csproj') '<Project><PropertyGroup><Version>0.5.4</Version><InformationalVersion>0.5.4</InformationalVersion></PropertyGroup></Project>'
Write-Text (Join-Path $good 'src\PCMig.WinUI\MainWindow.xaml') '<Window Title="PCMig 迁移工具 · v0.5.4"><TextBlock Text="v0.5.4"/></Window>'

Write-Output '=== source 阶段 ==='

# P1 正向：全对 ⇒ 通过
$r = Test-VersionSourceTruth -RepoRoot $good -Version $Ver
Check 'P1 全对时 source 闸门通过' $r.Ok ($r.Problems -join ' | ')

# N1 负向：删掉 ApplyDisplayVersion ⇒ 必须失败（这正是旧断言结构上发现不了的）
$n1 = Join-Path $root 'n1'; Copy-Item $good $n1 -Recurse
Write-Text (Join-Path $n1 'src\PCMig.WinUI\MainWindow.xaml.cs') ('// 有人把 ApplyDisplayVersion 删了' + $nl + 'private void Something() { }' + $nl)
$r = Test-VersionSourceTruth -RepoRoot $n1 -Version $Ver
Check 'N1 删掉 ApplyDisplayVersion 必须失败' (-not $r.Ok) '闸门仍然通过 —— 说明它仍是恒真断言'
Check 'N1 失败原因点名 ApplyDisplayVersion' (($r.Problems -join ' ') -match 'ApplyDisplayVersion') ('Problems=' + ($r.Problems -join ' | '))

# N2 负向：ApplyDisplayVersion 还在，但改读 AssemblyVersion ⇒ 必须失败（真值来源被改）
$n2 = Join-Path $root 'n2'; Copy-Item $good $n2 -Recurse
Write-Text (Join-Path $n2 'src\PCMig.WinUI\MainWindow.xaml.cs') (
    'private void ApplyDisplayVersion()' + $nl +
    '{' + $nl +
    '    var raw = asm.GetName().Version?.ToString();' + $nl +
    '}' + $nl)
$r = Test-VersionSourceTruth -RepoRoot $n2 -Version $Ver
Check 'N2 改读 AssemblyVersion 必须失败' (-not $r.Ok) '闸门仍然通过 —— 真值来源已变却无人拦'

# N3 负向：InformationalVersion 节点重复出现两处 ⇒ 必须失败
$n3 = Join-Path $root 'n3'; Copy-Item $good $n3 -Recurse
Write-Text (Join-Path $n3 'src\PCMig.WinUI\PCMig.WinUI.csproj') '<Project><PropertyGroup><Version>0.5.4</Version><InformationalVersion>0.5.4</InformationalVersion><InformationalVersion>0.5.3</InformationalVersion></PropertyGroup></Project>'
$r = Test-VersionSourceTruth -RepoRoot $n3 -Version $Ver
Check 'N3 InformationalVersion 重复两处必须失败' (-not $r.Ok) '闸门仍然通过 —— 构建取哪个节点将不可控'

# N4 负向：InformationalVersion 版本号不对 ⇒ 必须失败
$n4 = Join-Path $root 'n4'; Copy-Item $good $n4 -Recurse
Write-Text (Join-Path $n4 'src\PCMig.WinUI\PCMig.WinUI.csproj') '<Project><PropertyGroup><Version>0.5.4</Version><InformationalVersion>0.5.3</InformationalVersion></PropertyGroup></Project>'
$r = Test-VersionSourceTruth -RepoRoot $n4 -Version $Ver
Check 'N4 InformationalVersion 版本不对必须失败' (-not $r.Ok) '闸门仍然通过 —— 界面会显示错误版本'

# N5 负向：XAML 里没有任何降级徽章 ⇒ 必须失败
$n5 = Join-Path $root 'n5'; Copy-Item $good $n5 -Recurse
Write-Text (Join-Path $n5 'src\PCMig.WinUI\MainWindow.xaml') '<Window Title="PCMig 迁移工具"></Window>'
$r = Test-VersionSourceTruth -RepoRoot $n5 -Version $Ver
Check 'N5 无降级徽章必须失败' (-not $r.Ok) '闸门仍然通过 —— 程序集读不到时界面会显示旧值'

Write-Output ''
Write-Output '=== published 阶段 ==='

# published 阶段需要真实托管 DLL 才有版本资源；用本仓库已构建产物造夹具，
# 若本机没有任何产物则明确 SKIP（不伪造结论）。
$probe = $null
$repoRoot = Split-Path $PSScriptRoot -Parent
foreach ($cand in @(
        (Join-Path $repoRoot 'src\PCMig.Core\bin\Release\net8.0\PCMig.Core.dll'),
        (Join-Path $repoRoot 'src\PCMig.Core\bin\x64\Release\net8.0\PCMig.Core.dll'))) {
    if (Test-Path $cand) { $probe = $cand; break }
}
if (-not $probe) {
    Write-Output '  [SKIP] published 阶段：本机找不到任何已构建的 PCMig.Core.dll，跳过（不伪造结论）。'
} else {
    $realVer = ([string](Get-Item $probe).VersionInfo.ProductVersion)
    if ($realVer.Contains('+')) { $realVer = $realVer.Substring(0, $realVer.IndexOf('+')) }
    $realVer = $realVer.Trim()
    Write-Output ('  夹具产物：' + $probe)
    Write-Output ('  其 ProductVersion = ' + $realVer)

    $pub = Join-Path $root 'pub'
    New-Item -ItemType Directory -Force -Path (Join-Path $pub 'dist\winui') | Out-Null
    Copy-Item $probe (Join-Path $pub 'dist\winui\PCMig.Core.dll')

    # P2 正向：用产物真实版本号去比 ⇒ 通过
    $r = Test-VersionPublishedTruth -RepoRoot $pub -Version $realVer -RelativePaths @('dist\winui\PCMig.Core.dll')
    Check 'P2 产物版本与目标一致时通过' $r.Ok ($r.Problems -join ' | ')

    # N6 负向：宣称一个不同的版本 ⇒ 必须失败（这条能发现"源码写对了但构建没带 -p:Version"）
    $r = Test-VersionPublishedTruth -RepoRoot $pub -Version '9.9.9' -RelativePaths @('dist\winui\PCMig.Core.dll')
    Check 'N6 产物版本与目标不一致必须失败' (-not $r.Ok) '闸门仍然通过 —— 构建参数漏带版本却无人拦'

    # N7 负向：产物缺失 ⇒ 必须失败（不得静默跳过）
    $r = Test-VersionPublishedTruth -RepoRoot $pub -Version $realVer -RelativePaths @('dist\winui\NoSuch.dll')
    Check 'N7 产物缺失必须失败' (-not $r.Ok) '闸门仍然通过 —— 缺失产物被静默放过'
}

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Output ''
Write-Output ('=== 结果：PASS=' + $script:pass + ' FAIL=' + $script:fail + ' ===')
if ($script:fail -gt 0) { exit 1 }
exit 0
