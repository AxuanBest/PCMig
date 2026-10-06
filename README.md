# PCMig — 企业内网 Windows 数据与用户环境迁移工具

> 在新电脑上运行 PCMig，输入旧电脑的 IP 或计算机名，通过 SMB 把旧机器共享盘上的数据完整拉到新机器。
> 全程可暂停、可恢复、可验证、可追溯。

**直拉模式（Direct Pull）** —— 旧机不装代理、不落中转盘、不上云，数据只在新旧两台机器之间流动。

**当前稳定版本：v0.5.1**（2026-10-06） ｜ 开发：郑子轩（[Axuanbest](https://github.com/Axuanbest)）

---

## 目录

- [1. 项目简介](#1-项目简介)
- [2. 当前稳定版本](#2-当前稳定版本)
- [3. 适用场景与定位](#3-适用场景与定位)
- [4. 主流程](#4-主流程)
- [5. 技术栈](#5-技术栈)
- [6. 前端形态](#6-前端形态)
- [7. 传输引擎：双通道 Robocopy](#7-传输引擎双通道-robocopy)
- [8. 暂停、恢复、断点续传与异常恢复](#8-暂停恢复断点续传与异常恢复)
- [9. 可信度体系：Diagnostics / Flight Recorder / Loss Ledger](#9-可信度体系diagnostics--flight-recorder--loss-ledger)
- [10. 三 VM 测试框架](#10-三-vm-测试框架)
- [11. 构建与测试](#11-构建与测试)
- [12. 快速开始](#12-快速开始)
- [13. CLI 命令一览](#13-cli-命令一览)
- [14. 项目结构](#14-项目结构)
- [15. 更新日志](#15-更新日志)
- [16. 版本、Tag 与发布流程](#16-版本tag-与发布流程)
- [17. 安全边界](#17-安全边界)
- [18. 已知限制与未验证事项](#18-已知限制与未验证事项)
- [19. 路线图](#19-路线图)
- [20. 许可与致谢](#20-许可与致谢)

---

## 1. 项目简介

PCMig 是一个面向**企业内网换机场景**的 Windows 迁移工具。它在**新电脑**上运行，通过 SMB/UNC 直接把**旧电脑**上的数据拉过来，不需要在旧电脑安装任何常驻代理，也不需要先拷到移动硬盘或中转服务器。

它解决的问题不是"能不能拷过去"，而是换机迁移里真正会出事的三件事：

- **漏传与静默失败** —— 扫描残缺、路径引号被吃掉、排除规则不一致导致的整目录漏传，工具必须自己发现并且拒绝"看起来成功"。
- **进度不可信** —— 界面显示 99.9%、实际目标盘已写满；或者反过来，明明在传却显示 0%。进度必须是**可被来源约束**的，而不是估算出来的。
- **中断之后说不清状态** —— 断网、断电、进程被强杀之后，"已经传了多少"必须有权威依据，不能靠猜。

对应地，PCMig 的核心设计取舍是：

| 取舍 | 做法 |
|------|------|
| 权威状态从哪来 | **只追加的 Receipt** 是权威来源；`job-state.json` 损坏可由 Receipt 重建 |
| 进度能不能信 | 字节数只接受**已确认落盘**的来源；预分配通道（`/Z`、`/J`）的目标文件长度**永不**计入 |
| 失败能不能兜住 | 排除口径由**策略矩阵**统一驱动扫描 / 传输 / 验证三方，避免三方不一致 |
| 出事能不能复盘 | 内置 **Diagnostics** 子系统（Flight Recorder + Loss Ledger + 证据包导出） |

---

## 2. 当前稳定版本

**v0.5.1**（2026-10-06）—— 可信度紧急修正版，相对 v0.5.0 的实质变化：

1. **目标盘写满不再虚报**：修复传输编排器的事件委托注册顺序缺陷（跨线程写 `ObservableCollection` 导致多播订阅链被空 `catch` 静默中断），该缺陷会让字节回冲与盘满 / I/O 风暴双熔断**同时失效**，从而在磁盘写满时把进度虚报成 `completedBytes = 45097156608` / `percent = 99.9`。
2. **旧 WPF 前端退出正式交付物**：正式用户交付树只允许 `PCMig.WinUI.exe` 与 `pcmig-cli.exe` 两个可执行入口，发版脚本带硬门禁拦截历史经典入口文件。**WPF 源码仍保留在仓库中作为历史实现与回退参考**（见 [第 6 节](#6-前端形态)）。
3. **发版链自身缺陷修复**：WinUI 标题栏版本徽章曾漏改，v0.5.1 起把版本声明点纳入发版自查（含反向自查）。

**v0.5.0**（2026-10-06）为并列保留的正式版本：引入 WinUI 3 全新界面、Diagnostics 可信度体系与沉浸式传输进度。两个版本各自有独立的 release commit 与 annotated tag，历史安装包全部保留、互不覆盖。

完整版本历史见 [`docs/更新日志.md`](docs/更新日志.md)；各版本的**证据核对**（正式安装包、更新日志条目、公司环境测试章节、是否留有精确源码快照）见 [`docs/历史版本索引.md`](docs/历史版本索引.md)。

---

## 3. 适用场景与定位

- **目标环境**：企业内网、Windows 域或工作组环境下的 PC 换机（旧机 → 新机）。
- **数据面**：旧机 **D 盘等数据盘完整迁移**；系统盘只迁移**用户 Profile**与经批准的配置项，不动系统目录与整盘 ACL。
- **传输链路**：SMB / UNC（`\\主机\D$` 或已发布的共享），支持 IP 直连、DNS 不可达但 IP 可达、共享被撤销、权限不足、路径过长、锁文件等真实企业网络故障形态。
- **不依赖**：不要求旧机安装客户端，不要求域管理权限即可开始（凭据由操作者提供），不依赖公网或云服务。

> 面向的是"内网 + 管理可控 + 需要一个能说清楚状态"的迁移，而不是跨公网的大规模自动化。

---

## 4. 主流程

```
新电脑 (Windows 11, 运行 PCMig)
   │  ① 输入旧机 IP / 计算机名 + 凭据
   │  ② Preflight → Scan → Plan → (人工确认) → Transfer → Verify → Report
   ▼
\\旧机\D$  ──SMB──▶  Robocopy 双通道  ──▶  新机目标路径
```

| 阶段 | 做什么 | 关键产物 |
|------|--------|----------|
| **Preflight** | 连通性、共享可达性、权限、目标盘剩余空间预检 | `preflight.json` |
| **Scan** | 按策略矩阵扫描源根，建立对象清单与文件统计 | 扫描统计（含分流阈值之上的大文件数） |
| **Plan** | 每个一级目录 = 一个迁移对象，产出对象级计划与排除口径 | `plan.json` |
| **Transfer** | 逐对象执行 Robocopy 双通道，支持暂停 / 停止 / 重试 | `job-*.jsonl`、robocopy 原始日志、**Receipt** |
| **Verify** | L1 文件数 + 字节比对；L2 抽样哈希校验 | 验证结果（抽样 0 项会明确告警） |
| **Report** | 生成可读报告（可 `--open` 直接打开） | `report.*` |

计划产出后需要**人工确认**才开始传输（CLI 用 `--yes` 可跳过确认）。

---

## 5. 技术栈

| 层 | 选型 |
|----|------|
| 语言 / 运行时 | **C# / .NET 8** |
| 桌面前端 | **WinUI 3**（Windows App SDK，`net8.0-windows10.0.19041.0`） |
| 命令行前端 | .NET 8 控制台（`pcmig-cli.exe`） |
| 传输引擎 | **Robocopy**（多通道、多 pass 编排） |
| 日志 | **Serilog**（应用级 + 任务级 + robocopy 原始日志，文本 + 结构化 JSONL） |
| 诊断 | 自研 **PCMig.Diagnostics**（事件总线、分段写入、保留策略、规则引擎、证据包导出） |
| 配置 | **YAML** 策略矩阵（`matrix/migration-matrix.yaml`） |
| 测试 | **xUnit**（两个测试工程，见 [第 11 节](#11-构建与测试)） |
| 打包 | **Inno Setup**（安装包）+ 目录形态 Portable |

---

## 6. 前端形态

**v0.5.1 起，正式发布物只包含 WinUI 3 前端。**

| 交付物 | 说明 |
|--------|------|
| `PCMig.WinUI.exe` | **正式前端**：WinUI 3 四页向导（连接 → 选择 → 执行 → 结果）+ 沉浸式传输进度 |
| `pcmig-cli.exe` | 命令行入口，脚本化与自动化场景使用 |
| ~~`PCMig-classic.exe`~~ | **已退出交付物**（v0.5.0 及以前作为"经典界面·回退"随包发布） |

- 交付树（本地构建输出，见第 14 节）与安装后的工作副本都带**硬门禁**：出现 `PCMig-classic.exe` / `PCMig.exe` 会当场中止发版。
- **旧 WPF 源码（`src/PCMig.Gui/`）保留在仓库中**，作为历史实现与回退参考，仅"不再 publish、不再进安装包"，并未删除。
- 界面视觉与动效遵循仓库内的 PMML 规范文档（`docs/PCMig-Visual-Motion-Language.md` 等）。**装饰层（光波 / 粒子 / 涟漪 / 光晕）永远不得影响进度真值、字节数、回执与任务状态。**

---

## 7. 传输引擎：双通道 Robocopy

源根下的每个一级目录是一个**迁移对象**，逐对象执行；每个对象内部按需跑多个 pass：

| 通道 | 触发条件 | 实际命令行要点 |
|------|----------|----------------|
| **Bulk**（默认主通道） | 对象内未超过分流阈值的文件 | `/MT:<线程数>`；开启分流时附加 `/MAX:<阈值-1>` |
| **Large**（大文件通道） | 对象内存在 ≥ 阈值（默认 **512 MB**）的文件 | `/MIN:<阈值>`；模式见下 |
| **RootFiles** | 源根目录下散落的文件 | `/MT`，网络链路可加 `/J` |

**Large 通道的三种模式**（`--large-channel`）：

- `auto`（默认）：首次用 `/MT + /J`（SMB 高延迟链路上无缓冲 I/O，吞吐显著高于单线程 `/Z`）；**对象级重试（第 2 次尝试起）自动退回 `/Z`**，优先保证"文件内部可续传"。
- `restartable`：恒定使用 `/Z`（单线程、缓冲 I/O，文件内部可断点续传）。
- `multithreaded`：恒定使用 `/MT + /J`。

> 为什么不是"大文件一律 `/Z`"：真实生产实测下，恒定 `/Z` 单线程吞吐只有手工 `robocopy /MT:32` 的约 1/3（30 MB/s vs 90–137 MB/s）。因此默认走 `/MT + /J`，把 `/Z` 留给真正需要续传的重试场景。
>
> `/J` 只在链路任一端是网络路径时启用：本机 NVMe 的 A/B 实测显示 `/J` 会让本地吞吐掉到 `/Z` 的 0.5–0.6 倍。

**其它引擎特性**：对象级重试与退避、Unicode 日志、排除口径由策略矩阵统一驱动、目标盘写满 / I/O 风暴熔断。

---

## 8. 暂停、恢复、断点续传与异常恢复

| 能力 | 实现方式 |
|------|----------|
| **协作式暂停**（`pause`） | `pause.request` 文件驱动，当前文件传完后干净停下 |
| **立即暂停 / 停止**（`stop`） | 终止当前 robocopy 进程；已完成部分由 Receipt 记录，可续传 |
| **恢复**（`resume`） | 不指定 `--job` 时自动选择最近一个未完成任务；按通道分别计算**可信续传基线** |
| **断点续传** | **Receipt（只追加）为权威状态**；`job-state.json` 损坏可由 Receipt 重建；大文件通道退回 `/Z` 后可从文件内部续上 |
| **异常恢复** | 断网、断电、进程被强杀后，按"通道是否可能预分配"判定目标文件长度是否可信（预分配通道**永不**采信长度），避免把"预分配成全长"误当成"已传完" |
| **失败可见** | 失败与暂停状态在界面上显著呈现；抽样验证 0 项会明确告警而不是静默通过 |

> 设计原则：**"目标文件存在且长度正确"不等于"内容已落盘"**。任何会预分配最终长度的通道（`/Z`、`/J`）都不能作为进度分子来源。

---

## 9. 可信度体系：Diagnostics / Flight Recorder / Loss Ledger

`src/PCMig.Diagnostics/` 是独立于业务逻辑的**自诊断子系统**，目标是在没有人盯着的情况下，事后也能回答"当时到底发生了什么、有没有丢东西"。

| 组件 | 作用 |
|------|------|
| **DiagnosticHub** / `DiagnosticRuntime` | 事件总线与运行时装配：活动（Activity）、计量（Meters）、健康（Health） |
| **JsonlSegmentWriter** / `SegmentRecovery` | 分段结构化 JSONL 落盘与损坏段恢复 |
| **Flight Recorder** | 环形飞行记录：保留最近一段窗口的完整事件，崩了也能取到最后现场 |
| **Loss Ledger** | **丢失台账**：显式记录"哪些事件没有落盘、丢了多少"，让"没记到"本身可见，而不是假装完整 |
| **SequenceLedger** | 事件序号连续性核验（配合 Loss Ledger 判断缺口） |
| **ByteBudget** / `FanOutStage` / `BoundedBranch` | 背压与预算控制，保证诊断自身不会拖垮数据面 |
| **RetentionManager** / `RedactionPolicy` | 保留策略与脱敏策略（凭据等敏感字段不落盘） |
| **RuleEngine** / `EvidenceRules` / `FeedbackRules` | 基于证据的规则引擎：把事件序列判定为事件（Incident）与结论 |
| **DiagnosticPackageExporter** | 导出可交付的诊断证据包（诊断中心 / 自诊断场景使用） |

配套的可核对产物（由测试每次运行重新生成，属于"可复验而非口头声明"）：

- [`docs/诊断系统实施-事件覆盖矩阵.md`](docs/诊断系统实施-事件覆盖矩阵.md) —— 扫描生产源码里的事件发布点，统计 Produced / Deep-only / Reserved / Retired。
- [`docs/诊断系统实施-配置项接线审计.md`](docs/诊断系统实施-配置项接线审计.md) —— 逐项统计运行时源码里的**真实消费者**，只有 `Active` 才代表该配置真的生效。

架构与实施记录见 [`docs/方案-诊断中心与自诊断架构.md`](docs/方案-诊断中心与自诊断架构.md) 与 `docs/诊断系统实施-*.md`。

---

## 10. 三 VM 测试框架

真实迁移的故障形态（盘满、断网、共享撤销、域不可达、路径过长、锁文件）无法在单机上稳定复现，因此仓库内保留了一套**三 VM 实验框架**：

```
lab/three-vm/
├── README.md            框架总览：VM 快照、宿主共享、canonical 路径、执行纪律
├── configs/             VM 配置、网络与磁盘、宿主共享配置快照
├── docs/                框架文档索引 + 正式证据 canonical 路径表
├── scenarios/           场景脚本（场景 G/H、暂停恢复、停止恢复、case04/05/07/08、数据集生成、打包）
├── runner/              UI 自动化 Runner（窗口激活、InvokePattern 点击、滚动、取值、OCR）
├── scripts/             用例矩阵库与夹具生成原语（lib-cases.ps1 / labfile.ps1）
└── evidence-template/   证据包模板与最小构成要求
```

| 能力 | 说明 |
|------|------|
| 环境形态 | 域控（DC01）+ 源机（SRC01）+ 目标机（DST01），Hyper-V 虚拟机 + 宿主 SMB 共享 |
| 故障注入 | 断网 / 断电、DNS 不可达但 IP 可达、权限不足、共享撤销、目标盘写满、路径过长、锁文件、域不可达 |
| 数据生成 | `scenarios/make-dataset.ps1`（大文件与目录骨架）、`scripts/lib-cases.ps1`（大规模小文件树）——**数据本体按需重建，不长期占盘** |
| 小型回归数据 | `lab/smoke-data/`（约 57 MB）：覆盖小文件、多层目录、中文名、空目录、只读、合法长路径、较大样本、特殊扩展名 |
| 证据采集 | 场景脚本自带 UIA 取证与自检标记；证据包模板见 `evidence-template/` |

> 框架的定位是**保留测试能力**：VM 本体、场景定义、Runner、生成器与证据模板长期保留；大体积合成测试数据按需重新生成，不作为仓库资产入库。

---

## 11. 构建与测试

### 环境要求

- Windows 10 1809+ / Windows 11
- .NET SDK 8.0
- 构建 WinUI 前端需要 Windows App SDK 对应的 Windows SDK 组件

### 构建

```powershell
# 编译整个解决方案
dotnet build PCMig.sln -c Release

# 构建前建议先结束占用文件的进程，否则可能出现 MSB3021 / MSB3027 假失败
Get-Process PCMig.WinUI, PCMig.Cli, PCMig.Gui, PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
```

### 测试

```powershell
dotnet test tests/PCMig.Core.Tests/PCMig.Core.Tests.csproj -c Release
dotnet test tests/PCMig.Diagnostics.Tests/PCMig.Diagnostics.Tests.csproj -c Release
```

### 当前验证基线（v0.5.1）

| 项目 | 结果 |
|------|------|
| `dotnet build PCMig.sln -c Release` | **0 error / 4 warning** |
| `tests/PCMig.Core.Tests` | **569 / 569 通过**（失败 0、跳过 0） |
| `tests/PCMig.Diagnostics.Tests` | **382 / 382 通过**（失败 0、跳过 0） |

4 条已知警告为构建与测试基础设施提示，非功能性缺陷：`WMC1506` ×3（WinUI XAML 中可静态化的动态资源引用）与 `xUnit2031` ×1（测试断言写法建议）。

> ⚠️ **发版 / 发布 / 安装包制作只能通过 `tools/release.ps1` 执行**，不要手工 `dotnet publish` 或直接调用 Inno Setup —— 手工路径会绕过版本声明自查、口令残留扫描与逐文件哈希校验。

---

## 12. 快速开始

### 方式一：安装包

运行 `PCMigSetup-0.5.1.exe`，按向导安装（默认安装到 `%ProgramFiles%\PCMig`）。

### 方式二：Portable 绿色目录

交付区的 `Portable` 目录（本地构建输出，**不入 Git 仓库**）是自包含目录版，复制到目标机器直接运行 `PCMig.WinUI.exe` 即可（WinUI 需要与其原生组件同目录）。

### 方式三：命令行

```powershell
# 预检：连通性 / 共享 / 权限 / 目标盘空间
pcmig-cli.exe preflight --host OLD-PC --user OLD-PC\Administrator

# 建任务（含预检 + 扫描 + 计划）
pcmig-cli.exe new --host OLD-PC --source \\OLD-PC\D$ --target D:\ --user Administrator

# 执行（计划需人工确认；--yes 跳过确认）
pcmig-cli.exe run --job JOB-20260913-0001

# 查看进度（--watch 持续刷新）
pcmig-cli.exe status --job JOB-20260913-0001 --watch

# 验证与报告
pcmig-cli.exe verify --job JOB-20260913-0001 --level 2
pcmig-cli.exe report --job JOB-20260913-0001 --open

# 一条龙（预检 + 扫描 + 计划 + 传输）
pcmig-cli.exe quick --host OLD-PC --target D:\ --user OLD-PC\Administrator
```

---

## 13. CLI 命令一览

| 命令 | 说明 |
|------|------|
| `preflight` | 预检（连通性 / 共享 / 权限 / 空间） |
| `new` | 创建迁移任务（含预检 + 扫描 + 计划） |
| `quick` | 一条龙（预检 + 扫描 + 计划 + 传输） |
| `run` | 执行任务 |
| `pause` | 协作式暂停 |
| `stop` | 立即暂停（终止当前 robocopy，可续传） |
| `resume` | 恢复任务（不带 `--job` 自动选最近未完成任务） |
| `status` | 查看进度（`--watch` 持续刷新） |
| `verify` | 验证传输结果（`--level 2` 抽样哈希） |
| `report` | 生成报告（`--open` 直接打开） |
| `list` | 列出所有任务 |
| `changelog` | 查看内置更新日志（别名 `log` / `history`） |

常用选项：`--host` `--user` `--password` `--source` `--target` `--job` `--jobs` `--level`
`--threads` `--largemb` `--large-channel` `--matrix` `--xd` `--xf` `--yes`
`--allow-incomplete-scan` `--quiet` `--watch` `--kind` `--bench` `--open` `--help`

---

## 14. 项目结构

```
PCMig.sln
├─ src/
│  ├─ PCMig.Core/                   核心引擎（Preflight / Scan / Plan / Transfer / Verify / Report / Jobs / Logging / Matrix）
│  ├─ PCMig.WinUI/                  ★ 正式前端：WinUI 3 四页向导 + 沉浸式传输进度
│  ├─ PCMig.Cli/                    命令行入口（发布名 pcmig-cli.exe）
│  ├─ PCMig.Gui/                    旧 WPF 前端（历史实现 / 回退参考，v0.5.1 起不发布）
│  ├─ PCMig.Diagnostics/            自诊断子系统（Flight Recorder / Loss Ledger / 规则引擎 / 证据包导出）
│  └─ PCMig.Diagnostics.Abstractions/  诊断抽象层
├─ tests/
│  ├─ PCMig.Core.Tests/             核心引擎测试（569 例）
│  └─ PCMig.Diagnostics.Tests/      诊断子系统测试（382 例）
├─ matrix/
│  └─ migration-matrix.yaml         迁移策略矩阵（扫描 / 传输 / 验证三方统一排除口径）
├─ installer/
│  ├─ pcmig.iss                     Inno Setup 安装脚本
│  └─ pcmig.ico                     安装包与产品图标
├─ tools/
│  ├─ release.ps1                   ★ 唯一发版入口（版本写入 + 自查 + 打包 + 逐文件哈希校验）
│  ├─ stability-test.ps1            稳定性测试
│  ├─ uishot.ps1                    界面截图（带自检标记）
│  ├─ pcmiglab-vm.ps1 / l3-*.ps1    三 VM 实验环境编排
│  └─ md2txt.py                     Markdown → 纯文本（生成 docs/更新日志.txt）
├─ lab/
│  ├─ three-vm/                     三 VM 测试框架（见第 10 节）
│  └─ smoke-data/                   小型回归测试数据
└─ docs/                            使用说明、更新日志、发版铁律、测试报告、诊断与视觉规范
```

> 上面的目录树**只列出进入 Git 仓库的内容**。构建与发版产物目录、本地历史归档（`dist`、`archive` 等）均已在 `.gitignore` 中排除，**不随仓库发布、在 GitHub 上不存在**；仓库内另有 `.merkle-snapshot.json`（快照校验用）与 `AGENTS.md`（开发协作约定）。

---

## 15. 更新日志

**Changelog 入口：[`docs/更新日志.md`](docs/更新日志.md)**

- 记录从最初版本 `v0.1.0` 起的所有版本变更，按版本号从新到旧排列，附**版本号与发布日期对照表**。
- `docs/更新日志.txt` 由 `tools/md2txt.py` 从 Markdown 生成，随发布物交付。
- 应用内「更新日志」窗口读取的是**同一份** `更新日志.md`（作为嵌入资源随程序打包），因此界面里看到的版本记录与仓库文档一致。
- 命令行可用 `pcmig-cli.exe changelog` 直接查看。

> 发版纪律：**先写日志，再打包** —— 没写日志就打不出包（发版脚本会校验更新日志与使用说明，缺一项直接中止）。

---

## 16. 版本、Tag 与发布流程

### 版本与 Tag

- 每个正式版本对应一个 **annotated tag** `v<主>.<次>.<修订>`，指向该版本的 release commit。
- 当前并列保留的正式版本：

  | Tag | 版本 | 主题 |
  |-----|------|------|
  | `v0.5.1` | 0.5.1 | 可信度紧急修正：目标盘写满不再虚报 + 旧 WPF 前端退出交付物 |
  | `v0.5.0` | 0.5.0 | WinUI 3 全新界面 + Diagnostics 可信度体系 + 沉浸式传输进度 |

- `v0.5.0` 及其更早的正式安装包**全部保留在交付区**，作为历史版本存档；新旧版本**并列保留、互不覆盖**。
- 仓库中另有 **45 个历史基线 tag**（`v0.4.x-before-*`、`v0.5.0-before-*`、`winui-*` 等），属开发过程记录。**它们不标记其名字所指的状态**：49 个版本 / 开发历史 tag（含 `v0.5.0`、`v0.5.1`、`v0.4.5`、`v0.4.6` 与上述 45 个基线 tag）只落在 **18 个 commit** 上，其中 26 个 tag 共用同一个 v0.4.6 状态的 commit、7 个共用同一个 v0.5.0 前置基线 commit。另有 1 个**归档 tag** `legacy-installers-archive-20261006`（指向历史安装包归档说明节点，**不代表任何版本的源码快照**）⇒ 全仓库共 **50 个 tag / 19 个 commit**。**引用历史版本时请以交付区的历史安装包与 [`docs/历史版本索引.md`](docs/历史版本索引.md) 为准，不要以 tag 名为准。**
- ⚠️ 当前本地仓库**尚未配置远端**（`git remote` 为空），上述 tag 均为本地 tag；GitHub 托管与 push 由维护者手动完成。

### Historical releases / 历史发布与安装包

- **v0.5.1** —— 当前稳定版（信任修正版）。
- **v0.5.0** —— 首个正式 WinUI 3 大版本。
- **Legacy Installers Archive** —— 保存仍可确认的 v0.1.x – v0.4.x 历史安装包二进制；随附 `LEGACY-INSTALLERS-SHA256.txt` 指纹清单（版本 / 字节数 / SHA256 / `VersionInfo` / 快照状态 / 备注）。
- 三件事**分别记录、互不推断**：① 历史 Release **是否存在**；② 安装包**是否存在**；③ 是否存在**精确源码快照**（有安装包 ≠ 有源码快照，有更新日志 ≠ 有源码快照）。
- 详见 [`docs/历史版本索引.md`](docs/历史版本索引.md)（逐版本证据索引）与 [`docs/更新日志.md`](docs/更新日志.md)（逐版本说明）。

> 历史安装包属**发布产物**，以 Release asset 形式提供，**不写入 Git 历史**；也不为缺源码快照的历史版本补建版本 tag。

### 发布产物

| 产物 | 位置 | 命名 |
|------|------|------|
| 安装包 | 交付区根目录 | `PCMigSetup-<版本>.exe` |
| Portable | 交付区的 `Portable` 目录（本地构建输出，**不入 Git 仓库**） | 自包含目录（整棵 app 树） |
| 交付清单 / 哈希 | 随包 | 逐文件 SHA256 |

### 发布流程（摘要）

1. 先写三处：`docs/更新日志.md`（本版条目 + 版本对照表一行）、`docs/使用说明.txt`（本版段落）、源码中的版本声明。
2. 运行唯一发版入口：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File "tools/release.ps1" -Version X.Y.Z
   ```

3. 脚本会依次执行：版本一致性自查（含反向自查）→ 口令残留扫描 → 交付树硬门禁 → 编译与打包 → 逐文件 SHA256 校验（全部 MATCH 才算通过）→ 交付与安装包并列保留。
4. 发版后必须做**三验**（应用内版本、更新日志、安装与 CLI），详见 [`docs/发布流程.md`](docs/发布流程.md) 与 [`docs/发版铁律.md`](docs/发版铁律.md)。

---

## 17. 安全边界

**V1 明确不迁移**：凭据 / Cookie（与源机绑定的 DPAPI 数据）、驱动、安全软件、整盘 ACL、Windows 系统目录。

排除口径集中在 `matrix/migration-matrix.yaml`（含 `securityBlockedFileNames` 等），由扫描、传输、验证三方共用，避免"扫的时候排除、验证的时候又当成缺失"这类不一致。

**凭据处理**：口令不作为命令行参数长期留存，日志与诊断事件中的敏感字段按 `RedactionPolicy` 脱敏。

**误删保护**：修复清理逻辑对"大于分流阈值但本次不走可续传通道"的文件**跳过删除**，避免删了没人拷回来。

---

## 18. 已知限制与未验证事项

本节刻意把"已经验证过的"和"还没验证的"分开写，避免把未经实证的能力当成结论。

### 已验证

| 范围 | 证据 |
|------|------|
| 本机构建与测试 | `dotnet build PCMig.sln -c Release` → 0 error；Core 569 / 569、Diagnostics 382 / 382 |
| 实验室三 VM 环境 | 域控 + 源机 + 目标机（Hyper-V + 宿主 SMB 共享），已跑通盘满、断网 / 断电、共享撤销、权限不足、路径过长、锁文件、域不可达等故障注入场景（部分场景留有正式证据包） |
| 发版后三验 | 应用内版本与更新日志、随包文本日志、安装包与 CLI 的版本一致性 |

### 尚未验证 / 已知限制

1. **真实物理机端到端复验** —— v0.5.1 的结论来自本机与实验室三 VM，**尚未**在真实换机现场做完整端到端复验。
2. **人工视觉终验** —— 界面视觉与动效的最终验收由人工完成；仓库内没有、也不主张存在自动视觉验收流程。
3. **盘满场景的字节回冲路径** —— 实验室回归中该路径未被触发到（观测计数为 0），端到端实证仍待补。
4. **旧版本的系统兼容性** —— Legacy WPF 版本（v0.4.x 及更早）在技术栈上可能兼容更早的 Windows 版本，但**本仓库未对这些版本做过逐版本的真实操作系统兼容性测试**，因此不给出"某版本支持某系统"的断言。逐平台的**证据等级**（VERIFIED / LIKELY / UNVERIFIED / UNSUPPORTED）见 [`docs/历史版本索引.md`](docs/历史版本索引.md) 第六节；其中 Windows 7 在任何版本上都**只能作为 SMB 数据源**出现，从未被验证为 PCMig 的运行平台。
5. **平台限制** —— 依赖 Robocopy 与 WinUI 3，**仅支持 Windows**；不提供 Linux / macOS 支持。
6. **诊断预留事件** —— 事件覆盖矩阵中的 `Reserved` 事件是预留位，尚未接线生效，详见 [`docs/诊断系统实施-事件覆盖矩阵.md`](docs/诊断系统实施-事件覆盖矩阵.md)。
7. **发版基础设施** —— 发版脚本面向本机环境编写（Windows + PowerShell + Inno Setup + Windows App SDK），**未做 CI 化**，尚无自动化流水线。
8. **分发状态** —— 仓库尚未配置远端，也未建立 GitHub Releases；安装包与历史版本存档当前位于维护者的本机交付区，尚未公开发布。

---

## 19. 路线图

> 以下为**规划项，尚未实现**，不代表当前能力。当前能力以第 2 节版本说明与 `docs/` 文档为准。

- **V1 线（当前）**：数据面直拉、双通道传输、暂停 / 恢复 / 断点续传、L1/L2 验证、报告、Diagnostics 可信度体系。
- **后续（规划中）**：`State Plane` 用户环境与应用配置迁移（Outlook / 浏览器 Recipe、OneDrive KFM 策略）、零安装远程采集、旧系统源兼容模式、应用安装编排。

---

## 20. 许可与致谢

**许可**：本仓库当前**未附加开源许可证**，默认保留所有权利。如需在其它场景使用、分发或二次开发，请先联系作者。

**作者**：郑子轩（[Axuanbest](https://github.com/Axuanbest)）个人制作。

**第三方组件**：Robocopy（Windows 内置）、Serilog、Inno Setup、xUnit、Windows App SDK 等，各自遵循其原始许可。

---

*文档导航：[`docs/INDEX.md`](docs/INDEX.md)（文档总索引）· [`docs/使用说明.txt`](docs/使用说明.txt)（用户手册）· [`docs/更新日志.md`](docs/更新日志.md)（版本历史）· [`docs/发布流程.md`](docs/发布流程.md)（发版流程）*