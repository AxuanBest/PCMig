# PCMig 发版脚本 —— 先写日志，再打包；没写日志就打不出包。
#
# ★ 2026-10-24（R1-6C 发布构建可移植性整改）★
#   本脚本以前把三部机器的路径直接写死在文件头（$repo / $delivery / $mirror / $workCopy），
#   换机、换盘、别人接手都必须先改脚本本身 —— 这是"发布流程不可移植"的直接来源，
#   也是把开发机私有目录结构固化进公共仓库的隐患。现在改为**全部可参数化**：
#     · $repo     默认 = 本脚本所在 tools\ 的上级目录（跟着脚本走，不再写死机器路径）；
#     · $delivery / $workCopy  **必须**由参数或环境变量给出，没有就中止 ——
#       猜一个交付目录比拒绝执行危险得多（交付区与工作副本都是会被写入/清空的目录）。
#     · $mirror   留空 = 跳过源码镜像，不报错。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File <本脚本> `
#     -Version 0.5.4 -Delivery <交付目录> -WorkCopy <工作副本目录> [-Mirror <镜像目录>]
#
# 环境变量兜底（参数优先）：PCMIG_REPO / PCMIG_DELIVERY / PCMIG_WORKCOPY / PCMIG_MIRROR
#                           PCMIG_DOTNET / PCMIG_GIT / PCMIG_ISCC / PCMIG_PYTHON
#
# -SourceMode 把"发布输入从哪来"显式化（这是发布输入检查的一部分，不是装饰）：
#   Release  = 正式发布。工作树必须干净（无已跟踪改动、无未跟踪文件），
#              且 HEAD 必须已被打上 v<Version> 标签 —— 否则"发布的源码"和"用户装到的源码"对不上。
#   Clean    = 干净源码构建。只要求无**已跟踪**文件改动（允许未跟踪，如本地证据/临时脚本）。
#   Worktree = 开发机工作树构建。允许脏，但会把脏状态原样打印并落证，且明确禁止用于正式发布。
#
# 路径纪律见 docs/RELEASE.md：机器之间的差异一律通过参数或环境变量表达，脚本本体不含任何机器专属路径。
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Repo,
  [string]$Delivery,
  [string]$WorkCopy,
  [string]$Mirror,
  [ValidateSet('Release', 'Clean', 'Worktree')][string]$SourceMode = 'Release',
  [string]$DotnetExe,
  [string]$GitExe,
  [string]$IsccExe,
  [string]$PythonExe,
  [switch]$AllowReplaceDeliveryInstaller,
  [switch]$GateOnly
)
$ErrorActionPreference = 'Stop'

# ── 路径与工具解析（这里只解析，不校验；校验在闸门 −4/−3 里做，报错更集中）──────────────
$repo = if ($Repo) { $Repo } elseif ($env:PCMIG_REPO) { $env:PCMIG_REPO } else { Split-Path $PSScriptRoot -Parent }
$delivery = if ($Delivery) { $Delivery } elseif ($env:PCMIG_DELIVERY) { $env:PCMIG_DELIVERY } else { '' }
$workCopy = if ($WorkCopy) { $WorkCopy } elseif ($env:PCMIG_WORKCOPY) { $env:PCMIG_WORKCOPY } else { '' }
$mirror = if ($Mirror) { $Mirror } elseif ($env:PCMIG_MIRROR) { $env:PCMIG_MIRROR } else { '' }
$dotnetExe = if ($DotnetExe) { $DotnetExe } elseif ($env:PCMIG_DOTNET) { $env:PCMIG_DOTNET } else { 'dotnet' }
$gitExe = if ($GitExe) { $GitExe } elseif ($env:PCMIG_GIT) { $env:PCMIG_GIT } else { 'git' }
$isccExe = if ($IsccExe) { $IsccExe } elseif ($env:PCMIG_ISCC) { $env:PCMIG_ISCC } else { Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' }
# Python 解释器：保持原有的"环境变量 → py -3 → python"解析顺序，但允许 -PythonExe 直接指定。
if ($PythonExe) { $env:PCMIG_PYTHON = $PythonExe }

# dotnet 根目录：只在**没有显式指定解释器**时才做本机探测，避免把环境假设强加给调用方。
if (-not $DotnetExe) {
  if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet") {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $env:Path = "$env:DOTNET_ROOT;" + $env:Path
  } elseif (Test-Path 'C:\Program Files\dotnet\dotnet.exe') {
    $env:DOTNET_ROOT = 'C:\Program Files\dotnet'
  }
}

function Log($m) { Write-Output ('[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $m) }
function Abort($m) { Write-Output ''; Write-Output ('*** 发布中止：' + $m); Write-Output ''; exit 1 }
function EncOf($p) {
  $b = [IO.File]::ReadAllBytes($p)
  if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return (New-Object Text.UTF8Encoding($true)) }
  return (New-Object Text.UTF8Encoding($false))
}
function Patch($rel, $pattern, $del) {
  $p = Join-Path $repo $rel
  if (-not (Test-Path $p)) { Abort ('缺少文件 ' + $rel) }
  $enc = EncOf $p
  $t = [IO.File]::ReadAllText($p, $enc)
  $new = [regex]::Replace($t, $pattern, $del)
  if ($new -eq $t) {
    # 已经是目标版本（重跑/上次中断）就不算错。
    # ★ 2026-10-06 修复 ★ 但必须先确认文件里**真的**含有目标串：若 <Version> 这类节点根本不存在，
    #   正则匹配不到 ⇒ $new -eq $t 同样成立，旧写法会把"没改成"静默放过、只打印"已是目标版本"
    #   （v0.5.0 首发时 PCMig.WinUI.csproj 缺 <Version> 节点，正是这样被跳过、随后自查才报错中止）。
    if ($t.Contains($del)) {
      Write-Output ('   ' + $rel + ' 已是目标版本，跳过')
      return
    }
    Abort ('版本号写入失败：' + $rel + ' 里没有可替换的目标（期望出现「' + $del + '」）。请先补上对应节点再发版。')
  }
  [IO.File]::WriteAllText($p, $new, $enc)
  Write-Output ('   已改 ' + $rel)
}

# ============================================================================
# 闸门 −4/−3：发布输入（路径 / 源码完整性 / 工具链 / Git 状态 / 版本形态）
#
# ★ 2026-10-24（R1-6C）★ 为什么把这些放在最前面：
#   旧脚本的六道闸门全部针对"文档写没写、版本号改没改"，对"**输入的源码与环境到底对不对**"
#   一条都没有。于是开发机专用路径一旦不符、源码树缺文件、版本闸门脚本被删、Git 还脏着，
#   脚本都会一路跑到打包阶段才炸（甚至不炸，直接把错的包发出去）。
#   发布输入检查必须**先于**任何写操作与耗时构建，且必须明确报错、不许静默跳过
#   （静默跳过等于放行 —— 这正是版本闸门那次事故的形态）。
# ============================================================================
Log '闸门 -4/5：发布输入路径'
foreach ($p in @(
    [pscustomobject]@{ Name = '交付目录'; Value = $delivery; Flag = '-Delivery'; Env = 'PCMIG_DELIVERY' },
    [pscustomobject]@{ Name = '工作副本目录'; Value = $workCopy; Flag = '-WorkCopy'; Env = 'PCMIG_WORKCOPY' })) {
  if ([string]::IsNullOrWhiteSpace($p.Value)) {
    Abort ($p.Name + '未指定。本脚本不再内置任何机器专属路径：请用 ' + $p.Flag + ' <路径> 或环境变量 ' + $p.Env + ' 提供。')
  }
}
if (-not (Test-Path $repo)) { Abort ('仓库目录不存在：' + $repo + '（用 -Repo 或 PCMIG_REPO 指定，默认取本脚本 tools\ 的上级目录）') }
if (-not (Test-Path $delivery)) { New-Item $delivery -ItemType Directory -Force | Out-Null }
if (-not (Test-Path $workCopy)) { New-Item $workCopy -ItemType Directory -Force | Out-Null }
Write-Output ('   仓库=' + $repo)
Write-Output ('   交付=' + $delivery)
Write-Output ('   工作副本=' + $workCopy)
Write-Output ('   镜像=' + $(if ($mirror) { $mirror } else { '(未配置，跳过镜像)' }))

Log '闸门 -3/5：源码完整性'
# 发布需要的**输入文件**清单。缺任何一个都不该"跑到一半再说"：
# 早期的真实事故是版本闸门脚本被删后，两条恒不失败的断言继续放行（详见下方 source 阶段注释）。
$requiredInputs = @(
  'PCMig.sln',
  'src\PCMig.Core\PCMig.Core.csproj',
  'src\PCMig.Cli\PCMig.Cli.csproj',
  'src\PCMig.WinUI\PCMig.WinUI.csproj',
  'tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj',
  'tests\PCMig.Diagnostics.Tests\PCMig.Diagnostics.Tests.csproj',
  'installer\pcmig.iss',
  'docs\更新日志.md',
  'docs\使用说明.txt',
  'tools\version-gate.ps1',
  'tools\md2txt.py',
  'matrix',
  'README.md',
  'tools\version-gate-tests.ps1',
  'tools\release-gate-tests.ps1',
  'docs\RELEASE.md',
  'docs\INDEX.md',
  'installer',
  'src\PCMig.Diagnostics\PCMig.Diagnostics.csproj',
  'tests\PCMig.Cli.Tests\PCMig.Cli.Tests.csproj'
)
$missingInputs = @($requiredInputs | Where-Object { -not (Test-Path (Join-Path $repo $_)) })
if ($missingInputs.Count -gt 0) {
  Abort ('源码不完整，缺少发布必需的输入：' + ($missingInputs -join ' , ') +
         [char]10 + '从缺文件的源码树构建出来的包是不可复现的；请先补齐源码/检出正确分支再发版。')
}
# 版本闸门脚本必须真的会失败 —— 它自己带反向用例，缺了它就等于把"运行时版本真值"整段放行。
$versionGateSelfTest = Join-Path $repo 'tools\version-gate-tests.ps1'
if (-not (Test-Path $versionGateSelfTest)) {
  Write-Output '   [warn] 未找到 tools\version-gate-tests.ps1 —— 版本闸门的反向用例无法自证（不阻断，但发布证据不完整）'
}
Write-Output ('   源码完整性通过：' + $requiredInputs.Count + ' 项发布必需输入齐备')

Log '闸门 -2/5：工具链'
$toolProblems = New-Object System.Collections.Generic.List[string]
$dotnetCmd = Get-Command $dotnetExe -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { $toolProblems.Add('找不到 dotnet（用 -DotnetExe 或 PCMIG_DOTNET 指定）') }
else {
  try {
    $dv = (& $dotnetExe --version 2>&1 | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0) { $toolProblems.Add('dotnet --version 失败') }
    elseif ($dv -notmatch '^8\.') { $toolProblems.Add('dotnet 版本是 ' + $dv + '，本项目基线为 8.x') }
    else { Write-Output ('   dotnet ' + $dv) }
  } catch { $toolProblems.Add('dotnet 无法执行：' + $_.Exception.Message) }
}
$gitCmd = Get-Command $gitExe -ErrorAction SilentlyContinue
if (-not $gitCmd) { $toolProblems.Add('找不到 git（用 -GitExe 或 PCMIG_GIT 指定）') }
if (-not (Test-Path $isccExe)) { $toolProblems.Add('找不到 Inno Setup 编译器 ISCC.exe：' + $isccExe + '（用 -IsccExe 或 PCMIG_ISCC 指定）') }
if ($toolProblems.Count -gt 0) {
  Abort ('工具链不满足发布要求：' + [char]10 + '  - ' + ($toolProblems -join ([char]10 + '  - ')))
}
Write-Output '   工具链齐备：dotnet / git / ISCC'

Log '闸门 -1/5：版本形态与 Git 状态'
# 正式版的版本号只允许三段数字。Preview/rc 之类后缀一旦进了正式包，用户看到的版本就无法与 tag 对应。
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  Abort ('版本号「' + $Version + '」不是三段数字形态（X.Y.Z）。正式发布不接受 Preview / rc / build 后缀；预览包请走独立的预览构建链。')
}
$isGitRepo = Test-Path (Join-Path $repo '.git')
if (-not $isGitRepo) {
  if ($SourceMode -eq 'Release') { Abort ('SourceMode=Release 要求 ' + $repo + ' 是 Git 工作树（找不到 .git），否则无法证明发布的源码与 tag 一致。') }
  Write-Output ('   [warn] 非 Git 工作树（SourceMode=' + $SourceMode + '），跳过 Git 一致性检查')
} else {
  $porcelain = @(& $gitExe -C $repo status --porcelain=v1 2>&1)
  $trackedDirty = @($porcelain | Where-Object { $_ -notmatch '^\?\?' })
  $untracked = @($porcelain | Where-Object { $_ -match '^\?\?' })
  Write-Output ('   Git：已跟踪改动 ' + $trackedDirty.Count + ' 项，未跟踪 ' + $untracked.Count + ' 项')
  switch ($SourceMode) {
    'Release' {
      if ($porcelain.Count -gt 0) {
        Abort ('SourceMode=Release 要求工作树完全干净，实际有 ' + $porcelain.Count + ' 项改动/未跟踪（已跟踪 ' + $trackedDirty.Count + '，未跟踪 ' + $untracked.Count + '）。' +
               [char]10 + '正式发布必须从**与 tag 完全一致**的源码构建；脏工作树构建出来的包无法复现，也无法审计。' +
               [char]10 + '先在隔离工作树里检出目标 tag，或改用 -SourceMode Clean / Worktree 做非发布构建。')
      }
      # ★ 2026-10-24（R2-5）★ 这里原来写成 `& git ... | Select-Object -First 1`：
      # 管道提前停止会杀掉原生进程，$LASTEXITCODE 变成非 0，于是"标签完全正确"
      # 也会被判成失败（release-gate-tests.ps1 的 N13/P2 抓到的就是这个假失败）。
      # 改为先把输出整串取回，再在内存里取第一行。
      $tagOut = ([string](& $gitExe -C $repo describe --tags --exact-match HEAD 2>&1)).Trim()
      $descCode = $LASTEXITCODE
      if ($descCode -ne 0) { Abort ('SourceMode=Release 要求 HEAD 已被打上标签，但 git describe --tags --exact-match 失败：' + $tagOut) }
      $exactTag = (@($tagOut -split "`r?`n") | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
      if ([string]::IsNullOrWhiteSpace([string]$exactTag)) {
        Abort 'SourceMode=Release 要求 HEAD 已被打上标签，但 git describe --tags --exact-match 没有输出。'
      }
      $exactTag = ([string]$exactTag).Trim()
      if ($exactTag -ne ('v' + $Version)) {
        Abort ('HEAD 上的标签是「' + $exactTag + '」，与本次发布版本 v' + $Version + ' 不一致。先在正确的提交上打 tag 再发版。')
      }
      # ★ R2-5 ★ 未推送提交必须阻断：标签打在本地提交上、远端却没有该提交时，
      # 别人按 tag 检出的源码与本次发布的源码不是同一份，等于发布了一个不可复现的包。
      $upstream = ([string](& $gitExe -C $repo rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>&1)).Trim()
      if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)) {
        Abort ('SourceMode=Release 要求当前分支有上游追踪分支，但取不到 upstream（' + $upstream + '）。' +
               [char]10 + '正式发布必须能证明「tag 已存在于远端」，否则包与源码无法对应。')
      }
      $aheadText = ([string](& $gitExe -C $repo rev-list --count ($upstream + '..HEAD') 2>&1)).Trim()
      if ($LASTEXITCODE -ne 0) { Abort ('无法计算未推送提交数：' + $aheadText) }
      $aheadCount = 0
      if (-not [int]::TryParse($aheadText, [ref]$aheadCount)) { Abort ('未推送提交数解析失败：' + $aheadText) }
      if ($aheadCount -gt 0) {
        Abort ('SourceMode=Release 要求所有提交都已推送到 ' + $upstream + '，当前有 ' + $aheadCount + ' 个提交未推送。' +
               [char]10 + '先 push --follow-tags，确认远端 tag 指向同一提交，再发版。')
      }
      Write-Output ('   Release 模式：工作树干净，HEAD 标签 = ' + $exactTag + '，已推送（' + $upstream + '）')
    }
    'Clean' {
      if ($trackedDirty.Count -gt 0) {
        Abort ('SourceMode=Clean 要求没有已跟踪文件的改动，实际有 ' + $trackedDirty.Count + ' 项。' +
               [char]10 + '未跟踪文件允许存在（本地证据/临时脚本），但已跟踪文件的改动意味着发布的源码不等于任何可追溯的提交。')
      }
      Write-Output '   Clean 模式：已跟踪文件无改动（允许未跟踪）'
    }
    'Worktree' {
      Write-Output '   [warn] Worktree 模式：允许脏工作树，本次产物**不得**作为正式发布物，仅用于开发期验证。'
      foreach ($ln in ($porcelain | Select-Object -First 20)) { Write-Output ('      dirty: ' + $ln) }
    }
  }
}
Write-Output ''

if ($GateOnly) {
  Write-Output '   -GateOnly：发布输入闸门已全部通过，按要求在此停止（未执行内容闸门与构建）。'
  exit 0
}

# ============ 闸门 -0/5：非 Release 模式不得写入正式交付根 ============
# ★ 2026-10-24（R1-6C 复审 P0-1）★ `-SourceMode` 以前只是个标签：写进日志、参与输入检查，
#   但产物照样落进 `-Delivery` 交付根、与正式发布物并列 —— 那等于"区分了但没区分"。
#   现在只有 Release（工作树干净 + HEAD 有对应 tag）才允许写交付根；
#   Clean / Worktree 一律改写到 `<交付根>\candidate-<模式>`，让非发布产物物理上回不到正式交付区。
#   注意：这段放在 `-GateOnly` 之后 —— 只校验不产物的调用不受影响。
if ($SourceMode -ne 'Release') {
  $delivery = Join-Path $delivery ('candidate-' + $SourceMode.ToLowerInvariant())
  New-Item $delivery -ItemType Directory -Force | Out-Null
  Write-Output ('   [gate] SourceMode=' + $SourceMode + ' ⇒ 产物改写到非发布目录：' + $delivery)
  Write-Output '   [gate] 该目录下的安装包**不是**正式发布物，不得手动搬进交付根。'
}

# ============ 闸门：三处必须已经写好本版内容 ============
Log ('=== 发布 v' + $Version + ' ===')

# [闸门 0/5] 单元测试 —— 没有安全网不允许发版
# 注意：这里直接指向 csproj 而不是 .sln。若测试项目没被加进 sln，
# `dotnet test PCMig.sln` 会【显示成功但一个用例都不跑】，闸门形同虚设。
Log '闸门 0/5：单元测试全绿'
$testProj = Join-Path $repo 'tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj'
if (-not (Test-Path $testProj)) { Abort '找不到测试项目 tests\PCMig.Core.Tests（缺少安全网，不允许发版）' }
$testOut = & $dotnetExe test $testProj -c Release --nologo -v minimal 2>&1
$testOut | Select-String -Pattern '已通过|失败|error CS' | Select-Object -Last 3 | ForEach-Object { Write-Output ('   ' + $_.Line.Trim()) }
if ($LASTEXITCODE -ne 0) { Abort 'Core.Tests 未全绿 —— 先修测试再发版' }
$diagnosticsTestProj = Join-Path $repo 'tests\PCMig.Diagnostics.Tests\PCMig.Diagnostics.Tests.csproj'
if (-not (Test-Path $diagnosticsTestProj)) { Abort '找不到测试项目 tests\PCMig.Diagnostics.Tests（缺少诊断安全网，不允许发版）' }
$diagnosticsTestOut = & $dotnetExe test $diagnosticsTestProj -c Release --nologo -v minimal 2>&1
$diagnosticsTestOut | Select-String -Pattern '已通过|失败|error CS' | Select-Object -Last 3 | ForEach-Object { Write-Output ('   ' + $_.Line.Trim()) }
if ($LASTEXITCODE -ne 0) { Abort 'Diagnostics.Tests 未全绿 —— 先修测试再发版' }
Log '闸门 1/4：更新日志正文'
$clPath = Join-Path $repo 'docs\更新日志.md'
if (-not (Test-Path $clPath)) { Abort 'docs\更新日志.md 不存在' }
$cl = [IO.File]::ReadAllText($clPath, [Text.Encoding]::UTF8)
if ($cl -notmatch ('(?m)^## v' + [regex]::Escape($Version) + '\s')) {
  Abort ('docs\更新日志.md 里没有「## v' + $Version + '」条目。先把这一版改了什么写进日志（含背景、改了什么、根因），再打包。')
}
Log '闸门 2/4：版本号与发布日期对照表'
if ($cl -notmatch ('(?m)^\|\s*v' + [regex]::Escape($Version) + '\s*\|')) {
  Abort ('更新日志顶部的「版本号与发布日期对照」表里没有 v' + $Version + ' 一行。')
}
Log '闸门 3/4：使用说明'
$umPath = Join-Path $repo 'docs\使用说明.txt'
if (-not (Test-Path $umPath)) { Abort 'docs\使用说明.txt 不存在' }
if (([IO.File]::ReadAllText($umPath, [Text.Encoding]::UTF8)) -notmatch ('v' + [regex]::Escape($Version))) {
  Abort ('docs\使用说明.txt 里没有 v' + $Version + ' 的说明段落。')
}
Log '闸门 4/4：未重复发版'
$existingInstaller = Join-Path $delivery ('PCMigSetup-' + $Version + '.exe')
if ((Test-Path $existingInstaller) -and -not $AllowReplaceDeliveryInstaller) {
  Abort ('交付目录里已经存在 PCMigSetup-' + $Version + '.exe —— 默认禁止覆盖；仅在用户明确授权后传入 -AllowReplaceDeliveryInstaller。')
}
if (Test-Path $existingInstaller) {
  Write-Output ('   已获明确授权，将覆盖交付安装包 ' + $existingInstaller)
}
Write-Output '   四处闸门通过：日志、对照表、使用说明都已写好本版内容。'

# [闸门 5/5] 仓库口令残留（真实口令绝不进交付）
$secretPat = '1qaz' + '@' + 'wsx|pdell' + '210l|--password\s+[^\s<*$\x22\x27]{6,}'
# 追加形态：变量名含 Password/Pwd 的赋值语句被赋了非占位字面量（覆盖管理口令与加域口令这类命名）。
# 排除项：空串、尖括号占位、REPLACE_ME、双下划线模板令牌、以美元符开头的插值写法。
$secretAssignPat = '(?i)\$[A-Za-z_][A-Za-z0-9_:]*(?:password|pwd)[A-Za-z0-9_]*\s*=\s*[\x22\x27](?!\s*[\x22\x27]|<|REPLACE_ME|__|\$)[^\x22\x27\s]{6,}'
$secretPat = $secretPat + '|' + $secretAssignPat
$secretHits = @(Get-ChildItem $repo -Recurse -File -Include *.ps1, *.py, *.md, *.txt, *.cs, *.xaml, *.iss, *.json, *.yaml -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|dist)\\' } |
    Select-String -Pattern $secretPat -ErrorAction SilentlyContinue)
if ($secretHits.Count -gt 0) {
  Abort ('发现疑似真实口令 ' + $secretHits.Count + ' 处：' + (($secretHits | Select-Object -First 3 | ForEach-Object { $_.Filename + ':' + $_.LineNumber }) -join ' , ') + '（脱敏后再发版，示例写成 <口令>）')
}
Write-Output '   仓库无真实口令残留'

# ============ 版本号 ============
Log '写入版本号（改完逐个自查）'
$issPath = Join-Path $repo 'installer\pcmig.iss'
$old = ([regex]::Match([IO.File]::ReadAllText($issPath, (EncOf $issPath)), '#define MyAppVersion "([0-9.]+)"')).Groups[1].Value
if ($old -eq '') { Abort '读不出 installer\pcmig.iss 里的当前版本号' }
Patch 'src\PCMig.Gui\PCMig.Gui.csproj' '<Version>[0-9.]+</Version>' ('<Version>' + $Version + '</Version>')
Patch 'src\PCMig.WinUI\PCMig.WinUI.csproj' '<Version>[0-9.]+</Version>' ('<Version>' + $Version + '</Version>')
Patch 'src\PCMig.WinUI\MainWindow.xaml' 'Title="PCMig 迁移工具 · v[0-9.]+"' ('Title="PCMig 迁移工具 · v' + $Version + '"')
# ★ v0.5.1 新增，v0.5.4 Preview.2（2026-10-15）修订 ★ WinUI 主界面里还有两处版本文字，v0.5.0 时脚本漏改过它们：
#   ① 标题栏右侧 TitleBarVersionText（x:Name="TitleBarVersionText"）
#   ② 标题行「更新日志」徽章（ChangelogButton 里的 TextBlock，x:Name="PCMigVersionBadgeButton"）
#   两处形态都是 Text="vX.Y.Z"，用同一条正则一并替换；实测在 MainWindow.xaml 里恰好命中 2 处，
#   不会误伤 ChangelogButton 的 ToolTipService.ToolTip（那串是 ToolTip="查看从 v0.1.0 起…"）。
#
#   ⚠ 语义变更（必读）：自 v0.5.4 Preview.2 起，`MainWindow.xaml.cs` 新增 `ApplyDisplayVersion()`，
#     它**在运行时从程序集读 AssemblyInformationalVersion**，并覆写上述两处与窗口 Title。
#     因此 XAML 字面量**不再是运行时真值**，只是"程序集读不到时的降级兜底值"。
#
#   为什么仍保留这两条 Patch：① 降级路径要显示正确版本，不能停在历史值；
#     ② 不改变现有正式发版流程的行为（本次只做最小兼容调整）。
#
#   但由此产生一个必须防住的隐患：**Patch 成功 ≠ 界面会显示正确版本**。真正的运行时真值来自
#   四个 csproj 的 <InformationalVersion>（紧接其后的 L132-135 已在写）。因此在其后追加一条断言：
#   WinUI 的 <InformationalVersion> 必须等于本次 $Version —— 否则两者一旦脱节，界面会显示与本次
#   发布不符的版本，而旧的"XAML 自查"却照样通过。
Patch 'src\PCMig.WinUI\MainWindow.xaml' 'Text="v[0-9.]+"' ('Text="v' + $Version + '"')
Patch 'src\PCMig.Cli\PCMig.Cli.csproj' '<Version>[0-9.]+</Version>' ('<Version>' + $Version + '</Version>')
Patch 'src\PCMig.Core\PCMig.Core.csproj' '<Version>[0-9.]+</Version>' ('<Version>' + $Version + '</Version>')
Patch 'src\PCMig.Gui\MainWindow.xaml' 'Title="PCMig 迁移工具 v[0-9.]+"' ('Title="PCMig 迁移工具 v' + $Version + '"')
Patch 'README.md' '\*\*当前稳定版本：v[0-9.]+\*\*' ('**当前稳定版本：v' + $Version + '**')
Patch 'installer\pcmig.iss' '#define MyAppVersion "[0-9.]+"' ('#define MyAppVersion "' + $Version + '"')
Patch 'installer\pcmig.iss' 'VersionInfoVersion=[0-9.]+\.0' ('VersionInfoVersion=' + $Version + '.0')
# ★ v0.5.3 新增 ★ 四个 csproj 的 <InformationalVersion> 也必须是正式版本号（不带 Preview 后缀）：
#   它决定「关于」页、文件元数据与 `pcmig --version` 里显示的版本；v0.5.3 之前脚本完全没管这个节点，
#   于是 0.5.3 Preview 的 InformationalVersion 会跟着源码进正式包。节点不存在视为错误（Patch 会 Abort）。
Patch 'src\PCMig.Core\PCMig.Core.csproj' '<InformationalVersion>[0-9A-Za-z.\- ]+</InformationalVersion>' ('<InformationalVersion>' + $Version + '</InformationalVersion>')
Patch 'src\PCMig.Cli\PCMig.Cli.csproj' '<InformationalVersion>[0-9A-Za-z.\- ]+</InformationalVersion>' ('<InformationalVersion>' + $Version + '</InformationalVersion>')
Patch 'src\PCMig.Gui\PCMig.Gui.csproj' '<InformationalVersion>[0-9A-Za-z.\- ]+</InformationalVersion>' ('<InformationalVersion>' + $Version + '</InformationalVersion>')
Patch 'src\PCMig.WinUI\PCMig.WinUI.csproj' '<InformationalVersion>[0-9A-Za-z.\- ]+</InformationalVersion>' ('<InformationalVersion>' + $Version + '</InformationalVersion>')
# 声明式自查：逐个文件核对“该出现的那行”是否确实是新版本。
# 注意不能用“文件里不能出现旧版本号”来判——README/文档里出现历史版本号是正常的。
$expect = @(
  [pscustomobject]@{ F = 'src\PCMig.Gui\PCMig.Gui.csproj'; P = ('<Version>' + $Version + '</Version>') },
[pscustomobject]@{ F = 'src\PCMig.WinUI\PCMig.WinUI.csproj'; P = ('<Version>' + $Version + '</Version>') },
  [pscustomobject]@{ F = 'src\PCMig.WinUI\MainWindow.xaml'; P = ('Title="PCMig 迁移工具 · v' + $Version + '"') },
  [pscustomobject]@{ F = 'src\PCMig.WinUI\MainWindow.xaml'; P = ('Text="v' + $Version + '"') },
  [pscustomobject]@{ F = 'src\PCMig.Cli\PCMig.Cli.csproj'; P = ('<Version>' + $Version + '</Version>') },
  [pscustomobject]@{ F = 'src\PCMig.Core\PCMig.Core.csproj'; P = ('<Version>' + $Version + '</Version>') },
  [pscustomobject]@{ F = 'src\PCMig.Gui\MainWindow.xaml'; P = ('Title="PCMig 迁移工具 v' + $Version + '"') },
  [pscustomobject]@{ F = 'README.md'; P = ('**当前稳定版本：v' + $Version + '**') },
  [pscustomobject]@{ F = 'installer\pcmig.iss'; P = ('#define MyAppVersion "' + $Version + '"') },
  [pscustomobject]@{ F = 'installer\pcmig.iss'; P = ('VersionInfoVersion=' + $Version + '.0') },
  [pscustomobject]@{ F = 'src\PCMig.Core\PCMig.Core.csproj'; P = ('<InformationalVersion>' + $Version + '</InformationalVersion>') },
  [pscustomobject]@{ F = 'src\PCMig.Cli\PCMig.Cli.csproj'; P = ('<InformationalVersion>' + $Version + '</InformationalVersion>') },
  [pscustomobject]@{ F = 'src\PCMig.Gui\PCMig.Gui.csproj'; P = ('<InformationalVersion>' + $Version + '</InformationalVersion>') },
  [pscustomobject]@{ F = 'src\PCMig.WinUI\PCMig.WinUI.csproj'; P = ('<InformationalVersion>' + $Version + '</InformationalVersion>') }
)
foreach ($e in $expect) {
  $txt = [IO.File]::ReadAllText((Join-Path $repo $e.F), (EncOf (Join-Path $repo $e.F)))
  if (-not $txt.Contains($e.P)) { Abort ('自查失败：' + $e.F + ' 里没有「' + $e.P + '」') }
}
# ★ v0.5.1 新增 ★ 上面那条只证明"新版本串在"，还要证明"没有残留的旧徽章版本"。
#   MainWindow.xaml 里所有 Text="v<数字>" 形态（窗口 Title 不属于此形态）必须**全部**等于目标版本。
$mwPath = Join-Path $repo 'src\PCMig.WinUI\MainWindow.xaml'
$mwTxt = [IO.File]::ReadAllText($mwPath, (EncOf $mwPath))
$mwBadges = @([regex]::Matches($mwTxt, 'Text="v([0-9.]+)"') | ForEach-Object { $_.Groups[1].Value })
if ($mwBadges.Count -eq 0) { Abort '自查失败：src\PCMig.WinUI\MainWindow.xaml 里找不到任何 Text="vX.Y.Z" 版本徽章（标题栏版本文字 / 更新日志徽章）' }
$staleBadges = @($mwBadges | Where-Object { $_ -ne $Version })
if ($staleBadges.Count -gt 0) { Abort ('自查失败：src\PCMig.WinUI\MainWindow.xaml 仍有旧版本徽章 v' + ($staleBadges -join ' / v') + '（共 ' + $mwBadges.Count + ' 处，应全部为 v' + $Version + '）') }
Write-Output ('   WinUI 版本徽章 ' + $mwBadges.Count + ' 处全部为 v' + $Version)
# ★ v0.5.3 新增 ★ <InformationalVersion> 必须唯一且不含 Preview / 其它版本后缀（执行令 §五）。
foreach ($proj in @('src\PCMig.Core\PCMig.Core.csproj', 'src\PCMig.Cli\PCMig.Cli.csproj', 'src\PCMig.Gui\PCMig.Gui.csproj', 'src\PCMig.WinUI\PCMig.WinUI.csproj')) {
  $pp = Join-Path $repo $proj
  $vals = @([regex]::Matches([IO.File]::ReadAllText($pp, (EncOf $pp)), '<InformationalVersion>([^<]*)</InformationalVersion>') | ForEach-Object { $_.Groups[1].Value })
  if ($vals.Count -ne 1) { Abort ('自查失败：' + $proj + ' 里 <InformationalVersion> 出现 ' + $vals.Count + ' 处，应恰好 1 处') }
  if ($vals[0] -ne $Version) { Abort ('自查失败：' + $proj + ' 的 <InformationalVersion> 是「' + $vals[0] + '」，应为「' + $Version + '」（不得带 Preview 后缀）') }
}
Write-Output ('   四个 csproj 的 <InformationalVersion> 均为 v' + $Version)
# ★ v0.5.4 Preview.3 / R2 缺陷 E 修复（2026-10-17）★ 运行时真值一致性闸门（source 阶段）。
#
#   背景：自 v0.5.4 起 `MainWindow.xaml.cs` 的 `ApplyDisplayVersion()` 会在运行时用**程序集里的
#   AssemblyInformationalVersion** 覆写标题栏两处版本文字与窗口 Title。也就是说：
#       · XAML 里的 Text="vX.Y.Z"  ⇒ 只是降级兜底，**不是**运行时肉眼所见
#       · 真正决定界面显示的是四个 csproj 的 <InformationalVersion>
#
#   ★ 为什么改成调用外部函数（缺陷 E · R2 2026-10-17）★
#     旧版这里是**两条恒不失败**的断言：
#       · 旧 L195-201 重新读一遍 WinUI.csproj 的 <InformationalVersion> 再比 $Version
#         ⇒ 与上面 L180-186 对同一文件同一节点同一等式的检查**完全重复**，恒为 false；
#       · 旧 L203-205 `$mwBadges -notcontains $Version`
#         ⇒ L175 已保证非空、L176-177 已保证每项 == $Version，同样恒为 false。
#     两条断言写着"运行时版本真值闸门"，实际一次也不可能触发。
#     现在改为调用 `tools\version-gate.ps1` 的 `Test-VersionSourceTruth`，它检查的是
#     **旧断言结构上无法发现的三类真错**：
#       (1) ApplyDisplayVersion() 机制被删 / 被改成读 AssemblyVersion（真值来源变了）；
#       (2) <InformationalVersion> 节点出现 0 处或 2 处（构建取哪个不可控）；
#       (3) XAML 里连一处降级兜底徽章都没有（程序集读不到时界面显示旧值）。
#     `tools\version-gate-tests.ps1` 用正向 + 反向用例证明它真的会失败（当前 10/10 PASS）。
$versionGate = Join-Path $PSScriptRoot 'version-gate.ps1'
if (-not (Test-Path $versionGate)) { Abort ('版本闸门脚本缺失：' + $versionGate) }
. $versionGate
$srcGate = Test-VersionSourceTruth -RepoRoot $repo -Version $Version
if (-not $srcGate.Ok) {
  Abort ('运行时版本真值闸门失败（source 阶段）：' + [char]10 + '  - ' + ($srcGate.Problems -join ([char]10 + '  - ')) + [char]10 +
         '界面版本由 ApplyDisplayVersion() 从 <InformationalVersion> 读出并覆写标题栏与窗口标题 ⇒ 上述任一项不成立都会让用户看到错误的版本号，' +
         '而 XAML 自查却照样通过。请先修正再发版。')
}
Write-Output ('   运行时版本真值一致（source 阶段）：ApplyDisplayVersion 读 <InformationalVersion> = v' + $Version + '，降级徽章齐备')
Write-Output ('   版本号 ' + $old + ' → ' + $Version + '，' + $expect.Count + ' 处声明全部自查通过')

# ============ 日志 TXT + 打包 ============
# ★ 2026-10-24（R1-6C 复审 P2-10）★ 旧写法 `Get-Process PCMig` 是**按进程名前缀猜**：
#   Net8 自包含程序的进程名是 `PCMig.WinUI`，`Get-Process PCMig` 一个都匹配不到 ⇒ 预清理从来没生效过。
#   改成按可执行名精确匹配（不含扩展名），并且只杀这一版自检真正需要的那个名字。
foreach ($pn in @('PCMig.WinUI')) {
  $stale = @(Get-Process -Name $pn -ErrorAction SilentlyContinue)
  if ($stale.Count -gt 0) {
    $stale | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Output ('   已结束残留的 ' + $pn + ' 进程 ' + $stale.Count + ' 个')
  }
}
Start-Sleep -Seconds 2
Log '生成 更新日志.txt（记事本可直接打开）'
# ★ 2026-10-06 修复 ★ 不能直接写 `python`：本机 PATH 上的 python.exe / python3.exe 是
#   Microsoft Store 的“应用执行别名”存根（<WindowsApps 目录>），
#   调用它只会打印 "Python was not found; run without arguments to install from the Microsoft Store..."
#   并返回非 0，**却让脚本继续往下跑** —— 结果是 docs\更新日志.txt 根本没被重新生成，
#   紧随其后的闸门报「更新日志.txt 里没有 vX.Y.Z」而中止（v0.5.0 首发就卡在这里）。
#   这里按「环境变量 PCMIG_PYTHON → py -3 → 真实 python」顺序解析解释器；都不行就明确中止。
#   注意：py.exe 本身也在 WindowsApps 目录下，但它是**可用的**启动垫片（实测 `py -3` = Python 3.14.7），
#   因此只把 python / python3 的 WindowsApps 存根排除掉。
$pyExe = $null; $pyArg = @()
$cands = @()
if ($env:PCMIG_PYTHON) { $cands += ,@($env:PCMIG_PYTHON, @()) }
$cands += ,@('py', @('-3'))
$cands += ,@('python', @())
foreach ($c in $cands) {
  $exe = $c[0]; $extra = $c[1]
  if ($exe -like '*\*') {
    if (-not (Test-Path $exe)) { continue }
  } else {
    $cmd = Get-Command $exe -ErrorAction SilentlyContinue
    if (-not $cmd) { continue }
    $exe = $cmd.Source
    if ($exe -like '*\WindowsApps\*' -and $c[0] -ne 'py') { continue }
  }
  try {
    $ver = & $exe @extra --version 2>&1
    if ($LASTEXITCODE -eq 0 -and (($ver -join ' ') -match 'Python 3')) { $pyExe = $exe; $pyArg = $extra; break }
  } catch { }
}
if (-not $pyExe) {
  Abort '找不到可用的 Python 3 解释器（PATH 上的 python 是 Microsoft Store 别名存根）。请把环境变量 PCMIG_PYTHON 指向真实 python.exe 后重试。'
}
Write-Output ('   使用 Python：' + $pyExe + ' ' + ($pyArg -join ' '))
& $pyExe @pyArg (Join-Path $repo 'tools\md2txt.py')
if ($LASTEXITCODE -ne 0) { Abort ('md2txt.py 执行失败（退出码 ' + $LASTEXITCODE + '）—— 更新日志.txt 未重新生成，发版中止。') }
$txtPath = Join-Path $repo 'docs\更新日志.txt'
if (-not (Test-Path $txtPath)) { Abort '更新日志.txt 未生成' }
$bytes = [IO.File]::ReadAllBytes($txtPath)
if (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) { Abort '更新日志.txt 不是 UTF-8 BOM，记事本可能识别不了中文' }
if (([IO.File]::ReadAllText($txtPath, [Text.Encoding]::UTF8)) -notmatch [regex]::Escape($Version)) { Abort ('更新日志.txt 里没有 v' + $Version) }

Log 'publish（WinUI 主界面 + CLI，自包含）'
Remove-Item (Join-Path $repo 'dist\cli'), (Join-Path $repo 'dist\gui'), (Join-Path $repo 'dist\winui'), (Join-Path $repo 'dist\app') -Recurse -Force -ErrorAction SilentlyContinue
& $dotnetExe publish (Join-Path $repo 'src\PCMig.Cli\PCMig.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $repo 'dist\cli') 2>&1 | Select-String -Pattern 'error|-> ' | Select-Object -First 6
# ★ v0.5.0（2026-10-06）★ 主界面换成 WinUI 3。
#   为什么 WinUI 不能像旧 WPF 那样压成单文件：它是 unpackaged + WindowsAppSDK/Win2D 自包含应用，
#   原生组件（Microsoft.WindowsAppRuntime.* / Microsoft.Graphics.Canvas*.dll / resources.pri 等）
#   必须与 exe 同目录并存，压成单文件会在启动时解压失败或找不到原生依赖。
#   ⇒ 整目录 publish 后**整棵树**进包；安装包与 Portable 都以目录形态交付。
& $dotnetExe publish (Join-Path $repo 'src\PCMig.WinUI\PCMig.WinUI.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o (Join-Path $repo 'dist\winui') 2>&1 | Select-String -Pattern 'error|-> ' | Select-Object -First 6
if (-not (Test-Path (Join-Path $repo 'dist\winui\PCMig.WinUI.exe'))) { Abort 'WinUI 未产出' }
# ★ v0.5.1（2026-10-06）★ 旧 WPF 前端（src\PCMig.Gui）**退出正式交付链**：
#   源码继续保留在仓库里作为历史实现 / 回退参考，但**不再 publish、不再进安装包与 Portable**。
#   正式用户交付物只允许两个可执行文件：PCMig.WinUI.exe 与 pcmig-cli.exe。
#   下面组装 dist\app 之后有硬门禁，任何 PCMig-classic.exe / PCMig.exe 都会被当场拦下。
#   （v0.5.0 及以前曾把 WPF 打成 PCMig-classic.exe 随包发布作为"经典界面·回退"入口，本轮起取消。）

Log '组装 dist\app'
$app = Join-Path $repo 'dist\app'
New-Item $app -ItemType Directory | Out-Null
Copy-Item -Path (Join-Path $repo 'dist\winui\*') -Destination $app -Recurse -Force
Copy-Item (Join-Path $repo 'dist\cli\pcmig.exe') (Join-Path $app 'pcmig-cli.exe')
# [硬门禁] v0.5.1 起：旧 WPF 前端不得进入正式用户交付物。
# 这道断言就是"不得再以备用 / 经典界面 / 回退名义随包发布"的可执行约束——
# 将来谁把 publish PCMig.Gui 或 Copy-Item PCMig-classic.exe 加回来，发版会在这里中止。
foreach ($legacy in @('PCMig-classic.exe', 'PCMig.exe')) {
  if (Test-Path (Join-Path $app $legacy)) { Abort ('交付树 dist\app 里出现了旧 WPF 前端 ' + $legacy + ' —— v0.5.1 起旧 WPF 不得进入正式用户交付物（只允许 PCMig.WinUI.exe 与 pcmig-cli.exe）。') }
}
Copy-Item (Join-Path $repo 'matrix') (Join-Path $app 'matrix') -Recurse
Copy-Item $umPath (Join-Path $app '使用说明.txt')
Copy-Item $txtPath (Join-Path $app '更新日志.txt')
if (Test-Path (Join-Path $repo 'docs\首日实测检查表.md')) { Copy-Item (Join-Path $repo 'docs\首日实测检查表.md') (Join-Path $app '首日实测检查表.md') }

# ★ v0.5.4 Preview.3 / R2 缺陷 E 修复（2026-10-17）★ 运行时真值一致性闸门（published 阶段）。
#
#   ★ 为什么必须多这一道（旧版完全没有）★
#     上面 `:255` 与 `:261` 两条 `dotnet publish` **都没有显式传 -p:Version / -p:InformationalVersion**，
#     完全依赖前面的 Patch 已经把 csproj 写对。只要有人后来在 csproj 里加了条件属性、
#     或 publish 时被 Directory.Build.props / 全局属性覆盖，就会出现
#     「**源码里的版本号是对的、构建出来的 DLL 版本号是错的**」——
#     source 阶段闸门对此**结构上无感知**（它只读源码），而用户看到的是构建产物。
#     ⇒ 唯一能发现它的检查是：读**已构建 DLL 的版本资源**再比。
#
#     `Test-VersionPublishedTruth` 同时验两件事：
#       (1) ProductVersion（剥掉 `+<commit>` 后缀）必须 == $Version
#           —— 这正是 ApplyDisplayVersion() 运行时读到的 AssemblyInformationalVersion；
#       (2) FileVersion 必须非空且以 $Version 的数值段开头。
#     产物缺失时报错而非静默跳过（静默跳过等于放行）。
$pubGate = Test-VersionPublishedTruth -RepoRoot $repo -Version $Version -RelativePaths @(
  'dist\app\PCMig.WinUI.dll',
  'dist\app\PCMig.Core.dll',
  'dist\app\PCMig.Diagnostics.dll',
  'dist\app\PCMig.Diagnostics.Abstractions.dll')
if (-not $pubGate.Ok) {
  Abort ('运行时版本真值闸门失败（published 阶段）：' + [char]10 + '  - ' + ($pubGate.Problems -join ([char]10 + '  - ')) + [char]10 +
         '源码写对了不代表产物带上了：上面两条 dotnet publish 都没有显式传 -p:Version，' +
         '一旦被全局属性覆盖，用户装到的包里版本号就是错的，而 source 阶段闸门看不出来。')
}
Write-Output ('   运行时版本真值一致（published 阶段）：dist\app 的 4 个托管程序集 ProductVersion 均为 v' + $Version)

Log 'Inno Setup 编译'
$setup = Join-Path $repo ('dist\PCMigSetup-' + $Version + '.exe')
Remove-Item $setup -Force -ErrorAction SilentlyContinue
& $isccExe (Join-Path $repo 'installer\pcmig.iss') 2>&1 | Select-Object -Last 2
if (-not (Test-Path $setup)) { Abort '安装包未生成' }

Log '启动 WinUI 自检（★ R1-6C 复审 P0-3：必须在交付复制**之前**）'
# ★ 2026-10-24（R1-6C 复审 P0-3）★ 顺序问题：
#   旧实现先把安装包与 Portable 复制进交付根（再刷哈希），**最后**才启动自检。
#   于是"跑起来不是这一版 / 直接闪退"这种致命问题，交付区已经被污染了才被中止 ——
#   中止只阻止了后续步骤，拦不住已经落地的坏包。
#   现在自检放在复制之前，并且直接对 `dist\app` 的**待交付产物**做，而不是对工作副本做。
$selfExe = Join-Path $app 'PCMig.WinUI.exe'
if (-not (Test-Path $selfExe)) { Abort ('找不到待交付的 ' + $selfExe + ' —— 无法做启动自检，停止发版') }
# ★ v0.5.3 新增 ★ 除"没闪退"外，还要证明跑起来的**确实是这一版**：
#   ① 文件元数据（FileVersion / ProductVersion）里不能出现上一版或 Preview 后缀；
#   ② 主窗口标题必须是 MainWindow.xaml 里被改成的那串（`PCMig 迁移工具 · vX.Y.Z`）。
$vi = (Get-Item $selfExe).VersionInfo
Write-Output ('   文件元数据 FileVersion=' + $vi.FileVersion + ' ProductVersion=' + $vi.ProductVersion)
if ($vi.FileVersion -notlike ($Version + '*')) { Abort ('WinUI 文件元数据 FileVersion=' + $vi.FileVersion + ' 与目标版本 ' + $Version + ' 不一致') }
if ($vi.ProductVersion -notlike ($Version + '*')) { Abort ('WinUI 文件元数据 ProductVersion=' + $vi.ProductVersion + ' 与目标版本 ' + $Version + ' 不一致（是否残留 Preview 后缀？）') }
if ($vi.ProductVersion -match '(?i)preview') { Abort ('WinUI 文件元数据 ProductVersion=' + $vi.ProductVersion + ' 残留 Preview 标识，不允许进入正式包') }
# ★ R1-6C 复审 P2 ★ 自检进程必须**按 PID 收尾**：旧实现只 Start-Process 从不结束，
#   任何一次中止（或正常结束）都会把界面留在桌面上，下次自检还会撞上同一份进程名。
$selfProc = $null
try {
  $selfProc = Start-Process $selfExe -PassThru
  Start-Sleep -Seconds 22
  if ($selfProc.HasExited) { Abort ('WinUI 闪退，退出码 ' + $selfProc.ExitCode) }
  $expectedTitle = 'PCMig 迁移工具 · v' + $Version
  $title = ''
  for ($qi = 0; $qi -lt 20 -and $title -eq ''; $qi++) {
    $selfProc.Refresh()
    $title = [string]$selfProc.MainWindowTitle
    if ($title -eq '') { Start-Sleep -Milliseconds 500 }
  }
  Write-Output ('   WinUI PID=' + $selfProc.Id + '，窗口标题="' + $title + '"（期望="' + $expectedTitle + '"）')
  if ($title -eq '') { Abort 'WinUI 启动后读不到主窗口标题 —— 无法确认窗口已就绪，停止发版' }
  if ($title -ne $expectedTitle) { Abort ('WinUI 窗口标题是「' + $title + '」，期望「' + $expectedTitle + '」—— 跑起来的可能不是这一版') }
  Write-Output '   WinUI 窗口标题与目标版本一致'
} finally {
  if ($selfProc -and -not $selfProc.HasExited) {
    try { Stop-Process -Id $selfProc.Id -Force -ErrorAction Stop; Write-Output ('   已结束自检进程 PID=' + $selfProc.Id) }
    catch { Write-Output ('   [warn] 自检进程 PID=' + $selfProc.Id + ' 未能结束：' + $_.Exception.Message) }
  }
}
Log '交付 + 逐文件哈希比对'
New-Item $delivery -ItemType Directory -Force | Out-Null
# Portable 是发版脚本自己的产物目录：先清空再铺，避免上一版的旧 exe 与新版混在一起。
# （交付区根目录并列的历史安装包 PCMigSetup-*.exe **不动**，用户要求全量保留。）
$portable = Join-Path $delivery 'Portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item $portable -ItemType Directory -Force | Out-Null
# Portable 与工作副本都收**整棵 app 树**（WinUI 必须与其原生组件同目录才能启动）
# ★ v0.5.1（2026-10-06）★ 工作副本是**跨版本复用**的目录：上一版（v0.5.0）铺进去的
#   PCMig-classic.exe 会一直躺在工作副本目录（-WorkCopy / $env:PCMIG_WORKCOPY）里。下面那道"不得含旧 WPF 前端"的门禁如果只看
#   拷贝结果，会被这个**陈旧残留**触发中止（而它并不是本次 publish 带进来的）。
#   ⇒ 先清掉工作副本里的旧 WPF 残留，再整树铺新的；拷贝之后的门禁才真正校验"本次产出是否干净"。
foreach ($legacy in @('PCMig-classic.exe', 'PCMig.exe')) {
  $stale = Join-Path $workCopy $legacy
  if (Test-Path $stale) { Remove-Item $stale -Force; Write-Output ('   已从工作副本移除上一版残留的旧 WPF 前端 ' + $legacy) }
}
Get-ChildItem $app -Force | ForEach-Object {
  Copy-Item $_.FullName $portable -Recurse -Force
  Copy-Item $_.FullName $workCopy -Recurse -Force
}
# [硬门禁] v0.5.1 起：Portable 与工作副本同样不得含旧 WPF 前端（dist\app 已查过一遍，这里是第二/第三道）。
foreach ($legacy in @('PCMig-classic.exe', 'PCMig.exe')) {
  if (Test-Path (Join-Path $portable $legacy)) { Abort ('交付 Portable 里出现了旧 WPF 前端 ' + $legacy + ' —— v0.5.1 起旧 WPF 不得进入正式用户交付物。') }
  if (Test-Path (Join-Path $workCopy $legacy)) { Abort ('工作副本里出现了旧 WPF 前端 ' + $legacy + ' —— v0.5.1 起旧 WPF 不得进入正式用户交付物。') }
}
Copy-Item $setup (Join-Path $delivery ('PCMigSetup-' + $Version + '.exe')) -Force
Copy-Item (Join-Path $portable '使用说明.txt') (Join-Path $delivery '使用说明.txt') -Force
Copy-Item (Join-Path $portable '更新日志.txt') (Join-Path $delivery '更新日志.txt') -Force
$pairs = @(
  [pscustomobject]@{ Src = (Join-Path $app 'PCMig.WinUI.exe'); Dst = (Join-Path $portable 'PCMig.WinUI.exe') },
  [pscustomobject]@{ Src = (Join-Path $app 'PCMig.WinUI.exe'); Dst = (Join-Path $workCopy 'PCMig.WinUI.exe') },
  [pscustomobject]@{ Src = (Join-Path $app 'pcmig-cli.exe'); Dst = (Join-Path $portable 'pcmig-cli.exe') },
  [pscustomobject]@{ Src = (Join-Path $app 'pcmig-cli.exe'); Dst = (Join-Path $workCopy 'pcmig-cli.exe') },
  [pscustomobject]@{ Src = $setup; Dst = (Join-Path $delivery ('PCMigSetup-' + $Version + '.exe')) },
  [pscustomobject]@{ Src = (Join-Path $app '更新日志.txt'); Dst = (Join-Path $delivery '更新日志.txt') },
  [pscustomobject]@{ Src = (Join-Path $app '使用说明.txt'); Dst = (Join-Path $delivery '使用说明.txt') }
)
foreach ($p in $pairs) {
  if ((Get-FileHash $p.Src).Hash -ne (Get-FileHash $p.Dst).Hash) { Abort ('哈希不一致：' + $p.Dst) }
  Write-Output ('   MATCH ' + (Split-Path $p.Dst -Leaf) + '  ' + $p.Dst)
}
# 历史安装包一律保留：交付区按「各版本并列存放」使用（用户明确要求），全量存档在 dist\。
# 此处只列出并列版本，绝不删除。曾因自动清理删掉交付区 4 个历史包，已逐文件 SHA256 校验后恢复。
Get-ChildItem (Join-Path $delivery 'PCMigSetup-*.exe') | Sort-Object Name | ForEach-Object { Write-Output ('   并列版本 ' + $_.Name) }
if ($mirror) {
  # ★ v0.5.3（2026-10-09）★ 镜像步骤由 robocopy /MIR 改为「可证明安全的白名单同步」。
  #   为什么改：/MIR 会**删除镜像目标里多出来的文件和目录** —— 实测镜像顶层有 `.git` 与 `.codeartsdoer`，
  #   它们不在仓库顶层白名单里，一次 /MIR 就会把它们删掉；发版脚本不该有这种破坏力。
  #   现在的行为：只把**仓库顶层已存在的条目**单向复制到镜像，绝不 /PURGE、绝不删除镜像里的任何内容；
  #   复制前先跑一次 /L（dry-run）把完整影响范围落盘留证。
  $mirrorExcludeDirs = @('bin', 'obj', 'dist', '.git', '.vs', '.codeartsdoer')
  $mirrorItems = @(Get-ChildItem -LiteralPath $repo -Force | Where-Object { $mirrorExcludeDirs -notcontains $_.Name })
  if ($mirrorItems.Count -eq 0) { Abort '镜像白名单为空 —— 仓库顶层读不到任何条目，停止以免破坏镜像' }
  Write-Output ('   镜像白名单 ' + $mirrorItems.Count + ' 项：' + (($mirrorItems | ForEach-Object { $_.Name }) -join ' , '))
  #   ★ v0.5.3 修正（2026-10-09）★ 顶层**文件**不能用 robocopy /E 同步：
  #   `/E` 会把源当成目录遍历，实测对 README.md 这类文件报
  #   「ERROR 267 (0x0000010B) Accessing Source Directory … The directory name is invalid」并退出 16，
  #   于是 README.md / .gitignore / PCMig.sln 三个顶层文件被静默漏掉（镜像里 README 还停在旧版本）。
  #   现在：目录走 robocopy /E（并检查退出码 ≥8 即中止），文件走 Copy-Item -Force。
  $mirrorDryRun = Join-Path $env:TEMP ('pcmig-mirror-dryrun-v' + $Version + '.txt')
  $dryLines = New-Object System.Collections.Generic.List[string]
  $dryLines.Add('=== 源码镜像 dry-run（只列影响范围，不写入）v' + $Version + ' ===')
  foreach ($mi in $mirrorItems) {
    if ($mi.PSIsContainer) {
      $dryLines.Add('--- 目录 ' + $mi.Name + ' （robocopy /E /L） ---')
      $dryOut = @(& robocopy $mi.FullName (Join-Path $mirror $mi.Name) /E /L /XD bin obj dist .git .vs .codeartsdoer /XF *.user /NFL /NDL /NP /R:0 /W:0)
      foreach ($ln in $dryOut) { $dryLines.Add([string]$ln) }
    } else {
      $dryLines.Add('--- 文件 ' + $mi.Name + ' （将直接覆盖镜像中的同名文件） ---')
    }
  }
  $dryLines | Out-File -FilePath $mirrorDryRun -Encoding UTF8
  Write-Output ('   镜像 dry-run（/L）完整影响范围已留证：' + $mirrorDryRun)
  foreach ($mi in $mirrorItems) {
    if ($mi.PSIsContainer) {
      & robocopy $mi.FullName (Join-Path $mirror $mi.Name) /E /XD bin obj dist .git .vs .codeartsdoer /XF *.user /NFL /NDL /NP /R:0 /W:0 | Out-Null
      if ($LASTEXITCODE -ge 8) { Abort ('镜像同步失败：目录 ' + $mi.Name + '（robocopy 退出码 ' + $LASTEXITCODE + '）') }
    } else {
      try {
        Copy-Item -LiteralPath $mi.FullName -Destination (Join-Path $mirror $mi.Name) -Force -ErrorAction Stop
      } catch {
        Abort ('镜像同步失败：文件 ' + $mi.Name + ' —— ' + $_.Exception.Message)
      }
      Write-Output ('   镜像文件已同步 ' + $mi.Name)
    }
  }
  Write-Output '   镜像同步完成：单向复制，未删除镜像中的任何文件（无 /MIR、无 /PURGE）'
} else {
  Write-Output '   未配置源码镜像（$mirror 为空），跳过镜像步骤'
}

# ★ v0.5.1（2026-10-06）★ 经典回退界面的启动自检已删除：旧 WPF 前端退出正式交付链，
#   交付区与工作副本里不会再出现 PCMig-classic.exe，自检只启动 PCMig.WinUI.exe 这一个主界面。
#   旧 WPF 源码仍在仓库（src\PCMig.Gui），需要时自行 build 作为回退参考，但不随包发布。

Write-Output ''
Write-Output ('=== v' + $Version + ' 打包完成，发版后请做这三项验证 ===')
Write-Output '1) 应用内：顶栏「更新日志」，本版条目应在列表最上方并默认选中'
Write-Output '2) 文件：交付目录与安装目录的 更新日志.txt 用记事本打开应含本版'
Write-Output '3) 命令行：pcmig changelog 输出应含本版'
Write-Output '并请把本轮根因/修法/验证方式追加到 docs\测试报告-公司环境.md'
