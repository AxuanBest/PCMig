# 诊断系统实施 — Reserved 事件分级（D6.2-B）

> 状态：**D6.2 阶段产物**（2026-10-01）。本文只做**分级判定**，不实现任何 producer。
> 依据：`docs\诊断系统实施-事件覆盖矩阵.md`（140 total / 105 Produced / 12 Deep-only / 23 Reserved）
> 与 `docs\诊断系统实施-D6.2真实验证报告.md`（本轮真实运行的一手证据）。
> 结论口径：**只有 A 级才允许在 Stage B 之前实现 producer；B 级保持 Reserved 到最后需要时；C 级保持 Reserved 即设计本身。**

---

## 一、分级判据（先定口径，再逐条判）

不按事件名判，按下面五条依赖判：

| 代号 | 判据 | 说明 |
|---|---|---|
| R1 | **Rule Engine 依赖** | 是否有规则/证据链必须消费它；缺失时规则只能写 `EvidenceIncomplete`。 |
| R2 | **Stage B 需要** | Stage B（真实大迁移）一旦出现对应故障，**没有它就无法正确解释**（不是"有用"，是"不能判"）。 |
| R3 | **故障注入需要** | 我们已授权的故障矩阵（权限/锁/消失/断开/空间/异常退出/暂停/恢复/强杀/导出取消）里是否会必然触发。 |
| R4 | **性能成本** | 事件速率 × 数据量是否会在 210 万文件规模上构成负载（Verbose 类尤其）。 |
| R5 | **是否已有等价证据** | 已有 Produced/Deep-only 事件或 UI 回读能否给出同等信息；**Deep-only 的等价物要额外打问号**（见 §三）。 |

A = 必须在 Stage B 之前；B = Stage B 期间按需 / Stage B 之后；C = 设计上就该保持 Reserved。

---

## 二、分级结果（23 项）

### A 级（4 项，必须在 Stage B 之前实现 producer）

| 事件 | 级别 | 关键判据 | 本轮一手证据 |
|---|---|---|---|
| `UI.NavigationChanged` | **A** | R1 + R2 | D6.2 实测：真实点击两张步骤卡后**导航确实发生**（Ground Truth：页面切到 Step 3，`StateLineText` 变为「尚未开始迁移…」），但全事件表 `UI.NavigationChanged` **命中 0**；页面切换只留下 `controlId="unknown"` 的输入事件。⇒ 用户旅程断裂，"用户在哪一页做了什么"不可判定。 |
| `DIA.RingTriggered` | **A** | R2 + R5 | Deep Trace 触发封存的**前提**。D6.2 实测 8 次 Deep Trace 窗口、ring 68 条、`persistedWindows=0`。 |
| `DIA.RingSealed` | **A** | R2 + R5 | **Deep-only 证据唯一的出口**。D6.2 实测：导出包内 0 个 flight 文件，本地 `flight\checkpoint-active.jsonl` 永远以 `active-segment-skipped` 被排除，而 manifest 同时声称 `includedFlightWindows: true` ⇒ 承诺与实际不符。不落地这条，12 个 Deep-only 事件永远进不了证据包。 |
| `TRN.ProgressObserved` | **A**（可降级条件见备注） | R2 + R5 | Stage B 的核心可观测对象（速度/百分比/剩余与真实字节数的对账）。当前**没有** Produced 的进度事件；唯一带进度的 `RBC.OutputObserved` 是 **Deep-only**，而 Deep-only 目前不可导出（依赖上一条）。**备注**：若 `DIA.RingSealed` 先落地并能导出，则可用 `RBC.OutputObserved` + UI 回读（`UI.StateObserved`/`UI.ProjectionReadback`）取得等价证据，本项可降为 B。 |

### B 级（14 项，保持 Reserved，Stage B 期间按需再评估）

| 事件 | 级别 | 为什么不进 A | 已有等价证据 |
|---|---|---|---|
| `FS.FileReadFailure` | **B** | 口径本身缺失（D6.1 已列）。是否升 A 取决于 Stage B 是否出现"校验通过但内容不一致"的疑点——那时必须能区分**读失败**与**内容不一致**。 | `FS.StatFailure` / `FS.TargetStatFailure`（Verifier）、`FS.ScanIncomplete` |
| `FS.EnumerateError` | **B** | 枚举局部失败已被上层覆盖；逐个错误明细对结论无改变。 | `FS.DirectoryUnavailable`、`FS.ScanIncomplete`、`FS.ScanCompleted` |
| `FS.PathPolicySkip` | **B** | Verbose 明细；跳过数量已可从扫描统计推断。 | `FS.ScanCompleted`（计数口径） |
| `NET.CredentialProofObserved` | **B** | 只在"使用显式凭据"场景需要；且受隐私红线约束（只允许记语义类别，禁止口令/用户名）。 | `NET.SmbSessionConnectStarted.usedExplicitCreds`（D6.2 实测 `usedExplicitCreds:false`） |
| `NET.NativeErrorObserved` | **B** | 各失败路径已各自带 `win32Error` 与 reasonCode。 | `NET.SmbConnectFailed`、`NET.DnsFailed`、`NET.TcpProbeFailed`、`NET.ShareEnumerationFailed` |
| `PST.CheckpointLoaded` | **B** | 恢复（resume）链路本轮未真实执行；恢复场景需要时再评估。 | `PST.WriteSucceeded` / `PST.WriteStarted`(Deep-only) / `PST.ReadFailed` |
| `PST.FlushAcknowledged` | **B** | 落盘确认已在导出 cutoff 的 `flushStatus: acknowledged` 中表达（D6.2 四个包全部 `acknowledged`）。 | `manifest.cutoff.flushStatus`、`metrics.export-snapshot.flushLatencyMs` |
| `TRN.CheckpointLoaded` | **B** | 同 `PST.CheckpointLoaded`；D6.2 未跑真实断点恢复。 | `TRN.JobRunStarted`、`PST.*` |
| `TRN.PauseBoundaryReached` | **B** | 暂停语义已有更粗但可用的等价链。 | `TRN.PauseRequested` / `PauseObserved` / `Paused` / `PauseRequestCleared` |
| `TRN.RootFilesVisibilityEnsured` | **B** | 明细级确认；对象级事件已覆盖"这个文件做完了"。 | `TRN.ObjectStarted` / `TRN.ObjectCompleted` |
| `TRN.TransferNoticeRaised` | **B** | 提示类信息；用户可见反馈已有契约校验。 | `UI.FeedbackExpected` / `UI.FeedbackConfirmed` |
| `RBC.JobGuardAssignFailed` | **B** | 作业守卫分配失败属启动期错误面，已有更强的事件覆盖。 | `RBC.SpawnFailed`、`RBC.ProcessExited`、`TRN.JobRunStarted` 缺失即异常 |
| `RBC.OutputParseFailed` | **B** | Verbose；Robocopy 输出异常已被聚合口径覆盖。 | `RBC.ErrorLinesAggregated`、`RBC.ProcessExited` |
| `DIA.SerializationFailed` | **B** | D6.1 已确认"**只有计数器、没有事件**"。它只在真的发生序列化失败时有意义；本轮未做自健康注入（T9 未执行），因此**先保持 B**，等注入验证证明"序列化失败会静默丢证据且 summary 不体现"再升 A。 | 计数器（`metrics`） |

### C 级（5 项，保持 Reserved 即设计）

| 事件 | 级别 | 理由 |
|---|---|---|
| `DIA.HealthSummary` | **C** | 与 `metrics.export-snapshot` / `summary.json` / 诊断中心「健康」「证据完整」文本**信息重复**，无新增语义。 |
| `DIA.SnapshotUnavailable` | **C** | D6.2 四个会话中 `snapshots\` 目录始终为空 ⇒ 快照子系统当前未启用；等它真的启用再谈失败事件。 |
| `PLN.PlanValidationFailed` | **C** | 计划校验失败在 UI 契约层已有更强的表达（`UI.ActionRejected` + `PLN.PlanEmpty`），重复。 |
| `UI.TestIntentObserved` | **C** | 测试意图属测试脚手架语义，**不应**进产品事件目录（会被当成产品噪声）。 |
| `UI.ThreadResponsivenessProbe` | **C** | 性能探针；D6.2 §十一明确不做像素级/性能大战，本项与 PMML/Stage B 判据无依赖。 |

**合计：A 4 / B 14 / C 5 = 23**（与覆盖矩阵的 Reserved 计数一致）。

---

## 三、跨条目发现（比单项分级更重要）

**Deep-only 目前等于"不可交付的覆盖率"。** 12 个 Deep-only 事件（含 `UI.InputObserved`、`RBC.OutputObserved`、`RBC.FileAttemptObserved`、`NET.TcpProbeAttempt`、`PST.WriteStarted` 等）只有在 Deep Trace 打开时才产生，而 D6.2 实测：

* Deep Trace 窗口开了 **8 次**，ring 内累计 **68 条**，`persistedWindows: 0`、`overwritten: 0`；
* 本地 `flight\checkpoint-active.jsonl` **永远**以 `active-segment-skipped` 被排除在包外；
* 四个导出包内 **0 个 flight 文件**，而 manifest 一律写着 `includedFlightWindows: true`。

⇒ 结论：`DIA.RingSealed`（A）不落地之前，**任何依赖 Deep-only 等价证据的 B 级判定都是纸面结论**。这也是把 `TRN.ProgressObserved` 放进 A 的直接原因。

---

## 四、实现顺序建议（Stage B 之前只做 A）

1. `DIA.RingTriggered` → `DIA.RingSealed`（打通"触发→封存→进包"这条链；含 export 侧把已封存窗口真正写进 zip）
2. `UI.NavigationChanged`（Step1/2/3/4 页面归属 + 页面切换语义；同时补上 D6.2 暴露的 `StepCardButton` 等 x:Name-only 控件的稳定 AutomationId）
3. `TRN.ProgressObserved`（若第 1 步已完成并验证可导出，本项可取消并降级到 B）

**B/C 一律保持 Reserved**：Event Catalog 的数量不是 KPI；`Reserved` 是诚实状态，不是欠账。
