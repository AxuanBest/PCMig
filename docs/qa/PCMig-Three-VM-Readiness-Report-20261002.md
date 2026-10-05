# PCMig Three-VM Readiness Report

- **阶段**：Three-VM Enterprise Domain Test — **Phase 0（Planning + Readiness Audit）**
- **性质**：只读规划审计。**未创建任何 VM / 未建交换机 / 未装系统 / 未建域 / 未装 PCMig / 未跑任何 Case。**
- **仓库基线**：分支 `feature/winui-v0.5.0`，HEAD `d1aefb2`（`docs(diagnostics): finalize D6.3 closure handover`），上一提交 `3708d12`，工作树 clean。
- **审计时间**：2026-10-02（Asia/Shanghai）
- **审计现场**：宿主机 `DESKTOP-5CSN7PT`（Dell Precision 7680）
- **最终状态**：**NOT READY**（阻塞项见 §20 与 §B）

---

## ① 实际 PCMig Runtime Topology（§2 九问逐条确认）

**结论：PCMig 的 UI、Copy Engine（robocopy）、Task State、Diagnostics 全部运行在「目标端/新电脑」；源端只当 SMB 服务端，不装任何 PCMig 组件。**

拓扑（`PCMig Actual Runtime Topology`，经代码核验，非文档推断）：

```
┌──────────────────────────── 源电脑（旧电脑 / LAB-SRC01） ────────────────────────────┐
│  只做 SMB 服务端，不装 PCMig                                                          │
│  D:\  → 共享名 D（真实公司行为；\\LAB-SRC01\D）                                       │
│  LanmanServer(445) + SMB2 + NetShareEnum（非管理员远端枚举通常 ACCESS_DENIED）         │
│  源盘 + 源端 AV 实时扫描                                                              │
└───────────────────────────────────▲──────────────────────────────────────────────────┘
                                    │ TCP 445 / SMB2
                                    │ （WNet 先连 \\LAB-SRC01\IPC$，再访问共享 UNC）
┌───────────────────────────────────┴───────── 目标电脑（新电脑 / LAB-DST01） ─────────┐
│  PCMig.WinUI（v0.5.0 开发线）/ PCMig.Gui / PCMig.Cli                                 │
│  → PCMig.Core: Preflight → SourceScanner → Planner → Verifier → Report               │
│  → NetworkShare(WNetAddConnection2) → 本机 SMB 客户端重定向器(mrxsmb/rdbss)            │
│  → TransferOrchestrator.RunPassAsync → RobocopyRunner                                │
│  → robocopy.exe（与 PCMig 同机、无窗口无 shell、被 Job Object 守护）                   │
│  → 直写本机 TargetRoot（无中转/暂存目录）                                              │
│  旁路落盘：                                                                           │
│    %ProgramData%\PCMig\Jobs\JOB-YYYYMMDD-HHmmss-xxxx\{job.json,plan.json,             │
│      observed-state.json,job-state.json,preflight.json,verify-report.json,           │
│      receipts\,report\,job.lock}                                                      │
│    %ProgramData%\PCMig\Jobs\...\logs\{job-*.log,.jsonl} + logs\robocopy\<ObjId>.log   │
│    %ProgramData%\PCMig\Logs\app-<date>.log(+.jsonl)                                   │
│    %LOCALAPPDATA%\PCMig\Diagnostics\<sessionId>\                                      │
│      → 导出 ZIP → 桌面\PCMig-Diagnostic\PCMig-Diagnostic-<stamp>.zip                   │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

九问逐条：

| # | 问题 | 结论 | 关键证据 |
|---|---|---|---|
| ① | PCMig UI 运行在哪一端 | **目标端（新电脑）** | `src\PCMig.Core\Models\Models.cs:65` `public string TargetRoot { get; set; } = ""; // 本地目标根，例如 D:\Migrated\OLD-PC`；`src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs:1217-1219` 用 `new DriveInfo(root).AvailableFreeSpace`（目标盘必须本机盘）；`src\PCMig.Cli\Program.cs:495-496`「只对本地盘做前置判断；UNC/网络目标不在这里猜」 |
| ② | Robocopy 运行在哪一端 | **与 PCMig 同机 = 目标端** | 全仓唯一启动点 `src\PCMig.Core\Transfer\RobocopyRunner.cs:465` `new ProcessStartInfo("robocopy.exe", args)`；`src\PCMig.Core\Preflight\PreflightChecker.cs:414` 检查 `%SystemRoot%\System32\robocopy.exe`；`RobocopyRunner.cs:503` `ProcessJobGuard.Assign(proc, _log)` |
| ③ | 拉取还是推送 | **目标端拉取（pull）**：源 = UNC，目标 = 本机路径 | `src\PCMig.Core\Transfer\TransferOrchestrator.cs:631` `await _runner.RunPassAsync(obj.SourcePath, obj.TargetPath, ...)`；`src\PCMig.Core\Planning\Planner.cs:59-65` 目标由 `job.TargetRoot + relative` 组合；`RobocopyRunner.cs:267` 参数顺序 `QuoteArg(src) + ' ' + QuoteArg(dst)` |
| ④ | 真实数据路径 | **DST01（PCMig UI + Task State + robocopy）→ SMB `\\LAB-SRC01\D` → 本地写入 DST01 目标盘**。与任务书假设**一致** | 同上；`src\PCMig.Core\Native\NetworkShare.cs:175` `var ipc = $@"\\{host}\IPC$";`、`:191-192` `WNetAddConnection2(...)`、`:455` `UncPath = $@"\\{host}\{item.shi1_netname}"` |
| ⑤ | Diagnostics 在哪 | **目标端本机** `%LOCALAPPDATA%\PCMig\Diagnostics\<sessionId>\` | `src\PCMig.Diagnostics\DiagnosticSessionStore.cs:146-147 DefaultRoot`（注释「当前用户权限，不要求管理员」）；装配 `src\PCMig.WinUI\Diagnostics\DiagnosticBootstrap.cs:43` 未设 StorageRoot → 走默认根 |
| ⑥ | Task State 在哪 | **目标端本机** `%ProgramData%\PCMig\Jobs\<JobId>\job-state.json` | `src\PCMig.Core\Jobs\JobManager.cs:247-249`；`:252` `NewJobId()` = `JOB-{yyyyMMdd}-{HHmmss}-{guid4}` |
| ⑦ | Resume State 在哪 | **同一 job 目录**：`job-state.json` + `receipts\{ObjectId}-{yyyyMMddHHmmss}.json` + `observed-state.json` + `plan.json` | `JobManager.cs:93-98`、`:302-311 FindUnfinished`、`:295-296 ResumablePhases`；大文件文件内续传靠 robocopy `/Z`（`RobocopyRunner.cs:300-301`） |
| ⑧ | 日志在哪 | 应用级 `%ProgramData%\PCMig\Logs\app-<date>.log/.jsonl`；任务级 `<JobDir>\logs\job-<date>.log/.jsonl`；robocopy 原始日志 `<JobDir>\logs\robocopy\{ObjectId}.log`；UI 实时日志 = 内存环 500 行，**不落盘** | `src\PCMig.Core\Logging\LogBootstrap.cs:19-22`、`:33-45`、`:64-76`；`TransferOrchestrator.cs:477` |
| ⑨ | 哪些故障真正位于数据路径上 | 见下方清单 | 见下 |

**位于数据路径上（注入有效）**：
1. PCMig 主进程（robocopy 的 stdout/stderr 经**重定向管道**被它读；它死 ⇒ robocopy 被 Job Object 连带杀）
2. `robocopy.exe`（目标端）
3. 目标端 SMB 客户端重定向器 + WNet 会话（凭据/冲突发生地）
4. TCP 445 链路
5. 源端 LanmanServer + 源盘
6. 目标盘卷/文件系统（空间/配额/只读/长路径/AV 过滤驱动）
7. 源端 AV 实时扫描
8. `ProcessJobGuard` 作业对象

**不在数据路径上（注入无效或低价值，禁止用来"证明测到了 PCMig"）**：诊断运行时、UI/渲染/PMML、matrix YAML 加载、plan/report/state 的 JSON 写、Changelog/浏览器打开、预检测速（默认关闭）、共享枚举本身。

**副作用提醒（写进证据记录）**：stdout/stderr 是重定向管道，"管道阻塞/UI 卡死"表现为**进度停滞**而不是 robocopy 失败（转发见 `TransferOrchestrator.cs:199-200`，独立停滞判定 `:871`）。

**不确定项（如实登记）**：代码中**没有**"禁止在源机运行 PCMig"的守卫。只要目标盘在本机，PCMig 就能跑；"装在新电脑"是部署约定，不是代码强制。

---

## ② 实际 SMB Data Path

```
robocopy.exe（DST01）
  → 目标路径：DST01 本地盘（如 D:\Migrated\LAB-SRC01）      ← 目标侧
  → 源路径：\\LAB-SRC01\D\<相对路径>                        ← 源侧（UNC）
       ↑ 会话由 WNetAddConnection2 对 \\LAB-SRC01\IPC$ 建立（凭据在此注入/复用）
       ↑ 共享 UNC 来自：NetShareEnum 枚举结果，或用户「添加共享」手输
  → TCP 445 → LAB-SRC01 LanmanServer → SRC01 的 D:\ 物理盘
```

- **无中转/暂存目录**：`PCMig.Core` 全仓无 `Path.GetTempPath` / `GetTempFileName`。
- **源端唯一写入**：仅当用户开启"连接测速"时，在源共享根写 `.pcmig-bench-{guid}.tmp` 32 MB，`finally` 删除（`PreflightChecker.cs:277`、`:331`）。⚠ 文案/注释写「64MB」是**错的**，实现为 `:282 const int fileMb = 32;`。
- **共享发现（附加 A）**：PCMig **没有网络电脑浏览**（无 `NetServerEnum`/`NetView`、无 SMB1 浏览器服务、无 SSDP/广播）——用户必须手输 IP/电脑名或粘 `\\IP\共享名`。
  - 枚举：`PreflightChecker.cs:161 report.Shares = NetworkShare.EnumShares(host);` → `NetworkShare.cs:366-401`（先 `TryEnumLevel2`，失败回退 `EnumLevel1`）；过滤 `type != STYPE_DISKTREE`；`IsAdminShare = name.EndsWith('$')`。
  - **兜底**：`Shares.Count == 0` 时才硬探测 `C$ D$ E$ F$`（`PreflightChecker.cs:194-210`，标 `Remark = "（直接探测发现）"`）。
  - 放行闸门：`PreflightChecker.cs:212-228 ok = Directory.Exists(src);`
- **「添加共享」输入转换（附加 B）**：`ConnectionViewModel.cs:210-289`。
  - `:217 var name = ExtractShareName(ManualShareName);` → `:220 var unc = $@"\\{host}\{name}"` → `:242 Directory.Exists(unc)` 判可达。
  - `ExtractShareName`（`:278-289`）接受 `d` / `D` / `D$` / `Users` / `\\host\D`；
  - **`D:` 与 `D:\` 不会被换算**（冒号保留 ⇒ `\\host\D:` ⇒ 必然失败）；全仓无盘符→共享名映射；无大小写归一、无字符集校验、无共享存在性预校验。

---

## ③ UI / Robocopy / Task State / Diagnostics 所在位置（单一清单）

| 组件 | 位置 | 运行账户/权限 | 证据 |
|---|---|---|---|
| PCMig UI（WinUI v0.5.0 开发线 / 旧 WPF v0.4.9） | **LAB-DST01 本机** | 登录的域用户（须为 `CORP\user01`） | `Models.cs:65`、`MigrationSessionViewModel.cs:1217-1219` |
| robocopy.exe | **LAB-DST01 本机** | 同上（继承 PCMig 令牌） | `RobocopyRunner.cs:465` |
| Task State / Resume State / 回执 / 计划 | **LAB-DST01**：`%ProgramData%\PCMig\Jobs\<JobId>\` | 该用户对 `%ProgramData%` 的写权限（**需实测**） | `JobManager.cs:247-252` |
| 任务/应用日志 | **LAB-DST01**：`%ProgramData%\PCMig\Logs\`、`<JobDir>\logs\` | 同上 | `LogBootstrap.cs:19-22` |
| robocopy 原始日志 | **LAB-DST01**：`<JobDir>\logs\robocopy\{ObjectId}.log` | 同上 | `TransferOrchestrator.cs:477` |
| Diagnostics | **LAB-DST01**：`%LOCALAPPDATA%\PCMig\Diagnostics\<sessionId>\` | 当前用户（**刻意不要求管理员**） | `DiagnosticSessionStore.cs:146-147` |
| Diagnostics 导出包 | 桌面 `PCMig-Diagnostic\*.zip` | 当前用户 | `src\PCMig.WinUI\MainWindow.xaml.cs:601-613` |
| 源端（LAB-SRC01） | **不装任何 PCMig 组件**；只有 LanmanServer + 共享 + 数据 | — | 拓扑核验 |

**潜在故障点（Phase 1 必须实测）**：Task State 选 `%ProgramData%`（通常需要管理员才能创建子目录，而代码**没有**对应处理），Diagnostics 却刻意选 `%LOCALAPPDATA%` 并注明「不要求管理员」。**两者口径不一致** —— 若普通域用户写 `%ProgramData%\PCMig\Jobs` 被拒，任务状态落盘会失败（状态写失败与数据写失败必须分开取证，见 F11）。

---

## ④ 宿主机资源（实测）

| 项 | 实测值 |
|---|---|
| 机型 | **Dell Inc. Precision 7680**（BIOS 1.30.0，SN BSK4CY3） |
| 主机名 / 域 | `DESKTOP-5CSN7PT` / FQDN = **WORKGROUP**（宿主未入域） |
| CPU | **13th Gen Intel Core i9-13950HX，24 核 / 32 逻辑处理器 @2200 MHz**，1 路 |
| 内存 | **34,104,659,968 B ≈ 31.76 GB**（1×32 GB 5600） |
| 物理盘 | 仅 **disk0：PC801 NVMe SK hynix 2 TB SSD**（MediaType SSD，NVMe） |
| 卷 | **C: 400.00 GB（剩 215.10）／D: 735.00 GB（剩 651.40）／E: 771.60 GB（剩 717.70）**，均 NTFS |
| 时区 | China Standard Time |
| 虚拟化已接管 | `HypervisorPresent=True`；`Win32_Processor.VirtualizationFirmwareEnabled=False` / `SLAT=False` **是 Hyper-V 已接管的显示假象，不是硬件缺陷** |
| 身份 | `DESKTOP-5CSN7PT\User`，`IsInRole(Administrator)=False`（**未提权**），但属于本机 `Administrators` 组与 **`BUILTIN\Hyper-V Administrators` (`S-1-5-32-578`)** |
| UAC | `EnableLUA=1`、`ConsentPromptBehaviorAdmin=5`、`PromptOnSecureDesktop=1`；**本会话审批被禁用 ⇒ 无法自提权** |
| 宿主网络 | `以太网 192.168.1.212/24`、`WLAN 192.168.1.60/24`、`以太网 2 10.0.252.67/32`、`vEthernet (Default Switch) 172.31.160.1/20`、蓝牙 169.254.x |
| 宿主 SMB 服务端 | SMB1 关闭、SMB2 开启、`RequireSecuritySignature=True`、`Smb2DialectMax=65535`；共享 `ADMIN$ C$ D$ E$ F$ G$ IPC$ Users`（**F$/G$ 是历史残留**，当前无 F:/G: 卷） |
| 未取到的数据 | **NTFS 簇大小**：`fsutil fsinfo ntfsinfo` 需提权（`Error 5: Access is denied`），本会话未提权 ⇒ **簇大小未知**，影响 §17 的空间精算（按 4 KiB 假设规划，Phase 1 用提权会话复核） |

---

## ⑤ 虚拟化平台（实测）

**Hyper-V = 当前唯一可用的虚拟化平台；VMware Workstation 当前未安装。**

| 项 | 实测 |
|---|---|
| Hyper-V 服务 | `vmms`(Automatic) / `vmcompute` / `HvHost` 全部 **Running** |
| Hyper-V PowerShell 模块 | 版本 **2.0.0.0**，可用 |
| Hyper-V Manager | `%WINDIR%\System32\virtmgmt.msc` 存在 |
| 在册 VM | **0 台** |
| 交换机 | **只有 `Default Switch`**（`SwitchType=Internal`、`AllowManagementOS=True`、无物理网卡、`IovEnabled=False`）；**`PCMigLab-Sw` 不存在** |
| NAT/上网 | **ICS 服务 `SharedAccess` = Running** ⇒ Default Switch 自带 NAT；`Get-NetNat` 为空（未配 NetNat） |
| VM 默认存储路径 | `VirtualMachinePath = C:\ProgramData\Microsoft\Windows\Hyper-V`；`VirtualHardDiskPath = C:\ProgramData\Microsoft\Windows\Virtual Hard Disks` ⚠ **指向 C:（仅剩 215 GB）** |
| 读操作免提权 | ✅ `Get-VM` / `Get-VMHost` 未提权成功（`Hyper-V Administrators` 组成员身份） |
| 写操作免提权 | ⚠ **未实测**（`New-VM`/`New-VMSwitch`/`Add-VMDvdDrive`/`Remove-VM`）——Phase 0 禁止创建 VM，故不作断言 |
| `Get-WindowsOptionalFeature` | 需提权（`请求的操作需要提升`）⇒ **无法用该 cmdlet 确认 Hyper-V 功能开关状态**（但服务在跑、cmdlet 可用，功能实际已启用） |
| VMware | **未安装**：注册表 Uninstall 无 VMware 项、无 `vmware*`/`vmnat`/`vmnetdhcp` 进程、无 `vmrun`、`C:\ProgramData\VMware` 不存在 |

**平台建议（供人工拍板，见 §19）**：优先 **Hyper-V**（现成可用、零安装风险、既有 L3 脚本与 hyperv-lab skill 都按 Hyper-V 写；历史文档的结论同样是 Hyper-V 胜出）。VMware 安装介质虽已备，但来源为第三方重打包站（sysin.org），且两版并存、当前未安装 —— 若要求用 VMware，必须先解决来源合规与版本选择。

---

## ⑥ 三 VM 最终配置（依据实测资源提出）

| VM | vCPU | 内存 | OS 盘 | 数据盘 | 交换机 | 规划 IP | 角色 |
|---|---|---|---|---|---|---|---|
| `LAB-DC01` | 2 | 4 GB（动态 2–4，关闭动态内存亦可） | 64 GB 动态 VHDX | — | `PCMigLab-Sw`(Internal) | 10.77.0.10 | AD DS + DNS + 测试域控 |
| `LAB-SRC01` | 4 | 6 GB | 80 GB 动态 | **120 GB 数据盘**（`D:`，承载源数据） | `PCMigLab-Sw` | 10.77.0.21 | 旧电脑/SMB 源（共享 `D`） |
| `LAB-DST01` | 4 | 6 GB（210 万路线建议 8 GB） | 80 GB 动态 | **160 GB 目标盘** + **16 GB 小容量故障盘** | `PCMigLab-Sw` | 10.77.0.22 | 新电脑/PCMig UI 实操端 |
| 合计 | 10 vCPU | 16 GB（峰值 18 GB） | — | — | — | — | 宿主 24C/32T、31.76 GB 内存可容纳；宿主自身保留约 14–16 GB |

统一规格：**Generation 2**、动态 VHDX、`Set-VMFirmware -EnableSecureBoot Off`（与既有 L3 脚本一致；Server 2025 / Win11 均可关闭 SecureBoot 引导）、无检查点套娃。

**VM 与磁盘存放位置（必须人工确认后改）**：
- 建议 `LabRoot = E:\PCMigLab`（E: 剩 717.70 GB，最充裕），目录布局沿用 hyperv-lab skill：`VMs` / `ISO` / `snapshots` / `logs` / `admin-queue` / `admin-results`。
- ⚠ **Hyper-V 宿主默认路径现在指向 C:**（剩 215 GB），建 VM 时必须显式指定 `-Path E:\PCMigLab\VMs`，或先用提权会话改 `Set-VMHost -VirtualMachinePath / -VirtualHardDiskPath`。
- ⚠ **环境变量冲突（必须先解决）**：当前 `PCMIG_LAB_ROOT = D:\PCMig-旧物归档\PCMigLab`（User 作用域；该目录存在且 `ISO`/`VMs`/`snapshots`/`logs`/`admin-queue`/`admin-results` **全为空**），而 `C:\Users\User\.dsh\skills\hyperv-lab\SKILL.md:64` 明确「环境变量会覆盖默认值」，SKILL.md:76 的默认是 `E:\PCMigLab`。**按现状直接跑，VM 会落进"旧物归档"目录。** Phase 1 第一件事 = 改这个变量或每次显式 `--labroot`。
- host 侧 E: 空间验算（210 万路线）：3×OS 实际占用约 25–30 GB/台 ≈ 85 GB；SRC 数据 ≈ 55 GB；DST 目标 ≈ 55 GB；合计实际写入约 **200 GB**（动态盘按实际增长），远小于 E: 的 717.70 GB。**建议数据盘用固定大小 VHDX**（避免 210 万文件期间动态扩展抖动），固定预留 120+160 = 280 GB，仍在安全范围。

---

## ⑦ 网络方案（只规划，不创建）

| 项 | 方案 |
|---|---|
| 网段 | **10.77.0.0/24**，仅实验内部 |
| 网关 | 无（不需要 Internet 即可完成本阶段全部目标） |
| DC01 | 10.77.0.10（同时是 DNS） |
| SRC01 | 10.77.0.21 |
| DST01 | 10.77.0.22 |
| 域成员首选 DNS | **10.77.0.10**（**严禁**用公共 DNS 作域成员首选 DNS；严禁混入 8.8.8.8/114.114.114.114） |
| 交换机 | 新建 **Internal** 交换机 `PCMigLab-Sw`（复用 hyperv-lab skill 与既有 L3 脚本的既定名）；**禁止 Bridge 到真实公司 LAN**，**禁止 External/物理网卡绑定** |
| DHCP | **不用**。三台全部静态 IP（Internal 交换机不会自带 DHCP；ICS 只服务 Default Switch） |
| Internet（可选） | 若确需 Windows Update：**另建**一张 NAT 出口（Host 侧 ICS 或 `New-NetNat -InternalIPInterfaceAddressPrefix 10.77.0.0/24`），或在 build 阶段临时挂 Default Switch，装完即摘。**Phase 0 不创建任何网络** |
| 域名解析验证点 | 域成员上 `nslookup corp.test`、`nslookup LAB-SRC01.corp.test` 必须由 10.77.0.10 应答；短名 `\\LAB-SRC01\D` 与 FQDN `\\LAB-SRC01.corp.test\D` **两种都要单独测**（历史 L3-04 场景） |

**与真实公司的隔离红线（写进 Case 前提）**：不桥接、不改宿主 DNS/路由/防火墙、不把测试 VM 加入任何真实域、不让真实公司账号进测试域。

---

## ⑧ 域方案

| 项 | 值 |
|---|---|
| 域名（DNS） | `corp.test` |
| NetBIOS | `CORP` |
| 域控 | `LAB-DC01`（Windows Server + AD DS + DNS），单 DC，第一阶段**不做**第二 DC / DFS / 文件服务器集群 / VPN / Entra / 跨域信任 |
| 林/域功能级别 | 随所选 Server 版本默认（Phase 1 记录实际值） |
| DNS | 域控承载 `corp.test` 正向区域 + 动态更新（成员机注册 A 记录）；**不使用转发器**（无 Internet 需求） |
| OU 结构（最小） | `CORP\Lab\Users`、`CORP\Lab\Computers`（+ 可选 `CORP\Lab\Servers`） |
| 组 | `CORP\Lab-BusinessUsers`（user01/user02 同组）、`CORP\Lab-Admins`（admin） |
| GPO | 第一阶段只做**最小测试 GPO**（例：一条可开关的"禁用管理共享/关闭 SMB1/要求 SMB 签名"实验策略），且必须记录 GPO 链接状态与 `gpresult`，不得一次性引入多个策略变量 |
| 计算机对象 | `LAB-SRC01`、`LAB-DST01` 入域（默认 Computer 容器或 `CORP\Lab\Computers`） |
| 免测项（明确不做） | 域信任、跨域、只读域控、ADFS、证书服务、Entra 混合加入 |

⚠ **命名提醒**：`corp.test` 使用保留 TLD `.test`，适合隔离实验；**不要**改成公司真实域名（避免 DNS 后缀重合导致的解析污染）。

---

## ⑨ 账号方案

| 账号 | 类型 | 权限基线 | 用途 | 硬约束 |
|---|---|---|---|---|
| `CORP\user01` | 普通域用户 | 与 user02 **完全相同的有效业务权限** | Persona A：正常普通员工迁移 | 不得加入任何管理员组 |
| `CORP\user02` | 普通域用户 | **同上（默认完全一致）** | Persona B：傻瓜/重复/错误操作 | 权限差异**只能**在专门权限 Case 里通过专门目录/ACL/Share Permission 注入；不得用"换账号"制造权限差异 |
| `CORP\admin` | 域管理员 | 域管理/权限恢复/实验域维护 | Persona C：管理员路径 | **"admin 能成功"≠"普通员工测试通过"**，两者必须分开出 Verdict |
| （基础设施）本机 `Administrator` | 本地管理员 | 仅用于建域/装机/DC 维护 | 不入业务 Case | 不得在业务 Case 里用它跑 PCMig |
| （禁止）共享凭据 | — | — | — | 不允许把 admin 凭据预置成 user01 的 SMB 会话；不允许 `LocalAccountTokenFilterPolicy` 之类的"为了测试方便"的放宽 |

**关键纪律**：Phase 0 结束前**不创建任何账号**；登录基线必须走真实域交互登录（不用缓存凭据假装登录）。

---

## ⑩ 正常权限基线（硬 Gate：GATE-BASELINE 的先决条件）

**必须在任何故障测试之前成立**：`CORP\user01` 在 `LAB-DST01` 上登录后：

| # | 必须成立 | 验证方式（Case 开始前，允许用后台工具） |
|---|---|---|
| 1 | 能读 `\\LAB-SRC01\D`（根目录可枚举） | `CORP\user01` 会话内 `dir \\LAB-SRC01\D` 成功 |
| 2 | 能读 `\\LAB-SRC01\D\Desktop\|Documents\|Downloads\|Pictures\|Work` | 逐目录列目录 + 读取 3 个样本文件 |
| 3 | 能在目标测试目录**创建/覆盖/删除**文件 | 在 `D:\Migrated\LAB-SRC01\`（暂存）写入并删除测试文件成功 |
| 4 | 目标盘剩余空间 ≥ 源数据量 1.3 倍 | `Get-Volume` 记录 |
| 5 | `CORP\user02` 具备**完全相同**的上述 4 条 | 同一套命令跑一次，逐条比对 |
| 6 | 权限基线快照已拍 | Hyper-V checkpoint `TEST-BASELINE` |

**绝不能设计成"源能读、目标不能写"**——那会让 F01 基线失败，之后所有故障 Case 的归因都会污染。

Share / NTFS 建议基线（`LAB-SRC01`）：
- 共享：`D:\` → Share Name **`D`**（普通共享，非隐藏）；Share Permission：`CORP\Lab-BusinessUsers` = Change（**不是** Full）。
- NTFS：`D:\` 继承权限中 `CORP\Lab-BusinessUsers` = Modify（**不发 Deny**）。
- 管理共享 `D$` 保持系统默认（**不删、不改**，它是公司真实环境的组成部分，也是 F04/F06 的对照项）。
- 专门的"拒绝"目录（如 `D:\Work\denied`）只在 F04 里按 Case 需要现场创建 ACL，**基线里不得预先存在**。

---

## ⑪ D 共享测试方案（对应 §6）

**公司真实行为**：`D:\` 已共享为 `D`，但 PCMig 连接后自动发现区**可能不出现** `D`；员工流程 = 连接 → 发现没有 → 点「添加共享」→ 输入 `D` → 得到 `\\ComputerName\D`。

**禁止**：用 `D$` 隐藏共享顶替、造假共享、改产品配置、底层强行注入 UNC。**必须**真实创建普通共享 `D`。

两个独立 Case：

| Case | 目标 | 做法 | 允许的结论用词 |
|---|---|---|---|
| `CORP-DISC-01` 正常发现基线 | 记录 `D` 是否**自动出现** | 真实创建普通共享 `D`（Share Permission 给 `Lab-BusinessUsers`）；在 DST01 用 `CORP\user01` 全新连接（Cold Case，先清 SMB 会话与凭据）；**如实记录**自动发现区实际列出了什么 | 出现 ⇒ `D Auto-Discovered: YES`；不出现 ⇒ `D Auto-Discovered: NO`。**两者都是有效结果，不得人为隐藏或人为制造** |
| `CORP-DISC-02` 手动添加 `D` | 验证「添加共享→输入 `D`」功能 | 同环境下点「添加共享」，输入 `D`、`d`、`\\LAB-SRC01\D` 三种形态，各自记录 | 仅当实验室**真实复现**了"`D` 不自动显示"的条件时才可写 `Company Discovery Behavior Reproduced`；否则**只能**写 `Manual Add Share Function Verified` |

**Phase 0 的代码层分析（只分析，未改环境）——「为什么公司域下 D 不自动显示」的强候选机制**：

1. **最可能**：非管理员账号对远端 `NetShareEnum` 通常返回 `ACCESS_DENIED` ⇒ `EnumLevel1` 抛 IOException ⇒ `PreflightChecker.cs:161-171` 只记 Warning ⇒ `report.Shares.Count == 0` ⇒ 只跑 `:194-210` 硬编码的 `C$/D$/E$/F$` 探测，而**普通共享名 `D`（无 `$`）不在该列表** ⇒ 自动发现区**永不出现 `D`**。
2. 同盘合并吞条：`:370-383` 只显示被 pick 的 Name，被合并的别名只进 `Aliases`，UI 不显示。
3. 类型过滤：非 `STYPE_DISKTREE` 一律不出现。
4. 已有另一套凭据的 SMB 会话时**复用旧连接**（`NetworkShare.cs:194-210`）——例如资源管理器开着 `\\旧电脑` 窗口，枚举用的是旧账号权限。
5. 名称形态差异：枚举原样返回 `shi1_netname`，`D` 与 `D:` 完全不同物。
6. 与 SMB1 / 浏览器服务 / 网络发现**无关**；核心限制是**没有对方机器的管理员权限**。
7. 已排除：UI 无筛选、空态只与 `Shares.Count` 联动，不会隐藏已枚举到的共享。

---

## ⑫ 是否已找到「公司 D 不自动显示」的可复现条件

**结论：`NOT YET REPRODUCED`。**

- 现状：已有**强候选机制**（上节第 1 条）+ 可证伪的判据，但**尚未在实验室真实复现**（Phase 0 不建环境、不建域、不建共享）。
- Phase 1 的**首个验证点**：在 `CORP-DISC-01` 里用 `CORP\user01`（非管理员域用户）连接 `LAB-SRC01`，记录自动发现区实际内容。预期（**待证**）：**不出现 `D`**，多半只出现 `C$/D$/E$`（且这些管理共享对普通用户通常不可访问，可能连探测都失败 ⇒ 发现区为空）。
- 判定纪律：只有**真实复现**（真实域、真实普通域用户、真实普通共享 `D`、真实 `NetShareEnum` 失败）之后，才允许写 `Company Discovery Behavior Reproduced`。
- 记录要求：无论结果如何，都要保存 PCMig 侧证据（诊断事件里的共享枚举计数事件，`NetworkShare.cs:388-399` 只发计数）+ 独立证据（`net share` 在 SRC01 上的真实输出、SRC01 安全日志里的登录/拒绝记录）。

---

## ⑬ 产品契约（§23 十问，按**当前实现**报告）

| # | 问题 | 当前实现的事实 | 证据 |
|---|---|---|---|
| 1 | 目标已有文件时 Overwrite/Skip/Compare？ | **完全交给 robocopy 默认增量语义**：仅当「文件名+大小+时间戳」**全同**才判相同并跳过，其余一律重新复制覆盖。PCMig **不加** `/IS /IT /XO /XN /XC /XX`，无内容比对决策 | `RobocopyRunner.cs:267-320`（参数白名单）、`:283-286` `sb.Append(" /COPY:DAT /DCOPY:T /XJ");` + 注释「/IS /IT 无济于事（实测字节行"复制=0"），唯一可靠办法是先删掉目标那份」 |
| 2 | 目标独有文件会不会删？ | **永不删除**。全 `src` grep `/MIR\|/PURGE\|Mirror` **0 命中**；唯一删除行为是修复专用的 `RepairPurge` | `src\PCMig.Core\Transfer\RepairPurge.cs:47-63`、`:69-133` |
| 3 | Resume 准确含义？ | **重跑同一 job 的未完成对象**（robocopy 增量对已一致文件秒级跳过），**不是恢复 robocopy 进程**。仅"完全完成"的对象跳过；Failed / CompletedWithErrors / Interrupted 一律重跑。必须同 host + 同 targetRoot；Step4 绝不自动恢复 | `MigrationSessionViewModel.cs:590-594`、`:1274-1294`；`JobManager.cs:295-296`；`TransferOrchestrator.cs:225-232`、`:269-276` |
| 4 | Verify 查什么？ | **L1 = 双侧文件数 + 双侧字节数（默认）**；可选 **L2 = 确定性抽样 SHA-256 双向比对**（默认 1% 抽样，≤2000 个）。**不查时间戳/属性/ACL，不做全量哈希** | `src\PCMig.Core\Verify\Verifier.cs:109-127`、`:146-186`；`Models.cs:275` `Status => CountMatch && BytesMatch && HashMismatched == 0 ? "OK" : "MISMATCH"`；`Models.cs:95-96` 默认 `VerifyLevel=L1_CountSize`、`SampleHashPercent=1` |
| 5 | Repair 能修什么？ | **重跑失败/不一致对象**；**强制覆盖开启时**先 `RepairPurge` 删目标同名文件再重拷。**能修"内容不同但大小/时间相同"的文件，但必须开强制覆盖** | `MigrationSessionViewModel.cs:1678-1719`、`:1765-1789`；`Step4ResultPage.xaml.cs:251` → `MigrationSessionViewModel.cs:1431` → `TransferOrchestrator.cs:495` |
| 6 | Stop 是立即还是请求？ | **两者都有**：Cooperative = 请求（对象边界检查，`pause.request` 文件，1 s 轮询，**不杀进程**）；Immediate = **立即 `Process.Kill(entireProcessTree: true)`**，退出码记 **-1** | `MigrationSessionViewModel.cs:1334-1360`；`RobocopyRunner.cs:563-566`、`:617-635`、`:607-612`；`TransferOrchestrator.cs:707-746`、`:748-766` |
| 7 | PCMig 关闭后 robocopy 是否继续？ | **不会**。`KILL_ON_JOB_CLOSE` 作业对象守护 ⇒ PCMig 进程消失时 OS 连带终止 robocopy。**边界：Job 创建/Assign 失败只告警、传输继续（静默降级为无守护）** | `src\PCMig.Core\Native\ProcessJobGuard.cs:13-14`、`:57`、`:87-96`；挂接点 `RobocopyRunner.cs:503` |
| 8 | 任务状态什么时候落盘？ | **对象边界**（不是每文件、也不是只在结束时）：每对象一条 Receipt + 一次 job-state；一律 `tmp → File.Move(overwrite: true)` 原子替换（8 次 40 ms 退避 ≈1.12 s + 1 次 30 s 冷却）。**边界：状态写失败只告警 + `WriteSkipped`，不中断任务**（「Receipt 才是权威」） | `JobManager.cs:93-98`、`:266`；`TransferOrchestrator.cs:235-241`、`:278-283`、`:290-293`、`:321-345`、`:400`；`src\PCMig.Core\State\JsonStateStore.cs:10-13`、`:100-101`、`:121`、`:135-162` |
| 9 | 「成功」的决定性判据？ | **robocopy 退出码 0..7 且未被我们杀掉**（`>= 8` 才是失败位）。对象级 Completed / CompletedWithErrors / Failed；任务级有失败对象 ⇒ `CompletedWithErrors`。**成功不要求 Verify 通过** | `RobocopyRunner.cs:73` `IsSuccess(int exitCode) => exitCode >= 0 && exitCode < 8;`；`TransferOrchestrator.cs:544`、`:566`、`:573`、`:392-396`；`ReportGenerator.cs:98` |
| 10 | 失败/跳过怎么表达？ | **对象粒度 Receipt + Step4 失败清单 + 可选 HTML 报告**；**无逐文件清单、无迁移 ZIP**。⚠ `ObjectStatus.Skipped` 在代码里**从不被赋值**（"跳过"是展示层遗留标签）；robocopy 的正常增量跳过**不产生任何记录**；**逐文件权威记录只有 `logs\robocopy\{ObjectId}.log`** | `Models.cs:199-217 ObjectReceipt`；`RobocopyRunner.cs:349`（`MaxErrorLines=500`，不落盘）、`:360-396 SummarizeFailures`；`MigrationSessionViewModel.cs:2557-2565`、`:2985-2996`；`ReportGenerator.cs:188-190` |

**可直接当作三 VM 真机测试"预期"的六条硬判据**：
1. 目标存在同名同大小同时间文件 ⇒ **不覆盖、不报错**；要覆盖必须走 Step4「尝试修复」并勾「强制覆盖」。
2. 目标端多余文件**永不删**。
3. 杀掉 `PCMig.exe` ⇒ `robocopy.exe` 随之消失，**无孤儿**、不占目标文件。
4. Verify 默认只对账文件数与字节数 ⇒ 要发现"同大小同时间但内容损坏"**必须**用 L2（默认 1% 抽样哈希）或独立 HASH 域外证据。
5. 退出码 0..7 = 成功，**且不要求验证通过**（"迁移完成" ≠ "数据已核对一致"）。
6. 失败以**对象**为单位、以中文字符串落在 Receipt，并在 Step4 清单与 HTML 报告中呈现。

**文档与代码冲突（必须记住，以代码为准）**：`TransferOrchestrator.cs:212-213` 注释写「true = 本次运行对每个对象追加 robocopy /IS /IT」——**与代码不符**；`PreflightChecker.cs:120` 注释与 UI 文案写测速「64MB」——实现是 **32 MB**（`:282`）。

---

## ⑭ F01–F15 正式 Case Matrix（Phase 0 只设计，不执行）

通用前提（每 Case 隐含，不重复写）：环境 = `TEST-BASELINE`；Persona 按列指定；故障注入前后记录 **T1/T2/T3**；证据落宿主侧 `E:\Project\deepseek work\archive\threevm-lab\<CaseID>\`；每 Case 出 **Data / Product / Evidence / Human-UI** 四个 Verdict + Case Verdict；**故障未真正生效而 PCMig 正常运行 ⇒ `INVALID / INCONCLUSIVE`**。

| Case | 前置条件 | UI 操作 | 故障注入 | 预期业务行为（代码契约） | 预期 UI 行为 | 证据 | 恢复方式 |
|---|---|---|---|---|---|---|---|
| **F01** 正常基线 | GATE-ENV/BASELINE 通过；SRC01 共享 `D` 就绪；数据 = ROUTE A Smoke 集 | 连接电脑 → 选共享/目录 → 选模式 → Start → 等完成 → Verify → Export | 无 | 全部对象 Completed；退出码 0..7 | 进度递增、完成态明确、Verify 可点、结论无歧义 | 全量 §18 字段 + 独立 HASH 全量对照 | 无需恢复 |
| **F02** D Share 手动添加 | SRC01 已建普通共享 `D`（非隐藏） | 连接 → 记录自动发现区 → Add Share → 输 `D`/`d`/`\\LAB-SRC01\D` → 选数据 → Start 完成 | 无（**禁止**制造隐藏） | 三种输入都应得到 `\\LAB-SRC01\D` 且可达 | 「添加共享」状态文案正确；重复添加不重复条目 | 发现区实际内容截图 + `net share` 输出 + 诊断枚举计数事件 | 无需恢复 |
| **F03** 傻瓜输入/重复操作 | 同 F01（Persona B） | 依次：错误电脑名 → 不存在共享 → `D:` → `D:\` → 完整 UNC → 空白 → 前后空格 → 重复 Connect → 重复 Start → 处理中 Back → Stop 后立即 Resume → 关闭错误弹窗 → 连续 Retry | 无（输入即故障） | 不崩溃、不假成功、**不猜错路径后继续**；`D:`/`D:\` 应判不可达（`\\host\D:`） | 每组输入都有可读错误 + 下一步建议；按钮状态与阶段一致；无重复弹窗风暴 | UI 录屏 + 全量弹窗文本 + 日志 | 回 `TEST-BASELINE` 快照 |
| **F04** Share/NTFS 权限 | 基线权限就绪 | 正常组授权 → 迁移；然后按子案注入后重跑 | (a) 源 Share 拒绝 (b) 源 NTFS 局部拒绝 (c) 目标创建允许但**替换**拒绝 (d) 子目录显式 Deny | 拒绝项落对象 Receipt（ErrorClass/ErrorDetail），其余继续；退出码 ≥8 ⇒ CompletedWithErrors | 失败清单显示人话 + 可否修复的指引 | PCMig 日志 + robocopy 原始日志 + 独立 ACL 快照（`icacls` 前后）+ Event Log | 逐条还原 ACL/Share（**不整体回滚 DC**） |
| **F05** 身份/凭据 | 无既有 SMB 会话（Cold） | 正常凭据 → 错凭据 → 再正常 → 另一身份连接同服务器 | (a) 同服务器已有另一身份会话 (b) 错误凭据 (c) 过期凭据 (d) 重新认证 | **不得把凭据冲突误报成"共享不存在"**；错误须可区分 | 错误文案须能区分"凭据/权限"与"共享不存在" | 诊断事件 + `net use` + SRC01 安全日志（4625/4776） | `net use /delete` 清会话 + 重建 |
| **F06** 名称解析/共享连接 | Cold | 用短名连接 → 用 FQDN 连接 → 不再存在共享名 → 445 无响应场景 | (a) 解析失败（改 hosts/DNS 记录） (b) 名字失败但 IP 可达（证明是解析问题） (c) 共享不存在 (d) 445 无响应/防火墙拦 (e) SMB Server 服务停 | PCMig **无需**凭有限证据断言是 DNS 还是防火墙，但必须准确表达"当前不能连接"+ 用户怎么办 | 错误分层：第一层人话（发生了什么+怎么办），第二层错误码/SMB/HRESULT | 独立 `nslookup`/`Test-NetConnection`/`Get-SmbServerConfiguration` + UI 截图 | 还原 DNS/防火墙/服务 |
| **F07** 迁移中网络中断 | 迁移进行中（≥30% 且非全部完成） | Start → 中途拔网 → 等 → 复位 → Resume → Verify | 断**实际数据路径**网络（SRC01 或 DST01 的实验网卡）；须测 **T2 已生效**（RST/超时）；另加"等待连接超时时点 Stop"子案 | Stop 后已拷部分保留、可 Resume；Resume 只重跑未完成对象 | 中断要可见、不能长时间无反馈、不能显示"正在迁移"骗人 | 宿主侧时间线 + robocopy 日志 + 诊断 + Resume 回执 | 复位网络；必要时回快照 |
| **F08** 机器硬中断 | **两个独立子案，禁止合并** | Case A：远端 SRC01 硬关；Case B：PCMig 执行机 DST01 硬关 | **VM 硬 Turn Off（等价拔电）**，**不得**用正常 Shutdown 替代 | Case A：源消失 ⇒ 对象失败/中断，目标已拷数据保留；Case B：DST01 上的 robocopy 随 Job Object 终止，**无孤儿** | 重开 UI 必须如实显示 Interrupted/Unknown 并可 Resume，**不得假成功** | 宿主侧 T1/T2/T3 + 进程快照（robocopy 是否存在）+ 目标文件数/字节 + 状态文件 | 开机 → 检查 DNS/域信任 → Resume |
| **F09** PCMig 自身异常结束 | 迁移进行中 | Start → 结束 PCMig 进程 → 观察 robocopy → 重开 UI → 看任务状态 → Resume → Verify | 强杀 `PCMig.exe`（任务管理器/`Stop-Process`） | robocopy 被连带终止（Job Object）；job-state 停在最后对象边界；重开 UI 列出未完成任务 | 重开后可辨识"中断的任务"，Resume 语义明确 | 杀进程前后 `Get-Process robocopy` 快照 + job-state.json + receipts 计数 | 重新 Start/Resume |
| **F10** DC 不可达 | 分别构造三种会话状态 | 已有 SMB 会话下迁移；新 SMB 会话；触发新认证 | 停 DC01 / 断 DC 网卡 | **必须区分**：已有会话可能不受影响；新会话可能需要 DC；不能一律断言"必然失败" | 错误表达不能过度归因 | SRM/安全日志 + `nltest /sc_query` + 独立会话枚举（`net use`） | 恢复 DC → 验证 DNS/SYSVOL/NETLOGON/信任 |
| **F11** 磁盘/状态存储 | 三个子案 | Start → 触发条件 → 观察 | A：开始前目标空间不足；B：迁移中被吃满（`fsutil file createnew` 填盘）；C：数据盘可写但**状态/日志盘**不可写 | 必须区分 **数据写失败 / 状态保存失败 / 证据保存失败**（B ⇒ CompletedWithErrors；C ⇒ 传输继续但状态 WriteSkipped） | 提示要指明"哪一类空间/权限问题" | `Get-Volume` 前后 + job-state 时间戳序列 + 日志告警 | 删填充文件；还原目录权限 |
| **F12** 文件实时变化 | 迁移中 | Start → 在源端制造变化 | (a) 文件被独占锁 (b) 迁移中删除 (c) 迁移中重命名 (d) 迁移中持续写入 | 锁文件按 `/R:2 /W:5` 重试后跳过并如实报告；持续写入文件的最终一致性**按预先定义的契约**判定 | 失败/跳过如实呈现；不得声称某时间点完美一致 | robocopy 日志 + 独立 HASH（变化文件单列）+ 锁持有者进程 | 解锁；重跑 |
| **F13** 目标已有数据保护 | 目标预置数据 | Start → 观察覆盖行为 → Verify → Repair | (a) 同名同内容 (b) 同名不同内容 (c) 目标独有文件 (d) 同名文件/目录冲突；**保留**"共享名仍为 `D` 但后台重指向另一目录"子案 | 默认增量：同大小同时间不覆盖；(c) 永不删；(d) 冲突须有明确结局；共享重指后 **检查 PCMig 是否需要重新确认/阻止危险恢复** | 危险场景要有重新确认或阻断提示，不能静默继续 | 目标侧独立 HASH 全量 + 目录树 diff + 共享物理路径前后记录 | 还原目标预置数据 |
| **F14** Verify/Repair | 已有一次完成的迁移 | Verify → 看结果 → Repair → 再次 Verify | 人为制造：缺失文件、错误内容、**内容变化但 Size/Time 相同的受控样本**（**禁止**只改大小或时间戳当作损坏） | L1 可发现计数/字节差异；**"同大小同时间坏内容"必须靠 L2 抽样或独立 HASH**；Repair 开强制覆盖时先 purge 再重拷 | Verify/Repair 结论明确；MISMATCH 项可定位 | 独立 HASH 双向全量 + `verify-report.json` + RepairPurge 日志 | 回 `TEST-BASELINE` 数据快照 |
| **F15** 文件边界集 | 固定小数据集 | Start → 完成 → Verify → Repair | 无（数据本身就是边界） | `/XJ` 不递归 ReparsePoint、不进入目录环；中文/Emoji/0 byte/Hidden/ReadOnly/长路径/深目录/大量小文件/大文件全部按契约处理 | 边界项在清单里有明确归宿（成功/失败/跳过） | robocopy 日志逐项 + 独立 HASH + `fsutil`/`dir /a` 属性对照 | 回快照 |

**F15 固定数据集建议组成**（全部真实文件，非占位）：中文名（简/繁）、Emoji 名、0 byte、Hidden、System、ReadOnly、>260 字符长路径、40 层深目录、5,000 个 512 B 小文件、1 个 600 MB 大文件（走 `/Z` 通道）、1 个 Junction、1 个符号链接、1 个含环的目录结构。

---

## ⑮ Snapshot / Recovery Plan

三层基线（**不做六层套娃**）：

| 层 | 内容 | 何时拍 | 恢复用法 |
|---|---|---|---|
| `BASE-OS` | 干净 OS、已装好、未入域、未装 PCMig | 每台 VM 装完立刻 | 极端损坏时重建 |
| `DOMAIN-BASELINE` | AD DS/DNS 就绪 + SRC01/DST01 入域 + 账号/组/OU 完成 | 域验证通过后 | Secure Channel/机器账户/DNS 严重污染时 |
| `TEST-BASELINE` | PCMig 已部署 + 标准数据 + 标准权限 + 测试前清场（清 SMB 会话、清 job 目录、清诊断会话） | 每次正式 Case 开始前 | **普通故障的首选恢复点** |

- 工具：`C:\Users\User\.dsh\skills\hyperv-lab\scripts\hyperlab.mjs checkpoint create|restore|remove --name <vm>`（兜底）或 `mcp__hyperv-mcp__hyperv_checkpoint_*`（首选，带 `include_subtree` 语义）。
- **恢复纪律**：
  1. 普通故障（文件/权限/共享/网络/服务）**只用局部恢复**，不要回滚 DC、不要三台一起回滚。
  2. **禁止无计划单独回滚 DC**；涉及机器账户/信任/DNS 严重污染时才用域级恢复或直接重建实验域。
  3. **不要**默认"三台用同名 Snapshot 一起恢复 = AD 健康"。
  4. 每次恢复后**必查**：DNS（`nslookup corp.test`）、AD DS（`Get-ADDomain`）、SYSVOL/NETLOGON（`\\LAB-DC01\SYSVOL`）、成员机信任（`nltest /sc_verify:corp.test`）、普通用户登录、`\\LAB-SRC01\D` 可访问。
  5. **证据文件一律存宿主侧**（`E:\Project\deepseek work\archive\threevm-lab\`），**绝不**只留在 guest 内 —— 否则 Rollback 会连证据一起抹掉。
  6. `clone` 前先拍检查点（运行中复制 VHDX 只得崩溃一致性镜像）。

---

## ⑯ Evidence Template（每个正式 Case 一份）

```yaml
CaseID:            CORP-<FAMILY>-<NN>
RunID:             <yyyyMMdd-HHmmss>-<seq>
Persona:           CORP\user01 | CORP\user02 | CORP\admin
PCMigBuild:        分支/HEAD + 二进制 SHA256（WinUI 或 Gui，写实际用的那个）
WindowsBuild:      SRC01 / DST01 各自的 10.0.xxxxx
EnvBaselineID:     BASE-OS | DOMAIN-BASELINE | TEST-BASELINE + 快照名/时间
Source:            \\LAB-SRC01\D  (物理路径 + Share Name)
Target:            D:\<TargetRoot>  (盘/卷/剩余空间)
UserInput:         用户逐字输入（含空格、大小写、`D:` 之类原样）
UITimeline:        每条 UI 操作 + 时间（宿主侧时钟为权威；guest 时钟单列）
T1_InjectionIssued:  注入动作发出时间
T2_InjectionEffective: 环境故障确认生效时间 + 生效判据
T3_ProductObserved:  PCMig 真正感知时间 + 感知判据
Screenshots:       宿主机侧截图/录屏路径（含弹窗全文）
PCMigLogs:         %ProgramData%\PCMig\Logs\app-*.log + <JobDir>\logs\job-*.log
Diagnostics:       %LOCALAPPDATA%\PCMig\Diagnostics\<sessionId>\ 原样拷贝
DiagnosticsExport: 桌面\PCMig-Diagnostic\*.zip
RobocopyCommand:   robocopy 实际完整命令行
RobocopyExitCode:  进程退出码（区分 kill 的 -1）
WindowsEventLog:  SRC01/DST01/DC01 相关段（安全/系统/SMB Client+Server）
DNS_SMB_IdentityEvidence: 按 Case 需要（nslookup / net use / nltest / whoami /klist）
FinalFileCount:    源 / 目标
FinalByteCount:    源 / 目标
IndependentHash:   独立工具全量或按契约抽样的 HASH 对照结果
FailedFiles:       列表（对象粒度 + 逐文件来自 robocopy 日志）
SkippedFiles:      列表（同上；注意 PCMig 不单独记录"跳过"）
ExtraFiles:        目标侧多出来的文件
RecoveryAction:    实际做了什么恢复
ResumeResult:      Resume 后完成情况
VerifyResult:      OverallPass / Status / MissingSamples / HashMismatched
RepairResult:      修了什么、是否开强制覆盖、修后复验
Verdicts:
  DataVerdict:     PASS | FAIL | PARTIAL
  ProductVerdict:  PASS | FAIL | MISLEADING
  EvidenceVerdict: COMPLETE | PARTIAL | MISSING
  HumanUIVerdict:  PASS | FAIL
CaseVerdict:       PASS | FAIL | INVALID/INCONCLUSIVE
```

**四 Verdict 不可合并**：A 数据是否符合迁移契约；B PCMig 是否**正确表达**产品状态；C Diagnostics 是否**真实记录**（Complete/Partial/Missing）；D 普通用户能否理解"发生了什么、现在怎么办"。

**UI 错误提示的 10 项检查**（任何错误都要过一遍）：是否卡死 / 是否长时间无反馈 / 是否错误地继续显示"正在迁移" / 是否重复弹多个错 / 普通员工能否理解 / 有没有告诉下一步怎么办 / 能否安全重试 / 错误恢复后 UI 状态是否正确 / 按钮是否符合当前状态 / 是否出现假成功。
**只显示 `ERROR_BAD_NET_NAME`、`System Error 67`、`HRESULT 0x80070005`、`UnauthorizedAccessException` 这类技术串时，即使技术判断正确，Human/UI 也应判 FAIL。**

---

## ⑰ 210 万文件生成与空间计划（Phase 0 只规划，**禁止现在生成**）

### 现状（重要）
- **仓库里没有任何能产生百万级文件的脚本。** `lab\vm-gen-massdata.ps1` 上限 **50,000 个 512 B 小文件（≈25.6 MB）** + 40 层深路径 + 一个持锁 `.pst`；`lab\fill-e.ps1` 只到 ≈8.4 GB + 填盘。
- 历史最大实测规模仅 **50,001 文件 / 14 s / 3,571 文件/秒 / 100 MB**（`docs\qa\history\Private-Test-Lab-Final-Report.md:106`）。
- `lab\vm-gen-massdata.ps1` 还有**依赖缺陷**：第 3 段用的 `E:\迁移全量测试\被锁定目录\锁定文件.pst` 由 `fill-e.ps1` 创建，且用 `FileMode.Open` ⇒ **单独跑必抛异常**；顺序必须 `fill-e.ps1` → `vm-gen-massdata.ps1`。两个脚本都写死 `E:`。
- ⇒ **210 万文件生成器必须新写**（Phase 1 工作量），且必须在 **guest 内**运行、写 guest 的 `D:`，不得在宿主 E: 上跑。

### 目标组成（真实验收，不是模拟计数）

| 段 | 数量 | 大小 | 说明 |
|---|---|---|---|
| A 海量小文件 | **2,000,000** | 512 B ~ 4 KB（混合，**不是全 0 byte**） | 散布在多层目录（建议 2,000 个目录 × 1,000 文件，避免单目录 200 万） |
| B 中等文件 | 90,000 | 平均 256 KB（4 KB ~ 1 MB 区间） | 测 `/MAX`/`/MIN` 分流 |
| C 办公文档 | 8,000 | 平均 40 KB | 含中文名 + docx/xlsx 各半 |
| D 较大文件 | 200 | 平均 50 MB | 走 Bulk/Large 边界 |
| E 大文件 | 20 | 平均 500 MB | **走 `/Z` 可续传通道**（>512 MB 阈值附近两侧都要有样本，故 E 段跨 512 MB 阈值） |
| 合计 | **≈2,098,220 个文件** | **≈ 47 GB 数据** | 加上 512 MB 段后总量仍在 55 GB 内 |

### 空间精算（按 4 KiB 簇假设，簇大小待提权复核）

| 项 | 估算 |
|---|---|
| A 段数据 | 2.1M × 平均 2 KB = 4.0 GB，**占盘 2.1M × 4 KiB ≈ 8.0 GB** |
| B 段 | 90,000 × 256 KB ≈ 23 GB |
| C 段 | 0.3 GB |
| D 段 | 10 GB |
| E 段 | 10 GB |
| NTFS 元数据 | MFT 文件记录 ≈ 2.1M × 1 KB ≈ 2.1 GB + 索引/目录项 ≈ 1 GB |
| **SRC01 `D:` 需要** | **≈ 55 GB（建议盘 120 GB）** |
| **DST01 目标盘需要** | **≈ 55 GB（建议盘 160 GB，留出 Repair/重复复制余量）** |
| 宿主 E: 实际新增 | ≈ 55（SRC）+ 55（DST）+ 3×OS ≈ **200 GB**（E: 余 717.70 GB，安全） |

### 生成器设计约束（Phase 1 实现时必须满足）
1. **在 guest 内跑**，写 `D:\`，参数化（禁写死盘符）。
2. **幂等 + 可断点续跑**：以"目标文件数已达成本段配额即跳过"为准，而不是覆盖重写；提供 `-Verify` 模式只清点不写入。
3. **必须产出清单**：每段实际文件数 + 总字节数 + 抽样 HASH（写 `D:\_manifest\` 并拷回宿主），作为独立证据基线。
4. **不得全 0 byte**（否则测不到真实枚举/读取/哈希开销）。
5. **命名多样**：中文、Emoji、长路径、0 byte、Hidden、ReadOnly 各留少量受控样本（与 F15 共享）。
6. 生成耗时**必须实测记录**（预估 20–60 分钟，取决于簇/MFT 增长），不得用估计值当结论。
7. 生成后必须**重新拍 `TEST-BASELINE` 快照**，否则回滚一次就要重造。

### Route B 执行前置（GATE-ENV…GATE-UI 全部通过才进入 210 万）
| Gate | 判据 |
|---|---|
| `GATE-ENV` | 三 VM 稳定、快照恢复已实测有效、宿主机余量充足 |
| `GATE-BASELINE` | F01 普通用户完整成功迁移通过 |
| `GATE-DATA` | 无静默损坏 / 无错误目标写入 / 无未授权覆盖或删除 |
| `GATE-RECOVERY` | Stop / 异常退出 / Resume / Verify / Repair 全部收敛 |
| `GATE-IDENTITY` | 正常普通用户身份成立，不依赖遗留 admin 会话 |
| `GATE-EVIDENCE` | Diagnostics 与独立证据链成立（含导出包 7/7 校验类检查） |
| `GATE-UI` | 无假成功 / 无无限加载 / 无无反馈死等 / 可退出；Normal 与 Heavy 资源行为稳定 |

### Route B 必须验证的 18 步（正式执行时逐条留证）
① 完全通过 **UI** 开启超大数据模式 → ② UI 发现/选择/枚举/启动 → ③ 首个有效 UI 反馈时间 → ④ 峰值内存 → ⑤ 完整迁移 → ⑥ 成功/失败/跳过数量 → ⑦ 枚举期间 Stop → ⑧ 迁移期间 Stop → ⑨ 异常结束 → ⑩ 重启 UI → ⑪ Resume → ⑫ 完整 Verify → ⑬ 人为损坏少量指定文件 → ⑭ 再次 Verify → ⑮ Repair → ⑯ 独立 HASH → ⑰ Diagnostics UI → ⑱ Diagnostics Export。

### ⚠ Route B 的重大前置发现：「超大数据模式」在当前 WinUI 开发线**未接线**
| 事实 | 证据 |
|---|---|
| 产品名 = **「超大数据模式」**（别名「专家模式」）；旧 WPF 由 `ExpertMode` 承载 | `src\PCMig.Gui\MainWindow.xaml:1149-1150`、`:1187-1188`；`MainViewModel.cs:243` |
| **WinUI 侧开关被显式禁用**：`IsEnabled="False"`，提示「超大数据模式：暂未启用，当前迁移范围以目录选择为准。」 | `src\PCMig.WinUI\Views\Step2SelectDataPage.xaml:290-296` |
| 语义决议：`public bool ExpertModeIsWiredInPhaseA => false;` + 注释「`ExpertMode` **恒为 false**」 | `src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs:387-397` |
| 目录树侧明确禁止引导用户去开它（"会让人去找一个点不动的开关"） | `src\PCMig.WinUI\Presentation\DirectoryTreeViewModel.cs:322-323` |
| 该缺口已被**官方登记**为「A12 未接线项 / 已知缺口」 | `docs\方案-20260928-业务接线与可信度修复.md:337-356`（用户决议 = 方案 B）；`docs\archive\阶段A-交付说明-20260929.md:79` |

⇒ **§21 第①步「完全通过 UI 开启超大数据模式」在 v0.5.0 WinUI 开发线上当前无法执行**（旧 WPF `PCMig.Gui` 有该开关）。这**不是 Phase 0 能自行修改的范围**（本轮禁止改产品代码）——**必须由人工决定**：Route B 用旧 WPF 客户端执行、还是先在 v0.5.0 接线、还是把 Route B 推后。

**同时也说清：「无上限」不是这个开关给的，而是全局设计。** 真正要验证的是这套自动降级机制：

| 机制 | 阈值 | 位置 |
|---|---|---|
| 自动开启超大数据模式 | 共享已用 **≥ 2048 GB（2 TB）** | `src\PCMig.Core\Matrix\MigrationMatrix.cs:41-42`；`MainViewModel.cs:483-502`（零枚举，`GetDiskFreeSpaceEx`） |
| 单目录"建议开大数据模式"提示 | **> 20,000** 文件 | `MainViewModel.cs:1958-1962`（一次性） |
| 单目录**显示**上限 | **5,000**（显示截断，**不是**迁移上限） | `MainViewModel.cs:1943-1957` |
| 对象校验清单上限（超限自动切流式核对） | **2,000,000**/对象 | `src\PCMig.Core\Verify\Verifier.cs:25`、`:136-138` |
| 大文件分流阈值 | **512 MB** | `MigrationMatrix.cs:39` |
| `/MT` 线程档位 | 8/16/32/64/128，默认 **16**，钳制 1–128 | `Program.cs:802-803` |
| 进度轮询三级策略（平时零枚举；回退间隔自适应 = 上次耗时×3，8 s~120 s；只允许 Bulk 回退） | Large 12 s / Bulk 30 s 无文件行事件 | `TransferOrchestrator.cs:768-808` |
| 2 TB 自动检测**失败时静默不阻断** | — | `MainViewModel.cs:505` |

**Route B 期间的资源上限（用于判"有没有失控"）**：robocopy 错误行 **500**（`RobocopyRunner.cs:349`）；UI 日志窗口 **200** 行；失败清单容量 **200** + 去重索引 **50,000**（超出走"诚实丢弃计数…另有 N 条"）；诊断单事件 **64 KiB**、事件段 **16 MiB**、总配额 **512 MiB**、保留 **7 天**、FlightRecorder 内存环 **32 MiB / 65,536 条**、触发后窗口 **15,000 ms**、同时冻结窗口 **2**。

---

## ⑱ Route A / Route B 执行顺序

```
Phase 1  环境搭建
  1.1 拍板 LabRoot + 改 PCMIG_LAB_ROOT（或每次 --labroot）
  1.2 补 Windows Server 介质（阻塞项 B1）+ 确认各 ISO 的 install 索引（需提权）
  1.3 BASE-OS：三台装机（离线灌盘路线，见 §附录 C）
  1.4 建 Internal 交换机 + 静态 IP（10.77.0.0/24）
  1.5 DOMAIN-BASELINE：DC01 建域 → SRC01/DST01 入域 → 账号/组/OU
  1.6 PCMig 部署进 DST01（净新增工作量）
  1.7 TEST-BASELINE：标准数据（ROUTE A Smoke）+ 标准权限 + 清场
Phase 2  ROUTE A（故障正确性）  —— 每 Case 走「局部恢复 → 复检 → 下一 Case」
  A1 F01 正常基线（GATE-BASELINE）
  A2 F02 D 共享（CORP-DISC-01 自动发现实测 = §12 首要验证点）
  A3 F03 傻瓜输入
  A4 F15 文件边界
  A5 F04 权限 / F05 身份 / F06 名称解析
  A6 F07 网络中断 / F10 DC 不可达
  A7 F08 硬中断（两子案）/ F09 异常结束 / F14 接近完成时中断
  A8 F11 磁盘与状态存储 / F12 实时变化 / F13 目标已有数据
  A9 F14 Verify/Repair
Phase 3  GATE 评估 → 生成 210 万数据 → ROUTE B（18 步）
Phase 4  汇总报告（每 Case 四 Verdict）
```

顺序理由：先建立"能成功"的基线（否则一切失败都无法归因）；再测输入/边界这类**低破坏、高复用**项；然后才是权限/身份/网络这些会引入状态污染的项；硬中断与磁盘满放在**快照恢复已实测有效之后**；Verify/Repair 需要"已完成的迁移"做前提，放最后；Route B 在所有 Gate 通过后才开始。

---

## ⑲ 仍需人工决定的问题

| # | 问题 | 选项 | 影响 |
|---|---|---|---|
| D1 | **Windows Server 介质从哪来** | (a) 人工提供 Server 2025 ISO；(b) 人工提供微软评估中心 Server 2025 **VHD**（历史结论推荐：免安装、免按键）；(c) 其他 | **阻塞 LAB-DC01**，直接决定能否开工 |
| D2 | **虚拟化平台** | (a) Hyper-V（现成可用，**建议**）；(b) 安装 VMware 26H1/26H1u1（第三方重打包，需合规确认） | 决定 §附录 C 的建机路线与全部脚本 |
| D3 | **LabRoot 最终值** | (a) `E:\PCMigLab`（SKILL.md 默认，空间最充裕）；(b) 保留 `D:\PCMig-旧物归档\PCMigLab` | 决定 VM/证据落盘位置；含改 `PCMIG_LAB_ROOT` 环境变量 |
| D4 | **三 VM 的 Windows 版本基线** | 建议：DC01 = Server 2025 Standard(桌面体验)；SRC01/DST01 = **同一张** Win11 24H2 商业版（26100）；可选兼容基线 = Win10 22H2 商业版（19045）**留待第二轮** | 决定"环境变量可控"是否可以成立 |
| D5 | **Route B 的客户端** | (a) 用旧 WPF `PCMig.Gui`（有 `ExpertMode` 开关）；(b) 先在 v0.5.0 接线再测（属**产品改动**，须另开授权）；(c) Route B 推后 | 「超大数据模式」第①步现在无法在 WinUI 上执行 |
| D6 | **PCMig 部署形态** | (a) 用现有 `bin\Release` 构建产物（存在两棵重复构建树，需指定其中一棵）；(b) 正式走 `tools\release.ps1` 出包（会写日志+打包，属发版动作，需授权） | 决定 DST01 上跑的二进制与 SHA256 记录 |
| D7 | **是否允许一次性管理员令牌** | 建交换机 / 改 Hyper-V 默认路径 / 确认 ISO 索引 / 可能的新建 VM 都需要提权 | 本会话审批为 never，**无法自提权**；没有它 Phase 1 无法开工 |
| D8 | **`lab-admin-*` SYSTEM 执行器是否引入** | 建议**不引入**（历史已被 ReadKey 挂死；且实测免提权即可读 VM）。若引入，必须先把 `lab-admin-setup.ps1:26` 的 `I:\deepseek work\...` 改成权威路径 | 宿主持久提权面，属安全变更 |
| D9 | **210 万数据是否接受"新写生成器"** | 现成脚本只到 5 万小文件 | 决定 Phase 3 的工作量 |
| D10 | **证据根目录确认** | 建议 `E:\Project\deepseek work\archive\threevm-lab\`（在工作区权威证据根下、在仓库外、不随 VM 回滚消失） | 决定 §16 全部证据落点 |

---

## ⑳ 最终状态

# **`NOT READY`**

**阻塞项（不解决则无法开始环境建设）**：

| # | 阻塞项 | 事实 | 解除方式 |
|---|---|---|---|
| **B1** | **没有任何 Windows Server 安装介质** | 全盘（C:/D:/E:，含递归）搜索 `*.iso/*.vhd/*.vhdhx/*.vmdk/*.wim/*.esd`：只有 4 个**客户端** ISO + `C:\Aomei\AomeiBoot.wim` + `ampe.iso`；`D:\PCMig-旧物归档\PCMigLab\ISO` **为空**；`E:\PCMigLab` 不存在 | 人工提供 Server 2025 ISO 或评估中心 VHD（Phase 0 禁止下载） |
| **B2** | **虚拟化平台未拍板** | Hyper-V 可用；VMware 未安装但安装介质已备（第三方重打包，两版并存） | 用户决定 D2 |
| **B3** | **`PCMIG_LAB_ROOT` 冲突** | 现值为 `D:\PCMig-旧物归档\PCMigLab`，与 hyperv-lab skill 默认 `E:\PCMigLab` 冲突；照现状跑会把 VM 建进"旧物归档" | 用户决定 D3，然后改环境变量或每次 `--labroot` |
| **B4** | **无可用的 210 万文件生成器** | 现成脚本上限 50,000 小文件 ≈25.6 MB；且 `vm-gen-massdata.ps1` 锁文件段单跑必抛异常 | 接受 D9，Phase 1 新写 |
| **B5** | **ISO 的 install 索引无法在无提权下确认** | `Get-WindowsImage`/`dism` 与 `fsutil`（簇大小）都需管理员；本会话审批为 never | 需一次性管理员令牌（D7），或改在 Phase 1 装机步骤里用提权会话确认 |
| **B6** | **Hyper-V 写操作免提权未验证** | 读操作（`Get-VM`/`Get-VMHost`）免提权成功；`New-VM`/`New-VMSwitch`/`Add-VMDvdDrive` 未实测（Phase 0 禁止创建） | Phase 1 首次尝试即知；失败则需提权会话（D7） |
| **B7** | **Route B 第①步当前不可执行** | v0.5.0 WinUI 的「超大数据模式」开关被显式禁用（`IsEnabled=False`、`ExpertModeIsWiredInPhaseA => false`），属已登记的"已知缺口" | 用户决定 D5（本轮禁止改产品代码） |

**非阻塞但必须记录**：
- 宿主 NTFS 簇大小未知（`fsutil` 需提权）—— 影响 §17 空间精算的精度，不改变 120/160 GB 的盘容量结论。
- `Windows.iso` 经鉴定是 **Windows 10 客户端（19041 分支）ESD 介质**，**不是** Windows Server（详见 §B）。
- 历史 L3 资产已全部不可复用：`G:\`/`I:\`/`J:\` 盘不存在；既有 VM 资产（`G:\HyperV\PCMig-OldPC`、`E:\Documents\Virtual Machines\`）与已校验的 Server 2025 Eval ISO 均已不在盘上。
- 历史遗留：`CLIENT01` 曾出现 **PowerShell Direct 始终不可达（原因未查明）** ⇒ 新实验室的 `LAB-DST01` 必须**提前做一次 PD 可达性验证**，不能假定 PD 一定通。

**已就绪、可直接进入 Phase 1 的部分**：产品契约已查清（§13）；运行拓扑与数据路径已确认（§①②）；宿主机资源与虚拟化平台已确认（§④⑤）；网络/域/账号/权限基线方案已定（§⑦⑧⑨⑩）；F01–F15 Case Matrix 已设计（§⑭）；快照/证据/时间线方案已定（§⑮⑯⑲）；客户机 ISO 已选定且哈希可核（§C）；`hyperv-lab` skill 与 6 个可直接复用的 L3 脚本已定位（§附录 C）。

---

# Installation Media Readiness

## A. VMware

| 字段 | 值 |
|---|---|
| Current Installed Version | **NOT INSTALLED**（注册表无 VMware 卸载项；无 `vmware*`/`vmnat`/`vmnetdhcp` 进程；无 `vmrun`；`C:\ProgramData\VMware` 不存在） |
| Prepared Installer Version | **两个版本并存**：`26H1 (25388281)` 与 `26H1u1 (25688693)`；Windows exe 与 Linux bundle 两种形态 |
| Installer Path | `E:\系统ISO和Vm安装包\VM\VMware Workstation 26H1\` |
| SHA256 | 26H1 exe = `a0ef9087607d9cad20b08139e73e41242e044ad5bd8cee141d3bad314586737f`（287,670,872 B）<br>26H1 bundle = `3f6d2501e654dbc7701a8290ff6ffcfba6c5444cd5f35f4933cd08c9499f6d84`（340,821,664 B）<br>26H1u1 Windows exe = `3d775c3c2153600eef4642f95d519a514ba7e861400bda2598352bff792db473`（280,609,368 B）<br>26H1u1 Linux bundle = `da823c853cc7e57be7b9b070c8aed20fe9d75fd519ae6f175ab1dafc7283002e`（366,828,204 B）<br>**4 个随附 `.sha256` 全部自校验 MATCH**（说明文件未损坏，但**不证明来源官方**） |
| 附带 | `-CHS-Lang-M.zip` 2,072,942 B、`-CHS-Lang-Z.zip` 2,071,553 B、`-25688693-CHS-Lang-Z.7z` 1,590,061 B、`_Powered_by_sysin.org.url` 108 B |
| Usable for Three-VM Lab | **NEEDS REVIEW** —— ① 来源为第三方重打包站 sysin.org（含中文语言包、Linux bundle），非 Broadcom 官方渠道；② 两版并存未定；③ 本机 Hyper-V 已可直接使用。**Phase 0 不安装、不升级、不卸载** |

## B. Domain Controller ISO

| 字段 | 值 |
|---|---|
| Selected ISO | **NONE FOUND — 阻塞项 B1** |
| Path | —（`E:\系统ISO和Vm安装包\ISO\` 内 4 个 ISO 已逐一鉴定，**无一个是 Server**；`D:\PCMig-旧物归档\PCMigLab\ISO` 为空；全盘无 `*SERVER*` 命名的镜像） |
| Windows Version / Edition / Build / Language / Architecture | 不可提供 |
| Suitable for LAB-DC01 | **NO** |

**同节附上用户要求重点确认的 `Windows.iso` 的身份鉴定（结论：不是 Server）**：

| 字段 | 值 |
|---|---|
| 文件 | `E:\系统ISO和Vm安装包\ISO\Windows.iso` |
| 大小 / mtime | **4,983,554,048 B（4.64 GB）** / 2026-10-02 14:50:55 |
| SHA256 | `2189232877cd06b4e090b016817b00da48062babb119fc8a79f0924d56e55946` |
| 卷标 | **`ESD-ISO`** |
| 安装映像 | **只有 `sources\install.esd` 3,988.6 MB（4,182,397,390 B）**，`WIM版本=3584`、**映像数=4**；**无 `install.wim`** |
| setup 栈版本 | `setup.exe` **10.0.19041.1**；sources 内 DLL/EXE = 10.0.19041.1 / **10.0.19041.3685** |
| 其他结构 | 根含 `boot efi sources support autorun.inf bootmgr bootmgr.efi setup.exe`；`boot\bootfix.bin` 存在；`sources\product.ini`（通用 PID/key 表，**不能用于判定 edition**）；`sources\lang.ini` 仅 zh-cn（Fallback en-us）；**无 `EI.CFG`**；含 `sxs/uup/vista/xp/zh-cn/dlmanifests/etwproviders/migration` 等目录 |
| **鉴定结论** | **Windows 10 客户端镜像（19041 分支），绝对不是 Windows Server** |
| 判据 | ① **任何** Windows Server 版本的 setup 都不在 19041 分支（Server 2019=17763 / 2022=20348 / 2025=26100）；② 服务端介质用 `install.wim`，**ESD 形态**是客户端/媒体创建工具介质的典型特征；③ 卷标 `ESD-ISO` 是 ESD 转 ISO 的标记 |
| 未能确认 | **edition 名无法在无提权下枚举**（`Get-WindowsImage`/`dism` 需管理员；ESD 内 XML 资源为压缩态，手工按 WIM 头解析失败）。按 4 映像推断为消费级多版本（Home/Pro 系），**待提权确认** |
| 建议 | **不作为 DC 介质**；可留作备用的 Win10 客户端兼容性基线（edition 待确认） |

## C. Client ISO

| 字段 | 主选（建议） |
|---|---|
| Selected ISO | `zh-cn_windows_11_business_editions_version_24h2_updated_oct_2025_x64_dvd_a30b900a.iso` |
| Path | `E:\系统ISO和Vm安装包\ISO\` |
| Windows Version | **Windows 11 24H2**（商业版，2025-10 更新；联网佐证对应内部版本 **26100.6899**） |
| Edition | 商业版多版本，`install.wim` **映像数 = 5**；**专业版索引待提权确认**（`confirm-iso-images.ps1` 一句即可，Phase 1 必做） |
| Build | 26100（`setup.exe` 10.0.26100.1；`mediasetupuimgr.dll`/`setupplatform.dll` = **10.0.26100.6713**） |
| Language / Architecture | zh-CN / x64 |
| 大小 / SHA256 | 7,725,697,024 B（7.20 GB） / `52e6a63e9c38f60a3e4f25605915fb00ddce8cd88c45b02d27f586ac3765e519` |
| 其他 | 卷标 `CPBA_X64FRE_ZH-CN_DV9`；**有 `EI.CFG`**（原版商业版介质特征） |
| Suitable for LAB-SRC01 / LAB-DST01 | **YES**（商业版、可入域、Pro 版本可加域；Source/Target 用**同一张**以保证可控） |

**备选与对比**：

| 镜像 | 判定 | 理由 |
|---|---|---|
| `Windows11_ChinaOnly_professional_x64_zh-cn_26300_9457.iso`（8,825,178,112 B，SHA `95fe575e6e8069bd6a151f0aac784589c0d752794d20f51c4138f5796428934e`，卷标 `PCHA_X64FREO_ZH-CN_DV9`，install.wim 7,497.9 MB、**映像数=1**、**无 EI.CFG**、setup 栈 26100.9443，文件名/联网佐证 = **Win11 26H2（26300）**，与宿主自身 26300 同代） | **NEEDS REVIEW / 备选** | 单映象省事、版本最新，但属"**仅限中国**"渠道 SKU、build 极新（26H2），与"公司真实企业环境"的相似度低于商业版；不作主选 |
| `zh-cn_windows_10_business_editions_version_22h2_updated_oct_2025_x64_dvd_d4e92df7.iso`（6,985,566,208 B，SHA `2c026f88b826be82ec0fb5a084b822298fba026eef9d8cff8441e822fc72205e`，install.wim 5,780.8 MB、**映像数=5**、有 EI.CFG，**SHA256 与微软 MSDN 公布值逐字一致 ⇒ 原版可信**，内部版本 19045.6456） | **可选兼容基线（第二轮）** | 第一轮**不混入** OS 差异（任务书明确要求）；第二轮作为 `Optional Compatibility Baseline` |
| `D:\Users\User\Downloads\Windows11_ChinaOnly_professional_x64_zh-cn_26300_9457.iso` | 冗余 | 与 E: 同名同大小重复（8.22 GB） |

**`Primary Three-VM OS Baseline`（建议）**：
- `LAB-DC01` = **Windows Server 2025 Standard（Desktop Experience）** —— 介质待补（B1）
- `LAB-SRC01` = `LAB-DST01` = **Windows 11 24H2 商业版（26100 zh-CN）**，**同一个 ISO、同一个索引**
- 客户端**不用** ChinaOnly、**不用** Win10（第一轮）

**`Optional Compatibility Baseline`（第二轮再做）**：Windows 10 22H2 商业版 19045.6456 作为 SRC01（旧电脑更像 Win10 的真实场景）。

## D. Unidentified / Alternative ISO

| 文件 | 用途判定 | 是否需要 | 处置建议 |
|---|---|---|---|
| `E:\系统ISO和Vm安装包\ISO\Windows.iso` | **Win10 客户端 19041 分支 ESD 介质**（非 Server，edition 待确认） | 不必需 | **保留备用**；不作为 DC，也不作为第一轮客户端 |
| `C:\Program Files (x86)\AOMEI Partition Assistant\ampe.iso`（1,098.6 MB） | AOMEI 分区助手 **PE 维护盘** | 与实验无关 | 保留（不属于本实验资产） |
| `C:\Aomei\AomeiBoot.wim`（1,040.5 MB） | 同上，PE 引导映像 | 与实验无关 | 保留 |
| `PCMig\lab\answer.iso`（1,179,648 B）及若干历史副本 | 历史 L3 应答盘（`answer\autounattend.xml` 面向 **Win10 专业版**，且**含明文密码**） | 第一轮不需要 | **保留但不要直接复用**；若复用必须先处理凭据并另写 Server/Win11 应答 |
| 历史文档记载的 Server 2025 Eval ISO（SHA `855176cfb446a3561dc36e81110774e57fd2bb0fa6ccd8a14978662f9f47fd03`，7.845 GB） | 曾用于 L3 实验室 | **已不在盘上** | 需重新获取（B1） |

---

# 附录 A. 仓库内既有实验室资产的复用判定（只读审计结论）

| 资产 | 结论 |
|---|---|
| `tools\pcmiglab-vm.ps1`（175 行） | **复用性最高**。只建空 VM + 空 VHDX + 交换机；默认干跑 + `-LabRoot` 可覆盖（hyperv-lab skill 已用 `-LabRoot E:\PCMigLab` 适配）；硬编码 `G:\PCMigLab`、`PCMigLab-Sw`、`192.168.28`、DC01/FS01/CLIENT01 规格。**创建/删除 VM 可能仍需管理员令牌** |
| `tools\l3-inject.ps1`（400 行） | **★★★★★ 最值得复用**。离线灌盘：`Mount-VHD` → GPT(EFI 300MB/MSR/NTFS) → `Expand-WindowsImage` → `bcdboot /f UEFI` → 注入 `Windows\Panther\unattend.xml` + `SetupComplete.cmd` → 卸盘 → 建 VM → **硬盘首启**。头注释直接写明结论：「从 ISO 引导安装必然出现 `Press any key to boot from CD`（Hyper-V Gen2 UEFI 无法绕过）…离线部署完全不需要引导安装程序」。⚠ 风险点：`:291-293` `Remove-Partition -Confirm:$false`（按磁盘号，裸删分区）、`:210` 挂 ISO **无配对卸载**、`:296/:304` `-AssignDriveLetter` 会扰动宿主盘符、`:243` `Stop-VM -Force` |
| `tools\confirm-iso-images.ps1` | **唯一零副作用的宿主级脚本**（挂 ISO → 列 wim 清单 → 卸 ISO → 写 `IMAGES-LIST.txt`）；**新实验室必须先跑一次**（改 `$IsoPath`，且**删掉 `:111-114` 的写死结论**）。需管理员 |
| `tools\l3-diag.ps1` | **只读诊断，复用性最高**；打印服务/固件/启动顺序/DVD/HDD/网卡并给结论。若新方案改成硬盘首启，其"从光驱引导"的结论假设会把正确配置误判为问题 |
| `tools\l3-fixboot.ps1` | 修 `The boot loader did not load an operating system`；**灌盘起不来时的唯一现成解药**，建议一并适配保留 |
| `tools\l3-orchestrate.ps1` | **编排思路最值钱**：PowerShell Direct（不走网络）+ 超时轮询 + 等待域就绪 + 重试加域 + 写 json 证据 + "BLOCKED 而非假 FAIL"。需改 VM 名常量、域就绪判据、重试动作。⚠ 历史坑：域控上不能用裸 `Administrator` 连接（需 `.\Administrator` 或 `DOMAIN\Administrator`） |
| `tools\lab-admin-{setup,executor,run}.ps1` | **建议不引入**。注册 SYSTEM+Highest+开机自启+无时限计划任务，队列目录"可写即可提权"（脚本自述）；默认 `$ExecutorPath='I:\deepseek work\PCMig\tools\lab-admin-executor.ps1'`（**旧镜像路径**）；历史 v1 因脚本末尾 `ReadKey` 在无人值守下**永久阻塞并把执行器一起挂死** |
| `tools\l3\dc01-setup.ps1` | **DC01 可直接复用**（改域名字符串即可）：`Install-ADDSForest`、OU/组/用户、安全闸门"已是域成员即中止" |
| `tools\l3\fs01-setup.ps1` | 部分复用（目标改 Win11 + `D:` 共享后 ShareRoot/ShareName/IP 全改）；⚠ `Install-WindowsFeature -Name FS-FileServer` **在 Win11 上不存在，必须改** |
| `tools\l3\client01-setup.ps1` | 最接近 `LAB-DST01`（加域 + 造用户数据），但**完全不含 PCMig 部署逻辑**（只做 `Get-Command pcmig` 探测）⇒ **DST01 部署 PCMig = 净新增工作量** |
| `tools\l3\l3-scenarios.ps1` | **当前无法运行**：依赖 `J:\pcmig-lab\lib\LabCommon.ps1`（全工作区 glob **零命中**），调用的 10 个函数全部未定义 ⇒ 直接 `exit 2`。**场景设计可抄，代码不可抄** |
| `lab\vm-gen-massdata.ps1` | 上限 50,000 个 512 B 小文件；写死 `E:`；**锁文件段依赖 `fill-e.ps1` 先建文件，单跑必抛异常**；无幂等/续跑 |
| `lab\fill-e.ps1` | 4×2100 MB + 50×10 MB + 3,000×40 KB + 200 docx + 填满盘只留 150 MB；**只能在 guest 内跑** |
| `lab\unlock.ps1` | `Stop-Process` 杀掉**所有** powershell 进程 ⇒ **危险，禁止在宿主运行** |
| `lab\diskpart.txt` | `select disk 3` 裸按序号选盘 ⇒ 磁盘编号一变即**灾难性误清盘**；已被 `l3-inject.ps1` 的分区逻辑取代 |
| `lab\repair-src.ps1` | **与实验室无关**（按行号改源码里的 U+FFFD 损坏中文；路径在 `H:`，行号映射一次性强耦合，**误用会静默破坏源码**） |
| `C:\Users\User\.dsh\skills\hyperv-lab\SKILL.md`（6,476 B）+ `scripts\hyperlab.mjs`（6,845 B） | **⭐ 现成的建机入口**：`status/list/iso/plan/create/remove --purge-vhd/clone/export/import/checkpoint/create-lab`，**不加 `--execute` 一律干跑**；`create-lab` 转调 `tools/pcmiglab-vm.ps1 -LabRoot E:\PCMigLab`；安全口径完备（默认干跑、只认 `PCMigLab-` 前缀、隔离、不可逆操作先确认、凭据不写环境变量） |

# 附录 B. 历史教训（仍然成立的，直接进 Phase 1 纪律）

1. **严禁走"从 ISO 引导安装"这条路**。必然出现 `Press any key to boot from CD`，**Hyper-V Gen2 UEFI 无法绕过**；"把应答 ISO 设第一启动项就行"是**错的**（历史明确记录为不要做）。可行路线：① 官方 **VHD**（免安装程序/unattend/OOBE，历史强烈建议）；② 手工装一台 → `sysprep /generalize /oobe /shutdown` → 复制 VHDX 三次；③ **离线 apply-wim**（`l3-inject.ps1` 路线）。
2. **unattend.xml 不完整 ⇒ 卡 `IMAGE_STATE_UNDEPLOYABLE`**：症状是 `Install-WindowsFeature` 间歇失败、`Install-ADDSForest` 报"服务器名或凭据验证失败"、DNS 报 `ERROR_KEY_DELETED`。手改 `ImageState=COMPLETE` 能救但下次启动回退（属 hack）。装完**立刻查** `ImageState` 与 `C:\Windows\Panther\setuperr.log`。
3. **`specialize` 阶段的 `RunSynchronous` 命令必须返回 0**（中文组名的 `netsh advfirewall` 曾导致非零而中止 specialize）。
4. **12 条 PowerShell 5.1 坑**：`.ps1` 必须 UTF-8 BOM；不用字符串拼接生成代码；`param()` 必须最前；`-File` 不能绑数组参数（用 CSV 单 token + `-split ','`）；`Set-Location` 不改 .NET 当前目录（一律绝对路径）；**脚本末尾 `ReadKey` 在无人值守下永久阻塞**（必须用 `PCMIGLAB_HEADLESS` 之类守卫）；PowerShell Direct 会话结束时未重定向的子进程会被清理；**域控上不能用裸 `Administrator` 连接**；`Dismount-VHD` 在 VM 运行时会失败（灌盘前必须先 `Stop-VM` 并等真正停止）；执行器串行会阻塞后续提交。
5. **全隔离红线**：只用 Internal 交换机与实验域；不加入真实域、不用真实公司账号、不改宿主 DNS/路由/防火墙。
6. **不建第 4 台机器**（历史评估：增量收益仅约 5%，但内存与串行启动成本显著）。

# 附录 C. 最小可行建机路径（供 Phase 1 参考，本轮不执行）

1. `confirm-iso-images.ps1`（改 `$IsoPath`，**提权**）→ 确认 Win11 24H2 商业版与 Server ISO 的 install 索引。
2. 以 `l3-inject.ps1` 为蓝本（改 `$specs` 为 DC01/SRC01/DST01、`$LabRoot` 改 `E:\PCMigLab`、`$ImageIndex` 按确认值）→ **离线灌盘，无需按空格、无需应答 ISO**；DomainName 改 `corp.test`、IP 段改 `10.77.0.x`。
3. DC01：复用 `l3\dc01-setup.ps1` 改 `-DomainName corp.test` / `-NetbiosName CORP` / `-StaticIP 10.77.0.10`。
4. SRC01：以 `fs01-setup.ps1` 为蓝本，`ShareRoot='D:\'`、ShareName `D`、**删掉 `Install-WindowsFeature FS-FileServer`**（Win11 无此角色，改 `New-SmbShare`）。
5. DST01：**PCMig 部署为净新增**；造数可参考 `client01-setup.ps1:100-109`。
6. 编排抄 `l3-orchestrate.ps1` 骨架（改 `$targets`、域就绪判据），**并额外加一条 `LAB-DST01` 的 PowerShell Direct 可达性预检**。
7. 数据生成：现成脚本改盘符只能到 5 万小文件级；**210 万级必须新写**（见 §17 约束）。
8. 提权优先"显式一次性管理员令牌"；若确需 `lab-admin-*`，**先把 `ExecutorPath` 从 `I:\deepseek work\...` 改成权威路径并单独报批**。

# 附录 D. 本次审计的证据来源与不确定项

- **实测命令**（只读）：`Get-CimInstance Win32_Processor/Win32_ComputerSystem`、`Get-Volume`、`Get-VMHost`、`Get-VMSwitch`、`Get-VM`、`Get-Service SharedAccess`、`Get-NetIPAddress`、`Get-NetRoute`、`Get-NetNat`、`Get-SmbServerConfiguration`、`Get-FileHash`、`Mount-DiskImage`/`Dismount-DiskImage`、`Get-ChildItem -Recurse -Include *.iso,*.vhd,*.vhdx,*.vmdk,*.wim,*.esd`、注册表/进程/命令探测（VMware）、`Test-Path`、`git rev-parse/log/status`。
- **代码核验**：`E:\Project\deepseek work\PCMig\src\` 全量 grep（Robocopy/拓扑/契约/超大数据模式）+ 四路只读子代理交叉核验（运行拓扑、产品契约、历史资产与大规模模式、L3 工具链）。
- **未取到 / 不确定**：
  1. NTFS 簇大小（`fsutil` 需提权）—— 只影响 §17 空间精算精度。
  2. 各 ISO 内 edition 名（`Get-WindowsImage`/`dism` 需提权）—— 建 VM 前必须提权确认。
  3. Hyper-V **写**操作是否免提权 —— 未实测（Phase 0 禁止创建）。
  4. 宿主当前内存占用基线（只知总量 31.76 GB）—— Phase 1 建机前应实测。
  5. `C:\Users\User\.dsh\skills\hyperv-lab\scripts\hyperlab.mjs` 的实现未逐行读（只读了 SKILL.md）；`create-lab` 除 `-LabRoot` 外是否还做其他适配未确认。
  6. `docs\测试报告-公司环境.md`（1,348 行）与 `docs\qa\history\Private-Test-Lab-Blueprint.md`（344 行）只做了定向检索，未逐行通读。
  7. `LAB-DST01` 是否会被 GPO/Defender 策略影响（如 Controlled Folder Access 拦 robocopy 写入）—— 未测，属 Phase 1 场景（历史 L3-10）。
