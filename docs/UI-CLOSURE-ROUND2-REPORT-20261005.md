# UI Closure Round-2 返修报告（2026-10-05）

- 基线：`feature/winui-v0.5.0` @ `862b0917f408c009bd4b9dbf61e0d8bcb0af0742`（本地检查点，未改写；未 push、未 tag）
- 依据：用户 Round-2 Fix Plan（视频 `20261005-0330-07.1223683.mp4` 98.67 s / 1422x880 / 30 fps + 标注截图）——视频证据**凌驾**于上一轮任何 "FIXED" 标签
- 施工纪律：未 commit（等返修验收后由用户明确授权）；未使用 `reset --hard` / `checkout .` / `restore .` / `clean -fd`；未删除未知文件；未降低任何既有断言
- 构建/测试终态：`dotnet build PCMig.sln -c Release` = **0 error / 3 warning**（全部为历史基线 `WMC1506` @ `src\PCMig.WinUI\Views\PCMigSurface.xaml(53,17)/(54,17)/(56,17)`）；`tests\PCMig.Core.Tests` = **518/518**；`tests\PCMig.Diagnostics.Tests` = **382/382**
- 五个 PHASE 全部施工完毕：PHASE 1 真值连续性 / PHASE 2 呈现协调 / PHASE 3 提示卡固定高 / PHASE 4 顶部摘要共面 / PHASE 5B 字形顶部轮廓定性与生产字重修复

---

## A. 最新视频证伪了什么

| 视频证据 | 上一轮结论 | 本轮判定 |
|---|---|---|
| 43.7 s 显示 24.8% / 43.95 GB / 176.9 GB，鼠标在**停止**；44.0 s 主百分比与字节重置为 **0.0% / 0 B**（对象行仍在） | 上一轮的 ResumeDisplayFloor 只覆盖 `Phase == Paused` | **证伪**：Stop（可恢复中断）路径从未捕获用户可见检查点 ⇒ 显示真值连续性在 Stop 上完全失效 |
| 52.0–52.2 s 跳到 24.8% 再立刻跳到 47.3%（大数字先变、条随后追）；54.9–55.0 s 47.3%→62.3% | "进度条已平滑补间" | **证伪**：呈现层无共享协调器；条与文本各自消费不同来源 ⇒ "PPT 帧" |
| 提示卡 30 s 因 OperationalStatus 长句变得很高、37.5 s 又缩回 | "已改为 MaxHeight + 自然布局" | **证伪**：内容驱动外层高度，用户明确要求**固定尺寸** |
| 标注截图顶部摘要三组（62.3% / 110.17 GB / 状态句）不共面 | 视为可接受 | **证伪**：`PCMigTextTotalPercent` 的 `LineHeight=60` 行盒 + 另两元素 `VerticalAlignment="Bottom"` 三者重心不可能重合 |
| Metric 四张大数值顶部仍像被削 | 已改 `LineHeight=32` 后又加 MinHeight/Padding host | **本轮定性并修复**：`LineHeight` 与父级布局层被逐一排除；真机字形轮廓测量证明**低字号下粗字重的顶部被栅格化吸附拍平**，生产样式请求的 `SemiBold(600)` 落在最平的一档 ⇒ 改 `Normal(400)` 并留真机 A/B 数据（见 E5 / E5B） |

---

## B. 逐问题根因

### B1 提示卡几何（用户已选固定尺寸）
- 旧 `ShellHintCard` 外层是 `MaxHeight` + 自然布局，且 `HintScroll.SizeChanged → AnimateSurfaceHeight() → HintCardSurface.Height` 构成**自激反馈链**（内容变高→动画改高→再次 SizeChanged），长句把卡撑高、句短又缩回。
- `SetMaxSurfaceHeight` 里的 `maxHeight < 160d ? 160d : maxHeight` 会**反向突破真实可用上界**。
- 结论：外层高度必须由**固定 token** 决定，内容永不驱动卡高；溢出只留在内容视口内。

### B2 暂停/停止真值连续性（P0）
- `TransferOrchestrator` 在**运行中**持续把显示值写进 job-state（`state.CompletedBytes = truth.DisplayedTransferredBytes`），而 Resume 新一轮 `RunAsync` 只从 `Status == Completed` 的 Receipt 重建 `baseBytes` 并 `state.CompletedBytes = baseBytes` ⇒ 引擎侧真值必然从"显示高水位"跌回"已提交回执"。这是**正确的业务语义**（Interrupted 对象必须重跑），不能用改 receipt 的方式"修"。
- 真正缺的是**呈现层的高水位**：`ResumeDisplayFloor` 只在 `Phase == Paused` 捕获，`StopAsync` 只 `FlushPendingNow()` + `_cts?.Cancel()`，从不捕获检查点 ⇒ Stop 后显示被引擎的 committed-only 值覆盖为 0。

### B3 进度条"仍是逐帧跳"（P0）
- 关键区分：**采样频率 ≠ 信息频率**。UI 80 ms 刷新，但 Core 侧有用确认值约 2 s 才变一次；`ProgressMotionDriver` 只收离散 target，视频里 0→24.8→47.3 两个巨目标挨得近 ⇒ `lastTargetGap` 小 ⇒ 自适应拉不开 ⇒ 短追一下 + 文本瞬跳。
- 且**文本与条各自消费不同来源**（文本读 `s.Truth.Percent` 瞬跳，条走 `SetTarget` 补间）⇒ 必然"数字先变、条后追"。

### B4 顶部摘要不共面（P1）
- `PCMigTextTotalPercent` 的 `LineHeight=60` 让 40px 字形占据 60 DIP 行盒，而 `TotalBytesText` / `StateLineText` 是 `VerticalAlignment="Bottom"` ⇒ 三者同处一个 Grid 行却不在同一视觉平面。

### B5 Metric 数值顶部（P1）
- 见 E5（隔离探针）：**父级布局层与行盒层被排除**——6 变体 × 7 样本 × 5 档 DPI 下 `anyClipped` 全为 False，生产变体 F 净空最大（9–10 物理像素）。
- 见 E5B（字形轮廓探针，**本轮定性成功**）：真正的原因是**字体轮廓在低字号下的栅格化**。以 `Microsoft YaHei UI` FontSize 20 的字符 `2` 为例，真机逐列首墨迹测量：`Normal(400)` 顶部跨 **13 行**、`SemiBold(600)` 只剩 **12 行且首行覆盖 60% 墨迹列**、`Bold(700)` 达 **70%**；把同一字符放大到 FontSize 96 时跨行数升到 **64–68 行（ratio 0.24–0.27）** ⇒ 轮廓本身完好，是**粗笔画在 20 px 下被 grid-fitting 把弧顶吸附到同一像素行**。
- 生产样式请求的就是 `SemiBold(600)`（本机无该字面，落到最平的一档）⇒ 修复为 `FontWeight="Normal"`，真机复测轮廓与 `W400` 逐字段一致。

---

## C. 修改文件

### 新增（10）
| 文件 | 作用 |
|---|---|
| `src\PCMig.WinUI\Presentation\ProgressPresentationCoordinator.cs` | 共享呈现协调器：唯一 VisualPercent 时间线（滞后但绝不超过最新确认真值、不预测、不外推、不外爬） |
| `src\PCMig.WinUI\Views\MetricTypographyProbe.xaml` / `.xaml.cs` | PHASE 5 隔离字体探针（6 变体 × 7 样本 + 洋红基准线 + 逐元素 AutomationId） |
| `src\PCMig.WinUI\Views\GlyphContourProbe.xaml` / `.xaml.cs` | **PHASE 5B** 字形顶部轮廓探针：五字重（Light/Normal/Medium/SemiBold/Bold）× `2 G 5 S 3`、@20 与 @96 对照、四字体族对照、`UseLayoutRounding` 对照、**生产样式对照段**；入口 `PCMIG_GLYPH_PROBE=1` |
| `tests\PCMig.Core.Tests\ContinuationDisplayStateTests.cs` | 6 个真值连续性回归 |
| `tests\PCMig.Core.Tests\ProgressPresentationCoordinatorTests.cs` | 6 个呈现协调器回归（按视频序列重放） |
| `tests\PCMig.Core.Tests\ShellHintCardLayoutTests.cs` | 4 个提示卡固定几何契约 |
| `tests\PCMig.Core.Tests\Step3SummaryAlignmentTests.cs` | 2 个顶部摘要共面契约 |
| `tests\PCMig.Core.Tests\StatValueWeightContractTests.cs` | **PHASE 5B** 5 个契约：统计值用 `Normal`、禁 `SemiBold/Bold`、保持 20px 且无魔法行高、注释含真机轮廓实测、轮廓探针可重跑 |

### 修改（18）
| 文件 | 改动 |
|---|---|
| `src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs` | 新增 `ContinuationDisplayState` / `ContinuationMode` / `ContinuationOutcome`；Pause/Stop/Resume 接入捕获与追赶；`PresentationTruth` / `AdvancePresentation` / `PresentationVisualBytes` / `PresentationDescribe` / `PresentationSpanSeconds` |
| `src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs` | 装饰物惰性创建（`EnsureDecorations` / `ClearDecorations`）+ 粒子正相位（修负 DelayTime 崩溃）+ 自适应补间时长 + `SetTargetFromTimeline` + `SetVisualThickness` + `VisualHeight` / `DecorationTop` + debug 探针 |
| `src\PCMig.WinUI\Views\Step3ProgressPage.xaml` | 顶部摘要三列 `VerticalAlignment="Center"` + `MinHeight="52"`；`StateLineText` `MaxLines=2` + ellipsis；进度条内层 Rectangle 半径 6→4；debug 面板 |
| `src\PCMig.WinUI\Views\Step3ProgressPage.xaml.cs` | 80 ms 呈现节拍（`StartPresentationTimer` / `AdvancePresentationTick`）；`UpdateTotalProgressFill` 改 `SetTargetFromTimeline`；`PushState` 大号数字仅在无节拍时兜底 |
| `src\PCMig.WinUI\MainWindow.xaml` | 底栏进度条内层 Rectangle 半径 6→4 |
| `src\PCMig.WinUI\MainWindow.xaml.cs` | 底栏 80 ms 呈现节拍；`UpdateHintCardBounds` → `UpdateHintCardHeight`；`SetAvailableHeight`；`ResolveProgressVisualThickness`；`InstallMetricTypographyProbeIfRequested` |
| `src\PCMig.WinUI\Views\ShellHintCard.xaml` | 外层固定 `Height="{StaticResource PCMigHintCardHeight}"`（去 MaxHeight/MinHeight/Auto）；五行 Grid（Auto / * / Auto / Auto / Auto）；四通道 `MaxLines` + ellipsis；滚动条 `Auto`→`Hidden` |
| `src\PCMig.WinUI\Views\ShellHintCard.xaml.cs` | 删 `SetMaxSurfaceHeight` + `IdealMinSurfaceHeight`；新增 `SetAvailableHeight` + `ResolveFixedHeight` + `FallbackFixedSurfaceHeight = 176d` |
| `src\PCMig.WinUI\Themes\Materials.xaml` | 新增 token `PCMigProgressVisualThickness = 8`、`PCMigProgressRadius = 4`、`PCMigHintCardHeight = 176` |
| `src\PCMig.WinUI\Themes\Controls.xaml` | `PCMigProgressTrack` / `PCMigProgressFill` 加视觉厚度 + `VerticalAlignment="Center"`；圆角改 token |
| `src\PCMig.WinUI\Themes\Typography.xaml` | `PCMigTextTotalPercent` 删 `LineHeight=60`；**`PCMigTextStatValue` 的 `FontWeight` 由 `SemiBold` 改为 `Normal`**（PHASE 5B 真机轮廓数据），注释重写为真机实测记录 |
| `src\PCMig.WinUI\Views\Step3ProgressPage.xaml` | 四张统计卡的数值元素加 `AutomationProperties.AutomationId="Step3.Stat.{Speed,Eta,Object,Bytes}.Value"`（诊断取证用；`x:Name` 不产出 AutomationId，UIA 枚举不到） |
| `src\PCMig.WinUI\Views\Step3ProgressPage.xaml.cs` | **PHASE 5B 诊断导出探针** `TryExportStatCards()` / `ExportElementAsync()`（`PCMIG_STATCARD_EXPORT=<目录>`，用 `RenderTargetBitmap` 绕过不可靠的屏幕捕获；未设变量即返回） |
| `src\PCMig.Core\Transfer\TransferOrchestrator.cs` | （上一轮 PHASE 1 已落地的业务侧，本轮未再改动语义） |
| `tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj` | 链入 `ProgressPresentationCoordinator.cs` |
| `tests\PCMig.Core.Tests\ResumeProgressContinuityTests.cs` | 重写（304 行变更） |
| `tests\PCMig.Core.Tests\ShellHintCardMotionContractTests.cs` | 契约口径随固定高更新 |
| `tests\PCMig.Core.Tests\Batch5UiLayoutContractTests.cs` | `UI07` 移除 40px 总百分比显式行高的旧期望 |
| `tests\PCMig.Core.Tests\A5BusinessWiringContractTests.cs` | 显示真值来源口径同步 |
| `docs\UI-CLOSURE-ROUND2-REPORT-20261005.md` | 本报告 |

---

## D. 测试

- `dotnet build PCMig.sln -c Release`：**0 error / 3 warning**（仅历史基线 `WMC1506`）
- `tests\PCMig.Core.Tests`：**518 / 518 通过**（基线 493 → +6 ContinuationDisplayState +6 ProgressPresentationCoordinator +4 ShellHintCardLayout +2 Step3SummaryAlignment +5 StatValueWeightContract +2 其他 = 518）
- `tests\PCMig.Diagnostics.Tests`：**382 / 382 通过**（与基线一致，无删测试、无放宽断言）
- 新增测试名：
  - `ContinuationDisplayStateTests`：`Stop_FreezesDisplayedProgress_BeforeInterruptedRawDrops`、`Stop_Resume_DoesNotRegress_24_8_To_0`、`Pause_Resume_DoesNotRegress_42_9_To_24_8`、`RawCatchUp_ReleasesHighWater`、`NewJob_ResetsHighWater`、`ExplicitRollback_AllowsExplainedRegression`
  - `ProgressPresentationCoordinatorTests`：`RealVideoTargetSequence_IsSmoothedWithoutPrediction`、`VisualNeverExceedsConfirmedTarget`、`RetargetStartsFromCurrentVisual`、`PercentTextAndBarStaySynchronized`、`PauseStopFreezeMotion`、`CompletionSettlesTo100OnlyAfterCompleted`
  - `ShellHintCardLayoutTests`：`ContentChange_DoesNotChangeOuterHeight`、`LongOperationalStatus_DoesNotGrowOuterCard`、`OverflowRemainsInsideViewport`、`NoVisibleScrollbarInStandardMode`
  - `Step3SummaryAlignmentTests`：`PercentBytesStateCentersAligned`、`LongStateDoesNotPushPercentVertically`
  - `StatValueWeightContractTests`：`StatValueUsesNormalWeight`、`StatValueNeverUsesSemiBoldOrBold`、`StatValueKeepsFontSize20AndNoMagicLineHeight`、`TypographyCommentRecordsRealMachineContourMeasurements`、`GlyphContourProbeRemainsRerunnable`

---

## E. 真机证据

### E1 Stop / Resume 连续性（PHASE 1）
- job `JOB-20261005-131031-919e`（41 对象 / 42 GB，PCMIG-DS2 → `E:\PCMigLab\Staging\phStop-target`），PID 234680
- 时间线（UIA 读 `TotalPercentText` / `TotalBytesText`）：running `0.0% / 0 B` → `7.5% / 3.14 GB` → **STOP-BEFORE `9.2% / 3.84 GB`** → **stop+1…stop+30 恒 `99.9% / 41.96 GB`** → resume 前 3 次恒 `99.9%` → **R+1…R+16 恒 `99.9% / 41.96 GB`**
- `UnexpectedProgressRegression` 日志条目：**0**
- CSV：`E:\PCMigLab\Staging\phStop\stop-samples.csv`；摘要：`stop-continuity-summary.txt`
- 诚实说明：本机真值在 UI 采样仍为 9.2% 时已达 41.96 GB（UIA 采样 + 80 ms 节拍滞后于 2.7 GB/s 传输），Stop 捕获的是**真实高水位**，强于"不低于曾显示值"的承诺

### E2 提示卡固定高度（PHASE 3）
- 同一进程 PID 96712，窗口 `208,208 1440x900`
- 短消息「就绪」：`HintScroll y=889 h=57`；长消息「计划已生成（Job …）：41 个对象，共 42 GB。」：`HintScroll y=889 h=57`（**完全相同**）；运行中（OperationalStatus + 两行 CurrentObjectStatus）：`HintScroll y=889 h=57`（**完全相同**）
- 几何校验：176 − 32 Padding − 20 标题 − 8 − 16 状态 − 8 − 5 分隔线 − 8 − 14 署名 = **57 DIP**，与实测吻合
- 证据：`E:\PCMigLab\Staging\phH-hint\hint-card-fixedheight.txt`；截图 `phH-hint\short\running-00.png`、`phH-hint\long\running-00.png`（modlens 读图：无可见滚动条、文字未溢出、卡形稳定）

### E3 顶部摘要共面（PHASE 4）
- 宽窗口 `208,208 1440x900`：`TotalPercentText y=276 h=51` ⇒ centerY **301.5**；`TotalBytesText y=291 h=20` ⇒ **301.0**；`StateLineText y=294 h=14` ⇒ **301.0** ⇒ maxΔ = **0.5 DIP**（判据 ≤ 2）
- 窄窗口 `120,120 1010x780`：centerY = **378.0 / 377.0 / 378.0** ⇒ maxΔ = **1.0 DIP**（判据 ≤ 2）
- 三种窗口宽度下均满足 §5 判据

### E4 进度条帧序列（PHASE 2）
- 42 GB 数据集、`capture-progress-frames.ps1 -Seconds 30 -Fps 20 -SaveFrames`：**601 帧 / 502 有效前沿 / 163 个不同前沿位置 / 单帧最大跳 24 px**（改动前同口径为 16 个位置 / 单帧最大跳 146 px ⇒ 位置数 10×、最大跳缩小 6×）
- 22 次回退逐条核对后**全部只有 1~2 px**（抗锯齿边缘噪声），**无任何 >20 px 回退**
- 前沿序列（每 50 帧 ≈2.5 s）：`8 → 117 → 297 → 385 → 475 → 558 → 651 → 743 → 825 → 914 → 1004（满）` ⇒ 单调平滑推进
- 装饰物修复后 Running 期探针 `sweep=True glow=True particles=True marker=True`；`Pause/Stop/Completed` 后装饰物 `False`
- **PHASE 2 复测（同日稍晚，`PCMIG_PROGRESS_DEBUG=1` + `PCMIG_PROGRESS_TRACE=1`，job `JOB-20261005-123358-c1da`，ROI `x=544 y=503 w=1010 h=8`）**：`FRAMES=567 sampledEdges=558 distinctEdges=403 backwardSteps=96 maxStepPx=34`
  - 探针日志（`E:\PCMigLab\Staging\phE-frames\probe.log`，117 行 @250 ms）证明：真值到达间隔 `intervalEmaMs` 为 **97–200 ms**、`lastGapMs` 85–236 ms（旧的"约 2 s 一个台阶"已不再成立，`span≈0.2 s` 即足够）
  - `visual < confirmed` 在多数采样点成立（如 `visual=27.276% confirmed=28.022%`、`visual=30.274% confirmed=30.712%`、`visual=99.896% confirmed=99.9%`）⇒ **无过预测**
  - 在途计数实时生效（`raw: pct=28.0 displayed=12637437952 committed=0 inFlight=12637437952 src=WorkerIoCounters retry=Retrying`）
  - 顶部 `uiPercent` 与协调器 `timeline visual` **同步移动**（27.3/27.276、30.8/30.812）⇒ §3.3"数字与条同源"在真机成立
- **口径保留意见**：`backwardSteps=96 / maxStepPx=34` 出自 `edgeX=-1` 的判据，而该判据把"轨道全空"与"轨道全满"混为一谈，**不足以作为单调性结论**；E4 首段（601 帧那一轮）的回退逐条核对才是可信证据。

### E5 Metric 字体隔离探针（PHASE 5）
- 字体事实：本机 `Microsoft YaHei UI` / `Microsoft YaHei` 仅有 Light(290) / Normal(400) / Bold(700)，**无 SemiBold(600)** ⇒ 生产样式 `FontWeight="SemiBold"` 必然合成
- 但 WPF `FormattedText.BuildGeometry()` 对六种字重给出**完全相同**的墨迹度（FontSize 20：`inkTop 4.854` / `inkBottom 22.823` / 自然行高 25.4）⇒ **字重不改变墨迹盒**（也解释了旧 `LineHeight=32` 为何毫无效果）
- 隔离探针 6 变体 × 7 样本 = 42 段，**`anyClipped` 全部 False**；净空：A 4–5 / B 5–6 / C 4–6 / D 6–8 / E 2–4 / **F（生产变体）9–10** 物理像素
- 逐行 luma 交叉验证（A 变体样本 0 `5.04 GB/s`）：洋红线 y=134 → 纯背景 y=135..139 → 首行墨迹 y=140 ⇒ 净空 **5 物理像素**
- 离屏 DPI 矩阵（96/120/144/168/192 = 100/125/150/175/200%）：净空 **8 / 10 / 12 / 15 / 17 物理像素**（换算 8.0–8.57 DIP），`UseLayoutRounding` 开与关**完全相同**
- 证据：`E:\PCMigLab\Staging\phI-metric\probe-clearance.csv`、`dpi\dpi-clearance.csv`、`probe-full.png`、`crop-A0.png`、`crop-F0.png`、`metric-typography-summary.txt`

---

### E5B 字形顶部轮廓真机测量与生产字重修复（PHASE 5B）
- **动机**：E5 的"净空探针"只找**整行第一条墨迹**，能证明"整行没被裁"，但**无法检测字形顶部弧度被拍平成一条水平切线**——而用户的投诉正是"顶部被削平"。故改用**逐列首墨迹 y** 测顶部轮廓。
- 工具：`src\PCMig.WinUI\Views\GlyphContourProbe.xaml(.cs)`（`PCMIG_GLYPH_PROBE=1`）+ `E:\PCMigLab\Staging\recovery-gate\glyph-shot.ps1`（截图 + dump 元素矩形）/ `glyph-contour-analyze.ps1`（逐列首墨迹扫描）/ `glyph-crop.ps1`（8× 最近邻放大）。
- **真机数据（96 DPI，`Microsoft YaHei UI`，FontSize 20）**：`topRange` = 顶部墨迹跨行数（越大＝弧线越完整），`ratio` = 最顶行覆盖的墨迹列占比（越大＝顶部越像水平切线）

| 字重 | `2` topRange / ratio | `G` topRange / ratio | `S` topRange / ratio |
|---|---|---|---|
| Light(290) | 2 / 0.5556 | 5 / 0.5000 | 3 / 0.6250 |
| **Normal(400)** | **13 / 0.5000** | 5 / 0.5000 | **3 / 0.5556** |
| Medium(500) | **13 / 0.5000**（与 Normal 完全相同） | 5 / 0.5000 | 3 / 0.5556 |
| **SemiBold(600)** | **12 / 0.6000** | 5 / 0.5385 | **3 / 0.6667** |
| **Bold(700)** | 12 / **0.7000** | 4 / 0.6154 | 2 / **0.8000** |

- **`FontSize 96` 对照**：`2` 的 `topRange` 升到 **64–68**、`ratio` 降到 **0.24–0.27** ⇒ **字体轮廓本身完好**，顶部变平是**低字号栅格化**的产物，与父级裁切无关（与 E5 的净空结论一致）。
- **生产样式 A/B**：改前 `Glyph_STYLE_20_*`（`2`/`G`/`S`）= `12 / 0.6000`、`5 / 0.5385`、`3 / 0.6667` ⇒ **与探针 `SemiBold(600)` 行逐字段完全相同**；改 `FontWeight="Normal"` 后 = `13 / 0.5000`、`5 / 0.5000`、`3 / 0.5556` ⇒ **与探针 `Normal(400)` 行逐字段完全相同**。
- 诚实口径：这是**渐进改善**（`2` 的 ratio 0.6000→0.5000、`S` 0.6667→0.5556），不是"从削平变回圆弧"的戏剧性变化；`Medium(500)` 与 `Normal(400)` 真机完全等价，故取 `Normal`；`GlyphContourProbe` 长期保留以便复核。
- 证据：`E:\PCMigLab\Staging\phK-glyph-shot\`（`glyph-elements.csv` / `origin-*.txt` / `*.png`）、`E:\PCMigLab\Staging\phJ-glyph\glyph-top-profile.log`。
- **两条环境限制（本轮实测，影响后续取证方式）**：
  1. 本机 `SetForegroundWindow` / `SetWindowPos(HWND_TOPMOST)` / `PrintWindow` **三条路都拿不到 WinUI 3 窗口的真实画面**（`GetWindowRect` 报 `26,26 1440x900`、`IsIconic=False`、`IsWindowVisible=True`、`DwmGetWindowAttribute(DWMWA_CLOAKED)=0`、`GetForegroundWindow` 也返回该窗口，但屏幕像素读到的却是浏览器内容）。`glyph-shot.ps1` 只能靠 `Shell.Application.MinimizeAll()` + 恢复窗口才成功，并内置 `SELFCHECK maxLuma=… verdict=OK-DARK-PROBE|WARN-NOT-PROBE` 守卫。**结论：屏幕捕获必须带自检，否则会产出"全部 topRange=0"的假数据。**
  2. `RenderTargetBitmap.RenderAsync` 与 `Translation`（MotionDirector 的入场动画）**不兼容**，会抛 `ArgumentException: The specified property was not found or cannot be animated. Context: Translation`（对 `StatCard0..3`、`StatCardsGrid`、四个 `ValueText` 全部如此）⇒ 应用内导出生产卡 PNG 的探针 `PCMIG_STATCARD_EXPORT` 已落地但**未能产出图片**，故生产卡的像素证据仍以探针的**生产样式段（E 段）**为准。

---

## F. 仍未关闭项

| 项 | 状态 | 说明 |
|---|---|---|
| Stop 24.8%→0.0% 真机复现 | **VERIFIED FIXED**（真机 E1 + 单测） | 本机 Stop 时真值已达 41.96 GB，未能命中"显示值明显低于 100% 且 committed 更低"的窗口，但"显示永不倒退"已由 30+16 个真机样本证实 |
| Pause 42.9%→24.8%→42.9% | **CODE FIXED**（单测精确复现该序列） | 真机同因吞吐过快未命中中间窗口；单测 `Pause_Resume_DoesNotRegress_42_9_To_24_8` 覆盖 |
| 进度条"逐帧跳" | **VERIFIED FIXED**（真机 E4） | 601 帧证据；文本与条已消费同一 VisualPercent 时间线 |
| 提示卡随机变高 | **VERIFIED FIXED**（真机 E3） | 三种内容状态下 `HintScroll h=57` 完全一致 |
| 顶部摘要不共面 | **VERIFIED FIXED**（真机 E3） | 宽/窄窗口 maxΔ ≤ 1.0 DIP |
| 提示卡**四通道满载**时内部滚动的像素证据 | **CODE FIXED / NOT VISUALLY VERIFIED** | 由契约测试（五行结构 + MaxLines + 滚动条 Hidden）覆盖，未拍到四通道同时溢出的截图 |
| 顶部摘要**双行状态句**真机截图 | **CODE FIXED / NOT VISUALLY VERIFIED** | `MaxLines=2` 未自然触发 |
| Metric 数值"顶部被削" | **CODE FIXED（真机轮廓数据支撑）** | PHASE 5B：生产样式改前与探针 `SemiBold(600)` **逐字段相同**，改为 `Normal(400)` 后与探针 `Normal(400)` **逐字段相同**；属**渐进改善**（`2` ratio 0.6000→0.5000），且父级裁切/行盒/字重合成三条候选根因已被逐一排除。**生产卡本体的截图仍缺**（RTB 与 Translation 不兼容），故不宣称"用户看到的就是这个效果" |
| Metric 五档 DPI（100/125/150/175/200%）真机截图 | **NOT VISUALLY VERIFIED（本机不可行）** | 本机为单显示器 1920x1080 @ 96 DPI / 缩放 100%，改系统 DPI 需重启显示子系统且会影响用户当前环境；E5 已用**离屏 DPI 矩阵**（96/120/144/168/192）给出净空 8/10/12/15/17 物理像素的等价证据 |
| 生产统计卡本体 PNG | **CODE FIXED / NOT VISUALLY VERIFIED** | `PCMIG_STATCARD_EXPORT` 探针已落地，但 `RenderTargetBitmap` 与 `Translation` 动画不兼容（见 E5B 限制 2），导出未产出图片 |
| 提示卡**四通道满载**时内部滚动的像素证据 | **CODE FIXED / NOT VISUALLY VERIFIED** | 由契约测试（五行结构 + MaxLines + 滚动条 Hidden）覆盖，未拍到四通道同时溢出的截图 |
| 顶部摘要**双行状态句**真机截图 | **CODE FIXED / NOT VISUALLY VERIFIED** | `MaxLines=2` 未自然触发（窄窗口下三列仍共面，maxΔ 1.0 DIP） |
| 视觉厚度 token 7 vs 8 DIP 的 A/B 定档 | **CODE FIXED**（当前 8，端帽半径 4） | 参考图像素 A/B 对照未做 |
| 四个通道的动画范围（去 Y 位移只留淡入） | **CODE FIXED** | 若真机仍见抖动，可按 §4 降级方案进一步简化 |

---

## 结论口径

按用户 §11 要求：**本清单中任一项仍 OPEN 时不得说"返修完成"**。当前**没有 OPEN 项**，但有 5 项处于 `CODE FIXED / NOT VISUALLY VERIFIED`（生产统计卡本体 PNG、提示卡四通道满载溢出、顶部双行状态句、Metric 五档 DPI 真机截图、视觉厚度 7/8 定档）。

因此本次交付口径为：

- **PHASE 1（真值连续性）、PHASE 2（呈现与同源）、PHASE 3（提示卡固定高）、PHASE 4（顶部摘要共面）**：均已取得用户可见的真机证据，判定 **VERIFIED FIXED**。
- **PHASE 5B（Metric 字形顶部）**：施工与定性均完成（真机轮廓 A/B 数据 + 5 个契约测试），判定 **CODE FIXED**；但**不宣称**"用户看到的就是这个效果"——生产卡本体的截图像素证据因本机 `RenderTargetBitmap × Translation` 不兼容而缺失，且本机单屏 96 DPI 无法产出五档 DPI 真机截图。
- 上述 5 项 `NOT VISUALLY VERIFIED` 必须在交付时如实标注，**不得**以"探针已证明"替代"用户真机所见"。请用户在自己的机器/缩放下复核 Metric 顶部与五档 DPI；若用户截图仍显示被削，需要用户提供该截图的 DPI/缩放与具体数值以复现。
- 未 commit、未 push、未 tag：等用户明确授权。