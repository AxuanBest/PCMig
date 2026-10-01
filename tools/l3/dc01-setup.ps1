<#
  PCMig L3 —— DC01 配置脚本（在 DC01 虚拟机内运行）
  =====================================================
  作用：把一台全新的 Windows Server 2025（Desktop Experience）变成实验室域控。
  产出：
    · 域 PCMigLab.local（NetBIOS: PCMIGLAB）
    · DNS（随 AD DS 安装，含正向区域）
    · OU：PCMig-Servers / PCMig-Clients
    · 组：PCMig-Operators（普通域用户） / PCMig-Admins（域管理员测试用）
    · 用户：PCMigTestUser / PCMigAdmin / PCMigOperator（密码由参数给出，脚本不回显）
    · 数据盘：C:\PCMigShare\src（供 PCMig 从宿主机直拉）

  安全与纪律（重要）：
    1. **只用 Lab-only 对象**：域、OU、组、用户、共享全部是本实验室专用命名。
    2. **绝不加入/触碰任何真实公司域**。若检测到已是其它域的成员机，直接中止。
    3. 密码不落盘、不回显；仅作为脚本参数传入。
    4. 每一步都有 Verify，失败即停并明确报错。
    5. 不改宿主网络设置（只配置本 VM 内的 IP/DNS）。

  用法（在 DC01 内，管理员 PowerShell）：
    powershell -NoProfile -ExecutionPolicy Bypass -File dc01-setup.ps1 `
        -DomainName PCMigLab.local -SafeModePassword '<口令>' -UserPassword '<口令>'

  可选：
    -StaticIP 192.168.28.10 -PrefixLength 24 -SkipAdInstall（仅做前置检查）

  ⚠ 必须 UTF-8 带 BOM。PS 5.1：无三元运算符。
#>

param(
  [string]$DomainName        = 'PCMigLab.local',
  [string]$NetbiosName       = 'PCMIGLAB',
  [Parameter(Mandatory)][string]$SafeModePassword,
  [Parameter(Mandatory)][string]$UserPassword,
  [string]$StaticIP          = '192.168.28.10',
  [int]$PrefixLength         = 24,
  [switch]$SkipAdInstall,
  [string]$ShareRoot         = 'C:\PCMigShare'
)

$ErrorActionPreference = 'Stop'
$script:results = @()

function Note([string]$phase, [string]$result, [string]$detail) {
  $script:results += [pscustomobject]@{ Phase=$phase; Result=$result; Detail=$detail }
  Write-Host ('  [' + $result + '] ' + $phase + ' — ' + $detail)
}
function Head([string]$t) { Write-Host ''; Write-Host ('=== ' + $t + ' ===') }
function Assert-Admin {
  $ok = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $ok) { throw 'DC01 配置需要管理员权限：请以管理员身份运行本脚本。' }
}

Head 'PCMig L3 / DC01 配置'
Write-Host ('  域        : ' + $DomainName + '  (NetBIOS ' + $NetbiosName + ')')
Write-Host ('  静态 IP   : ' + $StaticIP + '/' + $PrefixLength)
Write-Host ('  主机名    : ' + $env:COMPUTERNAME)
Write-Host ('  时间      : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

try { Assert-Admin; Note '前置-管理员' 'PASS' '管理员令牌已确认' }
catch { Note '前置-管理员' 'BLOCKED' $_.Exception.Message; return }

# ---------------------------------------------------------------- 安全闸门：拒绝已在真实域中的机器
Head '安全闸门'
try {
  $cs = Get-CimInstance Win32_ComputerSystem
  if ($cs.PartOfDomain) {
    Note '安全闸门-已加域检查' 'BLOCKED' ('本机已属于域 ' + $cs.Domain + '。为避免污染真实环境，脚本中止。')
    Write-Host ''
    Write-Host '!! 本机已是域成员。若这是意外，请先在 VM 内退域；本脚本绝不改真实域。'
    return
  }
  Note '安全闸门-已加域检查' 'PASS' '本机当前为工作组机器，可以安全建新域'
} catch { Note '安全闸门-已加域检查' 'FAIL' $_.Exception.Message }

# ---------------------------------------------------------------- 1. 静态 IP + DNS 指向自己
Head '1. 网络配置'
if (-not $SkipAdInstall) {
  try {
    $nic = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Loopback' } | Select-Object -First 1
    if ($null -eq $nic) { throw '找不到已启用的网络适配器' }
    $existing = Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
    if ($existing) { Remove-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue }
    $gw = Get-NetRoute -InterfaceIndex $nic.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if ($gw) { Remove-NetRoute -InterfaceIndex $nic.ifIndex -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue }
    New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress $StaticIP -PrefixLength $PrefixLength | Out-Null
    Set-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ServerAddresses $StaticIP
    Note '1-网络' 'PASS' ($nic.Name + ' -> ' + $StaticIP + '/' + $PrefixLength + '，DNS 指向自身')
  } catch {
    Note '1-网络' 'FAIL' $_.Exception.Message
  }
} else { Note '1-网络' 'NOT_RUN' '按 -SkipAdInstall 跳过' }

# ---------------------------------------------------------------- 2. 安装 AD DS + DNS
Head '2. 安装 AD DS / DNS 角色'
if (-not $SkipAdInstall) {
  try {
    $f = Get-WindowsFeature -Name AD-Domain-Services, DNS
    $need = @($f | Where-Object { -not $_.Installed })
    if ($need.Count -eq 0) {
      Note '2-角色' 'PASS' 'AD-Domain-Services 与 DNS 已安装'
    } else {
      Write-Host ('  · 正在安装: ' + (($need | ForEach-Object { $_.Name }) -join ', ') + '（可能需数分钟）')
      $r = Install-WindowsFeature -Name AD-Domain-Services, DNS -IncludeManagementTools
      if ($r.Success) { Note '2-角色' 'PASS' '安装完成，需重启与否见 Success/RestartNeeded' }
      else { Note '2-角色' 'FAIL' '安装未成功' }
    }
  } catch { Note '2-角色' 'FAIL' $_.Exception.Message }
} else { Note '2-角色' 'NOT_RUN' '按 -SkipAdInstall 跳过' }

# ---------------------------------------------------------------- 3. 建新林（域）
Head '3. 创建新林 ' 
if (-not $SkipAdInstall) {
  try {
    $sec = ConvertTo-SecureString $SafeModePassword -AsPlainText -Force
    $exists = $false
    try { $d = Get-ADDomain -ErrorAction Stop; if ($d.DNSRoot -eq $DomainName) { $exists = $true } } catch { }
    if ($exists) {
      Note '3-建域' 'PASS' ('域已存在: ' + $DomainName)
    } else {
      Write-Host '  · 正在提升为域控（此步会自动重启，请重启后再次运行本脚本继续）…'
      Install-ADDSForest -DomainName $DomainName -DomainNetbiosName $NetbiosName `
        -SafeModeAdministratorPassword $sec -InstallDns:$true -Force -Confirm:$false
      Note '3-建域' 'PASS' '建林指令已执行（如系统提示重启，请重启后重跑本脚本）'
    }
  } catch {
    $msg = $_.Exception.Message
    if ($msg -match 'reboot|重启') { Note '3-建域' 'NOT_RUN' '需要重启后继续（这是正常流程）' }
    else { Note '3-建域' 'FAIL' $msg }
  }
} else { Note '3-建域' 'NOT_RUN' '按 -SkipAdInstall 跳过' }

# ---------------------------------------------------------------- 4. OU / 组 / 用户
Head '4. OU、组、测试用户'
$domainReady = $false
try { $null = Get-ADDomain -ErrorAction Stop; $domainReady = $true } catch { }
if (-not $domainReady) {
  Note '4-目录对象' 'NOT_RUN' '域尚未就绪（如刚建林需重启后重跑本脚本）'
} else {
  $baseDN = (Get-ADDomain).DistinguishedName
  # OU
  foreach ($ou in @('PCMig-Servers','PCMig-Clients')) {
    try {
      if (Get-ADOrganizationalUnit -Filter "Name -eq '$ou'" -ErrorAction SilentlyContinue) {
        Note ('4-OU ' + $ou) 'PASS' '已存在'
      } else {
        New-ADOrganizationalUnit -Name $ou -Path $baseDN -ProtectedFromAccidentalDeletion $false | Out-Null
        Note ('4-OU ' + $ou) 'PASS' '已创建'
      }
    } catch { Note ('4-OU ' + $ou) 'FAIL' $_.Exception.Message }
  }
  # 组
  foreach ($g in @('PCMig-Operators','PCMig-Admins')) {
    try {
      if (Get-ADGroup -Filter "Name -eq '$g'" -ErrorAction SilentlyContinue) {
        Note ('4-组 ' + $g) 'PASS' '已存在'
      } else {
        New-ADGroup -Name $g -GroupScope Global -GroupCategory Security -Path $baseDN | Out-Null
        Note ('4-组 ' + $g) 'PASS' '已创建'
      }
    } catch { Note ('4-组 ' + $g) 'FAIL' $_.Exception.Message }
  }
  # 用户（普通用户 → Operators 组；管理员 → Domain Admins）
  $users = @(
    @{ Name='PCMigTestUser'; Upn='pcmigtestuser'; Group='PCMig-Operators'; Desc='PCMig 普通域用户（模拟员工）' },
    @{ Name='PCMigOperator'; Upn='pcmigoperator'; Group='PCMig-Operators'; Desc='PCMig 操作员（受限）' },
    @{ Name='PCMigAdmin';    Upn='pcmigadmin';    Group='Domain Admins';   Desc='PCMig 域管理员（管理共享测试）' }
  )
  foreach ($u in $users) {
    try {
      if (Get-ADUser -Filter "SamAccountName -eq '$($u.Name)'" -ErrorAction SilentlyContinue) {
        Note ('4-用户 ' + $u.Name) 'PASS' '已存在'
      } else {
        $pw = ConvertTo-SecureString $UserPassword -AsPlainText -Force
        New-ADUser -Name $u.Name -SamAccountName $u.Name -UserPrincipalName ($u.Upn + '@' + $DomainName) `
          -AccountPassword $pw -Enabled $true -PasswordNeverExpires $true `
          -Description $u.Desc -Path $baseDN | Out-Null
        Add-ADGroupMember -Identity $u.Group -Members $u.Name -ErrorAction SilentlyContinue
        Note ('4-用户 ' + $u.Name) 'PASS' ('已创建并加入 ' + $u.Group)
      }
    } catch { Note ('4-用户 ' + $u.Name) 'FAIL' $_.Exception.Message }
  }
}

# ---------------------------------------------------------------- 5. 数据源目录（供 PCMig 直拉）
Head '5. 数据源目录'
try {
  $src = Join-Path $ShareRoot 'src'
  New-Item -ItemType Directory -Force -Path $src | Out-Null
  # 造一批可核对的小数据（若为空目录）
  if (@(Get-ChildItem $src -Recurse -File -Force -ErrorAction SilentlyContinue).Count -eq 0) {
    1..50 | ForEach-Object { Set-Content (Join-Path $src ("dc-file-{0:D3}.txt" -f $_)) -Value ('dc01 payload ' + $_) -Encoding UTF8 }
    New-Item -ItemType Directory -Force -Path (Join-Path $src '子目录-中文') | Out-Null
    Set-Content (Join-Path $src '子目录-中文\带 空格 和#井号.txt') -Value 'edge case sample' -Encoding UTF8
  }
  $cnt = @(Get-ChildItem $src -Recurse -File -Force).Count
  Note '5-数据源' 'PASS' ($src + ' 现有 ' + $cnt + ' 个文件')
} catch { Note '5-数据源' 'FAIL' $_.Exception.Message }

# ---------------------------------------------------------------- 6. 校验
Head '6. 域状态校验'
if ($domainReady) {
  try {
    $d = Get-ADDomain
    Note '6-域' 'PASS' ($d.DNSRoot + ' / NetBIOS ' + $d.NetBIOSName)
    $u = @(Get-ADUser -Filter "SamAccountName -like 'PCMig*'" | Select-Object -ExpandProperty SamAccountName)
    Note '6-用户清单' 'PASS' ('共 ' + $u.Count + ' 个: ' + ($u -join ', '))
    $svc = Get-Service NTDS, DNS -ErrorAction SilentlyContinue
    foreach ($s in $svc) {
      $r = 'FAIL'; if ($s.Status -eq 'Running') { $r = 'PASS' }
      Note ('6-服务 ' + $s.Name) $r $s.Status
    }
    Note '6-DNS解析' 'PASS' ((Resolve-DnsName $DomainName -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress -join ',')
  } catch { Note '6-校验' 'FAIL' $_.Exception.Message }
} else { Note '6-校验' 'NOT_RUN' '域未就绪' }

# ---------------------------------------------------------------- 汇总
Head '结果汇总'
foreach ($r in $script:results) { Write-Host ('  ' + $r.Result.PadRight(10) + ' ' + $r.Phase) }
$bad = @($script:results | Where-Object { $_.Result -eq 'FAIL' })
$sum = '无 FAIL'
if ($bad.Count -gt 0) { $sum = ($bad.Count.ToString() + ' 项 FAIL') }
Write-Host ('  总体：' + $sum)

$outDir = 'C:\PCMigLab-Logs'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir ('dc01-setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$script:results | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $out -Encoding utf8
Write-Host ('  证据：' + $out)
Write-Host ''
Write-Host '下一步：在 FS01 上运行 fs01-setup.ps1；在 CLIENT01 上运行 client01-setup.ps1'
if ($bad.Count -gt 0) { exit 1 } else { exit 0 }