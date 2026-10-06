# 工作交接 — 20261004 Trust-Critical Recovery Campaign（lab 修复战役轨道）

> **本文件的定位**：这是 **lab 修复战役（Trust-Critical Recovery）** 的会话交接，属**并行轨道**，
> **不是**产品 Current Handover。产品 Current 仍为 `docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md`，
> 本文件按"只增不覆"规则新增，不替换任何既有交接文档。
> 权威全文与证据在实验室侧：`<实验室根>\Evidence\Trust-Critical-Recovery\`。

- 交接时间戳：`20261004-2205`（用户主动收口：上下文过长）
- 轨道状态：**只读调查已完成并交付；修复战役仅完成 baseline；产品源码零改动**
- 权威施工指令：`<实验室根>\Evidence\Trust-Critical-Recovery\INSTRUCTION-Trust-Critical-Recovery-Campaign.md`（22 节，约束完整摘录）
- 完整交接：`<实验室根>\Evidence\Trust-Critical-Recovery\SESSION-HANDOFF-TRUST-CRITICAL-RECOVERY-20261004-2205.md`
- 粘贴用短卡：`<实验室根>\Evidence\Trust-Critical-Recovery\SESSION-HANDOFF-TRUST-CRITICAL-RECOVERY-20261004-2205-QUICK.txt`
- 当前检查点：`<实验室根>\Evidence\Trust-Critical-Recovery\CURRENT-TRUST-CRITICAL-CHECKPOINT.txt`
- 桌面复核包：`<用户目录D>\Desktop\<桌面交付根>\PCMig-Trust-Critical-Recovery-Handoff-20261004-2205\`（+ 同名 `.zip`）

## 1. 为什么开这条轨道

真实物理机 200+ GB 验收暴露严重问题，已由只读调查（`PCMig-Serious-Issue-Investigation-20261004-180846`，148 文件）定位到源码行：

| 编号 | 问题 | 结论 | 关键锚点 |
|---|---|---|---|
| P0-1 | 点「暂停」后传输从未停止 | CONFIRMED | `MainWindow.xaml.cs:461-471`（`:465` Eligibility 硬编码、`:467` `PauseAsync()` 默认 `immediate=false`）→ `MigrationSessionViewModel.cs:1311-1346`（`:1337` LastPauseOutcome 硬编码 Accepted）→ `TransferOrchestrator.cs:390-391`（**唯一**协作式检查点在对象边界）、`:874-913 WaitIfPausedAsync`、`:916-933 WatchImmediatePauseAsync`（`:923` 只认 `Immediate`）；对照 `StopAsync` 用 `_cts.Cancel()`（`:1361`）真能停 |
| P0-2 | UI 假状态（可反复点击） | CONFIRMED | `MigrationSessionViewModel.cs:2495-2524 ApplySnapshot`（`:2500 IsPaused = Phase==Paused`、`:2524 StatusMessage = s.Message`，2 s 轮询）、`:602 CanPause` |
| P0-3 | 诊断假绿（"事件卡：0 / 没有发现问题"） | CONFIRMED | `ActionTrace.cs:82 Expect`（footer 四按钮 0 处调用）、`FeedbackContract.cs:134-148 pause.v1`（`ExternalWaitTimeoutMs 3_600_000`）、`EvidenceRules.cs` 仅 7 条环境规则、`DiagnosticHealth.cs:148-163`、`DiagnosticCenterViewModel.cs:302-304` |
| P1-1 | 进度真值（0 B 长时间静止 / 边界大跳 / 回退；1024 进制标 GB） | CONFIRMED | `TransferOrchestrator.cs:339`（分子 = 已完成对象回执之和）、`:1055`、`:1108-1110 Percent`、`:205` F12 回冲；`Util\Format.cs` |
| UI | 底栏漂移/按钮被裁；进度条填充仅 3–4 px（轨道 12 px）；对象明细图标与标题中心差 8 px、提示小字截断；状态文案堆右栏挤压「连接与安全提示」 | CONFIRMED/HYPOTHESIS 已分级 | `MainWindow.xaml:115`（九列 Auto×5+80+\*+Auto+Auto）、`Step3ProgressPage.xaml:137-146`、`Step2SelectDataPage.xaml:14/:519/:522`、`Themes\Typography.xaml` |

覆盖缺口（须一并处理）：Route A 全部 case **从未点击 Pause**（仅 `"op":"read"`），`RouteA-Spec.md` 零次出现「暂停/Pause」⇒ **Acceptance Scope Gap**（不机械撤销 FULL ROUTE A）；产品日志对暂停窗口零覆盖（`app-20261004.jsonl` 末条早于传输开始）。

## 2. baseline（本轮落盘，可直接复用）

- 仓库 `<仓库根>`，分支 `feature/winui-v0.5.0`，HEAD `d1aefb2fb8b36b135cf136afc71532c3450226a8`
- 工作树：`git status --porcelain=v1` = 29（19 modified + 10 untracked）；`git diff --stat` = **19 files changed, 918 insertions(+), 64 deletions(-)**
- 证据目录 `<实验室根>\Evidence\Trust-Critical-Recovery\baseline\`（10 文件）：`baseline-meta.txt`、`git-head.txt`、`git-branch.txt`、`git-status-porcelain.txt`、`git-diff-stat.txt`、`git-diff-name-only.txt`、`git-diff-full.patch.txt`、`git-log-1.txt`、`git-stash-list.txt`、`build-identity-before.csv`
- 当前候选构建身份（四哈希）：exe `20CA24445D40D1445131C6F398FBFC7ACB129B62BF5C7845A211B13C6004DD0F`、Core.dll `C233F270394A666EEACF5A367BF67E2D6755B553BCF7F577D05D7F3184D25A2C`、WinUI.dll `E25927058EF4056CE7B997B24358C363E84FCD5F3CA70B178E6F527179441CD5`、WinUI.pri `481CDD88A8B0D515CE72559D450387BC92728D5FF8BED1C44CC64CFD37322A66`
- 本轮**未**构建、未部署、未启动 VM、未启动应用、未改任何产品源码。

## 3. 下一步（严格顺序，不得打乱）

`FIX BATCH 1 Pause Core` → 2 Pause UI Truth → 3 Diagnostics Action Fulfillment → 4 Progress Truth Model → 5 Bottom Bar/Visual/Typography → 6 Status Routing → 7 Cross-Regression + 物理机预验收 → 8 Final Candidate Launch。
每批：Reproduce → RED 测试 → Fix → GREEN → Core+Diagnostics 全量 → Release build → VM/真机定向验证 → 证据 → 才进下一批。
先做：写 `FIX-BATCH-1-PAUSE.md`（含机制选择与理由）→ Core RED 测试（SLA 常量 5 s/10 s、target bytes 静止 ≥5 s、当前对象不得产生 completion receipt）→ 实现 → GREEN → 门 → 证据。

## 4. 红线（与项目铁律叠加，冲突取更严者）

- 禁止：push / tag / release / 删历史 Evidence / 删 Route A Handoff / 改用户真实数据 / 降低断言 / 把请求成功当业务成功 / 只修 UI 假象 / 用 sleep 掩盖 race / 改文案逃避 Pause 语义。
- 禁止破坏性 git（铁律 9）：`git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`；必须保住当前 19 改 + 10 新增（完整补丁见 baseline\git-diff-full.patch.txt）。
- 注意与本文件的定位相关：本次会话为**并行 lab 轨道**，功能冻结（铁律 1）的范围改动已由用户在本战役指令中**明确授权**（Pause 语义、诊断契约、进度真值、UI 结构），但**发版相关铁律不解除**：本战役结束**不发版**（Final Fix Candidate 不是 Release）。
- UI 改动命中 PMML 硬性链路（AGENTS.md 三点六）：Batch 5 开工前须读 `docs/PCMig-Visual-Motion-Language.md` 与 `docs/PMML-UI修改硬性规范.md` 并按 Gate 声明。

## 5. 待用户决策（须用弹窗确认）

1. Pause 机制：A 杀当前 robocopy 进程树 + 当前对象标 unfinished + Resume 重跑 / B Job Object / C restartable。
2. SLA 常量：正常 5 s、硬失败 10 s。
3. Pausing 期间 Stop 是否可用。
4. 单位方案：保留 1024 数值 + 改 IEC 标签（KiB/MiB/GiB）。
5. 物理机 2–5 GB 预验收与 200+ GB 重跑时机（由用户主导，应用保持打开，事后查日志）。

## 6. 本文件对应的证据与索引

- 调查包（只读）：`<用户目录D>\Desktop\PCMig-Serious-Issue-Investigation-20261004-180846\`（20 份报告，核心 03/04/06/07/08/09/15/16）
- 真实失败轮现场：`<本机程序数据目录>\Jobs\JOB-20261004-171522-b785\`、诊断会话 `40d85e6396ff4545bdc8e3167cde5b1d`（709 事件、incidents 空）与 `48a7ef9a58c6421299db758813b50ab7`
- 本文件在桌面复核包内的副本：`investigation-reports\` 同级 `REPO-HANDOFF-20261004-Trust-Critical-Recovery.md`