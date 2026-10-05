# 工作交接 — 2026-10-05 UI Closure（UI-01…UI-14 + PMML 同步）

> **性质**：用户人工验收后的 **UI/UX/Visual Motion 专项收口**（不是业务主链路）。
> **授权**：用户 2026-10-05 02:22 指令（问题表 = `docs\UI-CLOSURE-ISSUES-20261005.md`；PMML 偏离授权见该文件 §118，PMML-R15 路径）。
> **未被本文件替换**：产品 Current Handover 仍是 `docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md`；本文件与并行 lab 轨道同属"只增不覆"。
> **一句话**：14 项已确认 UI 问题**全部代码级 FIXED**，Release 构建 **0 error / 3 warning**（= 基线 WMC1506×3），
> PMML 按授权同步（新增附录 A §19–§24 + 规则 R16–R31）；真实运行侧已验 Step1 连接与状态路由；改动**未 commit / 未 push / 未 tag**。

---

## 一、本轮做了什么

### 1. 代码改动（逐文件）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `src\PCMig.Core\Util\Format.cs:10` | `Units` `["B","KiB",…]` → **`["B","KB","MB","GB","TB","PB"]`**（除法仍 1024；诊断证据域刻意不走本 formatter） |
| 2 | `src\PCMig.Core\Transfer\TransferOrchestrator.cs` | `MarkInterrupted(receipt, obj)` 补 `MeasureTarget`（被打断对象实测落盘量）；字节累计对 `Interrupted` 取单调下限（`Math.Max(state.CompletedBytes, settledBytes)`）；两处调用点同步 |
| 3 | `src\PCMig.WinUI\Presentation\ConnectionViewModel.cs` | 新增 `FlowStatus` / `InlineNote` 两通道 + `SetFlowStatus` / `SetInlineNote`；**15 处**写入点分流（流程级 7 / 字段·错误级 8），文件内 `Status =` 直接赋值归零 |
| 4 | `src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs` | 订阅 `ConnectionViewModel.FlowStatus` → `SetOperational`；结果页暂停文案改字节口径（`剩余 {Format.Bytes(TotalBytes−CompletedBytes)}（{…}%）未传`） |
| 5 | `src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs` | **新增**（`internal sealed`）：UI-05 补间（满宽 Fill + `InsetClip.RightInset`、追赶 55%/s、单段 60–400 ms、线性复现起点）+ UI-06 装饰（Sweep 44 DIP/alpha 0x1E、Glow 26 DIP/峰 9 DIP、粒子 8 颗/带 34 DIP/0.90 s）+ `SetActive` 状态映射 + Token 读时长 |
| 6 | `src\PCMig.WinUI\MainWindow.xaml` | `FooterPercentText` `Width 48→68` + `TextTrimming None`；`FooterEtaText` `Width 100→112` + `TextTrimming None`；`FooterProgressFill` 加 `Width="Auto" HorizontalAlignment="Stretch"`；侧栏内层 Grid 命名 `SidebarHost` |
| 7 | `src\PCMig.WinUI\MainWindow.xaml.cs` | `UpdateFooterProgressFill()` 接补间驱动 + `SetActive(Running)`；`AttachFooterToSession()` 建驱动、挂 `SidebarHost/StepNav` 尺寸事件；新增 `UpdateHintCardBounds()`；字段 `_footerMotion` / `_lastFooterFillWidth` |
| 8 | `src\PCMig.WinUI\Views\Step3ProgressPage.xaml` | `TotalProgressFill` 加 `Width="Auto" HorizontalAlignment="Stretch"`；`ObjectPaneHintText` `LineHeight 16→18` |
| 9 | `src\PCMig.WinUI\Views\Step3ProgressPage.xaml.cs` | 接 `ProgressMotionDriver`（`SetTrackWidth/SetActive/SetTarget/SnapTo`）；`normalProgress = Phase==Running && width >= _lastRenderedWidth` |
| 10 | `src\PCMig.WinUI\Views\Step1ConnectPage.xaml:28` | `StatusLineText` 绑定 `Status` → **`InlineNote`** |
| 11 | `src\PCMig.WinUI\Views\ShellHintCard.xaml` | 外层 `Border` 命名 `HintCardSurface`；内容包进 `ScrollViewer` `HintScroll`（UI-08 上界 + 内部滚动） |
| 12 | `src\PCMig.WinUI\Views\ShellHintCard.xaml.cs` | `SetMaxSurfaceHeight()`（下界 160 DIP）+ `AnimateSurfaceHeight()`（180 ms `CubicEase/EaseOut`，走 XAML `Height`）+ `SetLine()` + `PlayEntrance()`（170 ms，Opacity + `Offset (0,6,0)→0`） |
| 13 | `src\PCMig.WinUI\Views\Step2SelectDataPage.xaml` | TaskPicker Presenter/Flyout/Item 三段圆角改 Token；`ExistingJobsList` 去 `MinWidth/MaxWidth`；FlyoutPresenter `MaxWidth 380→1200` |
| 14 | `src\PCMig.WinUI\Views\Step2SelectDataPage.xaml.cs` | 新增 `AlignExistingJobsFlyoutWidth()`（`anchorWidth − 6`，chrome = Padding 2+2 + BorderThickness 1+1） |
| 15 | `src\PCMig.WinUI\Themes\Materials.xaml` | 新增 `PCMigRadiusOverlay(16)` / `PCMigRadiusListItem(10)` + 层级注释更新 |
| 16 | `src\PCMig.WinUI\Themes\PcmigComboBoxRoll.xaml` | 收起态 / `HighlightBackground` 圆角 `ControlCornerRadius(4)` → `PCMigRadiusInput(14)`；`PopupBorder` `OverlayCornerRadius(8)` → `PCMigRadiusOverlay(16)` |
| 17 | `src\PCMig.WinUI\Themes\Typography.xaml` | `LineHeight` 17→**26**、13→**20**、40→**60**、20→**32**（自然行高 + 余量口径）+ 注释重写 |
| 18 | `tests\PCMig.Core.Tests\ProgressTruthModelTests.cs` | PG-08 改名 `PG08_Bytes_UsesWindowsStyleLabels`（InlineData 改 KB/MB/GB/TB）；PG-08b 断言反转；PG-14 改"至少一个遥测样本"（保留遥测接线锁） |
| 19 | `tests\PCMig.Core.Tests\Batch5UiLayoutContractTests.cs:249-253` | 单位契约改 `"KB"/"GB"`、`DoesNotContain "KiB"/"GiB"` |
| 20 | `tests\PCMig.Core.Tests\PauseCoreSemanticsTests.cs:93-94` | `CompletedBytes == 0` → `> 0 且 < 4 MiB`（编码"暂停即归零"的旧断言正是 UI-02 的假进度本体） |

> 注：#1–#3、#18–#20 属"用户规格演进"而非业务功能变更；其余为纯视觉 / 布局 / 动效。

### 2. 文档改动（PMML 同步，用户 §19 要求 17 章节）

| 文件 | 改动 |
|---|---|
| `docs\PCMig-Visual-Motion-Language.md` | 末尾追加 **附录 A：§19 几何基础标准 / §20 排版与光学基线 / §21 布局隔离与锚定 / §22 进度视觉语言 / §23 运动与入场 / §24 Motion 性能规则**，覆盖全部 17 个必需章节；并追加 **R16–R31**（16 条新规则）索引 |
| `docs\PMML-Implementation-Audit.md` | Progress 族新增「2026-10-05 UI Closure 更新」节（补间/柔光/扫描/粒子/状态映射全部带实现值与文件）；新增「Token / 样式增量」表；上表两条过时结论已加取代说明 |
| `docs\PMML-Legacy-Deviations.md` | L-07 更新（`ProgressSweepDuration` 已接线）；G-04 更新（Sweep 已实现，仅 Dialog 仍缺）；**新增 L-17**（圆角字面量 50 处 / 33 处字面量）与 **L-18**（三个圆角 Token 仍零引用） |
| `docs\UI-CLOSURE-ISSUES-20261005.md` | §0 IMG5 结论更新（像素测量部分证伪 + 两条真依据）；状态表 14 条全部更新为 FIXED 并附证据 |
| `docs\INDEX.md` | §七 新增本轮并行轨道交接条目 |

### 3. 证据文件（`E:\PCMigLab\Evidence\Trust-Critical-Recovery\UI-CLOSURE-20261005\`）

- `measure-text-widths.py` / `text-widths.json`、`measure-text-widths2.py` / `text-widths2.json`：关键数字与 ETA 的程序化宽度（Pillow）；
- `measure-font-metrics.py` / `font-metrics.json`：字体自然行高（Microsoft YaHei UI）；
- `shot-01-initial-step1.png`：候选启动后初始页截图；
- `b10-full-transcript.txt`：编号核查用留档。
- （另在 `E:\PCMigLab\Evidence\UI-Closure-20261005\` 有子代理的 `pixel-measure-report.md` + 24 个 `measure-*.py` + `crop-*.png`：4 项像素实测报告。）

---

## 二、构建与测试

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build PCMig.sln -c Release` ⇒ **成功 / 0 error / 3 warning**（= 基线 `Views\PCMigSurface.xaml:53/54/56` WMC1506） |
| Core 测试 | 全量 **468 / 468 通过**（`--no-build`，17 s）—— 含 5 处随规格同步的断言更新 |
| 编译期踩坑（已修） | `error CS0021`：`VisualCollection` **无索引器** ⇒ `Children[i]` 非法，改 `ToArray()`；`warning CS8602`：改用局部引用 `motion` 而非字段 |

---

## 三、真实运行证据（本轮已做）

| 项 | 证据 |
|---|---|
| 候选身份 | `…\win-x64\PCMig.WinUI.dll` SHA256 `4A9E0744B0B47F9A1996A879A5095EF7F98C971E2CFC2D3EDE3DDD9821B28199`（02:52:14）；`PCMig.Core.dll` `64E36DE7…`；apphost `PCMig.WinUI.exe` 哈希不变属正常（281 KB 启动器） |
| 启动 | PID `223888`，标题 `PCMig 迁移工具 · v0.5.0`，停在初始页（Step1） |
| Step1 连接（UI-10） | 夹具 `step1-connect.ps1 -Host_ localhost`：**发现 8 个共享**；`STEP1-STATUS=[]`（**表单内状态行空** ⇒ 流程级文本不再内联）；夹具在打印第 4 项时因 UIA 返回 `∞` 坐标崩溃（**夹具缺陷，非产品缺陷**） |
| 初始页截图 | 提示卡「就绪 / 暂无报错」、底栏 `0.0% 0 B / 0 B`、四步卡片正常（见 `shot-01-initial-step1.png`） |

**未做的真实复验（需人工，见 §五）**：真实迁移中的进度平滑/装饰（Running 态）、暂停归零（UI-02）真机、Popup 对齐（UI-06）改后实测、圆角（UI-07）改后实测、Hint 上界/动画（UI-08/09）、文本入场（UI-14）。

---

## 四、回退方式

- 本轮**未 commit**：`git checkout -- <文件>` 即可整体回退（20 个源码/测试文件 + 5 个文档）。
- 新增文件（无版本历史）需手工删除：`src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs`。
- `ProgressMotionDriver` 是**纯装饰驱动**：若需临时停用，把两个调用点的 `new ProgressMotionDriver(...)` 置空即可，业务真值路径不受影响（驱动不持有任何业务引用）。
- 二进制回退备份沿用上轮：`E:\PCMigLab\Staging\final-candidate-backup-20261005-0152\`（旧候选四件）。

---

## 五、未做 / 下一步

1. **人工视觉复验**（用户主导，用户 §22/§24 清单）：Progress `0%/9.9%/15.9%/99.9%/100%`；States `Running/Pausing/Paused/Stopped/Resuming/Completed`；单位 `MB/GB`；Metric 短长速度与 ETA、`0/30` 与 `30/30`；Hint 1/3/6 行 + 达上界 + 超上界；Dropdown closed/open/scroll；Motion **录连续帧或短视频**证明非 PPT 跳帧。
2. UI-02 的**真机暂停/恢复**复验（Core 已修 + 单测绿，但需真实迁移确认显示不再归零）。
3. UI-06「Popup 与 Anchor 改后实测 Δ=0」、UI-07「下拉框圆角改后实测」两项像素复核（改前值已存档：Δ=59 px；r≈2..3 px）。
4. UI-12 的装饰效果需在**真实 Running** 下目视确认（本次仅验证了代码路径与编译；未在真实迁移中目视）。
5. 顺延未结（非本轮范围，沿用前一轮）：缺陷#3（case8 Resume 首次点击被吞的归属判定）、Case06/Case09–16、FIX-BATCH-7 文档、§16 物理预验收、§18 十五份交付文件 + ZIP。
6. 既有 OPEN RISKS 不变：R-011（Stop 缺引擎侧真值对等物，P1）、R-012（进程 IO 计数器口径，P2）、R-014（SecurityHint 写入点单薄，P3）、R-007（诊断测试负载下偶发 flake）。

---

## 六、纪律与风险提示

- 本轮**未改任何 Business Core 的行为语义**（唯一 Core 改动是进度真值/单位，且已被用户规格明确要求，并有测试同步）。
- 装饰层全部包在 `try/catch` 中：**装饰失败绝不影响进度真值显示**。
- 进度动画的硬约束已写进 PMML（R17/R19/R25/R26）：目标只能是真实最新值；非 Running 一律 `SnapTo` 并关装饰；装饰不得越界到未完成区。
- 编号口径提醒：`UI-CLOSURE-ISSUES-20261005.md` 的 `UI-01…UI-14` 是**该问题表内部编号**（按施工顺序 U1–U4 重排），与用户指令正文的条目顺序可能不同；核对请以问题表的标题/描述为准。