# PCMig 发布输入闸门 —— 负向对照测试
#
# ★ 2026-10-24（R1-6C）★ 发布脚本的闸门**必须能被证明会失败**，否则它只是注释。
#   本项目已经吃过一次亏：release.ps1 里两条"运行时版本真值断言"结构上恒为 false
#   （见 tools\version-gate-tests.ps1 头部注释），写着闸门却一次也不可能触发。
#   因此 release.ps1 新增的发布输入闸门（路径 / 源码完整性 / 工具链 / Git 状态 / 版本形态）
#   全部在这里用**反向用例**证明：缺什么就必须在对应位置中止。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-gate-tests.ps1
# 产物：控制台 PASS/FAIL 汇总；退出码 0 = 全部如期，1 = 有闸门没拦住。
#
# 纪律：所有用例的交付/工作副本目录都指向临时目录，**绝不触碰正式交付区**；
#       除正向用例外全部期望 release.ps1 以非 0 退出并给出指定原因。

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$release = Join-Path $here 'release.ps1'
$repoReal = Split-Path $here -Parent
if (-not (Test-Path $release)) { Write-Output ('*** 找不到 release.ps1：' + $release); exit 1 }

$script:pass = 0
$script:fail = 0
$tmpRoot = Join-Path $env:TEMP ('pcmig-release-gate-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item $tmpRoot -ItemType Directory -Force | Out-Null
$tmpDelivery = Join-Path $tmpRoot 'delivery'
$tmpWorkCopy = Join-Path $tmpRoot 'workcopy'

function Say($m) { Write-Output $m }

function New-StubRepo {
  param([string]$Name, [string[]]$DropInputs)
  $r = Join-Path $tmpRoot $Name
  New-Item $r -ItemType Directory -Force | Out-Null
  # 顶层目录用 junction 指回真仓库（避免复制整棵树），需要"缺文件"的目录则建成真实空目录再挑拣复制。
  $dropDirs = @()
  foreach ($d in $DropInputs) { $dropDirs += (Split-Path $d -Parent) }
  foreach ($d in @('src', 'tests', 'installer', 'docs', 'tools', 'matrix')) {
    if ($dropDirs -contains $d) { New-Item (Join-Path $r $d) -ItemType Directory -Force | Out-Null }
    elseif (Test-Path (Join-Path $repoReal $d)) { New-Item (Join-Path $r $d) -ItemType Junction -Value (Join-Path $repoReal $d) | Out-Null }
  }
  foreach ($f in @('PCMig.sln', 'README.md')) {
    if (Test-Path (Join-Path $repoReal $f)) { Copy-Item (Join-Path $repoReal $f) (Join-Path $r $f) -Force }
  }
  # 在"被删"的目录里补齐除目标文件之外的其余必需项（保证只有一处是缺的）。
  if ($dropDirs -contains 'installer') {
    Copy-Item (Join-Path $repoReal 'installer\pcmig.iss') (Join-Path $r 'installer\pcmig.iss') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $r 'installer\pcmig.iss') -Force -ErrorAction SilentlyContinue
  }
  if ($dropDirs -contains 'tools') {
    foreach ($f in @('md2txt.py', 'version-gate.ps1', 'version-gate-tests.ps1')) {
      $src = Join-Path $repoReal ('tools\' + $f)
      if (Test-Path $src) { Copy-Item $src (Join-Path $r ('tools\' + $f)) -Force }
    }
    foreach ($d in $DropInputs) {
      $p = Join-Path $r $d
      if (Test-Path $p) { Remove-Item $p -Force }
    }
  }
  return $r
}

function Invoke-Case {
  param(
    [string]$Id,
    [string]$Title,
    [string[]]$ReleaseArgs,
    [string]$ExpectPattern,
    [int]$ExpectExit = 1
  )
  $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $release) + $ReleaseArgs
  $out = & powershell.exe @args 2>&1 | Out-String
  $code = $LASTEXITCODE
  $okExit = ($code -eq $ExpectExit)
  $okText = ($out -match [regex]::Escape($ExpectPattern))
  if ($okExit -and $okText) {
    $script:pass++
    Say ('  PASS  ' + $Id + '  ' + $Title + '  （exit=' + $code + '，已拦下）')
    return
  }
  $script:fail++
  Say ('  FAIL  ' + $Id + '  ' + $Title)
  Say ('        exit=' + $code + '（期望 ' + $ExpectExit + '）；命中期望文本=' + $okText)
  Say ('        期望包含：' + $ExpectPattern)
  $tail = @($out -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 6)
  foreach ($t in $tail) { Say ('        | ' + $t) }
  return
}

function New-GitSampleRepo {
  param([string]$Name)
  # 真实薄文件（不用 junction —— git 会递归 junction 指回的整个真仓库，几万个文件）。
  # 只满足 release.ps1 的 20 项 $requiredInputs；-GateOnly 在此之前早退，不会真的构建。
  $r = Join-Path $tmpRoot $Name
  New-Item $r -ItemType Directory -Force | Out-Null
  foreach ($d in @('src\PCMig.Core', 'src\PCMig.Cli', 'src\PCMig.WinUI', 'src\PCMig.Diagnostics',
                   'tests\PCMig.Core.Tests', 'tests\PCMig.Diagnostics.Tests', 'tests\PCMig.Cli.Tests',
                   'installer', 'docs', 'tools', 'matrix')) {
    New-Item (Join-Path $r $d) -ItemType Directory -Force | Out-Null
  }
  foreach ($x in @('PCMig.sln', 'README.md',
                   'src\PCMig.Core\PCMig.Core.csproj', 'src\PCMig.Cli\PCMig.Cli.csproj',
                   'src\PCMig.WinUI\PCMig.WinUI.csproj', 'src\PCMig.Diagnostics\PCMig.Diagnostics.csproj',
                   'tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj',
                   'tests\PCMig.Diagnostics.Tests\PCMig.Diagnostics.Tests.csproj',
                   'tests\PCMig.Cli.Tests\PCMig.Cli.Tests.csproj',
                   'installer\pcmig.iss', 'docs\更新日志.md', 'docs\使用说明.txt',
                   'docs\RELEASE.md', 'docs\INDEX.md',
                   'tools\version-gate.ps1', 'tools\version-gate-tests.ps1',
                   'tools\release-gate-tests.ps1', 'tools\md2txt.py')) {
    New-Item (Join-Path $r $x) -ItemType File -Force | Out-Null
  }
  New-Item (Join-Path $r 'matrix\.keep') -ItemType File -Force | Out-Null
  & git -C $r init -b main --quiet | Out-Null
  & git -C $r config user.email 'gate-tests@pcmig.local' | Out-Null
  & git -C $r config user.name 'PCMig Gate Tests' | Out-Null
  & git -C $r config core.autocrlf false | Out-Null
  return $r
}

function Invoke-Git {
  param([string]$Repo, [string[]]$GitArgs)
  $out = & git -C $Repo @GitArgs 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw ('git ' + ($GitArgs -join ' ') + ' 失败：' + $out) }
  return $out.Trim()
}
$base = @('-Version', '0.5.4', '-Delivery', $tmpDelivery, '-WorkCopy', $tmpWorkCopy)

Say ('=== 发布输入闸门负向对照（临时目录：' + $tmpRoot + '）===')

# ── 闸门 −4：路径 ────────────────────────────────────────────────────────────
Invoke-Case -Id 'N1' -Title '缺 -Delivery 必须中止' -ExpectPattern '交付目录未指定' `
  -ReleaseArgs @('-Version', '0.5.4', '-WorkCopy', $tmpWorkCopy)
Invoke-Case -Id 'N2' -Title '缺 -WorkCopy 必须中止' -ExpectPattern '工作副本目录未指定' `
  -ReleaseArgs @('-Version', '0.5.4', '-Delivery', $tmpDelivery)
Invoke-Case -Id 'N3' -Title '仓库目录不存在必须中止' -ExpectPattern '仓库目录不存在' `
  -ReleaseArgs (@('-Version', '0.5.4', '-Repo', (Join-Path $tmpRoot 'no-such-repo')) + $base[2..5])

# ── 闸门 −3：源码完整性 ──────────────────────────────────────────────────────
$stubMissingIss = New-StubRepo -Name 'stub-missing-iss' -DropInputs @('installer\pcmig.iss')
Invoke-Case -Id 'N4' -Title '缺 installer\pcmig.iss 必须中止' -ExpectPattern '源码不完整' `
  -ReleaseArgs (@('-Version', '0.5.4', '-Repo', $stubMissingIss) + $base[2..5])
$stubMissingGate = New-StubRepo -Name 'stub-missing-version-gate' -DropInputs @('tools\version-gate.ps1')
$stubComplete = New-StubRepo -Name 'stub-complete' -DropInputs @()
Invoke-Case -Id 'N5' -Title '缺 tools\version-gate.ps1 必须中止' -ExpectPattern '源码不完整' `
  -ReleaseArgs (@('-Version', '0.5.4', '-Repo', $stubMissingGate) + $base[2..5])

# ── 闸门 −2：工具链 ──────────────────────────────────────────────────────────
Invoke-Case -Id 'N6' -Title '缺 Inno Setup 编译器必须中止' -ExpectPattern '工具链不满足发布要求' `
  -ReleaseArgs ($base + @('-IsccExe', (Join-Path $tmpRoot 'no-such-iscc.exe')))
Invoke-Case -Id 'N7' -Title '缺 dotnet 必须中止' -ExpectPattern '工具链不满足发布要求' `
  -ReleaseArgs ($base + @('-DotnetExe', (Join-Path $tmpRoot 'no-such-dotnet.exe')))
Invoke-Case -Id 'N8' -Title '缺 git 必须中止' -ExpectPattern '工具链不满足发布要求' `
  -ReleaseArgs ($base + @('-GitExe', (Join-Path $tmpRoot 'no-such-git.exe')))

# ── 闸门 −1：版本形态与 Git 状态 ─────────────────────────────────────────────
Invoke-Case -Id 'N9' -Title 'Preview 后缀不得进正式发布' -ExpectPattern '不是三段数字形态' `
  -ReleaseArgs @('-Version', '0.5.4-preview.7', '-Delivery', $tmpDelivery, '-WorkCopy', $tmpWorkCopy)
Invoke-Case -Id 'N10' -Title 'Release 模式下脏工作树必须中止' -ExpectPattern '要求工作树完全干净' `
  -ReleaseArgs ($base + @('-Repo', $repoReal, '-SourceMode', 'Release'))
Invoke-Case -Id 'N11' -Title 'Clean 模式下已跟踪改动必须中止' -ExpectPattern '要求没有已跟踪文件的改动' `
  -ReleaseArgs ($base + @('-Repo', $repoReal, '-SourceMode', 'Clean'))
Invoke-Case -Id 'N12' -Title '非 Git 目录 + Release 模式必须中止' -ExpectPattern '是 Git 工作树' `
  -ReleaseArgs (@('-Version', '0.5.4', '-Repo', $stubComplete, '-Delivery', $tmpDelivery, '-WorkCopy', $tmpWorkCopy, '-SourceMode', 'Release'))

# ── 闸门 −1 补：提交是否真的推到了远端 ───────────────────────────────────────
# 为什么必须有这一条：tag 打在本地提交上、远端没有该提交时，别人按 tag 检出的源码
# 与本次发布的源码不是同一份 —— 包就不可复现。这里用"本地领先一个提交"精确制造该态。
$gitBare = Join-Path $tmpRoot 'origin-unpushed.git'
& git init --bare $gitBare --quiet | Out-Null
$gitUnpushed = New-GitSampleRepo -Name 'git-unpushed'
& git -C $gitUnpushed add -A | Out-Null
& git -C $gitUnpushed commit -m 'C1 发布输入' --quiet | Out-Null
$c1 = Invoke-Git -Repo $gitUnpushed -GitArgs @('rev-parse', 'HEAD')
New-Item (Join-Path $gitUnpushed 'EXTRA.txt') -ItemType File -Force | Out-Null
& git -C $gitUnpushed add -A | Out-Null
& git -C $gitUnpushed commit -m 'C2 未推送的本地提交' --quiet | Out-Null
& git -C $gitUnpushed tag 'v0.5.4' | Out-Null
& git -C $gitUnpushed remote add origin $gitBare | Out-Null
& git -C $gitUnpushed push --quiet origin ($c1 + ':refs/heads/main') | Out-Null
& git -C $gitUnpushed fetch --quiet origin | Out-Null
& git -C $gitUnpushed branch --set-upstream-to=origin/main main | Out-Null
Invoke-Case -Id 'N13' -Title '已打 tag 但提交未推送必须中止' -ExpectPattern '未推送' `
  -ReleaseArgs (@('-Version', '0.5.4', '-Repo', $gitUnpushed, '-Delivery', $tmpDelivery, '-WorkCopy', $tmpWorkCopy, '-SourceMode', 'Release'))

# ── 正向：干净 + 已提交 + 已打 tag + 已推送 ⇒ 必须放行 ────────────────────────
$gitBare2 = Join-Path $tmpRoot 'origin-pushed.git'
& git init --bare $gitBare2 --quiet | Out-Null
$gitPushed = New-GitSampleRepo -Name 'git-pushed'
& git -C $gitPushed add -A | Out-Null
& git -C $gitPushed commit -m 'C1 发布输入' --quiet | Out-Null
& git -C $gitPushed tag 'v0.5.4' | Out-Null
& git -C $gitPushed remote add origin $gitBare2 | Out-Null
& git -C $gitPushed push --quiet -u origin main --tags | Out-Null
Invoke-Case -Id 'P2' -Title '干净+已提交+已打 tag+已推送 ⇒ 应放行' -ExpectPattern '发布输入闸门已全部通过' -ExpectExit 0 `
  -ReleaseArgs (@('-Version', '0.5.4', '-Repo', $gitPushed, '-Delivery', $tmpDelivery, '-WorkCopy', $tmpWorkCopy, '-SourceMode', 'Release', '-GateOnly'))
# ── 正向：Worktree + GateOnly 必须放行并停在那一步 ────────────────────────────
Invoke-Case -Id 'P1' -Title 'Worktree + -GateOnly 应放行（不构建）' -ExpectPattern '发布输入闸门已全部通过' -ExpectExit 0 `
  -ReleaseArgs ($base + @('-Repo', $repoReal, '-SourceMode', 'Worktree', '-GateOnly'))

Say ''
Say ('=== 结果：PASS=' + $script:pass + '  FAIL=' + $script:fail + ' ===')
if ($script:fail -gt 0) {
  Say '有闸门没有如期拦下 —— 发布输入检查不足以阻止错误发布，必须修好再谈发版。'
  exit 1
}
Say '全部负向用例如期拦下，正向用例放行。'
exit 0