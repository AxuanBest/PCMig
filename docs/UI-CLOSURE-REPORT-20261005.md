# PCMig UI Closure 交付报告 — 2026-10-05

> **授权**：用户 2026-10-05 02:22 指令（问题表 `docs\UI-CLOSURE-ISSUES-20261005.md`；PMML 偏离授权见该文件 §118，PMML-R15 路径）。
> **性质**：人工验收后的 UI/UX/Visual Motion 专项收口，**不改业务主链路**。
> **状态**：`docs\UI-CLOSURE-ISSUES-20261005.md` 全部 **14 项 = FIXED**；构建 **0 error / 3 warning**（= 基线）；Core 测试 **468/468**；改动**未 commit / 未 push / 未 tag**。

---

## 一、汇报字段（用户 §25 口径）

| 字段 | 结果 |
|---|---|
| **UI CLOSURE** | 完成（14 项全部 FIXED，代码级 + 构建级 + 真机运行级验证；视觉终验待人工） |
| **PROGRESS PAUSE-STOP TRUTH** | 已修：`MarkInterrupted` 补实测落盘量 + `Interrupted` 进度取单调下限；暂停/停止不再把已传字节覆写为 0，`剩余 X% 未传` 改字节口径 |
| **PERCENT DISPLAY** | 已修：`FooterPercentText` `Width 48→68` + `TextTrimming None`（实测 `100.0%` = 61.0 px，改前可用宽 39..41 px，缺 14..16 px） |
| **UNITS** | 已改：唯一 user-facing formatter `Format.cs:10` → `B/KB/MB/GB/TB/PB`（除法仍 1024），全 UI / HTML 报告 / CLI / 预检 / Step2 目录树一处生效 |
| **METRIC CLIPPING** | **像素实测证伪**"顶部被一刀切"（系批注黑块遮挡，卡顶→label 59 px 未贴边）；按度量加固行高（40 号 52→60 修正真裁 2 px；20 号 28→32 消除零余量） |
| **FOOTER LAYOUT** | 已隔离：百分比 68 / 字节 150 / 速率 100 / ETA 112 全部定宽，动作按钮各 124 固定，动态数值不再推动动作区 |
| **DROPDOWN ALIGNMENT** | 已修：`AlignExistingJobsFlyoutWidth()` 用 `anchorWidth − 6`（chrome 2+2+1+1），去 `MinWidth/MaxWidth` 硬夹取，FlyoutPresenter `MaxWidth 380→1200`（改前实测 Δ = 59 px） |
| **HINT PANEL** | 已修：上界 = `SidebarHost.ActualHeight − StepNav.ActualHeight − 12`（下界 160 DIP），超出走卡内 `ScrollViewer`；扩张/收缩 180 ms `CubicEase/EaseOut`（走 XAML `Height`，参与布局） |
| **PROGRESS SMOOTH MOTION** | 已实装：`ProgressMotionDriver` 满宽填充 + `InsetClip.RightInset` 标量补间（追赶 55%/s、单段 60–400 ms、线性、`Stopwatch` 精确复现起点） |
| **PROGRESS PARTICLE** | 已实装：8 颗柔和亮点（半径 2.0/1.65/1.3、alpha 峰 0.26、0.90 s、负 `DelayTime` 错相、带 34 DIP、`#8CB4FF`/`#BCA4FF`），挂轨道宿主子树，只随真实前沿运动 |
| **PROGRESS SWEEP** | 已实装：44 DIP 窄峰（alpha 0x1E）挂填充子树、自动被 `InsetClip` 裁在已完成区，时长取 Token `PCMigMotionProgressSweepDuration` = 1.60 s；另有前沿柔光 26 DIP（峰 9 DIP、`#5ABCA4FF`） |
| **TEXT·LOG MOTION** | 已实装：`PlayEntrance`（Opacity + `Offset (0,6,0)→0`，170 ms），只在折叠↔显示或文本真变化时播一次，Reduced Motion 降级，装饰失败不影响文本 |
| **PMML SYNC** | 已同步三份文档：规范正文新增**附录 A §19–§24 + R16–R31**；Audit 新增 Progress 族更新与 Token 增量；Legacy 更新 L-07/G-04、新增 L-17/L-18 |
| **PMML SECTIONS CHANGED** | §19 几何基础（圆角 8 档 / 间距 / 关键数字几何 / 禁止文字裁切）、§20 排版与光学基线、§21 布局隔离与锚定、§22 进度视觉语言（填充几何 / 柔光 / 扫描 / 粒子）、§23 运动与入场（插值 / 面板 / 文本）、§24 Motion 性能规则 —— 覆盖用户 §19 要求的全部 17 个检查章节 |
| **BUILD** | `dotnet build PCMig.sln -c Release` ⇒ **成功 / 0 error / 3 warning**（= 基线 `Views\PCMigSurface.xaml:53/54/56` WMC1506） |
| **TESTS** | `PCMig.Core.Tests` 全量 **468 / 468 通过**（含 5 处随用户规格同步的断言更新，非放宽） |
| **FINAL CANDIDATE** | `src\PCMig.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe`（`PCMig.WinUI.dll` SHA256 `4A9E0744B0B47F9A1996A879A5095EF7F98C971E2CFC2D3EDE3DDD9821B28199` @ 02:52:14） |
| **APP PID** | **223888**（标题 `PCMig 迁移工具 · v0.5.0`，停在正常初始页 Step1） |
| **EVIDENCE** | `<实验室根>\Evidence\Trust-Critical-Recovery\UI-CLOSURE-20261005\`（文本宽度/字体度量 JSON、初始页截图、全窗 Text 转储、编号核查留档）+ `<实验室根>\Evidence\UI-Closure-20261005\pixel-measure-report.md`（4 项像素实测） |
| **OPEN UI ISSUES** | UI-02 真机暂停/恢复复验；UI-06/UI-07 改后像素复核；UI-12 装饰在真实 Running 下目视；UI-14 编号口径（见 §五） |

---

## 二、逐项修复说明

### UI-01 底栏百分比显示不全（Critical Numeric Label）
现象：截图实读 `15....`，用户批注"显示 100% 的时候也是这个样子"。
根因：`MainWindow.xaml` 的 `FooterPercentText` `Width="48"` + `TextTrimming="CharacterEllipsis"`。
程序化测量（Pillow + `msyhbd.ttc`，17 Bold）：`100.0%` = **61.0 px**；截图反推可用宽 ≈ 39..41 px ⇒ `15.9%` 缺 4..6 px、`100.0%` 缺 14..16 px。
修：`Width 48→68`（61.0 + 7 余量）、`TextTrimming None`。宽度固定 ⇒ 数值位数变化不推动 Data/Speed/ETA/Action。

### UI-02 暂停/停止后进度错误归零
现象：暂停后 `0.0%`、`0 B / 176.9 GiB`、「剩余 100% 未传」。
根因三条：① `TransferOrchestrator.MarkInterrupted` 不调 `MeasureTarget` ⇒ `TargetBytes` 停在默认 0；② 字节累计无条件赋值 ⇒ 28.1 GiB 被覆写为 0；③ `Report` 的 fallback 快照 `inFlightConfirmedBytes: 0` ⇒ 百分比按 0 重算（放大器：0 被 `SaveState` 落盘）。
修：`MarkInterrupted(receipt, obj)` 补实测；累计对 `Interrupted` 取 `Math.Max` 单调下限；结果页暂停文案改字节口径。测试同步更新三处（旧断言 `CompletedBytes == 0` 正是"暂停即归零"的编码）。
边界纪律：`CompletedObjects = 0` 是真实值（一个对象都没传完），**只修字节与百分比通道，不伪造对象计数**。

### UI-03 Metric Card 数值/标签顶部被裁切 —— **像素实测证伪**
子代理像素测量：放大图相对整窗放大 ≈1.25×；整窗图卡顶边框 y=296、label 墨迹 y355..365、value 墨迹 y377..394、卡底 y414..416 ⇒ 卡顶→label **59 px**；放大图可见带内 y347..370 墨迹数 = 0 ⇒ 真实卡顶 y≈297 **落在黑色标注块（y0..346）内部**。
⇒ "最顶部像被一刀切"来自批注遮挡。**没有给四张卡加 Magic Margin**（用户 §23 明确禁止）。
但产出两条真依据并据此加固：40 号总百分比 `LineHeight 52 < 自然 54`（**真裁 2 px**）；20 号 `28 = 自然 28`（**零余量**，高 DPI 有风险）。行高改 17→26、13→20、40→**60**、20→**32**（自然行高 + 余量口径）。

### UI-04 用户面向单位改回 KB/MB/GB/TB
唯一 user-facing formatter = `src\PCMig.Core\Util\Format.cs`（审计确认**无第二套实现**），改 `Units` 一处即覆盖 WinUI / HTML 报告 / CLI / WPF 线 / 预检 / 快照消息 / Step2 目录树 / VM ~20 处。
除法保持 1024（与 Windows 资源管理器习惯一致）；**诊断证据域刻意不走本 formatter**（`DiagnosticCenterViewModel` 硬编码 KiB 属证据域，不得改）。

### UI-05 ProgressBar 一帧一帧跳
`Presentation\ProgressMotionDriver.cs`（新增，`internal sealed`，零业务引用）：填充改为**常驻满宽**（`Width="Auto"` + `HorizontalAlignment="Stretch"` 本地覆盖样式 `Width=0`），可见长度只由 `InsetClip.RightInset` 标量动画表达；追赶上限 55%/s，单段 60–400 ms，**线性不加 easing**（`InsetClip` 无可回读动画值，加 easing 会让新段起点无法精确复现 ⇒ 回跳）；起点用 `Stopwatch` + 上段起止值线性复现。
禁按帧写 `Width`/几何属性（旧实现 20 Hz 真值 × 60 fps 插值 = 每秒 60 次 layout）。目标只能是真实最新值，绝不预测。

### UI-06 进度粒子 / 光波 / 前沿柔光
- 前沿柔光：26 DIP 宽、峰值 9 DIP 处置于最亮（`#5ABCA4FF`）、右侧渐隐至全透明（视觉外溢不可见）。
- 扫描高光：44 DIP 窄峰（alpha 0x1E）挂**填充子树** ⇒ 自动被 `InsetClip` 裁在已完成区，绝不覆盖未完成区/文字/百分比；时长取既有 Token 1.60 s。
- 粒子流：8 颗柔和亮点，`CompositionSpriteShape` + 共享渐变笔刷，`Offset`/`Opacity` `Forever` 动画、负 `DelayTime` 错相；形态克制（**非**火花/星空/闪粉/噪点）；一次创建、稳态零创建。
- 状态：**只有 Running** 开装饰；`Pausing/Paused/Stopped/Failed/Completed` 一律 `SnapTo(真值)` + 关装饰（暂停时继续扫 = 撒谎）。Reduced Motion 下不建装饰对象、业务零变化。全部包 `try/catch`。

### UI-07 Hint Panel 上界
`ShellHintCard.SetMaxSurfaceHeight()`：上界 = `SidebarHost.ActualHeight − StepNav.ActualHeight − 标准段间距 12`（= `HintCard.Margin.Top`，XAML 同源，不新造数字），下界 160 DIP；内容包进卡内 `ScrollViewer`（超出轻量滚动）。**不监听 HintCard 自身尺寸**（避免回环）。

### UI-08 扩张/收缩动画
`AnimateSurfaceHeight()`：目标 = 内容 extent + Padding，180 ms `CubicEase/EaseOut`；走 **XAML `Height`**（必须参与布局；Composition `Size/Offset` 只改渲染尺寸 ⇒ 父容器不重排、命中测试错位、滚动条长度错）；目标差 <0.5 不重播；Reduced Motion 直接落位。

### UI-09 状态文字入场动效
`ShellHintCard.SetLine()` + `PlayEntrance()`：Option B（Opacity 0→1 + `Offset (0,6,0)→0`，170 ms），**只在折叠↔显示或文本真变化时播一次**；同一句话重复抵达绝不重播（避免变成每两秒自播的干扰源）；无 bounce、无 overshoot。

### UI-10 流程级提示进左侧提示卡
`ConnectionViewModel` 新增 `FlowStatus` / `InlineNote` 两通道，**15 处写入点分流**（流程级 7 处：正在连接…/连接结论/已定位到共享/自动列出失败/正在探测→FlowStatus；字段·错误级 8 处 → InlineNote）；`MigrationSessionViewModel` 订阅 `FlowStatus` → `SetOperational`（提示卡通道）；`Step1ConnectPage.xaml:28` 状态行改绑 `InlineNote`。
**真机验证通过**：连接 `localhost` 后全窗 Text 转储显示 `已连接 localhost，发现 8 个共享。选择共享后可进入下一步。` 出现在**左侧提示卡**（"提示"与"暂无报错"之间），而 `STEP1-STATUS=[]`（表单内状态行为空）。

### UI-11 Popup 与 Anchor 左右对齐
`AlignExistingJobsFlyoutWidth()`：`ExistingJobsList.Width = Max(180, anchorWidth − 6)`；`flyoutChrome = 6 DIP = FlyoutPresenter Padding 2+2 + BorderThickness 1+1`（常量带构成注释，不得写死魔法偏移）；移除 `MinWidth="220"/MaxWidth="360"` 硬夹取；FlyoutPresenter `MaxWidth 380→1200`（宽度改由锚点驱动）。改前实测 Δ = 59 px（红竖线口径）/ 51 px（底边口径）。

### UI-12 圆角统一
新增 Token `PCMigRadiusOverlay(16)`（浮层容器，与 Card 同档）与 `PCMigRadiusListItem(10)`（列表条目）；ComboBox 收起态与 `HighlightBackground` 由系统 `ControlCornerRadius(4)` → `PCMigRadiusInput(14)`；`PopupBorder` 由 `OverlayCornerRadius(8)` → `PCMigRadiusOverlay(16)`；TaskPicker Presenter/Flyout/Item 三段同步。改前实测下拉框 r≈2..3 px vs 同行路径框 8..9 px。剩余 33 处字面量圆角已登记为 Legacy 偏离 L-17（单独授权后清理）。

### UI-13 Footer 动态数值不推动动作区
定宽化：百分比 68 / 字节 150 / 速率 100 / ETA 112（Pillow 实测最长串 `约 23 小时 59 分` = 100.0 px）；四按钮各 124 固定；进度条占弹性列。数值位数变化不再影响按钮 X 位置。

### UI-14 全部标注截图逐条登记
桌面「新建文件夹 (5)」5 张标注截图已**逐张用 `modlens_read_image` 转录批注原文**并登记到 `UI-CLOSURE-ISSUES-20261005.md` §0（含每条的判定 A/B/C 与"改前实测"）。编号口径提醒：问题表 `UI-01…UI-14` 为**问题表内部编号**（按施工顺序 U1–U4 重排），与指令正文条目顺序不完全对应。

---

## 三、构建与测试

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build PCMig.sln -c Release` | **成功 / 0 error / 3 warning**（= 基线 WMC1506×3） |
| Core 测试 | `dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release --no-build` | **468 / 468 通过**（17 s） |
| 编译期踩坑 | `ProgressMotionDriver.cs` | `CS0021`：`VisualCollection` **无索引器** ⇒ 改 `ToArray()`；连带 XamlCompiler WMC0001 全消。`CS8602`：字段改局部引用 |

---

## 四、真实运行证据

| 项 | 证据 |
|---|---|
| 候选启动 | PID **223888**，标题 `PCMig 迁移工具 · v0.5.0`，停在初始页；截图 `shot-01-initial-step1.png`（506837 B） |
| Step1 连接 | 夹具 `step1-connect.ps1 -Host_ localhost` ⇒ **发现 8 个共享**；表单内状态行 **空** |
| 状态路由（UI-10） | 全窗 Text 转储 `TEXT-COUNT=75`，含提示卡内 `已连接 localhost，发现 8 个共享。选择共享后可进入下一步。` |
| 夹具缺陷（非产品） | 该夹具在打印第 4 个 ListItem 时因 UIA 返回 `∞` 坐标崩溃（`Cannot convert value "∞" to type "System.Int32"`）——**已记录为夹具问题** |

**待人工视觉复验**（用户 §22/§24 清单）：Progress `0%/9.9%/15.9%/99.9%/100%`；States `Running/Pausing/Paused/Stopped/Resuming/Completed`；单位 `MB/GB`；Metric 短长速度/ETA、`0/30` 与 `30/30`；Hint 1/3/6 行 + 达上界 + 超上界；Dropdown closed/open/scroll；**Motion 需连续帧或短视频证明非 PPT 跳帧**。

---

## 五、PMML 同步与 diff summary

| 文件 | 变更 |
|---|---|
| `docs\PCMig-Visual-Motion-Language.md` | **+185 −1**（新增「附录 A：UI Closure 视觉基础标准 §19–§24」+ R16–R31 索引） |
| `docs\PMML-Implementation-Audit.md` | **+55 −2**（Progress 族 UI Closure 更新节 + Token/样式增量节 + 两条过时结论取代说明） |
| `docs\PMML-Legacy-Deviations.md` | **+5 −1**（L-07/G-04 更新；新增 L-17 圆角字面量 / L-18 零引用 Token） |
| `docs\INDEX.md` | **+34 −1**（并行轨道最新交接条目） |
| `docs\UI-CLOSURE-ISSUES-20261005.md` | **新增**（问题表 + 状态表 14 项 FIXED + IMG5 证伪结论） |
| `历史交接记录` | **新增**（完整交接：改动/证据/回退/未做） |

**PMML 原则落实**：只写"以后所有同类 UI 都必须遵守"的基础标准，未写入任何一次性问题（不含 JobId / 具体页面文案 / 某 Case 诊断结论）；每条参数尽量只有一个出处（时长取 Token，本轮调用点不写死 sweep 时长）。

---

## 六、开放项与回退

- **开放**：UI-02 真机暂停/恢复复验；UI-06/UI-07 改后像素复核（改前值已存档：Δ=59 px、r≈2..3 px）；UI-12 装饰在真实 Running 目视；顺延的缺陷#3（case8 Resume 归属判定）、Case06/Case09–16、§16 物理预验收、§18 十五份交付文件 + ZIP；既有风险 R-011 / R-012 / R-014 / R-007。
- **回退**：本轮未 commit ⇒ `git checkout -- <文件>`（20 个源码/测试 + 6 个文档）；手工删除新增文件 `src\PCMig.WinUI\Presentation\ProgressMotionDriver.cs`；装饰可置空（不持有任何业务引用），业务真值路径不受影响；二进制回退沿用 `<实验室根>\Staging\final-candidate-backup-20261005-0152\`。