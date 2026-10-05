# 工作交接 — 2026-10-05 会话级交接（接管收口 → UI Closure → 打包交付）

> **本文件的读者是"下一个新会话"**。目标：让它读完就能无缝接着干，不需要重新调查。
> **与已有文档的关系**：本文件是**会话级**交接（做了什么 / 没做什么 / 文件在哪 / 下一步）；
> 产品技术侧交接是 `docs\工作交接-20261005-UI-Closure.md`（逐文件改动 + 证据 + 回退）；
> 交付报告是 `docs\UI-CLOSURE-REPORT-20261005.md`；问题表是 `docs\UI-CLOSURE-ISSUES-20261005.md`。
> 三者**只增不覆**，互相引用，不互相替代。
> **可粘贴短卡**：`docs\工作交接-20261005-UI-Closure-QUICK.txt`（给新会话直接粘贴用）。

---

## 0. 一分钟现状

| 项 | 值 |
|---|---|
| 权威工作区 | `E:\Project\deepseek work\PCMig`（分支 `feature/winui-v0.5.0`） |
| HEAD | `d1aefb2fb8b36b135cf136afc71532c3450226a8`（**本会话没有产生任何 commit**） |
| 工作树 | `git status --porcelain=v1` = **82 条**（55 modified + 27 untracked）—— 含**上一轮遗留**的 51+ 与**本会话新增**的改动 |
| 本会话新增改动 | **28 个仓库文件**（21 个已跟踪文件被改 + 7 个新文件），逐条见 §3 |
| 构建 | `dotnet build PCMig.sln -c Release` ⇒ **成功 / 0 error / 3 warning**（= 基线 `Views\PCMigSurface.xaml:53/54/56` WMC1506） |
| 测试 | `PCMig.Core.Tests` **468 / 468 通过**；`PCMig.Diagnostics.Tests` 本轮**未重跑**（最近记录 382/382） |
| 运行中程序 | `PCMig.WinUI.exe` **PID 227500**（标题 `PCMig 迁移工具 · v0.5.0`），停在正常初始页 Step1 |
| 发布状态 | **未 commit / 未 push / 未 tag / 未发版**。本会话产出的是**待人工验收的候选版** |
| 桌面交付 | `D:\Users\User\Desktop\新建文件夹 (4)\` 下 4 项（见 §6） |

---

## 1. 本会话（2026-10-05 01:49 → 03:15）分三轮做了什么

### 第 1 轮：接管收口（01:49 → 01:55，用户接管指令）
1. **Reality Sync（只读，3 分钟）**：`git rev-parse HEAD` / `branch` / `status --porcelain`（当时 75 条 = 51 M + 24 ??）/ `diff --stat`（51 files, +2930 / -411）；确认候选 EXE `mtime 00:54:35` 晚于最新真实源 `00:54:03`（不陈旧）；无 robocopy 在飞；无失控 case runner。
2. **清点失效自动化状态**：结论是**没有需要清理的**（无遗留 robocopy、无 fault injector、未启动新 runner）。理由记录：未对残留 `powershell`/`pwsh` 做无差别 kill —— 它们可能是 DSH 宿主/会话宿主，误杀风险高于收益。
3. **Final Release build**：`dotnet build PCMig.sln -c Release` ⇒ 成功，当时 **0 error / 4 warning**（4 = 基线：3×WMC1506 + 1×既有）。
4. **启动候选并交给用户**：用 `E:\PCMigLab\Staging\recovery-gate\launch-app.ps1 -WaitSec 12` 启动，PID 194664，停在初始界面。
5. **落盘**：新建 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\FINAL-FIX-CANDIDATE-IDENTITY-20261005-0151.txt`（含 6 个产物 SHA256）；在 `CURRENT-TRUST-CRITICAL-CHECKPOINT.txt` 尾部追加「2026-10-05 01:52 FINAL FIX CANDIDATE 移交记录」段。
6. **桌面复验包**：`PCMig-FinalFixCandidate-ManualAcceptance-20261005-0155\`（6 份证据副本 + `MANIFEST-复验清单.md`）。
7. **二进制回退备份**：`E:\PCMigLab\Staging\final-candidate-backup-20261005-0152\`（`PCMig.WinUI.exe` / `PCMig.WinUI.dll` / `PCMig.Core.dll` / `PCMig.Diagnostics.dll`）。
8. **本轮零产品代码改动**。

### 第 2 轮：UI Closure 专项（02:22 → 03:00，用户 UI Closure 指令）
用户指令口径：只做 UI/UX/Visual Motion 收口；**PMML 只收基础可复用规范，不许把 UI Bug 塞进 PMML**；提供 5 张标注截图（桌面「新建文件夹 (5)」）；§21 规定施工顺序 U1→U6；§23 列了假修复禁令；§24 交付 12 项；§25 只要 19 行汇报。

**做了什么（14 项全部代码级修复，见 §3）**：
- U1 进度显示真值 + 数字几何：底栏百分比不截断（`48→68` + 去 Ellipsis）；暂停/停止不再把已传字节归零（Core 三处）；单位为 `KB/MB/GB/TB`（唯一 formatter）。
- U2 排版与布局：Metric Card 顶部裁切经**像素实测证伪**（系标注黑块遮挡），但据此修了 40 号行高的**真实 2 px 裁切**与 20 号的**零余量**；底栏定宽隔离；Popups 与锚点等宽；圆角统一为 Token。
- U3 提示栏与路由：提示卡上界（= 侧栏高 − 导航高 − 12，下界 160 DIP）+ 卡内滚动；扩张/收缩 180 ms 动画；流程级提示改走提示卡通道（`FlowStatus`/`InlineNote` 15 处分流）。
- U4 动效：新增 `ProgressMotionDriver`（平滑补间 + 前沿柔光 + 扫描高光 + 低密度粒子流 + 状态开关）；状态文本轻入场。
- U5 PMML 同步：规范正文新增附录 A **§19–§24 + 规则 R16–R31**；Audit 新增两节；Legacy 更新 L-07/G-04 并新增 L-17/L-18。
- U6 视觉复验：**部分完成**（见 §5 的"没做什么"）。

**并行委派的 4 个只读子代理（结果已落盘，主控已核验）**：
1. 像素测量（`E:\PCMigLab\Evidence\UI-Closure-20261005\pixel-measure-report.md` + 24 个 `measure-*.py`）—— 给出 4 项改前实测值：底栏百分比可用宽 39..41 px（缺 4..6 / 14..16 px）、Popup 越出 59 px（底边口径 51 px）、下拉框圆角 r≈2..3 px vs 路径框 8..9 px、统计卡文字未贴边（证伪标注）。
2. WinUI Composition 调研（`D:\Users\User\Desktop\_pcmig-motion-research\WinUI-Composition-Motion-Plan.md`，592 行）—— 实测 SDK = **WindowsAppSDK 2.5.1**、UI 节拍 **50 ms / 20 Hz**、`AccentGradientBrush` 是 45° Relative 渐变 ⇒ **结论：禁用 `Scale.X`（会拉伸渐变与圆角）、禁继续写 `Width`（20 Hz × 60 fps = 每秒 60 次布局），改用「满宽填充 + `InsetClip.RightInset` 标量动画」**；并给出 glow/sweep/粒子的挂载位置纪律。
3. PMML 覆盖审计（`D:\Users\User\Desktop\_pcmig-pmml-audit\PMML-Coverage-Matrix.md`）—— 17 个目标章节里 5 个完全缺失、10 个部分存在、2 个重复分散；给出三份文档的同步落点。
4. 单位与归零追踪（`D:\Users\User\Desktop\_pcmig-trace\units-and-pause-trace.md`）—— 定位唯一 formatter 与"暂停归零"三处根因链（`MarkInterrupted` 不实测 / 累计无单调下限 / `Report` fallback 置 in-flight 0）。

### 第 3 轮：打包与交付（03:03 → 03:15，用户要求把改动文件与整版打成包）
1. `PCMig-UI-Closure-改动代码-20261005-0303\`（桌面）：**28 个改动文件按原始相对路径镜像** + `01-清单`（21 个逐文件 diff + 340 KB 合并补丁 + 105 文件 SHA256 + manifest.csv + verify-summary）+ `03-仓库外新增产物`（48 项证据/夹具）+ `00-说明.md`。
2. `PCMig-UI-Closure-这一版源码-20261005.zip`（桌面，**8.39 MB / 505 条目 / 未压缩 12.76 MB**）：497 个源码与文档文件（`src`/`tests`/`tools`/`installer`/`matrix`/`docs`/`lab` + 根文件）+ `_交付清单`（diffs/清单/校验）+ `00-说明.md`（**面向外部 AI**：项目定位、构建运行命令、本版改动、5 个可请对方给方法的开放问题、阅读路径、约束）。
3. 包外附一份 `PCMig-UI-Closure-这一版源码-说明.md` 便于直接阅读转发。

---

## 2. 本会话"明确没做"的事（不要以为已经做过）

> 这些是本会话**主动留白的**，都有理由；新会话不要误判为"已完成"。

1. **没有 commit / push / tag / 发版**（用户长期纪律 + 本轮指令明令禁止）。
2. **没有重跑 Diagnostics 测试**（最近记录 382/382；本轮改的是 UI 与 Core 显示层，未触发诊断契约）。
3. **没有做运动类效果的人工视觉终验**：进度平滑、扫描高光、粒子流、前沿柔光、提示卡扩张动画、文本入场 —— 只完成了**代码级 + 编译级**验证；用户 §22 要求"录短视频或连续帧确认非 PPT 跳帧"，**尚未录**（需要真实迁移运行 + 屏幕录制）。
4. **没有做 UI-02 的真机暂停/恢复复验**：Core 修法与单测已绿，但"暂停后界面保留真实已传字节"未在真实迁移里目视确认（改前真机现象已存档：`0.0% / 0 B / 剩余 100% 未传`）。
5. **没有做 UI-06（Popup 对齐）/ UI-07（圆角）的改后像素复核**：改前实测值已存档（Δ=59 px、r≈2..3 px），改后未再测量。
6. **没有做 UI-12（粒子/光波）在真实 Running 状态的目视确认**：装饰只在 `Phase == Running` 打开，本次未跑真实迁移。
7. **没有等 125% / 150% DPI 的行高复核**（用户 §5/§19.4 要求多 DPI 无裁切）。
8. **没有处理上一轮遗留的卡点**（都不属本轮范围）：缺陷#3（case8「进程被杀→重启→采纳中断任务→首次点恢复被吞」的归属未判定）、Recovery Gate 的 Case06 / Case09–16 / Case17–18、`FIX-BATCH-7-*.md` 证据文件、§13 失败注入留档、§16 两台真机 2–5 GB 物理预验收、§18 十五份交付文件 + ZIP。
9. **没有删除桌面上的失败尝试残留**（详见 §6 末尾），因为未获用户对桌面文件的删除指令。
10. **没有修改 `tools\release.ps1` / 发版链路 / 安装包**，也没有生成安装包（用户明确"不发版，只是压缩包"）。

---

## 3. 本会话逐文件改动（28 个仓库文件）

> 机器可读的完整 diff：桌面包 `01-清单\diffs\`（21 个 `.diff` + `ALL-tracked-changes.patch`）。
> 新增文件清单：`01-清单\new-files-in-this-revision.txt`。

### A. 业务显示真值（3）
1. **`src\PCMig.Core\Util\Format.cs`**（改）—— 唯一 user-facing formatter 的标签数组：`["B","KiB","MiB","GiB","TiB","PiB"]` → **`["B","KB","MB","GB","TB","PB"]`**；除法仍 1024（与 Windows 资源管理器习惯一致）；注释写明**诊断证据域刻意不走本 formatter**（`DiagnosticCenterViewModel` 的硬编码 KiB、`PreflightChecker.cs:434-435` 的事件 payload 不许改）。
2. **`src\PCMig.Core\Transfer\TransferOrchestrator.cs`**（改）—— 三处：
   - `MarkInterrupted(ObjectReceipt, PlannedObject)`：签名加 `obj`，方法体内**补 `MeasureTarget(receipt, obj)`**（被打断对象也实测落盘量）；两处调用点（bulk 取消 / large 取消）同步。
   - 字节累计：`state.CompletedBytes` 对 `receipt.Status == ObjectStatus.Interrupted` 取 **`Math.Max(state.CompletedBytes, settledBytes)`**（单调下限，绝不把已确认传完的字节改小）。
   - 保留行为：`Report()` 的 fallback 仍按"只有已完成字节"构造（未改），因为修复 1+2 后被打断对象的落盘量已从 in-flight 通道转入 committed 通道。
3. **`src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs`**（改）—— 结果页暂停文案改字节口径（`剩余 {Format.Bytes(TotalBytes−CompletedBytes)}（{百分比}%）未传`）；另一处 `SetOperational` 的暂停文案原本就是字节口径，未改。

### B. 状态路由（1）
4. **`src\PCMig.WinUI\Presentation\ConnectionViewModel.cs`**（改）—— 新增两通道 + 两个私有写入器：
   - `FlowStatus`（流程级：正在连接… / `BuildConnectStatus` 结论 / 已定位到共享 / 自动列出失败 / `Status +` 注意段 / 正在探测 / 已添加并勾选）
   - `InlineNote`（字段级与错误级：请先输入 IP、路径格式不正确、连接失败、请输入共享名、共享名不合法、已在列表中、探测失败两种、`NotifyNextStepUnavailable` 两分支）
   - `SetFlowStatus(...)` / `SetInlineNote(...)` 内部同时写兼容字段 `Status`（顶栏 ToolTip 等既有消费点不变）。
   - **文件内 `Status =` 直接赋值已归零**（15 处全部改走两个方法）。

### C. 进度视觉与动效（1 新增）
5. **`src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs`**（**新**，约 470 行，`internal sealed`，零业务引用）：
   - 构造 `(FrameworkElement fill, FrameworkElement? trackHost = null)`；对 `fill` 的 Visual 设 `Compositor.CreateInsetClip(0,0,HiddenRightInset,0f)`。
   - `SetTrackWidth(double)` / `SetTarget(double filledPixels)` / `SnapTo(double)`：追赶上限 **55%/s**、单段 **60–400 ms**、段内**线性不加 easing**（理由：`InsetClip` 无可回读动画值，新段起点必须用 `Stopwatch` + 上段起止值精确复现，带 easing 会回跳）。
   - UI-06 装饰常量：`GlowWidth=26f`、`GlowPeakAt=9f`、`SweepBandWidth=44f`、`SweepPeakAlpha=0x1Eu`、`ParticleSpan=34f`、`ParticleCycleSeconds=0.90d`、`ParticleCount=8`、`FallbackSweepSeconds=1.60d`。
   - 挂载纪律：**扫描高光挂 `fill` 子树**（自动被 `InsetClip` 裁在已完成区内）；**前沿柔光与粒子挂 `trackHost` 子树**（一个元素只能挂 1 个 child visual ⇒ 各自一个 `ContainerVisual`）。
   - `SetActive(bool)`：**只有 Running 打开**；其余状态 `SnapTo` + `IsVisible=false` + `StopAnimation`。
   - `ResolveSweepSeconds()`：读 Token `PCMigMotionProgressSweepDuration`（`Application.Current.Resources`），失败回退同值常量。
   - 全程 `try/catch`；`VisualCollection` 无索引器 ⇒ 用 `Children.ToArray()`。
   - **注意（编译期踩坑）**：`error CS0021`（VisualCollection 索引）会连带触发一堆 `XamlCompiler WMC0001: Unknown type '<页面类>'`，修好 C# 后 XAML 错误全消。

### D. Shell 与页面（10）
6. **`src\PCMig.WinUI\MainWindow.xaml`**（改）—— `FooterPercentText` `Width 48→68` + `TextTrimming None`；`FooterEtaText` `Width 100→112` + `TextTrimming None`；`FooterProgressFill` 加 `Width="Auto" HorizontalAlignment="Stretch"`；侧栏内层 Grid 命名 `x:Name="SidebarHost"`。
7. **`src\PCMig.WinUI\MainWindow.xaml.cs`**（改）—— 新增字段 `_footerMotion` / `_lastFooterFillWidth`；`AttachFooterToSession()` 内建 `new ProgressMotionDriver(FooterProgressFill, FooterProgressHost)` 并挂 `SidebarHost/StepNav` 尺寸事件；新增 `UpdateHintCardBounds()`（上界 = 侧栏高 − StepNav 高 − 12）；`UpdateFooterProgressFill()` 改为把宽度交给驱动 + `SetActive(Session.Phase == JobPhase.Running)`。
8. **`src\PCMig.WinUI\Views\Step3ProgressPage.xaml`**（改）—— `TotalProgressFill` 加满宽两属性；`ObjectPaneHintText` `LineHeight 16→18`。
9. **`src\PCMig.WinUI\Views\Step3ProgressPage.xaml.cs`**（改）—— 新增 `using PCMig.Core.Models;`、字段 `_progressMotion` / `_lastRenderedWidth`；构造函数建驱动；`UpdateTotalProgressFill()` 重写（`normalProgress = Phase == Running && width >= _lastRenderedWidth`，否则 `SnapTo`）—— 用**局部 `motion`** 而非字段（避免 `CS8602`）。
10. **`src\PCMig.WinUI\Views\Step1ConnectPage.xaml`**（改）—— `StatusLineText` 的 `Text` 绑定 `{Binding Status}` → **`{Binding InlineNote}`**（AutomationId 仍是 `Step1.StatusText`）。
11. **`src\PCMig.WinUI\Views\ShellHintCard.xaml`**（改）—— 外层 Border 命名 `HintCardSurface`；内容包进 `<ScrollViewer x:Name="HintScroll" …>`。
12. **`src\PCMig.WinUI\Views\ShellHintCard.xaml.cs`**（改）—— 新增 `SetMaxSurfaceHeight(double)`（下界 160 DIP）、`AnimateSurfaceHeight()`（180 ms `CubicEase/EaseOut`，走 XAML `Height`，`EnableDependentAnimation=true`）、`SetLine()`（只在折叠↔显示或文本真变化时播一次）、`PlayEntrance()`（`Opacity 0→1` + `Offset (0,6,0)→0`，170 ms，走 `MotionDirector.SystemAnimationsEnabled` 门控）。
13. **`src\PCMig.WinUI\Views\Step2SelectDataPage.xaml`**（改）—— TaskPicker Presenter/Flyout/Item 三段圆角改 `PCMigRadiusInput` / `PCMigRadiusOverlay` / `PCMigRadiusListItem`；`ExistingJobsList` 去掉 `MinWidth="220" MaxWidth="360"`；`PCMigTaskPickerFlyoutStyle` 的 `MaxWidth 380→1200`。
14. **`src\PCMig.WinUI\Views\Step2SelectDataPage.xaml.cs`**（改）—— `ExistingJobsPickerButton_Click` 改块体并先调新增的 `AlignExistingJobsFlyoutWidth()`：`ExistingJobsList.Width = Math.Max(180d, anchorWidth − flyoutChrome)`，`flyoutChrome = 6d`（= FlyoutPresenter `Padding 2+2` + `BorderThickness 1+1`）。
15. （同 13 文件内的浮层宽度，见上）

### E. 主题与排版（3）
16. **`src\PCMig.WinUI\Themes\Materials.xaml`**（改）—— 新增 `<CornerRadius x:Key="PCMigRadiusOverlay">16</CornerRadius>` 与 `<CornerRadius x:Key="PCMigRadiusListItem">10</CornerRadius>`；圆角层级注释更新为 `… / StepCard 16 / Overlay 16 / InsetSurface 13 / Input 14 / Button 12 / ListItem 10 / Badge 9`。
17. **`src\PCMig.WinUI\Themes\PcmigComboBoxRoll.xaml`**（改）—— 收起态 `CornerRadius` `{ThemeResource ControlCornerRadius}` → `{StaticResource PCMigRadiusInput}`；`HighlightBackground` 的 `ComboBoxHiglightBorderCornerRadius` → `PCMigRadiusInput`；`PopupBorder` 的 `OverlayCornerRadius` → `PCMigRadiusOverlay`。
18. **`src\PCMig.WinUI\Themes\Typography.xaml`**（改）—— 注释重写（新增"自然行高 + 余量"口径与度量依据）；`LineHeight`：`PCMigTextFooterPercent` 24→**26**、`PCMigTextFooterValue` 18→**20**、`PCMigTextTotalPercent` 52→**60**、`PCMigTextStatValue` 28→**32**。**字号与字重未动**。

### F. 测试（3，全部是"随用户规格同步"，不是放宽断言）
19. **`tests\PCMig.Core.Tests\ProgressTruthModelTests.cs`**（改）—— PG-08 改名 `PG08_Bytes_UsesWindowsStyleLabels`，InlineData 改 `1 KB` / `1.5 KB` / `1 MB` / `1 GB` / `1 TB`；PG-08b 改名 `PG08b_NoIecLabelsAnywhereInFormattedOutput` 并断言反转；PG-14 的 `Assert.All(positive, InFlightSource == WorkerIoCounters)` → `Assert.Contains(...)`（保留"遥测接线必须存在"的锁）。
20. **`tests\PCMig.Core.Tests\Batch5UiLayoutContractTests.cs`**（改）—— 单位契约改为 `Contains("KB")` / `Contains("GB")` / `DoesNotContain("KiB")` / `DoesNotContain("GiB")`。
21. **`tests\PCMig.Core.Tests\PauseCoreSemanticsTests.cs`**（改）—— P-04 的 `Assert.Equal(0, persisted.CompletedBytes)` → `Assert.True(> 0)` + `Assert.True(< 4 MiB)`；保留 `CompletedObjects == 0`、`Percent < 100`、`CurrentObjectId == null`。

### G. 文档与导航（7）
22. **`docs\UI-CLOSURE-REPORT-20261005.md`**（新）—— 交付报告（§25 汇报字段 / 逐项修复 / 构建测试 / 真实运行证据 / PMML diff summary / 人工复验清单 / 开放项与回退）。
23. **`docs\UI-CLOSURE-ISSUES-20261005.md`**（新）—— 问题表：5 张标注截图逐条登记（含 modlens 逐字转录）+ IMG5 的**证伪结论** + 14 项状态表（全部 FIXED 并附改前实测数字）。
24. **`docs\工作交接-20261005-UI-Closure.md`**（新）—— 产品技术侧交接（20 行逐文件改动表 + 文档改动表 + 证据 + 构建测试 + 真实运行证据 + 回退 + 未做 + 纪律提醒）。
25. **`docs\PCMig-Visual-Motion-Language.md`**（改）—— 追加 **附录 A：§19 几何基础标准 / §20 排版与光学基线 / §21 布局隔离与锚定 / §22 进度视觉语言 / §23 运动与入场 / §24 Motion 性能规则** + **规则 R16–R31**（+185 行）。
26. **`docs\PMML-Implementation-Audit.md`**（改）—— Progress 族新增「2026-10-05 UI Closure 更新」节（含实现纪律与踩坑）+ 新增「Token / 样式增量」表 + 两条过时结论加取代说明（+55 −2）。
27. **`docs\PMML-Legacy-Deviations.md`**（改）—— L-07 更新（Sweep Token 已接线）、G-04 更新（Sweep 已实现）、**新增 L-17**（33 处字面量圆角）、**L-18**（三个圆角 Token 仍零引用）。
28. **`docs\INDEX.md`** 与 **`AGENTS.md`**（改）—— 同步导航指针：INDEX §七 新增本轮并行轨道交接条目 + 报告指引；AGENTS.md §五 新增「并行 lab 轨道（UI Closure）」条目。

---

## 4. 证据与产物的存放坐标（新会话需要时会用到）

### 仓库内（权威）
- 本文件、`docs\工作交接-20261005-UI-Closure.md`、`docs\UI-CLOSURE-REPORT-20261005.md`、`docs\UI-CLOSURE-ISSUES-20261005.md`
- PMML：`docs\PCMig-Visual-Motion-Language.md`（附录 A §19–§24 + R16–R31）、`docs\PMML-Implementation-Audit.md`、`docs\PMML-Legacy-Deviations.md`、`docs\PMML-UI修改硬性规范.md`
- 导航：`docs\INDEX.md`、`AGENTS.md`

### 实验室/证据侧（本会话新建）
- `E:\PCMigLab\Evidence\Trust-Critical-Recovery\UI-CLOSURE-20261005\`：`measure-text-widths.py` / `text-widths.json`、`measure-text-widths2.py` / `text-widths2.json`（**"100.0%"@17 Bold = 61.0 px；"约 23 小时 59 分"@13 = 100.0 px**）、`measure-font-metrics.py` / `font-metrics.json`（**20→28 零余量、40→54 vs 配置 52 真裁 2 px**）、`shot-01-initial-step1.png`、`b10-full-transcript.txt`
- `E:\PCMigLab\Evidence\UI-Closure-20261005\`：`pixel-measure-report.md` + 24 个 `measure-*.py` + `crop-*.png`（4 项改前实测）
- `E:\PCMigLab\Evidence\Trust-Critical-Recovery\FINAL-FIX-CANDIDATE-IDENTITY-20261005-0151.txt`（第 1 轮候选身份）
- `E:\PCMigLab\Evidence\Trust-Critical-Recovery\CURRENT-TRUST-CRITICAL-CHECKPOINT.txt`（尾部新增 01:52 移交段）
- `E:\PCMigLab\Staging\recovery-gate\dump-texts.ps1`（**新**：全窗 Text 转储，用于验证状态路由）
- `E:\PCMigLab\Staging\recovery-gate\build-ui-closure-package.ps1`（**新**：桌面「改动代码」包生成器；**已彻底 ASCII 化**）
- `E:\PCMigLab\Staging\build-src-package.ps1`（**新**：整版源码包 staging 生成器；同样 ASCII 化）
- `E:\PCMigLab\Staging\PCMig-v0.5.0-这一版源码-20261005\`（源码包 staging，可直接再压缩）
- `E:\PCMigLab\Staging\final-candidate-backup-20261005-0152\`（二进制回退备份）
- 子代理产物（桌面侧）：`_pcmig-motion-research\WinUI-Composition-Motion-Plan.md`、`_pcmml-audit`→`_pcmig-pmml-audit\PMML-Coverage-Matrix.md`、`_pcmig-trace\units-and-pause-trace.md`

### 桌面交付（`D:\Users\User\Desktop\新建文件夹 (4)\`）
| 项 | 内容 |
|---|---|
| `PCMig-UI-Closure-这一版源码-20261005.zip` | **8.39 MB / 505 条目**（497 源码与文档 + `_交付清单`）；根目录 `PCMig-v0.5.0-这一版源码-20261005\` |
| `PCMig-UI-Closure-这一版源码-说明.md` | 包外说明副本（面向外部 AI 的报告） |
| `PCMig-UI-Closure-改动代码-20261005-0303\` | 28 改动文件镜像 + `01-清单`（21 diff + 340 KB patch + 105 文件 SHA256）+ `03-仓库外新增产物`（48 项）+ `00-说明.md` |
| `PCMig-FinalFixCandidate-ManualAcceptance-20261005-0155\` | 第 1 轮的候选身份与交接副本 |
| `PCMig-UI-Closure-20261005\` | 第 2 轮的过渡文档包（**只含文档与证据、不含改动源码**，格式与惯例不同；用户可自行删除） |
| ⚠ 残留 | `PCMig-UI-Closure-代码-20261005-030445\`（本会话打包脚本失败尝试留下的**残缺名**目录，可直接删）；`PCMig-UI-Closure-改动代码-20261005-0303.zip`（2.37 MB，**来源未确认**，非本会话生成） |

---

## 5. 验证状态（哪些已验、哪些没验）—— 新会话不要混用

**已验（可复述为事实）**
- 构建：`dotnet build PCMig.sln -c Release` ⇒ 成功 / **0 error / 3 warning**（= 基线）。
- 测试：`dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release` ⇒ **468/468**。
- 启动：PID **227500**、标题 `PCMig 迁移工具 · v0.5.0`、停在初始页；初始页截图 `shot-01-initial-step1.png`。
- 真机 Step1 连接：夹具 `step1-connect.ps1 -Host_ localhost` ⇒ **发现 8 个共享**；`STEP1-STATUS=[]`（表单内状态行空）；`dump-texts.ps1` 显示 **`已连接 localhost，发现 8 个共享。选择共享后可进入下一步。` 出现在左侧提示卡** ⇒ **UI-10 通过**。
- 像素实测（改前基线）：底栏百分比可用宽 39..41 px；Popup 越出 59 px（底边口径 51 px）；下拉框圆角 r≈2..3 px；统计卡文字未贴边（证伪"被一刀切"）。
- 字体度量：自然行高 11→15 / 13→18 / 17→23 / **20→28** / **40→54**。

**未验（必须标注为未验）**
- 运动类全部（平滑插值 / 扫描高光 / 粒子 / 柔光 / 提示卡扩张 / 文本入场）—— 需真实迁移 + 连续帧或短视频。
- 暂停恢复真值（UI-02）真机复验。
- Popup 对齐与圆角的**改后**像素复核。
- 多 DPI（125% / 150%）行高无裁切复核。
- Diagnostics 测试未重跑。

---

## 6. 下一步建议（新会话接手顺序）

1. **先只读同步现实**（≤3 分钟）：`git rev-parse HEAD` / `branch` / `status --porcelain=v1`（应为 82 条）/ 确认 `PCMig.WinUI.exe` 进程与路径（必须是 `bin\x64\Release\…`，**不要用** `bin\Release\…` 的旧产物）/ 确认没有遗留 robocopy。
2. **问用户要指令**：本会话所有代码改动**已就位但未验收**，用户的下一条指令决定是"继续修 UI/动效"、"跑真机复验"、还是"回到上一轮的 Recovery 卡点"。
3. 若继续 UI 线，按用户 §22 清单做**真实运行复验**：优先解决"运动类如何证明非跳帧"（可用连续帧截图 + 帧间像素差，或录屏）。
4. 若回到 Recovery 线：**唯一未结卡点 = 缺陷#3**（case8 首次点「恢复任务」被吞），判定入口在 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\recovery-gate\case08*.log` 与 `case08c-flyout-probe.ps1 -StopAfterAdopt` + `probe-focus-eat.ps1`；判定必须走 **Action Trigger Gate**（UI.UserActionObserved + 引擎事件双证），无 Action Event 一律判自动化无效，**不得**据此改产品。
5. 任何新改动都要：改 → `dotnet build PCMig.sln -c Release`（0 error）→ 相关测试 → 启动新 EXE 截图 → 再交付；**保持"只增不覆"的交接文档与桌面交付包习惯**。

---

## 7. 纪律红线（新会话必须原样继承）

1. **禁止** `git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`；**禁止** push / tag / release；**禁止**动用户真实源数据。
2. **禁止**降低断言、把请求成功当业务成功、只修 UI 假象、用 sleep 掩盖 race、加大 timeout 求绿、把失败降 warning。
3. **五层一致**：用户动作 → 真实效果 → 真实 UI → 真实诊断 → 最终数据一致。
4. **只看真实证据**：模型假设 < 真实 UI 状态；"我点击了"的文字描述 < 动作事件；夹具 Running < 引擎事件。
5. **不得把夹具失败写成产品缺陷**（本会话已严格区分：`step1-connect.ps1` 的 `∞` 坐标崩溃 = 夹具缺陷；UI-10 的修复 = 产品修复且有真机证据）。
6. **PMML 纪律**：任何影响 UI 视觉/布局/材质/动效/模板的改动，开工前读 `docs\PCMig-Visual-Motion-Language.md` 与 `docs\PMML-UI修改硬性规范.md`，完成后按 Compliance Gate 逐项声明；纯文案/逻辑写 `PMML Visual Impact: None`。
7. **PS 5.1 陷阱（本会话踩了两次）**：pwsh 工具实为 Windows PowerShell 5.1 —— 含中文的 `.ps1` **必须 UTF-8 with BOM**；**无 BOM 的 UTF-8 脚本里连中文注释都会被按 GBK 解码并吞掉换行**，从而破坏相邻语句（本会话因此产出过 `02-仓库内文件` 这种残缺目录名）。写法：脚本**全 ASCII**，中文一律用 `[char]` 码位拼接，或写完后 `Set-Content -Encoding UTF8` 补 BOM 并彻查无 CJK。
8. **编号口径提醒**：`docs\UI-CLOSURE-ISSUES-20261005.md` 的 `UI-01…UI-14` 是**该问题表内部编号**（按施工顺序 U1–U4 重排），与用户指令正文条目顺序**不完全对应** —— 引用时以"编号 + 描述性标题"并列，避免误导。
9. **用户口令**：听到「进度暂停」立即停止并冻结落盘（只读待命）；听到「进度恢复」才继续。
10. **桌面交付习惯**：每一大轮结束把本轮改动/相关/待核对文件复制到桌面「新建文件夹 (4)」的带日期子目录（**只复制不改源**）+ 清单/校验；本轮的命名与结构惯例是 `PCMig-<主题>-<日期或时间戳>\` + `00-说明.md` + `01-清单` + `02-仓库内改动文件` + `03-仓库外新增产物`。

---

## 8. 回退办法

- **源码**：本会话全部改动**未 commit** ⇒ 在源仓按 `01-清单\diff-stat.txt` 清单执行 `git checkout -- <文件>` 就能整体回退；
- **新增文件**需手工删除：`src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs`、`docs\UI-CLOSURE-REPORT-20261005.md`、`docs\UI-CLOSURE-ISSUES-20261005.md`、`docs\工作交接-20261005-UI-Closure.md`（+ 本文件）；
- **装饰层可软关闭**：把 `MainWindow.xaml.cs` / `Step3ProgressPage.xaml.cs` 里的 `new ProgressMotionDriver(...)` 置空即可（它不持有任何业务引用，业务真值路径不受影响）；
- **二进制**：`E:\PCMigLab\Staging\final-candidate-backup-20261005-0152\`（第 1 轮备份）；`E:\PCMigLab\Staging\PCMig-v0.5.0-这一版源码-20261005\`（源码包 staging，可重新压缩）。