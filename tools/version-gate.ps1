# PCMig 版本身份闸门 —— 可被单独测试的检查函数（无副作用、可注入仓库根）。
#
# ★ 存在的理由（缺陷 E · R2 2026-10-17）★
#   tools\release.ps1 里原有两条自称"运行时版本真值闸门"的断言（旧 L197-201 与 L203-205）
#   实际**永远不会失败**：
#     · 旧 L197 比较 WinUI csproj 的 <InformationalVersion> 与 $Version —— 而紧邻其上的
#       L180-186 已经对**同样四个 csproj（含 WinUI）**断言过同一个等式 ⇒ 恒为 false 分支；
#     · 旧 L203 要求 $mwBadges 含 $Version —— 而 L174-177 已断言"徽章数 > 0 且每项都等于 $Version"
#       ⇒ 恒成立。
#   结果是这两条断言只在注释里"看起来"守住了运行时版本，真正出事时一条都拦不住。
#
#   本文件把"运行时版本真值"重写成**两条互不依赖**的检查：
#     · source    阶段：验证"界面版本由程序集决定"这条**机制**本身还在
#                          （MainWindow.xaml.cs 的 ApplyDisplayVersion 仍在读
#                            AssemblyInformationalVersionAttribute），
#                          以及 <InformationalVersion> 恰好一处、XAML 降级徽章存在。
#                          —— 这能发现"有人删掉/改名了 ApplyDisplayVersion"这类
#                             L180-186 结构上无法发现的事故。
#     · published 阶段：直接读**已构建产物**的 Win32 版本资源（ProductVersion / FileVersion），
#                          与本次 $Version 比对。
#                          —— 这是唯一能发现"源码写对了但构建没带上"的检查，
#                             与任何源码文本检查都不共享前提。
#
# 纪律：
#   · 本文件**只读**文件系统，不写、不删、不启动进程；不得在内部 exit。
#   · 返回 [pscustomobject]@{ Ok; Problems }；调用方负责中止。
#   · PowerShell 5.1 兼容（不得使用 ?? / ?. / 三元运算符）。

function Get-GateTextEncoding([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) {
        return (New-Object Text.UTF8Encoding($true))
    }
    return (New-Object Text.UTF8Encoding($false))
}

function Read-GateText([string]$path) {
    return [IO.File]::ReadAllText($path, (Get-GateTextEncoding $path))
}

# ── source 阶段：验证"运行时真值来自程序集"这条机制还在 ──────────────────────────────
# 参数：
#   RepoRoot 仓库根（必须存在）
#   Version  本次发布版本（不带 v 前缀）
function Test-VersionSourceTruth {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Version
    )
    $problems = New-Object System.Collections.Generic.List[string]

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $problems.Add('本次发布版本为空 —— 无法做版本真值校验。')
        return [pscustomobject]@{ Ok = $false; Problems = @($problems) }
    }

    # ① 机制检查：ApplyDisplayVersion 必须存在，且必须仍在读 AssemblyInformationalVersionAttribute。
    #    纯呈现层的"运行时真值"就靠它；一旦被删/被改成读 AssemblyVersion，
    #    四个 csproj 的 <InformationalVersion> 就不再决定界面显示，L180-186 的断言随之失去意义。
    $csPath = Join-Path $RepoRoot 'src\PCMig.WinUI\MainWindow.xaml.cs'
    if (-not (Test-Path $csPath)) {
        $problems.Add('缺少 src\PCMig.WinUI\MainWindow.xaml.cs —— 无法确认运行时版本真值来源。')
    } else {
        $cs = Read-GateText $csPath
        if ($cs -notmatch 'void\s+ApplyDisplayVersion\s*\(') {
            $problems.Add('src\PCMig.WinUI\MainWindow.xaml.cs 里找不到 ApplyDisplayVersion() —— 界面版本不再由程序集决定，XAML 降级值会变成运行时真值。')
        }
        if ($cs -notmatch 'AssemblyInformationalVersionAttribute') {
            $problems.Add('src\PCMig.WinUI\MainWindow.xaml.cs 的 ApplyDisplayVersion 不再读 AssemblyInformationalVersionAttribute —— 版本真值来源已改变。')
        }
    }

    # ② WinUI csproj 的 <InformationalVersion> 必须**恰好一处**（0 处 = 没写；2 处 = 节点重复，构建取哪个不可控）。
    $projPath = Join-Path $RepoRoot 'src\PCMig.WinUI\PCMig.WinUI.csproj'
    if (-not (Test-Path $projPath)) {
        $problems.Add('缺少 src\PCMig.WinUI\PCMig.WinUI.csproj。')
    } else {
        $proj = Read-GateText $projPath
        $hits = @([regex]::Matches($proj, '<InformationalVersion>([^<]*)</InformationalVersion>') | ForEach-Object { $_.Groups[1].Value })
        if ($hits.Count -ne 1) {
            $problems.Add('PCMig.WinUI.csproj 的 <InformationalVersion> 出现 ' + $hits.Count + ' 处，应恰好 1 处。')
        } elseif ($hits[0] -ne $Version) {
            $problems.Add('PCMig.WinUI.csproj 的 <InformationalVersion> 是「' + $hits[0] + '」，应为「' + $Version + '」。')
        }
    }

    # ③ XAML 降级兜底：程序集读不到时界面回退到 XAML 字面量，因此至少要有一处 Text="v<版本>"。
    $xamlPath = Join-Path $RepoRoot 'src\PCMig.WinUI\MainWindow.xaml'
    if (-not (Test-Path $xamlPath)) {
        $problems.Add('缺少 src\PCMig.WinUI\MainWindow.xaml。')
    } else {
        $badges = @([regex]::Matches((Read-GateText $xamlPath), 'Text="v([0-9.]+)"') | ForEach-Object { $_.Groups[1].Value })
        if ($badges.Count -eq 0) {
            $problems.Add('MainWindow.xaml 里找不到任何 Text="vX.Y.Z" 降级徽章 —— 程序集读不到时界面会显示空白/旧值。')
        }
    }

    return [pscustomobject]@{ Ok = ($problems.Count -eq 0); Problems = @($problems) }
}

# ── published 阶段：读**已构建产物**的版本资源，与本次版本比对 ──────────────────────────
# 与源码文本检查不共享任何前提：即使所有 .csproj 都写对了，只要构建参数没带上
# （例如漏了 -p:Version=...），这里就会失败 —— 这正是旧断言拦不住的场景。
function Test-VersionPublishedTruth {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [string[]]$RelativePaths = @(
            'dist\winui\PCMig.WinUI.dll',
            'dist\winui\PCMig.Core.dll'
        )
    )
    $problems = New-Object System.Collections.Generic.List[string]
    $want = $Version.Trim()

    foreach ($rel in $RelativePaths) {
        $p = Join-Path $RepoRoot $rel
        if (-not (Test-Path $p)) {
            $problems.Add('缺少已构建产物 ' + $rel + ' —— 无法验证运行时版本真值。')
            continue
        }
        $vi = (Get-Item $p).VersionInfo
        # ProductVersion 来自 AssemblyInformationalVersion（可能带 "+<commit>" 后缀，界面也会剥掉它）。
        $pv = [string]$vi.ProductVersion
        if ($pv.Contains('+')) { $pv = $pv.Substring(0, $pv.IndexOf('+')) }
        $pv = $pv.Trim()
        if ($pv -ne $want) {
            $problems.Add('产物 ' + $rel + ' 的 ProductVersion 是「' + $vi.ProductVersion + '」，本次发布版本是「' + $want + '」' +
                          ' —— 源码版本写对了但构建没带上（检查 publish 是否漏了 -p:Version / -p:InformationalVersion）。')
        }
        # FileVersion 只做"非空 + 前导段一致"的弱校验：它由 <Version> 的数值部分派生（0.5.4.0 形态）。
        $fv = [string]$vi.FileVersion
        if ([string]::IsNullOrWhiteSpace($fv)) {
            $problems.Add('产物 ' + $rel + ' 没有 FileVersion 资源 —— 版本资源未被打进程序集。')
        } else {
            $numeric = ([regex]::Match($want, '^[0-9]+(\.[0-9]+)*')).Value
            if ($numeric -ne '' -and -not $fv.StartsWith($numeric)) {
                $problems.Add('产物 ' + $rel + ' 的 FileVersion 是「' + $fv + '」，与本次版本数值段「' + $numeric + '」不一致。')
            }
        }
    }

    return [pscustomobject]@{ Ok = ($problems.Count -eq 0); Problems = @($problems) }
}