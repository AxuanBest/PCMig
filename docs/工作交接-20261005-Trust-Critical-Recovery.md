# 工作交接 — PMCig Trust-Critical Recovery（2026-10-05 02:00）

> 本文档按「交接只增不覆」新增，**不覆盖**产品 Current Handover（`docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md`），
> 也不改动上一份并行轨道交接 `docs\工作交接-20261004-Trust-Critical-Recovery.md`。
> 完整细节、逐 case 证据、陷阱清单见实验室侧交接：
> `E:\PCMigLab\Evidence\Trust-Critical-Recovery\SESSION-HANDOFF-TRUST-CRITICAL-RECOVERY-20261005-0200.md`（+ `…-QUICK.txt`）。

## 一、本轮（本会话）在仓库里做了什么

- 依据权威施工指令（22 节，`E:\PCMigLab\Evidence\Trust-Critical-Recovery\INSTRUCTION-Trust-Critical-Recovery-Campaign.md`）**完成 FIX BATCH 1→7**：
  - **BATCH 1** Pause 核心语义（机制 A：杀当前 robocopy 进程树、当前对象不记完成回执、Resume 重跑该对象）；SLA 常量集中在 `src\PCMig.Diagnostics.Abstractions\ActionSla.cs`（5 s / 10 s / 2 s / 12 s / 30 s / 60 s）；`ActionSla` 无 ProjectReference 依赖，是引擎/诊断/UI 的唯一真值源。
  - **BATCH 2** Pause UI 状态机（`PauseUiState`、`IsPaused/IsPausing/IsPauseFailed/PauseButtonText/PauseStateText`、`CanResume` 重写、过期失败态丢弃）。
  - **BATCH 3** 诊断动作兑现（`pause.v2` 契约、`ExpectationFailure`/`OnFailure`、第 8 条规则 `ACTION_FULFILLMENT_FAILED`(TRN-023)、`DiagnosticHealth.MarkActionUnfulfilled`）。
  - **BATCH 4** 进度真值（`ProgressTruthSnapshot`、`WorkerIoProgressTracker`、`ITransferWorker.TryGetWorkerReadBytes` + `GetProcessIoCounters`；`Format` 改 IEC 单位、未知 ETA 显示 `—`）。
  - **BATCH 5** 底栏/进度条/排版（九列 `Auto,Auto,*,Auto,Auto,0,0,Auto,Auto`、按钮固定 124 px、`PCMigProgressBar` 12 px 样式、Typography `LineHeight`、响应式令牌改为"固定保留宽 + 弹性进度列"）。
  - **BATCH 6** 状态消息路由（`StatusChannel.cs` + 五个语义属性；左提示卡/右执行卡/安全提示卡各自只吃自己的来源；UI 不再自造业务状态）。
  - **BATCH 7** 全量质量门：`dotnet build PCMig.sln -c Release --no-incremental` = **0 error / 4 warning**（= 基线：xUnit2031×1 + WMC1506×3）；`PCMig.Core.Tests` **468/468**；`PCMig.Diagnostics.Tests` **382/382**（trx 留档 `…\batch7-trx\`）。
- **修复两个真实信任级缺陷**（均已 RED 双证 + 真机复测通过）：
  - 缺陷#1：`MigrationSessionViewModel.WaitForPauseSettledAsync` 原来要求"暂停中"中间态已到达 ⇒ 真实达成的暂停被判 `pause-unsettled`；改为轮询等待 `Paused/PauseFailed`。测试 PU-09/PU-10。
  - 缺陷#2：`WaitForResumeSettledAsync` 原来用"等待开始时 Phase==Paused"作判据 ⇒ 起新一轮运行的恢复被判 `resume-unsettled`；改为 `_resumeOriginPhase` + `ResumeSettled(origin)`。测试 PU-11/PU-12。
- **RECOVERY GATE**：case1 Happy Path、case2 小文件暂停、case3 单大文件暂停、case4 Verify、case5 连点暂停、case7 Stop→Resume = **全部 CLEAN**（每条都有 Product Truth + Independent Truth；暂停用例含 Action Causality；数据风险用例含逐文件 SHA256 Data Truth）。
- **未做**：case6 / case9–16 / case17–18、`FIX-BATCH-7-*.md`、§13 失败注入留档、§16 物理预验收、§17 200+ GB 重跑、§18 交付包、§19/§20 Final Fix Candidate 启动。

## 二、当前仓库状态（勿误判）

- 分支 `feature/winui-v0.5.0`，HEAD `d1aefb2fb8b36b135cf136afc71532c3450226a8`；**本会话未 commit / 未 push / 未 tag**。
- 工作树：`git status --porcelain=v1` = **74 条**（51 modified + 23 untracked；`git diff --shortstat` = 51 files changed, +2,920 / −411）。
- 本轮新增的主要源文件：`src\PCMig.Core\Transfer\{PauseSla,ProgressTruthSnapshot,WorkerIoProgressTracker}.cs`、`src\PCMig.Diagnostics.Abstractions\ActionSla.cs`、`src\PCMig.WinUI\Presentation\StatusChannel.cs`。
- 本轮新增测试：`tests\PCMig.Core.Tests\{PauseCoreSemanticsTests,PauseUiStateMachineTests,ProgressTruthModelTests,Batch5UiLayoutContractTests,Batch6StatusRoutingTests}.cs`、`tests\PCMig.Diagnostics.Tests\PD7ActionFulfillmentTests.cs`。
- 收紧口径（非放松）：`A5BusinessWiringContractTests`、`A5P15UiThrottleTests`（不再断言乐观的假状态）、`WinUiDpiContractTests`（新几何）、`D4RuleEngineTests`（规则数 7→8）、`D4bFeedbackTests`（预算改 `ActionSla.PauseFulfillmentDeadlineMs`）。
- 产品 Current Handover 与功能冻结铁律不变：本战役授权改动 Pause 语义 / 诊断契约 / 进度真值 / UI 结构，**不解除发版铁律**（产物是 Final Fix Candidate，不发版）。

## 三、唯一未结技术卡点

**缺陷#3（case8：进程被杀 → 重启 → 采纳中断任务）**：第一次点「恢复任务」被**完全吞掉**（应用侧零 `UI.UserActionObserved`/零命令事件、界面零进展），第二次同坐标点击立即生效；用户手动点击也正常。已确定性复现（`…\recovery-gate\case08b\probe-resume2.log`）。
- 已排除"应用静默吞掉"：`src\PCMig.WinUI\Diagnostics\ActionTrace.cs:65` 的 `ActionTrace.Begin()` 无条件发布 `UI.UserActionObserved` ⇒ 零事件只可能是点击没到 handler。
- 待判定的两种解释：**(A)** 采纳后「已有任务」浮层 light-dismiss 吞掉浮层外首击（`Step2SelectDataPage.xaml.cs:606-623` 的 `ExistingJobsList_ItemClick` 未 `Flyout.Hide()`）；**(B)** 夹具在点击前调用 `ShowWindow/SetForegroundWindow` 强制激活吃掉了合成点击。
- 接手第一步见实验室侧交接 §5.5（case08d 判定实验）+ §11（恢复后顺序），并先硬化夹具"点击后校验 + 重试"。

## 四、红线（不变）

`git push/tag/release` 禁止；`git reset --hard` / `checkout .` / `clean -fd` 禁止（必须保住 74 条改动集）；不得删历史证据与 Route A handoff；不得改用户真实源数据；不得降低断言 / 把请求成功当业务成功 / 只修 UI 假象 / 加大 timeout 求绿 / 把失败降 warning；UI 改动走死律 7 闭环 + PMML 声明；`.ps1` 必须 UTF-8 with BOM（死律 8）；需要用户选择用 `ask_user_question` 弹窗；todo 轨道常驻原地更新。

## 五、导航指针

- 实验室侧交接（权威、含全部证据坐标与陷阱）：`E:\PCMigLab\Evidence\Trust-Critical-Recovery\SESSION-HANDOFF-TRUST-CRITICAL-RECOVERY-20261005-0200.md` / `…-QUICK.txt`
- 现场账本：`…\CURRENT-TRUST-CRITICAL-CHECKPOINT.txt`（尾部 `PAUSE / FREEZE RECORD` 2026-10-05 01:44）
- 风险台账：`…\OPEN-RISKS.md`；逐批证据：`…\FIX-BATCH-1..6-*.md`；恢复门：`…\recovery-gate\`
- 桌面复核包：`D:\Users\User\Desktop\新建文件夹 (4)\PCMig-Trust-Critical-Recovery-BATCH7-RecoveryGate-20261005-0200\`（+ 同名 .zip）