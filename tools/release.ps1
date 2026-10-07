# PCMig 发版脚本 —— 先写日志，再打包；没写日志就打不出包。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File "E:\Project\deepseek work\PCMig\tools\release.ps1" -Version 0.4.5
# 路径纪律（见 docs/RELEASE.md）：
#   本机（个人电脑）：仓库 E:\Project\deepseek work\PCMig ｜ 交付 E:\Project\PCMig ｜ 工作副本 D:\PCMig ｜ 源码镜像 E:\Project\镜像备份源码\PCMig
#   公司电脑        ：仓库 E:\deepseek work\PCMig ｜ 交付 E:\K\PCMig ｜ 工作副本 D:\PCMig ｜ 源码镜像（按需指定）
#   换机只改下面这几行，其余一律不动。$mirror 留空＝跳过镜像；镜像不含 dist/bin/obj，放在交付盘之外。
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = 'E:\Project\deepseek work\PCMig'
$delivery = 'E:\Project\PCMig'
$mirror = 'E:\Project\镜像备份源码\PCMig'
$workCopy = 'D:\PCMig'
if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet") {
  $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
  $env:Path = "$env:DOTNET_ROOT;" + $env:Path
} elseif (Test-Path 'C:\Program Files\dotnet\dotnet.exe') {
  $env:DOTNET_ROOT = 'C:\Program Files\dotnet'
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

# ============ 闸门：三处必须已经写好本版内容 ============
Log ('=== 发布 v' + $Version + ' ===')

# [闸门 0/5] 单元测试 —— 没有安全网不允许发版
# 注意：这里直接指向 csproj 而不是 .sln。若测试项目没被加进 sln，
# `dotnet test PCMig.sln` 会【显示成功但一个用例都不跑】，闸门形同虚设。
Log '闸门 0/5：单元测试全绿'
$testProj = Join-Path $repo 'tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj'
if (-not (Test-Path $testProj)) { Abort '找不到测试项目 tests\PCMig.Core.Tests（缺少安全网，不允许发版）' }
$testOut = & dotnet test $testProj -c Release --nologo -v minimal 2>&1
$testOut | Select-String -Pattern '已通过|失败|error CS' | Select-Object -Last 3 | ForEach-Object { Write-Output ('   ' + $_.Line.Trim()) }
if ($LASTEXITCODE -ne 0) { Abort 'Core.Tests 未全绿 —— 先修测试再发版' }
$diagnosticsTestProj = Join-Path $repo 'tests\PCMig.Diagnostics.Tests\PCMig.Diagnostics.Tests.csproj'
if (-not (Test-Path $diagnosticsTestProj)) { Abort '找不到测试项目 tests\PCMig.Diagnostics.Tests（缺少诊断安全网，不允许发版）' }
$diagnosticsTestOut = & dotnet test $diagnosticsTestProj -c Release --nologo -v minimal 2>&1
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
if (Test-Path (Join-Path $delivery ('PCMigSetup-' + $Version + '.exe'))) {
  Abort ('交付目录里已经存在 PCMigSetup-' + $Version + '.exe —— 换一个版本号（或先删掉旧包）。')
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
# ★ v0.5.1 新增 ★ WinUI 主界面里还有两处**运行时肉眼可见**的版本文字，v0.5.0 时脚本漏改过它们：
#   ① 标题栏右侧 TitleBarVersionText（虽有 x:Name，但没有任何 .cs 给它赋值 ⇒ XAML 字面量就是运行时真值）
#   ② 标题行「更新日志」徽章（PCMigVersionBadgeButton 里的 TextBlock）
#   两处形态都是 Text="vX.Y.Z"，用同一条正则一并替换；实测在 MainWindow.xaml 里恰好命中 2 处，
#   不会误伤 ChangelogButton 的 ToolTipService.ToolTip（那串是 ToolTip="查看从 v0.1.0 起…"）。
Patch 'src\PCMig.WinUI\MainWindow.xaml' 'Text="v[0-9.]+"' ('Text="v' + $Version + '"')
Patch 'src\PCMig.Cli\PCMig.Cli.csproj' '<Version>[0-9.]+</Version>' ('<Version>' + $Version + '</Version>')
Patch 'src\PCMig.Core\PCMig.Core.csproj' '<Version>[0-9.]+</Version>' ('<Version>' + $Version + '</Version>')
Patch 'src\PCMig.Gui\MainWindow.xaml' 'Title="PCMig 迁移工具 v[0-9.]+"' ('Title="PCMig 迁移工具 v' + $Version + '"')
Patch 'README.md' '\*\*当前版本：v[0-9.]+\*\*' ('**当前版本：v' + $Version + '**')
Patch 'installer\pcmig.iss' '#define MyAppVersion "[0-9.]+"' ('#define MyAppVersion "' + $Version + '"')
Patch 'installer\pcmig.iss' 'VersionInfoVersion=[0-9.]+\.0' ('VersionInfoVersion=' + $Version + '.0')
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
  [pscustomobject]@{ F = 'README.md'; P = ('**当前版本：v' + $Version + '**') },
  [pscustomobject]@{ F = 'installer\pcmig.iss'; P = ('#define MyAppVersion "' + $Version + '"') },
  [pscustomobject]@{ F = 'installer\pcmig.iss'; P = ('VersionInfoVersion=' + $Version + '.0') }
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
Write-Output ('   版本号 ' + $old + ' → ' + $Version + '，' + $expect.Count + ' 处声明全部自查通过')

# ============ 日志 TXT + 打包 ============
Get-Process PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
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
dotnet publish (Join-Path $repo 'src\PCMig.Cli\PCMig.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $repo 'dist\cli') 2>&1 | Select-String -Pattern 'error|-> ' | Select-Object -First 6
# ★ v0.5.0（2026-10-06）★ 主界面换成 WinUI 3。
#   为什么 WinUI 不能像旧 WPF 那样压成单文件：它是 unpackaged + WindowsAppSDK/Win2D 自包含应用，
#   原生组件（Microsoft.WindowsAppRuntime.* / Microsoft.Graphics.Canvas*.dll / resources.pri 等）
#   必须与 exe 同目录并存，压成单文件会在启动时解压失败或找不到原生依赖。
#   ⇒ 整目录 publish 后**整棵树**进包；安装包与 Portable 都以目录形态交付。
dotnet publish (Join-Path $repo 'src\PCMig.WinUI\PCMig.WinUI.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o (Join-Path $repo 'dist\winui') 2>&1 | Select-String -Pattern 'error|-> ' | Select-Object -First 6
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

Log 'Inno Setup 编译'
$setup = Join-Path $repo ('dist\PCMigSetup-' + $Version + '.exe')
Remove-Item $setup -Force -ErrorAction SilentlyContinue
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" (Join-Path $repo 'installer\pcmig.iss') 2>&1 | Select-Object -Last 2
if (-not (Test-Path $setup)) { Abort '安装包未生成' }

Log '交付 + 逐文件哈希比对'
New-Item $delivery -ItemType Directory -Force | Out-Null
# Portable 是发版脚本自己的产物目录：先清空再铺，避免上一版的旧 exe 与新版混在一起。
# （交付区根目录并列的历史安装包 PCMigSetup-*.exe **不动**，用户要求全量保留。）
$portable = Join-Path $delivery 'Portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item $portable -ItemType Directory -Force | Out-Null
# Portable 与工作副本都收**整棵 app 树**（WinUI 必须与其原生组件同目录才能启动）
# ★ v0.5.1（2026-10-06）★ 工作副本是**跨版本复用**的目录：上一版（v0.5.0）铺进去的
#   PCMig-classic.exe 会一直躺在 D:\PCMig 里。下面那道"不得含旧 WPF 前端"的门禁如果只看
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
  robocopy $repo $mirror /MIR /XD bin obj dist /XF *.user /NFL /NDL /NP /R:0 /W:0 | Select-Object -Last 1
} else {
  Write-Output '   未配置源码镜像（$mirror 为空），跳过镜像步骤'
}

Log '启动 WinUI 自检'
$pr = Start-Process (Join-Path $workCopy 'PCMig.WinUI.exe') -PassThru
Start-Sleep -Seconds 22
if ($pr.HasExited) { Abort ('WinUI 闪退，退出码 ' + $pr.ExitCode) }
Write-Output ('   WinUI PID=' + $pr.Id + '，标题应为 PCMig 迁移工具 · v' + $Version)

# ★ v0.5.1（2026-10-06）★ 经典回退界面的启动自检已删除：旧 WPF 前端退出正式交付链，
#   交付区与工作副本里不会再出现 PCMig-classic.exe，自检只启动 PCMig.WinUI.exe 这一个主界面。
#   旧 WPF 源码仍在仓库（src\PCMig.Gui），需要时自行 build 作为回退参考，但不随包发布。

Write-Output ''
Write-Output ('=== v' + $Version + ' 打包完成，发版后请做这三项验证 ===')
Write-Output '1) 应用内：顶栏「更新日志」，本版条目应在列表最上方并默认选中'
Write-Output '2) 文件：交付目录与安装目录的 更新日志.txt 用记事本打开应含本版'
Write-Output '3) 命令行：pcmig changelog 输出应含本版'
Write-Output '并请把本轮根因/修法/验证方式追加到 docs\测试报告-公司环境.md'
