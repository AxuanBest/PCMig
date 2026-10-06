# UI Closure 返修报告 · PHASE A–D（2026-10-05）

> 本文件记录用户返修技术方案（m00011）§6 PHASE A→D 的施工结果。
> 纪律：只写有证据的结论；无真机/像素/动态证据的一律写 OPEN，不写"理论上已修"。

---

## A. 根因

### A1. 提示卡文字堆叠 / 越多越乱 / 出现滚动条（UI-07/08/09 回归）

**根因一（文字堆叠）**：`ShellHintCard.PlayEntrance()` 把 **`Visual.Offset` 当成 TranslateY** 使用。
`Visual.Offset` 是"相对父 Visual 的位置"属性，对已经由 XAML 布局排好位置的行写 Offset
等于把它们一起塌向父原点 ⇒ 多行文字互相重叠、越写越多越乱。
**正确 API**：`ElementCompositionPreview.SetIsTranslationEnabled(element, true)` +
`visual.StartAnimation("Translation", …)` —— 渲染期位移，不参与布局，也不改 layout slot。

**根因二（频繁重播）**：`SetLine()` 对**每一次文本变化**都重播入场动画。
进度/速率文本每 200 ms 变一次 ⇒ 每 200 ms 一次 Y 位移重启 ⇒ 视觉抖动。
**正确策略**：语义级判定（对象 ID 真正切换、通道 Collapsed→Visible、空→有内容才播一次），
普通文本刷新只改 `Text`。

**根因三（滚动条/自激）**：`HintScroll.SizeChanged → AnimateSurfaceHeight() → HintCardSurface.Height`
构成反馈链：高度动画本身改变 `ScrollViewer` 尺寸 ⇒ 再次触发 SizeChanged ⇒ 高度持续变化 +
虚假 overflow ⇒ 滚动条。第一阶段直接切断：不监听 `SizeChanged`、`Height` 回归 `Auto`、
只设 `MaxHeight`、让 XAML 自然布局、`ScrollViewer` 只负责真溢出。

**根因四（上界被反向突破）**：`SetMaxSurfaceHeight` 里
`maxHeight < 160d ? 160d : maxHeight` 会在可用空间不足时**返回 160**（比传入值还大），
使提示卡突破真实上限。**正确**：`safeCeiling = max(0, calculatedAvailable)`，
`target = min(desired, safeCeiling)`，160 只作为理想最小高度参与 desired 的计算。

### A2. 暂停 42.9% → 恢复后掉到 ~24.8% → 过一会回 42.9%（P0）

根因链（两条口径叠加）：
1. **暂停收尾保号**：`TransferOrchestrator.RunAsync` 中
   `state.CompletedBytes = receipt.Status == ObjectStatus.Interrupted ? Math.Max(state.CompletedBytes, settledBytes) : settledBytes;`
   已确保暂停时 job-state 保留"用户已看到的显示值"（其中含运行期 poll 写入的 in-flight 确认字节）。
2. **恢复重建清零**：新一轮 `RunAsync` 开头只从 `Status==Completed` 的回执重建
   `completedIds` 与 `baseBytes`，然后 `state.CompletedBytes = baseBytes;`
   ⇒ 若暂停时完成的对象少（本机复验时为 0 个完成对象），job-state 直接归 0；
   而 UI 若直接跟随 raw 值，百分比就从 42.9% 掉到 24.8%，随后 robocopy 续传追回 42.9%。

**为什么不能"假修"**：把 `CompletedBytes = Math.Max(old, baseBytes)` 当完成回执照写、
或把 Interrupted 对象标成 Completed，都会污染业务权威（`ResumeProgressReconstructionTests`
要求恢复不得盲信陈旧 job-state，真正 Completed 仍以 Receipt 为权威，Interrupted 对象
Resume 后必须重跑）。

### A3. 进度条动画肉眼不可见 / 像"变粗"（P0）

**根因一（错误前提）**：Core 真实传输轮询里有 `await Task.Delay(2000, ct)`，
所以"50 ms UI flush"并不等于"50 ms 新真值"；`ProgressMotionDriver.MaxSpanSeconds = 0.40`
使每约 2 秒来一个目标、只动 0.4 秒、其余 ~1.6 秒完全静止 ⇒ 人眼看到"一段一段跳"。

**根因二（装饰物根本不存在，PHASE C 最重要的发现）**：真机探针在修复前 45/45 次输出
`decoErr=ArgumentException: An invalid DelayTime is specified. It must be within the range of 0-24 days. sweep=False glow=False particles=False marker=False`
⇒ `StartParticleLoops()` 里 `TimeSpan.FromSeconds(-ParticleCycleSeconds * (i / (double)…)`
用了**负 DelayTime**，Composition 拒绝 ⇒ `CreateDecorations()` 抛异常 ⇒ 构造函数
`catch { _sweepVisual = null; _glowVisual = null; _particleHost = null; }` **静默降级**。
用户报告"粒子/Sweep/Glow 肉眼基本无效果"完全正确；上一轮"已实现"是假结论。

**根因三（装饰物创建时机）**：`SetElementChildVisual` 必须在元素进入可视树后调用，
构造函数里的早期抛异常也与时机相关 ⇒ 改为惰性创建（Loaded → SizeChanged → SetTrackWidth）。

### A4. 统计卡大号数值顶部像被削一条（UI-03）

本机 100% DPI 下**未能复现**（见 E4 的三次视觉复核结论）。
但代码侧确实存在 §4 明确点名的风险源：`PCMigTextStatValue` 写死
`LineHeight="32"`（FontSize 20）。`TextBlock.LineHeight` 默认 0 = 按字体度量自动算行盒，
写死数值与真实字体度量不符时会把墨迹顶到行盒上沿，且 DPI 缩放变化后必然失配。
本轮按 §4 方向改为 **不声明 LineHeight + `LineStackingStrategy=MaxHeight` + 容器几何兜底**，
定位为**消除 DPI 缩放风险的防御性修正**，而非"已复现并修好"。

### A5. 诊断与可观测性缺口

§5 要求的 `ProgressTruthTransition` 结构化日志与 `UnexpectedProgressRegression` 尚未完整实现
（本轮新增了 `UnexpectedProgressRegression` 判定与结构化落盘，详见 B/C；诊断中心展示面仍 OPEN）。

---

## B. 修改文件

### PHASE A（提示卡）
1. `src\PCMig.WinUI\Views\ShellHintCard.xaml.cs`
   —— `PlayEntrance()` 改用 `SetIsTranslationEnabled` + `StartAnimation("Translation")`；
   加 `StopAnimation("Translation"/"Opacity")` 接管；终态 `Translation=0 / Opacity=1`；
   Reduced Motion 直接归零；`SetLine()` 改为语义级重播策略；移除 `HintScroll.SizeChanged`
   订阅与 `AnimateSurfaceHeight()` 自激链；`SetMaxSurfaceHeight` 改为
   `target = min(desired, max(0, calculatedAvailable))`。
2. `src\PCMig.WinUI\Views\ShellHintCard.xaml`
   —— `HintCardSurface.Height` 恢复 `Auto`、只保留 `MaxHeight`；不设 `Clip`。

### PHASE B（Resume 连续性）
3. `src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs`
   —— 新增 Presentation 层 `ResumeDisplayFloor`（`_resumeDisplayFloorBytes/_resumeDisplayFloorJobId/_resumeDisplayFloorObjectId`）
   与 `ApplySnapshot` 中的 `Effective` 合成；`Percent/ProgressText/ActualBytesText/PlanBytesText/BalanceText`
   统一读同一份 continuity 快照；`LastTruth` 仍保留原始 Core truth 用于诊断；
   新增 `ProgressTruthTransition` 结构化日志与 `UnexpectedProgressRegression` 判定。
4. `tests\PCMig.Core.Tests\ResumeProgressContinuityTests.cs`（新增，16 个用例）
   —— 42.9→raw 24.8 精确复现、UI effective 不回退、raw 追上后 floor 自动解除、
   RollingBack 允许回退但带解释事件、不破坏 Interrupted 对象重跑语义。

### PHASE C（进度动效）
5. `src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs`
   —— 惰性装饰创建（`EnsureDecorations()`/`ClearDecorations(Exception)`/`_decorationError`）；
   粒子错相改正延时；自适应补间（`MaxSpanCeilingSeconds=1.80`、`AdaptiveSpanRatio=0.85`、
   `_lastTargetGapMs`、`SetActive(true)` 重置间隔统计）；`PCMIG_PROGRESS_DEBUG=1` 探针
   （`DescribeDebug()`、`LastTruthUtc`、`TargetIntervalEmaMs`、`LastAnimationSpanSeconds`、
   前沿标记线、高可见度装饰参数）。
6. `src\PCMig.Core\Transfer\TransferOrchestrator.cs`
   —— `LightPollMs = 200`（5 Hz 轻量真值通道）；`heavyDue`（2 秒重活档）门控
   `SaveState` 落盘与目录枚举回退；`Report(…truth:)` 每轮推送。
7. `src\PCMig.WinUI\Views\Step3ProgressPage.xaml(.cs)`
   —— 调试浮层 `ProgressDebugPanel/ProgressDebugText`（默认 Collapsed）+ `StartDebugProbe()`。
8. `tests\PCMig.Core.Tests\ProgressMotionContractTests.cs`（新增）
   —— 装饰物必须真实创建、错相延时必须非负、自适应补间不得回跳。
9. `tests\PCMig.Core.Tests\SourceTreeHygieneTests.cs`
   —— `DirectoryFallbackProgress_IsLimitedToBulkPassOnly` 断言随 `heavyDue &&` 前缀更新
   （语义更严，非放宽）。

### PHASE D（Metric 排版）
10. `src\PCMig.WinUI\Themes\Typography.xaml`
    —— `PCMigTextStatValue` 删除 `LineHeight="32"`，改 `LineStackingStrategy=MaxHeight` +
    `VerticalAlignment=Center`；其余三处数值样式行高未动。
11. `src\PCMig.WinUI\Views\Step3ProgressPage.xaml`
    —— 四张统计卡数值各包 `SpeedValueHost/EtaValueHost/ObjectValueHost/BytesValueHost`
    （`MinHeight=34`、`Padding=0,3,0,3`、无 Clip）。
12. `src\PCMig.WinUI\MainWindow.xaml` + `src\PCMig.WinUI\Presentation\ResponsiveLayoutController.cs`
    —— 底栏宽度声明与 XAML 实际渲染值对齐（`FooterPercentWidth` 48→68、`FooterEtaWidth` 100→112，
    三档 Wide/Normal/Compact）。
13. `tests\PCMig.Core.Tests\Batch5UiLayoutContractTests.cs`
    —— UI01 改为「XAML 宽度 + 响应式 token 一致」双向断言；UI06 行高 16→18；
    UI07 移除 StatValue 魔法行高；新增 `UI07b_StatValueUsesLineStackingInsteadOfMagicLineHeight`。
14. `src\PCMig.WinUI\Themes\Motion.xaml`、`src\PCMig.WinUI\Presentation\MotionDirector.cs`
    —— 动效 Token 与 Translation 动画路径（PHASE A 复用的既有实现）。

---

## C. 核心设计

- **为什么 Offset → Translation**：`Visual.Offset` 属于布局坐标系（相对父 Visual 的位置），
  写入它会破坏 XAML 已算好的行位置；`Translation` 是渲染期后置位移，
  `SetIsTranslationEnabled` 打开后才可动画，不影响 layout slot、不触发重新测量 ⇒ 不再堆叠。
- **为什么不再"字符串不同就重播"**：动画属于**语义事件**（对象切换 / 通道首次出现 / 空→有内容），
  不属于**内容刷新**。把内容刷新当语义事件 ⇒ 每 200 ms 一次位移重启 ⇒ 抖动、甚至溢出。
- **为什么业务 committed truth 与 display continuity 分开**：`Completed` 回执是业务权威
  （决定 skip / 完成对象数 / 最终判定），绝不能被 UI 连续性污染；
  用户已经看到的百分比在正常 Resume catch-up 期间不无解释倒退是**表现层契约**。
  两者用一个显式的 floor 连接（`_resumeDisplayFloorBytes`），并且当 raw 追上 floor 后自动解除，
  显式回滚（RollingBack / Repair / Source identity change / 换任务）时按规则解除并写解释事件。
- **为什么 50 ms UI flush ≠ 50 ms Core truth**：流量真值只在 Core 轮询里产生；
  UI 刷新频率再高也只是重复渲染同一个旧真值。因此必须先把 Core 真值采样频率提到 5 Hz
  （只做 O(1) 的内存计数与进程 I/O 计数器读取），而 job-state 落盘与目录枚举保持 2 秒节流，
  避免把磁盘写满。**不允许预测、不允许自爬**：无高频可信计数的通道保持最后真实值或
  诚实视觉滞后。
- **为什么 Metric 不再靠 LineHeight 猜**：`LineHeight=0` 才是"按字体度量自动算行盒"的正解；
  写死数值是用魔法数去补偿一个本来不该存在的问题，且随 DPI 缩放必然失效。
  行盒交给排版引擎，几何高度交给容器（MinHeight + 对称 Padding），
  两者都稳定后裁切才无从发生。

---

## D. Build / Test

| 项目 | 结果 |
|---|---|
| `dotnet build PCMig.sln -c Release` | **0 error / 0 warning**（全量构建基线：3× `XamlCompiler WMC1506` @ `src\PCMig.WinUI\Views\PCMigSurface.xaml(53,17)/(54,17)/(56,17)`，属历史遗留、非本轮引入） |
| `dotnet test tests\PCMig.Core.Tests` | **通过 493 / 失败 0 / 总计 493**（基线 468，已超越） |
| `dotnet test tests\PCMig.Diagnostics.Tests` | **通过 382 / 失败 0 / 总计 382**（= 基线） |

新增测试：
- `ResumeProgressContinuityTests`（16）：`Resume_KeepsDisplayedPercent_WhenRawDropsTo248`、
  `Resume_FloorReleasesAfterRawCatchesUp`、`RollingBack_AllowsRegressionWithExplanation`、
  `InterruptedObject_StillReruns_AfterResume` 等。
- `ProgressMotionContractTests`：`Decorations_MustBeReallyCreated`、
  `ParticleLoopDelay_MustBeNonNegative`、`AdaptiveSpan_MustNotExceedCeiling`。
- `Batch5UiLayoutContractTests.UI07b_StatValueUsesLineStackingInsteadOfMagicLineHeight`。
- `ShellHintCardMotionContractTests`（PHASE A，7 个）：禁对 XAML 行写 `Visual.Offset`、
  高频刷新不重复触发 Translation、Reduced Motion 终态归零。

> 构建前必须先 `Stop-Process -Name PCMig.WinUI`，否则
> `MSB3021/MSB3027: …\PCMig.WinUI.exe … 被"PCMig.WinUI (PID)"锁定`。

---

## E. 真机证据

### E1. 提示卡（PHASE A）
- `<实验室根>\Staging\recovery-gate\` 下的 `shot-*.png` 与控件转储（`dump-controls.ps1` 输出）。
- 结构性契约已由 `ShellHintCardMotionContractTests` 锁定（源码级）。
- **OPEN**：四通道同时显示 / 连续 20 次对象更新 / 长文本 / Resize / DPI 五档 /
  Reduced Motion 的完整截图与 bounds dump 尚未成体系补齐（见 F）。

### E2. 进度条动效（PHASE C，决定性）
| 采集 | distinctEdges | 单帧最大跳 | 回退 |
|---|---|---|---|
| 改动前 `phC-big\frames.csv`（42 GB / 20 fps / 26 s） | 16 | 146 px | 2（均 <1.1% 处） |
| 改动后 `phC-5hz\frames.csv`（20 fps / 24 s） | **110** | **21 px** | 1 |
| 改动后 `phD-run\frames.csv`（30 s / 20 fps / 601 帧） | **163** | **24 px** | 22（**逐条核对全部仅 1~2 px**，抗锯齿噪声） |

- 前沿序列（`phD-run`，每 50 帧 ≈2.5 s）：`8 → 117 → 297 → 385 → 475 → 558 → 651 → 743 → 825 → 914 → 1004(满) → FULL → FULL`
  ⇒ 单调平滑、25 秒收敛满宽 ✓
- 探针：`lastGapMs=83.9`、`intervalEmaMs=155.0`、`span=0.060`、`targetChanges=121`（24 s 内）
  ⇒ **真值到达间隔从约 2 秒变为约 84 ms（5 Hz）** ✓
- 装饰物：修复前 45/45 采样 `sweep=False glow=False particles=False marker=False`；
  修复后 Running 期 30/30 采样 `decoErr=- sweep=True glow=True particles=True marker=True` ✓
- 装饰物像素证据（ROI 1010x12 固定列采样）：`103 → 99 → 103 → 101 → 103 → 82 → 99 → 101 → 103`
  ⇒ 存在独立于进度推进的时序变化 ✓
- 产物：`<实验室根>\Staging\phD-run\frames.csv`、`<实验室根>\Staging\phD-run\frames\`（61 张 ROI PNG）、
  `<实验室根>\Staging\phC-5hz\frames.csv`、`<实验室根>\Staging\phC-big\frames.csv`。

### E3. 暂停/恢复连续性（PHASE B，真机）
- 命令：`pause-resume-continuity.ps1 -OutDir '<实验室根>\Staging\phDB' -TargetRoot '<实验室根>\Staging\phDtarget' -PauseAfterSec 8 -ResumeSampleCount 30 -ResumeSampleMs 200`
- 时间线：`START 05:43:15.91` → `PAUSE-CLICKED 05:43:24.44`（`jobPercent=34.33 / uiPercent=39.7% / uiBytes=16.69 GB`）
  → 暂停后 `uiPercent=99.9% / jobPercent=99.9 / jobBytes=45097156608`（42 GB 已落盘）
  → `RESUME-CLICKED 05:43:34.99 uiPercentBeforeClick=99.9%`
  → **`RESUME+1…+4`：`uiPercent=99.9%` 未下降，而 `jobBytes=0`**（job-state 被 Resume 重建清零）
  → `RESUME+5` 起 `jobBytes=45097156608` 回写 ⇒ **UI 全程无倒退** ✓
- **OPEN**：本机 2.7~5 GB/s 吞吐使"暂停时显示值明显低于 100% 且 committed 更低"的窗口极难命中，
  42.9%→24.8%→42.9% 的**逐帧复现未完成**；单测已精确复现该序列（`ResumeProgressContinuityTests`），
  真机为等价场景（UI 不回退 + raw 清零）。

### E4. 统计卡大号数值（PHASE D，视觉复核）
| 图 | 路径 | 结论（视觉模型原文） |
|---|---|---|
| 英雄数字区 | `<实验室根>\Staging\phD-hero\hero-zoom2.png` | "The large percentage glyphs appear intact and rounded at the top, with clear padding above them inside the card" |
| 四张统计卡 | `<实验室根>\Staging\phD-hero\hero-zoom.png` | "BOTH have complete, fully rounded top edges — no horizontal truncation of their glyphs" |
| 改动后完成态 | `<实验室根>\Staging\phDdone\statcards-v3.png` | "the tops of 1, 4, G, B and / look intact rather than shaved" |
- **诚实口径**：本机 **100% DPI 下未复现**用户所报裁切 ⇒ 本轮改动为防御性修正。
  DPI 125/150/175/200% 原始截图 + geometry JSON **未完成**（需改系统缩放）。

---

## F. 尚未关闭（OPEN）

1. **PHASE A-8 真机证据不全**：四通道同时 / 连续 20 次对象更新 / 长文本 / Resize /
   DPI 五档 / Reduced Motion 的截图 + bounds dump 未成体系。结构性契约测试已通过，
   但**不替代真机视觉证据**。
2. **PHASE C-4 未做**：12 DIP layout host 内的独立 `ProgressVisualThickness`（6~8 DIP A/B）未实施，
   当前厚度未按参考图像素测量重定。
3. **PHASE C-6 单测不完整**：帧序列 CSV 判据已由真机证据覆盖，但 `ProgressMotionDriverTests`
   的完整用例矩阵（Pause SnapTo / Reduced Motion 直接落位 / sample interval 自适应）未补齐。
4. **PHASE D DPI 多档未验**：125/150/175/200% 需改系统缩放，未做；100% 档已完成且未复现裁切。
5. **42.9%→24.8% 真机逐帧复现未完成**（单测已复现，真机为等价场景）。
6. **诊断中心展示面**：`UnexpectedProgressRegression` 已产生结构化日志与判定，
   诊断中心的聚合 finding 展示未接。
7. **暂停态文案口径**：真机观测到"UI 显示 99.9% / 42 GB"与文案"已完成 0/41 个对象"并存，
   两个数字语义不同但并列易被误读，**未处理**。
8. 本文档之外的既有交接文档（`UI-CLOSURE-ISSUES-20261005.md` 等）尚未按本报告增量更新。

---

## 参考仓库事实
- 分支 `feature/winui-v0.5.0`，权威工作区 `<仓库根>`。
- 本轮候选产物：`src\PCMig.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe`。
- 复现口令：构建前 `Stop-Process -Name PCMig.WinUI` → `dotnet build PCMig.sln -c Release`
  → `dotnet test …` → `launch-app.ps1 -WaitSec 12` → `ensure-step3.ps1`
  → `step1-connect.ps1 -NoLaunch` → `step1-set-shares.ps1 -Include 'PCMIG-DS2'`
  → `step1-select-and-step2.ps1 -ShareMatch 'PCMIG-DS2'` → 清空目标
  → `step2-prepare.ps1 -Target <dir> -Prepare` → `goto-step.ps1 -Step Step3`。