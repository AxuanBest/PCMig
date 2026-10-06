# PCMig Three-VM Enterprise Domain Test Lab — Phase 1 Environment Build Report

- 报告时间：2026-10-02 23:40（Asia/Shanghai）
- 阶段：**Three-VM Phase 1 — Environment Build**
- 授权来源：人工指令 `START THREE-VM ENVIRONMENT BUILD`（含 27 节主指令 + 14 节「无人值守管理员执行模式」）
- 权威证据根：`<实验室根>\Evidence\Phase1-EnvironmentBuild-20261002-221412\`（D10）
- **最终状态：`THREE-VM ENVIRONMENT BUILD COMPLETE` / `Status: READY FOR UI TEST EXECUTION`**
- 本阶段**未启动过任何 PCMig 正式测试**：未 Connect、未 Add Share、未 Start/Stop/Resume/Verify/Repair、未跑 F01–F15、未生成 210 万文件、未改产品代码、未接线 ExpertMode、未动 PMML、未 push、未 Release。

---

## 0. 执行方式（与规格的差异说明）

用户规格假设「无人值守安装操作系统」。本机实测 **Hyper-V PowerShell 模块 2.0.0.0 不提供 `Send-VMKey`/`Get-VMKey`**，因此 `Press any key to boot from CD` 在 Gen2 UEFI 下**无法自动按键**（这正是历史交接中「最贵的一课」）。

采用的实际路线：**离线灌盘（offline apply-wim）**，不使用 ISO 引导：

```
Mount-VHD → Initialize-Disk GPT → EFI 300MB FAT32 + MSR 16MB + NTFS 主分区
→ Expand-WindowsImage -ApplyPath -Index <N>（镜像自带 bcdboot 写 ESP）
→ 注入 Windows\Panther\Unattend.xml（两处）+ Windows\Setup\Scripts\SetupComplete.cmd + C:\PCMigL3 载荷
→ 离线写 SOFTWARE hive 的 OOBE\BypassNRO=1 → 可选数据盘（GPT+NTFS 4096）
→ 卸盘 → Set-VMFirmware -FirstBootDevice 指硬盘
```

灌盘后各机一次开机即进入 unattend 的 specialize→oobeSystem，`SkipMachineOOBE/SkipUserOOBE/AutoLogon` 生效，
三台均达到 **`IMAGE_STATE_COMPLETE`**（历史最坑的 `IMAGE_STATE_UNDEPLOYABLE` 未发生），随后由 PowerShell Direct 接管配置。

三种机制的全部脚本与应答文件：`<实验室根>\Staging\scripts\`、`<实验室根>\Staging\unattend\`、`<实验室根>\Staging\payload\`（**未提交进 PCMig Git**）。

---

## 1. 实际安装介质

| 角色 | 介质 | 索引 | 说明 |
|---|---|---|---|
| LAB-DC01 | `20348.1787.230607-0640.fe_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso` | **2** | Windows Server 2022 Standard Evaluation (Desktop Experience) |
| LAB-SRC01 | `zh-cn_windows_11_business_editions_version_24h2_updated_oct_2025_x64_dvd_a30b900a.iso` | **3** | Windows 11 专业版 24H2 |
| LAB-DST01 | 同上（**同一张 ISO、同一索引**） | **3** | 同上 |

两个 ISO 均在 `<ISO 镜像目录>\ISO\`（**未复制到 Lab Root**，按人工裁定避免几十 GB 重复占用）。
`<ISO 镜像目录>\ISO\Windows.iso` 经鉴定位 **Windows 10 客户端 19041 分支 ESD 消费级介质（Index 1 家庭版/2 家庭单语言/3 教育版/4 专业版）——不是 Windows Server**，本阶段未使用。
Windows 10 22H2 商业版 ISO（`...d4e92df7.iso`，Index 3 = 专业版）按 D4 保留，供后续「旧系统→新系统」兼容性基线，第一轮未混入。

Server 2022 Eval 的 install.wim 完整索引：1 = Standard Evaluation / **2 = Standard Evaluation (Desktop Experience)** / 3 = Datacenter Evaluation / 4 = Datacenter Evaluation (Desktop Experience)。

## 2. Server ISO 来源与 SHA256

| 项 | 值 |
|---|---|
| fwlink | `https://go.microsoft.com/fwlink/p/?LinkID=2195280&clcid=0x804&culture=zh-cn&country=CN` |
| 解析后 URL | `https://software-static.download.prss.microsoft.com/dbazure/988969d5-f34g-4e03-ac9d-1f9786c66756/20348.1787.230607-0640.fe_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso` |
| 来源主机 | `software-static.download.prss.microsoft.com`（**Microsoft PRSS，官方**） |
| 本地路径 | `<ISO 镜像目录>\ISO\20348.1787.230607-0640.fe_release_svc_refresh_SERVER_EVAL_x64FRE_zh-cn.iso` |
| 字节数 | **5,478,957,056**（与官方 `Content-Length` 逐字节相符） |
| SHA256 | **`ced78fe5817f8ac3fcb2b741499ad26c66dba203a66499aec3953ebf93947cf6`** |
| Edition / Build / Arch / Language | Standard Evaluation (Desktop Experience) / 20348.1787 / x64 / zh-CN |

**未使用任何第三方来源**（sysin.org、网盘、论坛、重打包、精简版一律未采用）。
**真实性交叉证据**（不下载任何第三方工具）：挂载 ISO 后用系统内置 WinVerifyTrust（`Get-AuthenticodeSignature`）验证内嵌安装程序 ——
`F:\sources\setup.exe` = **`Valid`**，签名者 `CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`，
颁发者 `CN=Microsoft Windows Production PCA 2011`，指纹 `FE51E838A087BB561BBB2DD9BA20143384A03B3F`，
时间戳签名者 `CN=Microsoft Time-Stamp Service`；`setuphost.exe`、`bootmgr.efi` 同为 `Valid`。
（ISO 容器本身不签名，故以「官方 PRSS 主机 + 字节数逐字节相符 + 内嵌程序官方签名 Valid」三者合证。）
证据：`33-server-iso-provenance.txt`、`32-iso-inventory-sha256.tsv`、`11-iso-image-index-enumeration-server2022.txt`。

## 3. Hyper-V 版本

| 项 | 值 |
|---|---|
| 宿主 | DESKTOP-5CSN7PT（WORKGROUP） |
| Hyper-V PowerShell 模块 | **2.0.0.0** |
| 逻辑处理器 / 内存容量 | 32 / 34,104,659,968 B（≈31.76 GB） |
| `VirtualMachinePath` / `VirtualHardDiskPath` | **`<实验室根>\VMs`**（已从 `C:\ProgramData\...` 改到 Lab Root） |
| 平台裁定 | **Hyper-V（D2）**；VMware Workstation **未安装、不安装、不删除、不升级**（`<ISO 镜像目录>\VM\...26H1\` 保持原样） |
| 关键限制 | **无 `Send-VMKey`** ⇒ 见 §0；`Set-VMMemory` 固定内存只可传 `-DynamicMemoryEnabled $false -StartupBytes <n>`（同时传 Minimum/Maximum 会报「需要启用动态内存」） |

## 4. Virtual Switch / NAT

| 对象 | 配置 |
|---|---|
| vSwitch | **`PCMig-Lab-Switch`**，type = **Internal**，`AllowManagementOS = True`，无物理网卡绑定 |
| 宿主 vEthernet | `vEthernet (PCMig-Lab-Switch)` = **10.77.0.1/24**，ifIndex 55，prefixOrigin = Manual |
| NAT | **`PCMig-Lab-NAT`**，`InternalIPInterfaceAddressPrefix = 10.77.0.0/24`，`Active = True` |
| 未改动 | `Default Switch`（172.31.160.1/20，ICS 出口）原样保留 |

**隔离保证**：Internal 交换机**无物理网卡绑定**，绝不桥接真实公司 LAN；实验网段 `10.77.0.0/24` 与宿主真实网段（192.0.2.0/24 等）无交集；未加入、未连通任何真实公司域。

## 5. 三 VM 最终配置

| VM | 角色 | Gen | vCPU | RAM | OS 盘 | 数据盘 | vTPM | Secure Boot | 自动检查点 | 网卡 |
|---|---|---|---|---|---|---|---|---|---|---|
| **LAB-DC01** | AD DS + DNS | 2 | 2 | 4 GB **固定** | 80 GB 动态 VHDX | — | 否 | On（MicrosoftWindows） | **False** | PCMig-Lab-Switch（MAC 00155DFC4304） |
| **LAB-SRC01** | 旧电脑 / Source SMB 服务端 | 2 | 4 | 6 GB **固定** | 100 GB 动态 VHDX | Data1 **120 GB** → **D:** | 是 | On | **False** | PCMig-Lab-Switch（MAC 00155DFC4302） |
| **LAB-DST01** | 新电脑 / PCMig 执行端 | 2 | 4 | 6 GB **固定** | 100 GB 动态 VHDX | Data1 **160 GB** → **D:**；Data2 **20 GB** → **F:** | 是 | On | **False** | PCMig-Lab-Switch（MAC 00155DFC4303） |

全部 VHDX 位于 `<实验室根>\VMs\<VM>\`；`FirstBootDevice = File`（硬盘）；
数据盘 NTFS **AllocationUnitSize = 4096**（卷标 `Data-SRC01` / `Data-DST01` / `Fault-DST01`）。
证据：`12-vm-create.txt`、`31-vm-configuration.txt`。

## 6. Windows 版本 / Edition / Build

| VM | Caption | Build | EditionID | InstallationType | ImageState | 安装时间 |
|---|---|---|---|---|---|---|
| LAB-DC01 | Microsoft Windows Server 2022 Standard Evaluation | **20348.1787**（21H2） | ServerStandardEval | Server | **IMAGE_STATE_COMPLETE** | 2026-10-02 22:54:54 |
| LAB-SRC01 | Microsoft Windows 11 专业版 | **26100.6899**（24H2） | Professional | Client | **IMAGE_STATE_COMPLETE** | 2026-10-02 22:30:38 |
| LAB-DST01 | Microsoft Windows 11 专业版 | **26100.6899**（24H2） | Professional | Client | **IMAGE_STATE_COMPLETE** | 2026-10-02 22:30:45 |

- SRC01 / DST01 **同一 ISO、同一 Edition、同一 Build、同一补丁基线**（D4 要求全部满足）。
- 两点如实说明（不美化）：
  1. 客户端注册表 `ProductName` 显示 `Windows 10 Pro`，这是 Windows 11 介质沿用旧值的历史遗留字段；`Caption`/`EditionID`/`Build` 均为 Windows 11 专业版 24H2，**不是** Windows 10。
  2. 三台均**未联网执行 Windows Update**（实验室无出网需求），补丁基线 = 介质自带基线；DC 与两台客户端各自基线内一致。
- `dcdiag`/系统未报告任何待重启标记（CBS / WU 均为 False）。
- 证据：`43-os-identity-and-cold-state.txt`。

## 7. Domain：corp.test

| 项 | 值 |
|---|---|
| Forest / Domain | **`corp.test`**（新建森林，与真实公司域无任何关系） |
| NetBIOS | **CORP** |
| Domain Mode | Windows2016Domain |
| PDC Emulator | `LAB-DC01.corp.test` |
| DC | **`LAB-DC01`**（`DomainRole = 5`） |
| OU | **`LabUsers`**、**`LabComputers`**、**`LabGroups`**（均 `ProtectedFromAccidentalDeletion = False`） |
| 域组 | **`PCMig-Lab-Users`**（成员：`user01`、`user02`） |
| DNS 转发器 | `223.5.5.5`、`8.8.8.8`（**仅 DC 使用**；`corp.test` 由 LAB-DC01 权威解析） |
| 真实 GPO | **一个都未导入**；Firewall = ON、UAC = ON、SMB 安全行为 = 系统默认，未做任何「为了连得上」的放宽 |

## 8. Accounts

| 账号 | 类型 | 说明 |
|---|---|---|
| `CORP\user01` | 普通 Domain User | 仅属 `PCMig-Lab-Users`；**不在 Domain Admins**（已断言 `False`） |
| `CORP\user02` | 普通 Domain User | 仅属 `PCMig-Lab-Users`；**初始有效业务权限与 user01 完全相同**（user02 = 「误操作 persona」，不是「权限不同 persona」） |
| `CORP\admin` | Domain Admin | 仅用于实验域管理 / 故障注入 / 恢复，不参与普通用户 persona 测试 |

密码处理（按补充 §7 与 §11）：
- 全部为**实验室专用随机密码**（22 位），**未复用**宿主密码 / Microsoft 账户密码 / 公司域密码 / 任何个人密码。
- 唯一落盘位置：`<实验室根>\Staging\.secrets\lab-credentials.json`，**ACL 去继承，仅 `BUILTIN\Administrators`（F）+ `NT AUTHORITY\SYSTEM`（F）**。
- **未打印**在任何终端输出中（一律经 `$env:PCMIG_LAB_PWD` 在内存/子进程环境变量传递）；**未进入** Evidence 包、未进入桌面交付包、未进入 Git、未进入本报告。
- 传送给 guest 时只作为 powershell 子进程的环境变量存在，会话结束即移除。

## 9. IP / DNS

| 主机 | IP | Mask | Gateway | DNS |
|---|---|---|---|---|
| LAB-DC01 | **10.77.0.10** | 255.255.255.0 | 10.77.0.1 | 127.0.0.1（自身，权威） |
| LAB-SRC01 | **10.77.0.21** | 255.255.255.0 | 10.77.0.1 | **10.77.0.10**（仅 DC，**未设任何公共 DNS 为首选**） |
| LAB-DST01 | **10.77.0.22** | 255.255.255.0 | 10.77.0.1 | **10.77.0.10**（仅 DC，**未设任何公共 DNS 为首选**） |

卷标映射：SRC01 `Data-SRC01` → **D:**；DST01 `Data-DST01` → **D:**、`Fault-DST01` → **F:**。

## 10. Domain Health

- 服务：`NTDS` / `DNS` / `NETLOGON` / `DFSR` / `KDC` / `W32Time` **全部 Running**。
- `dcdiag /q` 输出 36 行，**全部为单 DC 实验室的预期噪声**，逐条归类：
  1. `警告: LAB-DC01 没有作为时间服务器进行播发` + `没有通过测试 Advertising` —— 单 DC、无上游时间源时的正常行为；
  2. `SYSVOL 共享后的最近 24 小时内出现了警告或错误事件` + `没有通过测试 DFSREvent` —— 单 DC 无复制伙伴的正常行为；
  3. `netprofm 服务因下列错误而停止`（0xC0001B6F）、`Printer Extensions and Notifications 服务标记为交互服务`（0xC0001B76）—— `没有通过测试 SystemLog` 的两个已知无害事件；
  4. `警告: DcGetDcName(TIME_SERVER) 调用失败，错误为 1355` —— 同上，无上游时间源。
  **没有任何一条指向 AD DS / DNS / SYSVOL 的实际损坏。**
- SYSVOL / NETLOGON：本机共享存在且路径存在（`C:\Windows\SYSVOL\sysvol`、`...\corp.test\SCRIPTS`），`\\LAB-DC01\SYSVOL` 与 `\\LAB-DC01\NETLOGON` **reachable = True**。
- 三台主机**短名与 FQDN 双解析全部正常**：`LAB-DC01`/`LAB-SRC01`/`LAB-DST01` 与 `*.corp.test` 均解析到各自 10.77.0.x。
  （各机的**自身**名字解析出 IPv6 链路本地属正常假象；对**其它**主机的解析均为正确的 10.77.0.x。）
- 时间同步：`w32tm` 引用源 `VMTP`（Hyper-V 时间同步集成服务），三机一致，Kerberos 未报时间偏差。
- 证据：`40-sanity-DC01.txt`、`40-sanity-SRC01.txt`、`40-sanity-DST01.txt`。

## 11. Secure Channel

| 主机 | 结果 |
|---|---|
| LAB-SRC01 | **`secure channel: True`**（`DomainRole = 1`） |
| LAB-DST01 | **`secure channel: True`**（`DomainRole = 1`） |
| LAB-DC01 | `n/a on a domain controller (validated with dcdiag / AD below)` —— 域控自身不适用该检查，由 §10 的 dcdiag + AD 查询代替 |

两台客户端的计算机对象均位于 **`OU=LabComputers,DC=corp,DC=test`**（加域时用 `-OUPath` 指定）。
`\\LAB-DC01\SYSVOL` 与 `\\LAB-DC01\NETLOGON` 在 SRC01 / DST01 上 **以 `CORP\admin` 身份 reachable = True**。
（说明：以**本地**账号 `labadmin` 访问 SYSVOL 被拒绝是正常安全行为，故该检查显式以域账号执行。）

## 12. SRC D Share

| 项 | 值 |
|---|---|
| 共享名 | **`D`**（即 `\\LAB-SRC01\D`） |
| 路径 | `D:\` |
| 类型 | **普通共享，不是 `D$`**（也未做成隐藏/特殊共享，未假装不可发现） |
| Share ACL | `CORP\admin` = **Full**；`CORP\PCMig-Lab-Users` = **Read** |
| 对照事实 | **`D$` 同时存在 = True** —— 这正是「普通共享 `D` 才有意义」的原因 |
| 描述 | `PCMig lab source share (normal share, not an admin share)` |

**真实域用户读测试**（不是声明，而是实际访问）：
- `CORP\user01` 访问 `\\LAB-SRC01\D` → **access OK**，顶层可见 `Desktop, Documents, Downloads, Pictures, Work`，**能读取 seed 文件**。
- `CORP\user02` → **access OK**，同上。

## 13. Share / NTFS Baseline

Source（SRC01 `D:\`）：
- 目录结构：`D:\Desktop`、`D:\Documents`、`D:\Downloads`、`D:\Pictures`、`D:\Work`（代表公司用户数据形态）。
- NTFS 基线：`CORP\PCMig-Lab-Users:(OI)(CI)(RX)`、`CORP\admin:(OI)(CI)(F)`。
- 当前**只有少量环境验证文件**（5 个 seed `env-check-*.txt`），**未生成任何大型数据**（本阶段禁止）。
- 本阶段只建立「**正常成功基线**」：**没有**对整盘做任何 Deny，权限故障留待后续用专门测试目录制造。
- 关于 Known Folder 依赖：本阶段**未构建任何 Folder Redirection / 复杂 GPO**。经只读审计，PCMig 的迁移范围由用户勾选的**目录树/整盘路径**决定（`MainViewModel.CollectCustomSelections()` 与 robocopy `/XD /XF`），**不依赖 Windows Known Folder API**，因此 `Desktop/Documents/...` 仅作为「看起来真实的目录形态」存在，不需要重定向；此结论已在此明确指出，未擅自增加 GPO。

## 14. DST Target Baseline

| 项 | 值 |
|---|---|
| 目录 | **`<发版工作副本>Target\`**（本地目标根，符合已确认的「PCMig 在 DST 本机写 TargetRoot」拓扑） |
| ACL | `CORP\user01:(OI)(CI)(M)` + `CORP\user02:(OI)(CI)(M)` + `CORP\admin:(OI)(CI)(F)` + `NT AUTHORITY\SYSTEM:(OI)(CI)(F)`（另有继承的 `BUILTIN\Users:ReadAndExecute`） |
| 目标盘 | D: 卷标 `Data-DST01`，NTFS，AllocationUnitSize 4096，160 GB（free ≈159.9 GB） |
| 当前内容 | **空**（0 项），已准备好接受迁移 |

**写权限是实测的，不是推断的**：以 `CORP\user01` 与 `CORP\user02` **各自的身份**在该目录内真实创建并删除文件，两者均 **write OK + delete OK**（见下节方法说明）。
**未建立任何「DST Share Permission」** —— 本拓扑中 DST 侧目标是**本地 NTFS 写入**，不存在「目标端共享权限」这一层，故不做无意义的对应设置。

关于「以用户自身令牌做本机写入」的方法说明（如实记录，含未走通的路）：
- `Start-Process -Credential` 在 PowerShell Direct 会话中**不可用**（`exit=-1073741502` = `0xC0000142 STATUS_DLL_INIT_FAILED`，会话无法为第二个用户构建令牌）。
- `Register-ScheduledTask -User/-Password` + `Start-ScheduledTask`：任务注册成功但**从未运行**（`lastResult=267011` = `SCHED_S_TASK_HAS_NOT_RUN`）；`schtasks /Create /RU CORP\user0x` 给出**真实原因**：「任务已注册，但无法启动。该任务主体需要启用批登录特权。」实测 `SeBatchLogonRight = *S-1-5-32-544,*S-1-5-32-551,*S-1-5-32-559`（**仅** Administrators / Backup Operators / Performance Log Users），即**域用户默认没有「作为批处理作业登录」特权**。
- **决策：不为了取证而放宽该特权** —— 那正是「接近正常企业安全状态的 Windows」要保留的东西。改用**该用户自己的令牌经 SMB 访问同一目录**（NTFS ACL 在同一目录上被同样地求值）完成取证：临时共享 `PCMigTargetWriteTest` → 以 `net use` 建立会话 → 写文件 → 读回 → 删除 → 删除临时共享。
- 事后校验：临时共享 **已移除**（存在性 = False）、残留 `_write-test-*` = **0**、`<发版工作副本>Target` 条目 = **0**。

## 15. Fault Disk

| 项 | 值 |
|---|---|
| 盘 | LAB-DST01 **`F:`**，20 GB 动态 VHDX（`Fault-DST01`），NTFS AllocationUnitSize 4096 |
| 目录 | **`<故障注入目标盘>\`** |
| ACL | 与正常目标**完全相同**（user01/user02 = Modify，admin/SYSTEM = FullControl） |
| 本阶段状态 | **已格式化、已建目录、已设 ACL、未填充**（free ≈19.9 GB） |
| 用途（后续） | 空间不足 / 迁移途中磁盘被吃满 / 小容量存储故障 |

## 16. PCMig Build / Deployment

| 项 | 值 |
|---|---|
| 仓库 | `<仓库根>` |
| 分支 / HEAD | `feature/winui-v0.5.0` / **`d1aefb2fb8b36b135cf136afc71532c3450226a8`（d1aefb2，= D7 指定稳定基线）** |
| 构建 | `dotnet publish src\PCMig.WinUI\PCMig.WinUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false`（SDK 10.0.401）→ **rc=0** |
| 产物 | `<实验室根>\Staging\pcmig-publish\winui`：**528 文件 / 224.3 MB**；主程序 `PCMig.WinUI.exe` **288,768 B**，SHA256 `20ca24445d40d1445131c6f398fbfc7acb129b62bf5c7845a211b13c6004dd0f` |
| 清单 | `pcmig-build-manifest-winui.tsv`（528 行，逐文件 SHA256+字节）、`pcmig-build-manifest-cli.tsv`、`pcmig-build-info.json` |
| 部署位置 | **`LAB-DST01:<本机交付副本>\`**（经 PowerShell Direct 拷贝，39.0 s） |
| 部署校验 | guest 侧重算 SHA256 → `35-pcmig-deployed-manifest.tsv`（530 行）；主程序存在且 288,768 B |
| 未使用 | **未用 Debug 构建、未用 CLI 替代、未用旧 GUI 替代**；CLI 仅作为构建产物保留（`...\pcmig-publish\cli`，200 文件 / 72.1 MB），**不作为正式用户入口** |
| 运行状态 | **已部署，未启动**；本阶段**未执行** Connect / Add Share / Start / Stop / Resume / Verify / Repair / Export |
| 已知事项 | `worktreeClean = False` 的唯一原因：仓库多出**未跟踪**文件 `PCMig\docs\qa\PCMig-Three-VM-Readiness-Report-20261002.md`（Phase 0 交付物）+ 本报告；**没有任何已跟踪文件被改动**，本阶段未对产品代码做任何修改 |

## 17. Checkpoint

自动检查点**全部关闭**；仅建立 3 个明确基线（每台 VM 各 3 个，共 9 个）：

| 基线 | 建立时间 | 含义 |
|---|---|---|
| **BASE-OS** | 22:55:20 / 22:55:25 / 22:55:30 | 三台 OS 安装完成、尚未进入域配置 |
| **DOMAIN-BASELINE** | 23:16:32 / 23:16:32 / 23:16:33 | AD/DNS + 客户端加域 + 账号 + 共享 + 权限全部完成 |
| **TEST-BASELINE** | 23:38:01 / 23:38:01 / 23:38:02 | **本阶段交付态**：冷连接清理之后、三台干净关机时建立 |

- 三台均在**关机状态**下建立检查点；建立后已重新启动为 **Running**（当前三台 Running，`dynamic=False`，4/6/6 GB）。
- 证据：`34-checkpoints-DOMAIN-BASELINE.txt`、`34-checkpoints.txt`、`41-coldclean-final.txt`。

**整套实验室的恢复办法（写入报告即视为移交）**：
1. 恢复必须**三台一起**回滚到**同一个**基线（`TEST-BASELINE` 优先，必要时退到 `DOMAIN-BASELINE`）。Hyper-V 检查点与 AD 的 generation ID 只有在三台一致时才能保证 AD 数据库一致。
2. **禁止单独回滚 LAB-DC01**（会造成客户端计算机账户密码与安全通道失配）。
3. **检查点不是 AD 备份**；不要把「有检查点」当作 AD 恢复方案。
4. 若 `DOMAIN-BASELINE` 已被严重污染，**优先整套重建实验室**，而不是强行修复被污染的 AD。
5. 无计划、未经授权不得回滚；回滚前先确认没有正在进行的测试。

## 18. Cold State 是否已恢复

**是。** 最终一次 cold cleanup 在关机建基线之前完成（`41-coldclean-final.txt`），并在重启后复验：

| 主机 | 出站映射 `net use` | 缓存凭据 `cmdkey` | 入站 SMB 会话 | DNS 客户端缓存 |
|---|---|---|---|---|
| LAB-DC01 | 0 | 0 | **2**（见下注） | 11 |
| LAB-SRC01 | 0 | 0 | **0** | 9 |
| LAB-DST01 | 0 | 0 | **0** | 11 |

- 清理动作：关闭测试留下的 ID 会话、断掉临时映射、`cmdkey /delete`、`Clear-DnsClientCache`、删除 `_write-test-*` 临时文件（三台 exit=0）。
- **DST01 → SRC01 的读测试热连接已被关闭**：清理前 SRC01 有 1 个来自 `10.77.0.22` 的入站会话，清理后为 0。
- 注：DC01 上剩余的 2 个入站会话来自 `10.77.0.21 (CORP\LAB-SRC01$)` 与 `10.77.0.22 (CORP\LAB-DST01$)`，是**计算机账户**为访问 SYSVOL/NETLOGON 产生的正常域登录流量（SMB 3.1.1），**不是测试员造成的用户热连接**，重启后也不会消失，属于域环境应有状态。
- DNS 缓存条目是**重启后普通解析活动**在内存中重新填充的（磁盘上不保留），基线检查点本身捕获的是**干净关机状态**。
- 结论：交付态**没有**「提前建立的 PCMig SMB 会话」，**没有**「测试员提前访问共享造成的热连接」。

## 19. Infrastructure Sanity 结果

| 检查项 | DC01 | SRC01 | DST01 |
|---|---|---|---|
| 身份 / 域 | `corp.test`，DomainRole=5 | `corp.test`，DomainRole=1 | `corp.test`，DomainRole=1 |
| Secure Channel | n/a（域控，由 dcdiag 代替） | **True** | **True** |
| IP / DNS / GW | 10.77.0.10 / 127.0.0.1 / .1 | 10.77.0.21 / **10.77.0.10** / .1 | 10.77.0.22 / **10.77.0.10** / .1 |
| 三主机短名+FQDN 解析 | 正常 | 正常 | 正常 |
| 防火墙（Domain/Private/Public） | True/True/True | True/True/True | True/True/True |
| SYSVOL / NETLOGON 可达 | True（本机共享） | True（as CORP\admin） | True（as CORP\admin） |
| 共享 / 目标 | SYSVOL+NETLOGON 共享在册 | 共享 `D`→`D:\`，ACL 正确，`D$`=True，5 seed | `\\LAB-SRC01\D` 以 user01/user02 **读成功** |
| 目标权限 | — | — | user01/user02 **写入+删除实测成功** |
| 卷 | — | D: 120 GB free 119.9 | C: 77.2 / D: 159.9 / F: 19.9 GB free |
| PCMig | — | — | **deployed: True**（未启动） |

**本阶段未启动 PCMig**，上述全部为 Windows 层面的基础设施验证。

## 20. 仍然存在的阻塞项

**没有阻塞三 VM 环境存在的阻塞项。** 环境建设已完成，可交付。

作为「后续阶段的前置条件」而非「环境阻塞」记录（原 B1/B5/B6 已解除）：

| 编号 | 状态 | 说明 |
|---|---|---|
| B1 | **已解除** | 官方 Server 2022 Eval ISO 已获取并通过三重来源/签名证据 |
| B2 | **已裁定** | 平台 = Hyper-V（VMware 不引入） |
| B3 | **已裁定** | Lab Root = `<实验室根>\`；`PCMIG_LAB_ROOT`（User 作用域）已改为该值 |
| B5 | **已解除** | 已提权枚举 install.wim 索引；数据盘 NTFS 簇大小已确定为 **4096** |
| B6 | **已解除** | 在一次性提权会话下，Hyper-V 写操作（建交换机/NAT/VM/VHDX/检查点）全部实测成功 |
| **B4** | **DEFERRED（Route B Gate）** | 见 §21 |
| **B7** | **DEFERRED（Route B Gate）** | 见 §22 |

## 21. B4 状态

**`DEFERRED TO ROUTE B GATE`**
- 210 万文件生成器尚未创建（现有脚本上限 5 万小文件 ≈25.6 MB，`lab\fill-e.ps1` 只到 ≈8.4 GB）。
- 原因分类：**Route B Precondition（大规模数据集）**，**不是** VM Environment Build 的阻塞项。
- 本阶段**未创建生成器、未生成百万文件、未修改仓库**。
- 进入 Route B 前必须单独授权开发 `Large File Count Dataset Generator` 并启用。

## 22. B7 状态

**`DEFERRED TO ROUTE B GATE`**
- v0.5.0 WinUI 中「超大数据模式（专家模式）」开关被显式禁用（`src\PCMig.WinUI\Views\Step2SelectDataPage.xaml:290-296` `IsEnabled="False"`；`MigrationSessionViewModel.ExpertModeIsWiredInPhaseA => false`），属已登记的 A12 未接线项。
- 原因分类：**Route B Precondition（UI 能力）**，**不是**环境阻塞、**不阻塞 Route A**。
- 正式约束：Route B **必须走当前 WinUI**；**禁止**用 CLI 或旧 GUI 绕过，**禁止**后台强开 `ExpertMode`。
- 进入 Route B 前必须单独启动并单独授权的任务：**WinUI ExpertMode / 超大数据模式接线**。本阶段**未修改任何代码**。

## 23. 是否已经具备正式 UI 黑盒测试环境

**是（READY FOR UI TEST EXECUTION）。** 逐项对照：

| 前置条件 | 状态 |
|---|---|
| 三台 Gen2 VM 就绪并运行 | ✅ DC01 / SRC01 / DST01 全部 Running |
| 实验域 `corp.test` 与 DNS 健康 | ✅ dcdiag 仅剩预期噪声 |
| 域名账号与权限 persona | ✅ `user01`/`user02`（同权）+ `admin`（域管） |
| 源端普通共享 `D` 与 NTFS 基线 | ✅ 已真实读通 |
| 目标端本地 NTFS 与故障盘 | ✅ 已真实写通 + `<故障注入目标盘>` 就绪 |
| PCMig 稳定基线产物部署到 DST | ✅ `d1aefb2` self-contained WinUI Release → `<本机交付副本>`（未启动） |
| 冷连接状态 | ✅ 无残留用户热连接 |
| 可恢复基线 | ✅ `TEST-BASELINE`（三台一致）+ 恢复办法见 §17 |
| 证据已归档 | ✅ `<实验室根>\Evidence\Phase1-EnvironmentBuild-20261002-221412\`（35 个文件） |

**注意**：`READY` 指「环境可以在人工批准后立即开始 UI 黑盒测试」，**不等于**已获准开始测试。本阶段到此强制停止。

---

## 附录 A — 本阶段遇到的坑与最终修法（给后续接手者）

| # | 现象 | 根因 | 修法 |
|---|---|---|---|
| 1 | `Set-VMMemory` 报「设置最大值、最小值和缓冲区设置需要启用动态内存」 | 固定内存时不能同时给 Minimum/Maximum | 只传 `-DynamicMemoryEnabled $false -StartupBytes <n>` |
| 2 | `deploy-os.ps1` 定位挂载盘失败：`mounted VHD disk object not found within timeout` | 挂载后 VHDX 文件路径在 `Get-Disk.Location`，**不在** `Get-Disk.Path` | 按 `BusType -eq 'File Backed Virtual'` + `GetFullPath($_.Location)` 匹配；超时 90 s。另 `Get-VHD` 必须带 `-Path` |
| 3 | DC01 灌盘 `bcdboot` **rc=193** `Failure when attempting to copy boot files.` | 宿主 bcdboot（Win11 26300）在 Secure Boot 开且 DB 含 2023 PCA 时强制用 Ex 二进制：`BFSVC: Using Ex bins because SB is on, BFSVC_USE_EX_BINS is set, and 2023 PCA is in DB.` → 找不到 `Windows\boot\EFI_EX\bootmgfw_EX.efi` → `Error code = 0xc1`。Server 2022 镜像**没有** `EFI_EX` | **用被灌镜像自带的 `<OS>:\Windows\System32\bcdboot.exe`**（rc=0）；脚本改为先试镜像自带、再退回宿主。（`$env:BFSVC_USE_EX_BINS='0'` 实测**无效**） |
| 4 | 阶段脚本空等 ISO 到超时 | `.ps1` 无 BOM 且含中文路径字面量 ⇒ PS 5.1 按 GBK 解码 ⇒ 路径乱码（**项目发布规则 8**） | 全部 `.ps1` 一律 UTF-8 **带 BOM**；复核中文行已正确解码 |
| 5 | 客户端角色脚本 `Copy-Item` 报 `Cannot find path ...` / `GUEST_SCRIPT_EXIT=-196608` | 脚本名写成 `role-LAB-SRC01-phase1.ps1`（不存在）；正确名为 `role-SRC01-phase1.ps1` | `run-guest-script.ps1` 增加源文件存在性检查（`exit 6`）+ guest 侧 `Test-Path` 校验（`exit 7`） |
| 6 | 加域后用**裸** `labadmin` 做 PowerShell Direct **5 分钟超时**（exit=3） | 加域后裸名会被当域名账号解析 | 一律用 **`.\labadmin`**（域控用 `CORP\Administrator`）；DC01 客户端回退链已写进脚本 |
| 7 | 以域用户身份做本机写入无法取证 | 域用户默认**无**「作为批处理作业登录」特权（`SeBatchLogonRight` 仅 Administrators / Backup Operators / Performance Log Users）；`Start-Process -Credential` 在 PD 会话中报 `0xC0000142` | **不放宽安全策略**；改用该用户自己的令牌经 SMB 访问同一目录取证（见 §14） |
| 8 | `New-PSDrive -Credential` 产生畸形路径 `\\host\share\host\share\...` | 本场景下 PSDrive root 解析异常 | 改用 `net use` 建立会话 + 直接使用 UNC 路径 |
| 9 | 优雅关机 3 分钟仍未 Off | 实验 VM 关机较慢 | 记录为事实后强制关机，并在基线文件中注明 |
| 10 | `Initialize-Disk` 自动产生 17 KB MSR + 脚本再建 16 MB MSR（两个） | DISM/GPT 行为 | 无害，不处理 |

## 附录 B — 本阶段脚本与配置清单（均**未**提交进 PCMig Git）

宿主侧 `<实验室根>\Staging\scripts\`：
`deploy-os.ps1`（离线灌盘，含分区/apply/bcdboot/unattend 注入/离线 OOBE 注册表/数据盘）、
`run-guest-script.ps1`（PD 投送并执行 guest 脚本，密码只经环境变量）、
`phase1-dc01-stage.ps1`（等 ISO + 验 SHA256 + 记出处 + 枚举 install.wim + 灌盘 DC01 + 启动）、
`phase1-domain-stage.ps1`（BASE-OS 检查点 → DC01 提升 → 客户端加域）、
`phase1-final-stage.ps1`（部署 PCMig → sanity → cold cleanup → DOMAIN-BASELINE → 收集证据）、
`phase1-baseline-finalize.ps1`（最终 cold cleanup → 关机 → TEST-BASELINE → 重启）、
`checkpoint-baseline.ps1`（通用「关机+建基线+写恢复说明」）、
`collect-evidence.ps1`（只读采集 30/31/32/34）、
`build-pcmig.ps1`（构建 + 清单 + build-info）、
`deploy-pcmig.ps1`（部署到 DST 并重算 SHA256）、
`run-sanity.ps1` + `sanity-guest.ps1`（基础设施 sanity）、
`cold-cleanup.ps1`（冷连接清理）、
`diag-writetest.ps1`（写测试根因诊断）、`writetest-smb.ps1`（用户令牌写取证）、`osinfo-guest.ps1`（OS 身份 + 冷状态复验）。

guest 角色脚本：`role-DC01-phase1.ps1`、`role-DC01-phase2.ps1`、`role-SRC01-phase1.ps1`、`role-SRC01-phase2.ps1`、`role-DST01-phase1.ps1`、`role-DST01-phase2.ps1`。

应答文件与载荷 `<实验室根>\Staging\unattend\`、`<实验室根>\Staging\payload\`：
`unattend-Server.xml`、`unattend-Client.xml`、`SetupComplete.cmd`、`firstlogon.cmd`（模板均为 ASCII + 占位符；含密码的**渲染版**只存在于 `<实验室根>\Staging\.secrets\`）。

状态文件：`<实验室根>\Staging\phase1-state.json`（按补充 §11 建立，**只作提示，不压过真实系统状态**）。

## 附录 C — 证据清单（`<实验室根>\Evidence\Phase1-EnvironmentBuild-20261002-221412\`）

`10-hyperv-network-and-host.txt`、`11-iso-image-index-enumeration.txt`、`11-iso-image-index-enumeration-server2022.txt`、`12-vm-create.txt`、
`30-hyperv-network-and-host.txt`、`31-vm-configuration.txt`、`32-iso-inventory-sha256.tsv`、`33-server-iso-provenance.txt`、
`34-checkpoints.txt`、`34-checkpoints-DOMAIN-BASELINE.txt`、`35-pcmig-deployed-manifest.tsv`、
`40-sanity-DC01.txt`、`40-sanity-SRC01.txt`、`40-sanity-DST01.txt`、`41-coldclean-DC01.txt`、`41-coldclean-SRC01.txt`、`41-coldclean-DST01.txt`、`41-coldclean-final.txt`、
`42-target-write-test.txt`、`43-os-identity-and-cold-state.txt`、
`deploy-os-LAB-DC01.log`、`deploy-os-LAB-SRC01.log`、`deploy-os-LAB-DST01.log`、
`pcmig-build-info.json`、`pcmig-build.log`、`pcmig-build-manifest-winui.tsv`、`pcmig-build-manifest-cli.tsv`、
`phase1-domain-stage.log`、`phase1-final-stage.log`、`phase1-LAB-DC01.log`、`phase1-LAB-SRC01.log`、`phase1-LAB-DST01.log`。

**全部证据文件不含任何密码。** 凭据唯一存在于 `<实验室根>\Staging\.secrets\`（ACL 仅 Administrators+SYSTEM）。

## 附录 D — 宿主资源与空间

| 卷 | 总量 | 剩余（本阶段结束时） |
|---|---|---|
| C: | 400.0 GB | 214.8 GB |
| D: | 735.0 GB | 653.0 GB |
| E: | 771.6 GB | **635.4 GB**（Lab Root + 3 VM VHDX + PCMig 产物 + Server ISO 已占用） |

宿主机：DESKTOP-5CSN7PT / i9-13950HX 24C32T / 31.76 GB RAM / 单块 2 TB NVMe；
三台 VM 合计固定占用 10 vCPU / 16 GB RAM，宿主余量充足。
**未重启宿主机**，未修改宿主安全策略，未创建任何常驻提权通道（D9：不引入 `lab-admin-*`）。