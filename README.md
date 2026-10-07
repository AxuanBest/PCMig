# PCMig — Windows PC-to-PC 数据迁移工具

> 在新电脑上运行 PCMig，输入旧电脑的 IP 或计算机名，通过 SMB 将共享数据直接拉取到新电脑。
> 迁移流程可规划、可暂停、可恢复、可验证，并保留可追溯的任务与诊断证据。

**直拉模式（Direct Pull）** —— 旧机不装代理、不落中转盘、不依赖云服务；数据仅在新旧两台机器之间流动。
**当前稳定版本：v0.5.1**（2026-10-06）｜作者：[AxuanBest](https://github.com/AxuanBest)｜仓库：[AxuanBest/PCMig](https://github.com/AxuanBest/PCMig)

---

## 目录

- [1. 项目简介](#1-项目简介)
- [2. 设计目标与技术路线](#2-设计目标与技术路线)
- [3. 当前稳定版本](#3-当前稳定版本)
- [4. 适用场景与定位](#4-适用场景与定位)
- [5. 主流程](#5-主流程)
- [6. 技术栈](#6-技术栈)
- [7. WinUI 3 / PMML 前端架构](#7-winui-3--pmml-前端架构)
- [8. 传输引擎：Robocopy 多 Pass 编排](#8-传输引擎robocopy-多-pass-编排)
- [9. 任务持久化、暂停、恢复与异常恢复](#9-任务持久化暂停恢复与异常恢复)
- [10. Progress Truth / Committed Truth](#10-progress-truth--committed-truth)
- [11. Diagnostics / Flight Recorder / Loss Ledger](#11-diagnostics--flight-recorder--loss-ledger)
- [12. 可靠性防线](#12-可靠性防线)
- [13. 三 VM 与测试体系](#13-三-vm-与测试体系)
- [14. 构建与测试](#14-构建与测试)
- [15. 快速开始](#15-快速开始)
- [16. CLI 命令一览](#16-cli-命令一览)
- [17. 项目结构](#17-项目结构)
- [18. 更新日志](#18-更新日志)
- [19. 版本、Tag 与发布流程](#19-版本tag-与发布流程)
- [20. 安全边界](#20-安全边界)
- [21. 已知限制与未验证事项](#21-已知限制与未验证事项)
- [22. 路线图](#22-路线图)
- [23. 许可与致谢](#23-许可与致谢)

---

## 1. 项目简介

PCMig 是一个建立在 Windows 原生 **Robocopy** 之上的 PC 数据迁移工具。它通过图形化交互、迁移规划、任务持久化、异常恢复、结果验证、可信进度和诊断取证，把原本需要手工掌握的 Robocopy 迁移流程封装为一套可操作、可恢复、可验证的迁移流程。

Robocopy 负责数据搬运；PCMig 负责环境预检、源数据扫描、迁移计划、参数与 pass 编排、任务状态、暂停/停止/恢复、结果验证、Progress Truth 与 Diagnostics。

随着实际换机场景扩大，项目重点落在三类问题：

- **漏传与静默失败** —— 扫描残缺、路径引号、排除规则不一致等问题不能被包装成“成功”。
- **进度不可信** —— 目标文件存在或长度达到最终值，不等于数据已经真实完成。
- **中断后状态不明** —— 断网、断电或进程终止后，任务需要有可解释、可续传的依据。

| 设计问题 | 当前做法 |
|------|------|
| 已结算状态从哪来 | `Receipt` 记录已结算对象；`job-state.json` 是状态投影，可由 Receipt 重建 |
| 运行中进度从哪来 | 已结算事实加受约束的可信 checkpoint；预分配通道的目标长度不直接计入 |
| 三个阶段如何同口径 | 策略矩阵统一驱动扫描、传输与验证的排除规则 |
| 出事如何复盘 | Diagnostics 提供 Flight Recorder、Loss Ledger、Sequence Ledger 与证据包导出 |

> 本项目不宣称任意断电均无状态损失、所有迁移均无漏传，或所有故障矩阵均已完成验证。限制与未验证范围见[第 21 节](#21-已知限制与未验证事项)。

---

## 2. 设计目标与技术路线

PCMig 的“简单”不是减少底层能力，而是把用户原本需要手工处理的判断、参数和状态移入程序内部。

| 用户原本需要处理 | PCMig 负责 |
|------|------|
| 主机/IP/UNC 与凭据 | 连接、共享访问与 Preflight |
| 源数据结构 | `SourceScanner` 扫描与对象划分 |
| 迁移对象和目标映射 | `Planner` 生成计划 |
| `/MT`、`/J`、`/Z`、`/MIN`、`/MAX` | `RobocopyRunner` 多 pass 参数编排 |
| 排除规则 | `matrix/migration-matrix.yaml` 统一口径 |
| 重试、盘满、错误风暴 | `TransferOrchestrator` 编排与熔断 |
| 中断后的继续执行 | Job、Receipt、checkpoint、Resume |
| 结果判断 | `Verifier` 的 L1/L2 验证 |
| 故障复盘 | Diagnostics 事件、丢失台账与证据导出 |

```text
Preflight → Scan → Plan → 人工确认 → Transfer → Verify → Report

Robocopy：数据传输执行器
PCMig：规划 + 状态 + 真值 + 呈现 + Diagnostics
```

---

## 3. 当前稳定版本

**v0.5.1**（2026-10-06）是当前稳定版，也是相对 v0.5.0 的可信度紧急修正版。

- 修复目标盘写满时，UI 可能长期显示接近 `99.9%` 的可信度问题。
- 正式交付物只保留 `PCMig.WinUI.exe` 与 `pcmig-cli.exe`；旧 WPF 源码保留作历史/回退参考，但不再随包发布。
- 发版脚本纳入 WinUI 标题栏版本点的写入与反向检查。

**v0.5.0** 同为保留的正式版本，引入 WinUI 3 前端、Diagnostics 可信度体系与沉浸式传输进度。详细变更、根因和验证记录见[更新日志](docs/更新日志.md)；版本证据见[历史版本索引](docs/历史版本索引.md)。

---

## 4. 适用场景与定位

- **目标环境**：企业内网、Windows 域或工作组环境下的 PC 换机（旧机 → 新机）。
- **当前数据面**：通过 SMB/UNC 拉取数据卷、共享目录或经批准的源根；不迁移 Windows 系统目录和整盘 ACL。
- **传输链路**：支持 IP 直连、DNS 不可达但 IP 可达、共享撤销、权限不足、路径过长、锁文件等场景的预检、执行或错误呈现。
- **不依赖**：旧机不要求安装 PCMig，不要求公网或云服务；凭据由操作者提供。

> 当前定位是“受控内网环境中的数据迁移”，不是跨公网的大规模自动化、通用备份产品，也不是完整应用状态迁移工具。

---

## 5. 主流程

```text
新电脑（运行 PCMig）
   │ ① 输入旧机 IP / 计算机名与凭据
   │ ② Preflight → Scan → Plan →（人工确认）→ Transfer → Verify → Report
   ▼
\\旧机\共享  ── SMB ──▶  Robocopy 多 Pass 编排  ──▶  新机目标路径
```

| 阶段 | 做什么 | 关键产物 |
|------|--------|----------|
| **Preflight** | 连通性、共享、权限、目标盘空间与 Robocopy 能力检查 | `preflight.json` |
| **Scan** | 按策略扫描源根，建立对象清单与统计 | 扫描统计 |
| **Plan** | 生成对象级计划、目标映射和排除口径 | `plan.json` |
| **Transfer** | 逐对象执行 Robocopy pass，支持暂停、停止、重试 | Receipt、原始 Robocopy 日志 |
| **Verify** | L1 文件数/字节比对；L2 抽样哈希 | `verify-report.json` |
| **Report** | 生成可读报告 | `report/` |

计划产出后默认需要人工确认；CLI 的 `--yes` 可跳过确认。

---

## 6. 技术栈

| 层 | 选型 |
|----|------|
| 语言 / 运行时 | **C# / .NET 8** |
| 桌面前端 | **WinUI 3**（Windows App SDK，`net8.0-windows10.0.19041.0`） |
| 命令行前端 | .NET 8 控制台（`pcmig-cli.exe`） |
| 传输引擎 | Windows **Robocopy**（多 pass 编排） |
| 日志 | **Serilog**（应用、任务和 Robocopy 原始日志；文本与 JSONL） |
| 诊断 | 自研 **PCMig.Diagnostics**（事件、分段写入、规则与证据导出） |
| 配置 | YAML 策略矩阵（[`matrix/migration-matrix.yaml`](matrix/migration-matrix.yaml)） |
| 测试 | **xUnit**（Core 与 Diagnostics 两个测试工程） |
| 打包 | **Inno Setup** + Portable 目录交付 |

---

## 7. WinUI 3 / PMML 前端架构

### 正式前端与历史 WPF

| 入口 / 工程 | 状态 | 说明 |
|------|------|------|
| `PCMig.WinUI.exe` / `src/PCMig.WinUI` | **IMPLEMENTED / 正式前端** | WinUI 3 四页向导：连接、选择、执行、结果 |
| `pcmig-cli.exe` / `src/PCMig.Cli` | **IMPLEMENTED** | CLI 与脚本化入口 |
| `src/PCMig.Gui` | **历史/回退参考** | WPF 源码保留；不 publish、不进 Portable、安装包、开始菜单或 SHA256 清单 |

### PMML

**PMML（PCMig Material Motion Language）** 是 PCMig 自己的 UI 材质与动效规范，不是 Microsoft、Apple 或 WinUI 官方标准。其作用是冻结并审计当前界面的资源、层级、动效与已知偏差，而不是把视觉表现变成业务真值。

- `Themes/Colors.xaml`、`Materials.xaml`、`Typography.xaml`、`Controls.xaml`、`Motion.xaml`：颜色、材质、控件、文字与动效资源。
- `MotionDirector`：动效 token 与 Composition 动画。
- `PageTransitionCoordinator`：只管理页面可见性与 Composition translation，不重建页面、ViewModel 或 binding。
- `FluidZoomTransitionCoordinator`：入口与工具面之间的形态过渡。
- `InteractionFeedback`：Hover/Pressed 反馈；当前以 Translation 为主，不用 Scale 放大。
- `UniformScaleHost`：当前生产界面的主要整体缩放路径（`DesignSurface → UniformScaleHost → Viewbox/Uniform scaling`）。`ResponsiveLayoutController` 不是默认主路径。

PMML 的冻结规范、真实资源参数和偏差审计分别见：

- [`docs/PCMig-Visual-Motion-Language.md`](docs/PCMig-Visual-Motion-Language.md)
- [`docs/PMML-Implementation-Audit.md`](docs/PMML-Implementation-Audit.md)
- [`docs/PMML-Legacy-Deviations.md`](docs/PMML-Legacy-Deviations.md)

**KNOWN GAP**：Dark Theme 尚未完全闭合；部分 XAML 动画尚未完全遵循 Reduced Motion；部分现有资源与 PMML 的 L0–L4 语义仍有历史偏差。不得据此宣称“PMML 已完全实现”。

### Immersive Progress

沉浸式进度的实际数据链为：

```text
Robocopy / TransferOrchestrator
  → ProgressTruthSnapshot
  → MigrationSessionViewModel
  → ProgressPresentationCoordinator
  → ImmersiveTransferProgress / Bottom Bar
```

`ProgressPresentationCoordinator` 可平滑视觉呈现，但满足 `VisualProgress <= ConfirmedProgress`。`ImmersiveTransferProgress` 的 fill、particles、light band、halo、ripple 和 Compact/Full 表现只读取 `Value` 与进度状态，禁止写回 Receipt、committed bytes、job state 或 transfer phase。

---

## 8. 传输引擎：Robocopy 多 Pass 编排

每个源根的一级目录会成为迁移对象；对象内再按文件类型和阈值运行多个 pass。

| Pass | 触发条件 | 主要参数与职责 |
|------|----------|----------------|
| **Bulk** | 未超过分流阈值的常规文件 | `/MT:<线程数>`；开启分流时带 `/MAX:<阈值-1>` |
| **Large** | 大于等于阈值的文件（默认 **512 MB**） | `/MIN:<阈值>`；可选择 `auto`、`restartable`、`multithreaded` |
| **RootFiles** | 源根目录的散落文件 | 根层文件 pass，使用 `/MT`，网络路径可加 `/J` |

`--large-channel` 行为：

- `auto`（默认）：首次使用 `/MT + /J`；对象级重试从第 2 次起回退 `/Z`，优先文件内部续传。
- `restartable`：固定使用 `/Z`。
- `multithreaded`：固定使用 `/MT + /J`。

`RobocopyRunner` 同时负责路径引用、Unicode 日志、排除项、源/目标参数和进程树守护；`TransferOrchestrator` 负责对象顺序、重试、熔断、状态与 Receipt。

> 历史实测曾比较 `/Z` 与 `/MT` 的吞吐；性能会受网络、磁盘、文件尺寸和线程数影响，不将历史数字表述为通用承诺。

---

## 9. 任务持久化、暂停、恢复与异常恢复

一个 Job 的目录通常包含：

```text
job.json                 # 创建参数
plan.json                # 迁移计划
job-state.json           # 当前状态投影
preflight.json           # 预检结果
receipts/                # append-only 对象结算事实
logs/robocopy/           # Robocopy 原始日志
verify-report.json       # 验证结果
report/                  # 可读报告
job.lock                 # 互斥锁
```

| 原则 | 实现 |
|------|------|
| 已结算事实 | Receipt 是 append-only 记录；`Completed` Receipt 形成恢复基线 |
| 状态投影 | `job-state.json` 不是唯一真相；损坏时可按 Receipt 重建 |
| 异常识别 | 已存在但不可读取的 state 文件按 `Interrupted` 处理，而不是伪装成新任务 |
| Resume | 读取 Job 与 Receipt；`Completed` 对象跳过，`Failed` / `CompletedWithErrors` / `Interrupted` 重跑；Robocopy 增量跳过一致文件 |
| 并发保护 | `JobLock` 防止两个 Resume 同时操作同一 Job |
| 暂停达成 | 是否真暂停以 worker/进程实际停止为准，而不只看请求文件 |

Resume 不是恢复已经死亡的 Robocopy 进程，而是以 Job/Receipt 重建任务，并让 Robocopy 对目标执行增量补差。

**KNOWN LIMIT**：状态写入采用临时文件、替换与重试等机制，但当前不宣称具有 `FlushFileBuffers` 级别的绝对物理断电持久化保证；不能宣传“任何断电都绝不丢最后一次状态”。

---

## 10. Progress Truth / Committed Truth

PCMig 将业务真值、运行中 checkpoint 与 UI 呈现分开：

| 层 | 含义 |
|------|------|
| **Committed** | 已 `Completed` 且有持久化 Receipt 支撑的结算事实 |
| **可信 In-flight Progress** | 运行中经过约束的 checkpoint；不是预分配长度 |
| **Presentation Progress** | UI 的平滑显示；不得领先已确认进度 |

关键规则：

- 目标文件存在或达到最终长度，**不等于**内容已经完成。
- `/Z`、`/J` 等可能预分配的通道中，目标长度不能直接作为可信已完成字节来源。
- 运行中显示最高为 `99.9%`；只有任务 settled 后才允许进入 `100%`。
- 视觉层只可向真值靠拢，不能反向影响 Receipt、对象状态或传输 phase。

这套口径的目的不是让 UI “看起来更快”，而是避免失败或预分配场景被误呈现为完成。

---

## 11. Diagnostics / Flight Recorder / Loss Ledger

`src/PCMig.Diagnostics/` 不只是普通日志系统。它试图同时回答：**发生了什么、当前证据是否完整、Diagnostics 自己是否发生丢失。**

| 组件 | 作用 |
|------|------|
| **DiagnosticHub** / `DiagnosticRuntime` | 事件摄入、运行时装配、健康与停止语义 |
| **JsonlSegmentWriter** / `SegmentRecovery` | 分段 JSONL 落盘、尾部损坏恢复与 manifest |
| **Flight Recorder** | 有界环形现场记录 |
| **Loss Ledger** | 显式记录 retention overwrite、coalesced 与实际丢失 |
| **Sequence Ledger** | 只有连续 settled sequence 才形成覆盖水位 |
| **ByteBudget** / `FanOutStage` / `BoundedBranch` | 背压与资源预算，避免诊断拖垮数据面 |
| **RetentionManager** / `RedactionPolicy` | 保留与敏感字段脱敏 |
| **RuleEngine** / `EvidenceRules` / `FeedbackRules` | 基于事件、健康和完整性产生结论 |
| **DiagnosticPackageExporter** | 生成诊断证据包；manifest 与 summary 共享完整性判定 |

如果发生诊断事件丢失，目标是将“丢失本身”变成可观察事实，而不是宣称 Diagnostics 永远不会丢事件。

配套可复验文档：

- [`docs/诊断系统实施-事件覆盖矩阵.md`](docs/诊断系统实施-事件覆盖矩阵.md)
- [`docs/诊断系统实施-配置项接线审计.md`](docs/诊断系统实施-配置项接线审计.md)
- [`docs/方案-诊断中心与自诊断架构.md`](docs/方案-诊断中心与自诊断架构.md)

---

## 12. 可靠性防线

| 问题 | 风险 | 当前防线与边界 |
|------|------|------|
| **0/0 假成功** | Source / Target 都统计为 0 时，简单数值相等可能产生假 OK | 扫描完整性与数值相等分开；Complete / Partial / Unknown 进入验证和诊断证据链。**边界**：不能仅凭 `0 == 0` 宣称完整迁移。 |
| **预分配假进度** | 目标长度可能提前达到最终值 | Committed、可信 checkpoint 与 Presentation 分层；预分配通道长度不直接计入。 |
| **Scenario H：盘满近 99.9%** | UI 订阅方跨线程异常曾阻断后续 truth subscriber，导致回冲/熔断未执行 | truth subscriber 优先注册 + UI subscriber 隔离 + 订阅异常可观测。该事故与 `/Z` 预分配是不同根因；细节见[更新日志](docs/更新日志.md)。 |
| **`CompletedWithErrors` 伪装成功** | 部分对象失败却显示完整完成 | UI 区分 Completed 与 CompletedWithErrors，不强制 `100%`。**KNOWN LIMIT**：CLI 为历史兼容在 `CompletedWithErrors` 下仍可能返回 exit code `0`；自动化必须读取 phase、失败对象与 Receipt，不能只看 exit code。 |
| **同名共享换底** | Resume 从已变化的源继续复制 | Run / Resume / Repair 前校验 SourceIdentity，不符即拒绝继续。 |

可靠性来自可验证机制和对失败状态的显式呈现，不来自“绝对不会出错”的承诺。

---

## 13. 三 VM 与测试体系

真实迁移故障难以在单机稳定复现，仓库保留三 VM 框架：

```text
DC01（域控） + SRC01（源机） + DST01（目标机 / 运行 PCMig）
```

框架位置：[`lab/three-vm/`](lab/three-vm/)，其中包含 VM/网络配置、故障场景脚本、UIA/OCR Runner、数据集生成工具及 evidence template。

### VERIFIED（有明确历史实测记录）

- Scenario H 的目标盘写满可信度问题及其修复后的历史验证。
- 部分公司环境真实迁移、状态损坏/恢复、盘满等历史测试记录，见[`docs/测试报告-公司环境.md`](docs/测试报告-公司环境.md)。
- 发布验证基线中的 Core/Diagnostics 自动化测试，见[第 14 节](#14-构建与测试)。

### PARTIALLY VERIFIED（框架/脚本/部分场景存在，不等于全矩阵通过）

- 三 VM 框架、UIA Runner、OCR、故障脚本与证据模板均在仓库中。
- 场景设计覆盖断网、断电、DNS、权限、共享撤销、盘满、路径过长、锁文件、暂停/恢复等。

### NOT YET FULLY RE-RUN

- 当前 HEAD 的完整三 VM 故障矩阵。
- 全量 UIA 自动化与所有场景的最新 evidence package。
- 百万/两百万文件级压力验证。

> 测试框架存在不等于当前版本全矩阵全部通过。三 VM Phase 0 readiness 审计明确记录其本身未创建 VM、未运行任何 Case，见[`docs/qa/PCMig-Three-VM-Readiness-Report-20261002.md`](docs/qa/PCMig-Three-VM-Readiness-Report-20261002.md)。

---

## 14. 构建与测试

### 环境要求

- Windows 10 1809+ / Windows 11
- .NET SDK 8.0
- 构建 WinUI 前端需对应 Windows SDK / Windows App SDK 组件

### 构建

```powershell
# 编译整个解决方案
dotnet build PCMig.sln -c Release

# 若输出文件被运行中程序占用，先结束进程
Get-Process PCMig.WinUI, PCMig.Cli, PCMig.Gui, PCMig -ErrorAction SilentlyContinue | Stop-Process -Force
```

### 测试

```powershell
dotnet test tests/PCMig.Core.Tests/PCMig.Core.Tests.csproj -c Release
dotnet test tests/PCMig.Diagnostics.Tests/PCMig.Diagnostics.Tests.csproj -c Release
```

### 当前登记的 v0.5.1 发布验证基线

> 本 README 改版未重新运行构建或测试；以下是已登记的发布验证基线，不是本次文档修改的新测试结果。

| 项目 | 结果 |
|------|------|
| `dotnet build PCMig.sln -c Release` | **0 error / 4 warning** |
| `tests/PCMig.Core.Tests` | **569 / 569 通过**（失败 0、跳过 0） |
| `tests/PCMig.Diagnostics.Tests` | **382 / 382 通过**（失败 0、跳过 0） |

4 条已知警告为 `WMC1506` ×3 与 `xUnit2031` ×1。发布或制作安装包只能通过 [`tools/release.ps1`](tools/release.ps1)，不要手工 `dotnet publish` 或直接调用 Inno Setup。

---

## 15. 快速开始

### 安装包

运行 `PCMigSetup-0.5.1.exe`，按向导安装（默认 `%ProgramFiles%\PCMig`）。

### Portable

交付区的 `Portable` 目录是自包含目录版；复制至目标机器后运行 `PCMig.WinUI.exe`。Portable 为本地构建输出，不入 Git。

### CLI

```powershell
# 预检：连通性 / 共享 / 权限 / 目标盘空间
pcmig-cli.exe preflight --host OLD-PC --user OLD-PC\Administrator

# 创建任务：预检 + 扫描 + 计划
pcmig-cli.exe new --host OLD-PC --source \\OLD-PC\D$ --target D:\ --user Administrator

# 执行（计划默认需要确认；--yes 跳过）
pcmig-cli.exe run --job JOB-20260913-0001

# 查看进度
pcmig-cli.exe status --job JOB-20260913-0001 --watch

# 验证与报告
pcmig-cli.exe verify --job JOB-20260913-0001 --level 2
pcmig-cli.exe report --job JOB-20260913-0001 --open
```

---

## 16. CLI 命令一览

| 命令 | 说明 |
|------|------|
| `preflight` | 预检：连通性、共享、权限、空间 |
| `new` | 创建任务：预检、扫描、计划 |
| `quick` | 一条龙：预检、扫描、计划、传输 |
| `run` | 执行任务 |
| `pause` | 请求协作式暂停 |
| `stop` | 终止当前 Robocopy；任务可续传 |
| `resume` | 恢复任务；省略 `--job` 时选择最近未完成任务 |
| `status` | 查看进度；`--watch` 持续刷新 |
| `verify` | 验证结果；`--level 2` 为抽样哈希 |
| `report` | 生成报告；`--open` 直接打开 |
| `list` | 列出任务 |
| `changelog` | 查看内置更新日志（别名 `log` / `history`） |

常用选项：`--host`、`--user`、`--password`、`--source`、`--target`、`--job`、`--jobs`、`--level`、`--threads`、`--largemb`、`--large-channel`、`--matrix`、`--xd`、`--xf`。

---

## 17. 项目结构

```text
src/
  PCMig.Core/                    # 扫描、计划、传输、验证、Job 与状态
  PCMig.WinUI/                   # 正式 WinUI 3 前端与 PMML 资源
  PCMig.Cli/                     # 命令行入口
  PCMig.Gui/                     # 历史 WPF 实现（不进入正式交付）
  PCMig.Diagnostics/             # Diagnostics 运行时与证据导出
  PCMig.Diagnostics.Abstractions/# 诊断事件、载荷与抽象

tests/                           # Core 与 Diagnostics xUnit 测试
matrix/                          # 迁移策略矩阵
installer/                       # Inno Setup 脚本
tools/                           # 发布、稳定性、截图与实验室辅助工具
lab/                             # 三 VM 框架、场景、Runner 与证据模板
docs/                            # 用户、架构、QA、PMML 与发布文档
```

---

## 18. 更新日志

完整版本历史、用户可见变更、根因与修复说明见 [`docs/更新日志.md`](docs/更新日志.md)。

---

## 19. 版本、Tag 与发布流程

- 当前稳定版是 `v0.5.1`；`v0.5.0` 为并列保留的正式版本。
- 正式版本使用 annotated tag；已发布版本及其 tag 不可移动或覆盖。
- 历史开发基线 tag 不一定是精确正式版本源码快照；证据等级与安装包/源码快照对应关系见 [`docs/历史版本索引.md`](docs/历史版本索引.md)。
- 发布唯一入口：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools/release.ps1" -Version X.Y.Z
```

发版规则、五道闸门、版本声明点、交付物和 SHA256 校验见 [`docs/RELEASE.md`](docs/RELEASE.md)。

---

## 20. 安全边界

- 真实口令不得进入仓库、交付物或示例；发布脚本包含口令残留扫描。
- 诊断日志依据 `RedactionPolicy` 脱敏敏感字段。
- 不迁移凭据、Cookie、证书、驱动、安全软件、整盘 ACL 或 Windows 系统目录；规则见 [`matrix/migration-matrix.yaml`](matrix/migration-matrix.yaml)。
- 用户负责在有授权的网络、共享和数据范围内运行迁移。

---

## 21. 已知限制与未验证事项

1. **完整 State Plane 未实现**：Outlook/浏览器 Recipe、OneDrive KFM、应用重装编排、Pre-stage、Incremental Sync、Cutover 与完整应用状态迁移均不属于当前已实现能力。
2. **断电边界**：任务会依据 Receipt 重建，但不承诺任意时刻断电均保留最后一次状态写入。
3. **验证边界**：L2 是抽样哈希；抽样数为 0 会告警，不等于全量内容哈希。
4. **0/0 边界**：数值相等不自动等于扫描与证据完整；需结合完整性状态、验证和 Diagnostics 判读。
5. **CLI 自动化边界**：`CompletedWithErrors` 可能为兼容性返回 exit code `0`；脚本必须检查任务 phase、失败对象和 Receipt。
6. **前端边界**：Dark Theme、部分 XAML Reduced Motion 与 PMML 历史偏差仍待收口。
7. **实验室边界**：三 VM 框架与场景存在，但当前 HEAD 的全场景矩阵尚未完整复跑。
8. **平台边界**：依赖 Robocopy 与 WinUI 3，仅支持 Windows；没有 Linux/macOS 支持。
9. **发布基础设施**：发布流程面向 Windows 本机环境，尚未 CI 化。

---

## 22. 路线图

> 以下均为**规划中 / 未实现**，不代表当前能力。

- **当前 V1**：SMB 直拉数据迁移、多 pass Robocopy、暂停/恢复、L1/L2 验证、报告与 Diagnostics。
- **后续探索**：State Plane、Outlook/浏览器 Recipe、OneDrive KFM、零安装远程采集、旧系统源兼容、应用安装编排。

---

## 23. 许可与致谢

**许可**：本仓库当前未附加开源许可证，默认保留所有权利。如需在其他场景使用、分发或二次开发，请先联系作者。

**作者**：[AxuanBest](https://github.com/AxuanBest)

**第三方组件**：Robocopy（Windows 内置）、Serilog、Inno Setup、xUnit、Windows App SDK 等，各自遵循其原始许可。

---

*文档导航：[`docs/INDEX.md`](docs/INDEX.md)（文档总索引）· [`docs/使用说明.txt`](docs/使用说明.txt)（用户手册）· [`docs/更新日志.md`](docs/更新日志.md)（版本历史）· [`docs/RELEASE.md`](docs/RELEASE.md)（发布规则）· [`docs/发布流程.md`](docs/发布流程.md)（发版流程）*
