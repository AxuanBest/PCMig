# PCMig Corporate Simulation Blueprint（L3）

> 版本：**v2（2026-09-19 用户决策后更新）** ｜ 基线：v0.4.6
> 性质：**设计蓝图**。**尚未创建任何 VM、未安装域控、未下载任何介质。**
> 用户决策（2026-09-19）：**做**；可准备 Windows Server 2025 Evaluation；**但先 Blueprint，再创建 VM**。
> 配套：`Private-Test-Lab-Final-Report.md`（L0–L2 执行结果）｜`Coverage-Matrix-V046.md`｜`..\First-Day-Company-Test-Checklist.md`

---

## 一、宿主机资源审计（13 项，全部本机实测）

| # | 项 | 实测值 | 判定 |
|---|---|---|---|
| 1 | **CPU** | **AMD Ryzen 5 9600X** ｜ **6 核 / 12 线程** @3.9GHz | 够跑 2–3 台轻量 VM |
| 2 | **RAM 总量** | **31.1 GB** | 充裕 |
| 3 | **RAM 当前可用** | **12.1 GB**（其余被宿主占用） | **2 台 VM 舒适；3 台偏紧；4 台需串行** |
| 4 | **磁盘可用** | C 117.6 ｜ D 24.5(FAT32) ｜ E 130.1 ｜ F 129.8 ｜ **G 177.2** ｜ H 27 ｜ I 76.1 ｜ J 127 ｜ K 80（GB） | **总空闲约 880 GB** |
| 5 | **VMware** | 🔴 **半损坏**：注册表 26.0.0、服务（VMAuthdService / NAT）Running，但 **`C:\Program Files\VMware\VMware Workstation` 下无 `vmware.exe` / `vmrun.exe`** | **当前无法启动任何 VMware VM** |
| 6 | **Hyper-V** | ✅ **全栈可用**：`vmms` / `vmcompute` / `HvHost` 全 **Running**；`Microsoft-Hyper-V-All`=1 | **推荐平台**（`Get-VM` 需提权） |
| 7 | **VT-x / AMD-V** | `VirtualizationFirmwareEnabled`=**True**；SLAT / VMMonitor 显示 False（**因 Hyper-V 已接管，非硬件缺失**）；**VBS = 2（Running）** | ✅ 虚拟化可用 |
| 8 | **已有 VM** | Hyper-V 在册目录为空；**`<Hyper-V 镜像盘>\PCMig-OldPC\` 有 12.0 GB vhdx + 18.0 GB avhdx + Snapshots + VMRS**（历史真实建过的 PCMig 测试 VM，未在册）；`<本机文档>\Virtual Machines\` 有 **7 台 VMware 格式**（Win10×3 / Win11×2 / CentOS×2，共约 67 GB） | ⭐ **既有资产可复用/待注册** |
| 9 | **lab 的 vm-*.ps1** | `vm-deep.ps1`（704B，\\?\ 深路径）、`vm-gen-massdata.ps1`（2.6KB，5 万小文件/40 层/锁文件）、`unlock.ps1`（危险：杀所有 powershell） | 见 §三 复用判定 |
| 10 | **VM Answer ISO** | `lab\answer.iso`（1.18 MB）+ `lab\answer\autounattend.xml`（5.5 KB，自动装 Win10 专业版 + **`LocalAccountTokenFilterPolicy=1`**） | ⭐ **高价值可复用**（⚠ 含明文密码需处理） |
| 11 | **现有虚拟网络** | VMnet1 **198.51.100.1/24 Up**（host-only）｜VMnet8 **192.0.2.1/24 Up**（NAT）｜`vEthernet (Default Switch)` **198.51.100.1/20** | **隔离网络已就绪** |
| 12 | **已有 Windows Client VM** | VMware 格式有 Win10×3 / Win11×2（**因 VMware 用户态缺失暂不可用**）；Hyper-V 侧无在册 VM | ⚠ 需修复平台或重装 |
| 13 | **Windows Server ISO** | 🔴 **无任何 Server 介质**（全盘扫描仅 Win10 22H2 / Win11 24H2 客户端镜像） | **需从官方评估中心获取** |
| — | 宿主网络 | 有线 2.5GbE `192.0.2.250/24`（家用路由 NAT 后），DNS 192.0.2.1；WLAN Disconnected | 隔离方案见 §四 |

---

## 二、Windows Server 2025 Evaluation 介质准备（用户批准）

### 2.1 获取渠道（官方，不使用第三方）

| 项 | 内容 |
|---|---|
| **来源** | **Microsoft Evaluation Center**（官方评估中心） |
| **版本** | **Windows Server 2025 Evaluation**（用户指定） |
| **授权** | **180 天**评估期 |
| **激活要求** | 安装后官方要求**前 10 天内通过 Internet 激活**，否则会自动关机 |
| **形式** | ISO（约 5–6 GB），可选 Datacenter / Standard、Desktop Experience / Core |
| **建议选项** | **Standard + Desktop Experience**（有 GUI，便于配置 AD/DNS；Core 版对实验不便） |

### 2.2 存放与校验（待用户下载后执行）

| 项 | 建议 |
|---|---|
| 存放位置 | **`<实验室镜像盘>\ISO\`**（G 盘 177 GB 空闲，与 J 盘测试数据物理分离） |
| 记录 SHA256 | 下载后立即记录，写入 `<实验室镜像盘>\ISO\MANIFEST.md` |
| 空间预算 | ISO ≈ 6 GB + VM 磁盘（见 §五） |

> ⚠ **本轮未下载任何介质**（用户要求：先 Blueprint）。本节仅为可执行准备清单。

---

## 三、lab 资产复用判定（用户第九节"优先复用"）

| 资产 | 复用判定 |
|---|---|
| **`lab\answer\autounattend.xml`** | ⭐ **直接复用**：自动装 Win10 专业版、计算机名、**开放"文件和打印机共享"防火墙组**、**`LocalAccountTokenFilterPolicy=1`**（允许本地管理员远程访问 —— 正是 PCMig 需要的对端配置）、SkipMachineOOBE + 自动登录。<br>⚠ **含明文密码**（口令样式示例一律写作 <口令>；具体值见受控凭据保管处）→ **使用前必须先处理**。 |
| `lab\answer.iso` + `New-AnswerIso.ps1` | ✅ 复用：IMAPI2FS 把 `answer\` 打成应答 ISO（脚本内默认指向旧 H 盘，**需改路径**）。 |
| `lab\vm-gen-massdata.ps1` | ✅ 复用：VM 内造数（5 万小文件 / 40 层深路径 / 持锁文件 / 空间自适应）。 |
| `lab\vm-deep.ps1` | ✅ 复用：`\\?\` 前缀造 40 层深路径。 |
| `lab\diskpart.txt` | ✅ 复用：EFI 100MB + MSR 16MB + 主分区模板。 |
| `lab\fill-e.ps1` | ❌ **不可复用（有风险）**：会填**真实物理 E 盘**。只保留 `fsutil createnew` +"填充到剩 N MB"思路。 |
| `lab\unlock.ps1` | ❌ **危险**：`Stop-Process` 杀所有非自身 powershell。 |
| `lab\uia-changelog.ps1` | ✅ 已在本轮 GUI Smoke 复用并验证（UIA 按 AutomationId Invoke）。 |
| **`<Hyper-V 镜像盘>\PCMig-OldPC\`** | ⭐ **待提权校验后决定**：若可用可直接当 Client VM（省一次 Win10 安装，约 30 分钟）。 |
| **`<本机文档>\Virtual Machines\` 7 台** | ⚠ 依赖已损坏的 VMware 用户态 → 暂不可用；若改用 Hyper-V 需转换（不建议，成本高） |
| **PCMig Test Lab（`<外置实验室盘>\`）** | ⭐ **L3 场景直接复用**：`lib\LabCommon.ps1`（全量 SHA256 核对 / 环境快照 / job-state 判定 / 安全护栏）、`lib\testdata.ps1`、`scenarios\*.ps1` 生命周期模板 |

---

## 四、网络结构（隔离，用户明确要求）

```
宿主机 AXUAN  192.0.2.250（家用网络，保持不动，VM 不桥接）

  ┌────────────── 隔离虚拟网络：PCMigLab.local 内部段 ──────────────┐
  │                                                                  │
  │   DC01 ── AD DS + DNS                                             │
  │     │                                                             │
  │   FS01 ── SMB + NTFS ACL + 管理共享                               │
  │     │                                                             │
  │   CLIENT01 ── 加入测试域，模拟普通员工 PC                          │
  │                                                                  │
  └──────────────────────────────────────────────────────────────────┘
       ▲  所需虚拟网段：192.168.28.0/24（Hyper-V Private 或 VMnet1 host-only）
```

**硬约束（用户明确）**：
- ❌ **不把测试域加入真实公司网络**
- ❌ **不修改真实公司 DNS**
- ❌ **不让真实公司账号进入测试域**
- ❌ **不把测试 VM 加入公司生产域**
- ✅ 只用 **Lab-only**：accounts / shares / DNS / domain / VHD
- ✅ 不用 Bridged（宿主 192.0.2.x 完全不受影响）

---

## 五、推荐拓扑（用户建议：DC01 + FS01 + 1 Client，而非默认 4 台）

```
┌──────────────────────── 隔离网段 192.168.28.0/24 ────────────────────────┐
│                                                                          │
│  ┌────────────────────┐        ┌────────────────────┐                    │
│  │ VM-1  DC01         │◄──────►│ VM-2  FS01         │                    │
│  │ Windows Server 2025│        │ Windows Server 2025│                    │
│  │ ├ AD DS            │        │ ├ 文件服务         │                    │
│  │ ├ DNS              │        │ ├ SMB 共享         │                    │
│  │ ├ Domain Users     │        │ ├ NTFS ACL         │                    │
│  │ ├ Groups / OU      │        │ └ 管理共享 D$      │                    │
│  └────────────────────┘        └────────────────────┘                    │
│           ▲                              ▲                               │
│           └──────────┬───────────────────┘                               │
│                      │                                                   │
│            ┌─────────┴──────────┐                                        │
│            │ VM-3  CLIENT01     │                                        │
│            │ Windows 10 22H2    │  ← 加入 PCMigLab.local                 │
│            │ 模拟普通员工 PC     │     普通域用户登录                     │
│            └────────────────────┘                                        │
│                      ▲                                                   │
│                      │  SMB 直拉（PCMig 在宿主或 CLIENT01 上跑）          │
│            ┌─────────┴──────────┐                                        │
│            │ 宿主 AXUAN 跑PCMig │                                        │
│            └────────────────────┘                                        │
└──────────────────────────────────────────────────────────────────────────┘
```

| 节点 | 角色 | 规格建议 | 磁盘（dynamic） | 介质 |
|---|---|---|---|---|
| **VM-1 DC01** | **AD DS + DNS**；域 `PCMigLab.local`；用户 `PCMigTestUser`/`PCMigAdmin`/`PCMigOperator`；OU `PCMig-Clients`/`PCMig-Servers` | 2 vCPU / 3 GB | ~44 GB | **Server 2025 Eval（待下载）** |
| **VM-2 FS01** | 文件服务：普通共享 + **管理共享 `D$`** + NTFS ACL 模型 | 2 vCPU / 2 GB | ~44 GB | 同上 |
| **VM-3 CLIENT01** | 加域客户端，模拟普通员工 PC | 2 vCPU / 4 GB | ~60 GB | ✅ **本机已有 Win10 22H2 ISO + `autounattend.xml`** |

**资源合计**：内存 **9 GB**（当前可用 12.1 GB ✅）｜磁盘精简后 **约 120–150 GB** → 放 **`<实验室镜像盘>\VMs\`**（177 GB 空闲）。

### 为什么**不**默认 4 台（PC02）

| 方案 | 覆盖 | 缺失 | 成本 |
|---|---|---|---|
| 2 台（DC01 + CLIENT01，FS 兼在 DC01） | 域认证 / DNS / 域用户 / ACL / 管理共享 | 独立文件服务器角色 | ~9 GB / ~100 GB |
| **3 台（推荐）** | **上述 + 独立 FS 角色 + 管理共享** | 真实 EDR / VPN | **9 GB / ~150 GB** |
| 4 台（+PC02） | 双客户端（源/目标分离） | — | **13–16 GB → 需串行启动**，**增量收益仅约 5%** |

> **结论**：**PC02 暂不创建**。仅当测试矩阵证明"需要区分两台客户端"时再增加（用户明确要求）。

---

## 六、L3 测试目标（只复制 PCMig 真正依赖的企业条件）

### 6.1 优先验证（用户指定顺序）

| 序 | 目标 | 对应 L3 场景 |
|---|---|---|
| 1 | **AD** | L3-01/02/06 |
| 2 | **DNS** | L3-04 |
| 3 | **Domain User** | L3-01/02/09 |
| 4 | **SMB** | L3-03/07 |
| 5 | **UNC** | L3-04 |
| 6 | **NTFS ACL** | L3-05 |
| 7 | **Admin Share** | L3-03 |
| 8 | **普通用户 / 管理员** | L3-03/09 |
| 9 | **权限失败** | L3-05 |
| 10 | **网络失败 + 恢复** | L3-07/11 |
| 11 | **GPO** | L3-08 |

### 6.2 之后再考虑

Firewall ｜ UAC ｜ Restricted User ｜ 更复杂的 GPO

### 6.3 **不模拟**（PCMig 实际不依赖的企业系统）

- 企业 **EDR / 内核级安全软件**（用户明确不安装来源不明的内核级软件）
- **企业 VPN / 生产网段**
- 真实公司 DNS / 真实生产域

---

## 七、L3 场景清单（11 项）

| # | Scenario | 验证什么 | 优先级 |
|---|---|---|---|
| `L3-01` | 域用户凭据直拉（`--user PCMigLab\PCMigTestUser --password <真实>`） | **真实密码校验**（回环做不到）、`DOMAIN\user` 格式 | **P0** |
| `L3-02` | 错误域密码 | 真实 1326 路径与文案 | **P0** |
| `L3-03` | 管理共享 `\\FS01\D$`：域管理员 vs 普通域用户 | 管理共享权限 + UAC 远程令牌过滤 | **P0** |
| `L3-04` | DNS 短名 `\\FS01` vs FQDN `\\FS01.PCMigLab.local` | `PreflightChecker` 多地址解析分支 | **P0** |
| `L3-05` | NTFS ACL 域组拒绝 | 错误 5 真实分支 + 扫描残缺阻断 | P1 |
| `L3-06` | **1219 真实冲突**：凭据 A 先连，再用凭据 B | `NetworkShare.cs:185-199` 复用而非杀会话 | **P0** |
| `L3-07` | **共享消失 → 恢复 → Resume** | 状态机 Interrupted → Resume 完整性 | **P0** |
| `L3-08` | 域 GPO（UAC / 防火墙 / PS 执行策略） | 受策略约束客户端上的行为 | P2 |
| `L3-09` | CLIENT01 普通域用户跑 PCMig | 非管理员能力边界 | P1 |
| `L3-10` | Defender Controlled Folder Access | 写保护拦截 | P1 |
| `L3-11` | 网络分段（DNS 通 / SMB 不通） | 预检分支定性 | P1 |

**复用**：每场景用 `lib\LabCommon.ps1` 生命周期 + `Compare-LabTree` 全量核对；数据集用 `lib\testdata.ps1`。

---

## 八、执行阶段规划（Blueprint 之后）

| 阶段 | 内容 | 前置 |
|---|---|---|
| **P1-A（当前）** | ✅ 资源审计（13 项）｜✅ 本 Blueprint｜待下载 Server 2025 Eval ISO | — |
| P1-B | 创建 VM-1 DC01 → 安装 AD DS + DNS → 建域 `PCMigLab.local` | ISO |
| P1-C | 创建 VM-2 FS01 → 文件服务 + 共享 + ACL + 管理共享 | DC01 就绪 |
| P1-D | 创建 VM-3 CLIENT01 → 加域 → 普通域用户 | DC01 就绪 |
| P1-E | 跑 L3-01…L3-11（先 DC01→FS01→Client 主链，再 GPO/防火墙） | 三机就绪 |
| P1-F | 按需增加 PC02（**仅当矩阵证明必要**） | P1-E 结论 |

---

## 九、需要用户决策 / 授权（剩余）

| # | 事项 | 说明 |
|---|---|---|
| 1 | **下载 Windows Server 2025 Evaluation ISO** | 用户已批准；建议存 `<实验室镜像盘>\ISO\` 并记录 SHA256 |
| 2 | **虚拟化平台二选一** | 建议 **Hyper-V**（已全栈可用；VMware 用户态缺失需重装），或先修 VMware |
| 3 | 是否注册/复用 **`<Hyper-V 镜像盘>\PCMig-OldPC`** | 需提权 `Get-VHD` 校验后决定（可省一次 Win10 安装） |
| 4 | 是否允许创建 **Hyper-V Private 交换机**（隔离网段） | 需管理员 |
| 5 | 磁盘位置确认：**`<实验室镜像盘>\VMs\`** | 177 GB 空闲，与 J 盘测试数据物理分离 |

---

## 十、本轮未做的事（如实声明）

1. **未下载任何介质、未创建任何 VM、未安装域控、未改任何网络/BIOS 设置**。
2. 未注册 `PCMig-OldPC.vhdx`（需提权校验）。
3. "成本/收益"判断属**审计判断**，非实测。
4. 未验证 Server 2025 与 `autounattend.xml`（该应答文件是为 Win10 写的，**Server 需另写应答或手工安装**）。

---

*本 Blueprint 基于本机只读实测（硬件/平台/磁盘/介质/网络/资产 13 项）+ 项目 `lab\` 资产审计 + L0–L2 已积累的测试基础设施。未创建任何 VM，未修改任何生产代码。*
---

## 附录 D：执行入口与权限事实（2026-09-19 实测）

### D.1 权限事实（必须如实记录）

**本机会话的 shell 进程始终是 Medium 完整性级别，无法提权**：

```
IsAdmin        : False
Get-VM         : ✘ You do not have the required permission
Get-VHD        : ✘ You do not have the required permission
```

**"用户以管理员身份启动窗口"不会改变本会话进程的令牌** —— 那是两个独立进程。
因此 L3 的**任何 VM 创建/管理动作**都必须经由"**自我提升脚本 + 用户同意一次 UAC**"完成。

> 这条已在 L0–L2 阶段被验证有效：`<外置实验室盘>\scenarios\run-elevated.cmd` 就是这个模式，
> 用户双击一次即跑完 B2 全部 VHD 场景（含自动清理与汇总）。

**这不是缺陷，也不影响交付**：本会话能做的是"写工具 + 审计 + 设计 + 验证非特权部分"，
特权动作由用户一次授权完成。

### D.2 已就绪的执行入口

| 入口 | 用途 | 状态 |
|---|---|---|
| **`tools\pcmiglab-vm.ps1`** | L3 VM 创建/管理（DC01 / FS01 / CLIENT01） | ✅ **已写并干跑验证** |
| `<外置实验室盘>\scenarios\run-elevated.cmd` | L0–L2 的 VHD/FAT32/DiskFull 批次 | ✅ 已验证可用 |

`pcmiglab-vm.ps1` 的设计要点：

| # | 要点 |
|---|---|
| 1 | **默认只干跑**（不加 `-Execute` 绝不创建任何东西） |
| 2 | 创建需管理员令牌；非管理员时**打印自我提升指引并 exit 5**（不静默失败） |
| 3 | **完整隔离**：VM 名带 `PCMigLab-` 前缀、VHD 固定放 `<实验室镜像盘>\VMs\`、交换机 `PCMigLab-Sw`（Internal） |
| 4 | **不碰宿主网络**：只创建 Internal 交换机，**不自动改宿主 IP/DNS** |
| 5 | 删除需 `-Remove -Execute` **双确认**，且只删 `PCMigLab-` 前缀的对象 |
| 6 | 前置检查：Hyper-V 可用性、交换机是否存在、ISO 是否就位、盘空间、**同名 VM 冲突** |
| 7 | 动态内存 1 GB–上限、SecureBoot 关闭（便于实验镜像启动） |

**干跑实测输出**（不创建任何 VM）：

```
PCMigLab-DC01      IP=192.168.28.10  2vCPU / 3GB / 44GB   AD DS + DNS
PCMigLab-FS01      IP=192.168.28.20  2vCPU / 2GB / 44GB   文件服务 + SMB + ACL + 管理共享
PCMigLab-CLIENT01  IP=192.168.28.30  2vCPU / 4GB / 60GB   Win10 22H2 加域客户端
资源合计：内存 9 GB ｜ 磁盘 148 GB 上限（精简后实际约 30–50%）
G: 可用空间 177.2 GB ✔ ｜ 无同名 VM 冲突 ✔
```

### D.3 官方介质获取（唯一阻塞项）

| 项 | 内容 |
|---|---|
| **官方页面** | [Microsoft Evaluation Center — Windows Server 2025](https://www.microsoft.com/en-us/evalcenter/download-windows-server-2025) |
| **授权** | 免费评估 **180 天**；到期后实例将停用 |
| **形式** | 64-bit **ISO**（另有 VHD 形式） |
| **流程** | 需**注册表单**后下载（官方设计，**无免登录直链**）→ **必须由用户操作** |
| **建议版本** | **Standard + Desktop Experience**（有 GUI，便于配置 AD/DNS） |
| **存放位置** | **`<实验室镜像盘>\ISO\`**（目录已建，当前为空） |
| **下载后** | 记录 SHA256 至 `<实验室镜像盘>\ISO\MANIFEST.md` |

> ⚠ 本会话**未下载任何介质**：官方页面需注册，且按纪律不由 AI 代下第三方来源。

### D.4 与你（用户）的协作分界（下一次交互只需两件事）

| # | 动作 | 由谁做 | 说明 |
|---|---|---|---|
| 1 | **下载 Windows Server 2025 Eval ISO** 到 `<实验室镜像盘>\ISO\` | **你** | 需注册表单，官方无直链 |
| 2 | **同意一次 UAC** 跑 `tools\pcmiglab-vm.ps1 -Execute` | **你**（一次） | 之后 3 台 VM 由脚本创建，**无需再逐台手工操作** |

**其余全部由本会话完成**（不需提权）：AD/DNS 配置脚本、FS01 共享与 ACL 脚本、
CLIENT01 加域脚本、L3-01…L3-11 场景脚本、证据体系接入 `LabCommon`。

### D.5 待确认（不影响开工）

- [ ] 虚拟化平台是否确定用 **Hyper-V**（现状推荐：已全栈可用；VMware 用户态缺失需重装）
- [ ] 是否允许创建 **Internal 交换机** `PCMigLab-Sw`（脚本会创建；不碰宿主网络设置）
- [ ] 是否注册复用 `<Hyper-V 镜像盘>\PCMig-OldPC`（可省一次 Win10 安装；**需提权校验**）